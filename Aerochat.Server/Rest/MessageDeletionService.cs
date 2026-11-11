using Aerochat.Server.Auth;
using Aerochat.Server.Auth.OAuth;
using Aerochat.Server.Data;
using Aerochat.Server.Gateway;
using Aerochat.Server.Hardening;
using Aerochat.Server.Moderation;
using Microsoft.EntityFrameworkCore;

namespace Aerochat.Server.Rest;

public sealed class MessageMutationGate
{
    internal SemaphoreSlim Semaphore { get; } = new(1,1);
}

public sealed class MessageDeletionService(ChatDb db,IExternalUserStore users,SessionService sessions,
    TimeProvider clock,GatewayHub gateway,ModerationSettings settings,ModerationQuota quota,
    FixedWindowRateLimiter authorQuota,ModerationAudit audit,MessageMutationGate mutationGate)
{
    public async Task<IResult> DeleteAsync(HttpContext context,string conversationId,string messageId,CancellationToken token)
    {
        var actor=await ConversationAuth.TryGetCurrentUserAsync(context,sessions,users,token);
        if(actor is null)return ConversationAuth.Unauthorized(context);
        if(!Guid.TryParse(conversationId,out var conversation)||!Guid.TryParse(messageId,out var id))return ConversationAuth.InvalidRequest();
        await mutationGate.Semaphore.WaitAsync(token);
        try
        {
            await using var tx=await db.Database.BeginTransactionAsync(token);
            var message=await db.Messages.AsNoTracking().SingleOrDefaultAsync(m=>m.Id==id&&m.ConversationId==conversation,token);
            if(message is null)return ConversationAuth.NotFound();
            bool op=settings.IsOperator(actor.Id);
            if(!op&&message.AuthorId!=actor.Id)return ConversationAuth.Forbidden();
            context.Response.Headers.CacheControl="no-store";
            if(message.DeletedAt is not null)return Results.NoContent();
            var decision=op?quota.TryAcquire(actor.Id):authorQuota.TryAcquire(actor.Id.ToString("N"));
            if(!decision.Allowed)
            {
                context.Response.Headers.RetryAfter=Math.Max(1,(int)Math.Ceiling(decision.RetryAfter.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
                return Results.Json(new ErrorDto("rate_limited"),statusCode:429);
            }
            var at=clock.GetUtcNow().ToUniversalTime();
            var audience=await db.Participants.AsNoTracking().Where(p=>p.ConversationId==conversation).Select(p=>p.UserId).Distinct().ToArrayAsync(token);
            var tombstone=new GatewayMessageData(id,conversation,message.AuthorId,"",message.Kind,null,message.CreatedAt,message.EditedAt,at);
            token.ThrowIfCancellationRequested();
            gateway.CommitMessageDeletion(conversation,id,tombstone,audience,()=>
            {
                int changed=db.Messages.Where(m=>m.Id==id&&m.ConversationId==conversation&&m.DeletedAt==null)
                    .ExecuteUpdate(s=>s.SetProperty(m=>m.Body,"").SetProperty(m=>m.RefPayloadJson,(string?)null).SetProperty(m=>m.DeletedAt,at));
                if(changed!=1)throw new InvalidOperationException("Message deletion conflict.");
                audit.Append(actor.Id,"message.delete",id,conversation);
                db.SaveChanges();
                tx.Commit();
            });
            return Results.NoContent();
        }
        finally { mutationGate.Semaphore.Release(); }
    }
}
