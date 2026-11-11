using Aerochat.Server.Auth;
using Aerochat.Server.Auth.OAuth;
using Aerochat.Server.Data;
using Aerochat.Server.Gateway;
using Aerochat.Server.Moderation;
using Microsoft.EntityFrameworkCore;

namespace Aerochat.Server.Rest;

/// <summary>
/// Serializes message mutations per conversation: a send through its post-save publication and a
/// delete through its commit and gateway update, so a delayed message.created cannot republish
/// original content after a deletion. A message belongs to exactly one conversation, so different
/// conversations never wait for each other. A slot exists only while someone holds or awaits it.
/// </summary>
public sealed class MessageMutationGate
{
    private readonly object _sync=new();
    private readonly Dictionary<Guid,Slot> _slots=[];

    private sealed class Slot
    {
        internal readonly SemaphoreSlim Semaphore=new(1,1);
        internal int Users;
    }

    public int ActiveSlotCount{get{lock(_sync)return _slots.Count;}}

    public int UsersOf(Guid conversation){lock(_sync)return _slots.TryGetValue(conversation,out var slot)?slot.Users:0;}

    public async Task<IDisposable> EnterAsync(Guid conversation,CancellationToken token)
    {
        Slot slot;
        lock(_sync)
        {
            if(!_slots.TryGetValue(conversation,out slot!)){slot=new Slot();_slots[conversation]=slot;}
            slot.Users++;
        }
        try { await slot.Semaphore.WaitAsync(token).ConfigureAwait(false); }
        catch { Leave(conversation,slot);throw; }
        return new Releaser(this,conversation,slot);
    }

    private void Leave(Guid conversation,Slot slot){lock(_sync){if(--slot.Users==0)_slots.Remove(conversation);}}

    private sealed class Releaser(MessageMutationGate owner,Guid conversation,Slot slot):IDisposable
    {
        private int _released;
        public void Dispose()
        {
            if(Interlocked.Exchange(ref _released,1)!=0)return;
            slot.Semaphore.Release();
            owner.Leave(conversation,slot);
        }
    }
}

public sealed class MessageDeletionService(ChatDb db,IExternalUserStore users,SessionService sessions,
    TimeProvider clock,GatewayHub gateway,ModerationSettings settings,ModerationQuota quota,
    ModerationAudit audit,MessageMutationGate mutationGate,ILogger<MessageDeletionService> logger)
{
    public async Task<IResult> DeleteAsync(HttpContext context,string conversationId,string messageId,CancellationToken token)
    {
        var actor=await ConversationAuth.TryGetCurrentUserAsync(context,sessions,users,token);
        if(actor is null)return ConversationAuth.Unauthorized(context);
        if(!Guid.TryParse(conversationId,out var conversation)||!Guid.TryParse(messageId,out var id))return ConversationAuth.InvalidRequest();
        // Every DELETE already drew one unit of the shared per-user message budget in RateLimitingMiddleware.
        // The paths below that change nothing take neither the mutation gate nor a database transaction.
        var message=await db.Messages.AsNoTracking().SingleOrDefaultAsync(m=>m.Id==id&&m.ConversationId==conversation,token);
        if(message is null)return ConversationAuth.NotFound();
        bool op=settings.IsOperator(actor.Id);
        if(!op&&message.AuthorId!=actor.Id)return ConversationAuth.Forbidden();
        context.Response.Headers.CacheControl="no-store";
        if(message.DeletedAt is not null)return Results.NoContent();
        if(op)
        {
            var decision=quota.TryAcquire(actor.Id);
            if(!decision.Allowed)
            {
                context.Response.Headers.RetryAfter=Math.Max(1,(int)Math.Ceiling(decision.RetryAfter.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
                return Results.Json(new ErrorDto("rate_limited"),statusCode:429);
            }
        }
        var at=clock.GetUtcNow().ToUniversalTime();
        Guid[] audience;
        using(await mutationGate.EnterAsync(conversation,token))
        {
            await using var tx=await db.Database.BeginTransactionAsync(token);
            audience=await db.Participants.AsNoTracking().Where(p=>p.ConversationId==conversation).Select(p=>p.UserId).Distinct().ToArrayAsync(token);
            token.ThrowIfCancellationRequested();
            int changed=db.Messages.Where(m=>m.Id==id&&m.ConversationId==conversation&&m.DeletedAt==null)
                .ExecuteUpdate(s=>s.SetProperty(m=>m.Body,"").SetProperty(m=>m.RefPayloadJson,(string?)null).SetProperty(m=>m.DeletedAt,at));
            // Another request deleted it between the check above and the gate: nothing to audit or publish.
            if(changed==0)return Results.NoContent();
            audit.Append(actor.Id,"message.delete",id,conversation);
            db.SaveChanges();
            tx.Commit();
        }
        // The delete is committed. A gateway failure from here on must not turn it into a 500.
        try
        {
            gateway.ApplyMessageDeletion(conversation,id,
                new GatewayMessageData(id,conversation,message.AuthorId,"",message.Kind,null,message.CreatedAt,message.EditedAt,at),audience);
        }
        catch(Exception error)
        {
            logger.LogError(error,"Message deletion committed but gateway delivery failed for conversation {Conversation} message {Message}.",conversation,id);
        }
        return Results.NoContent();
    }
}
