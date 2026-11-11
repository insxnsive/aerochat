using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Aerochat.Server.Auth;
using Aerochat.Server.Data;
using Aerochat.Server.Data.Entities;
using Aerochat.Server.Gateway;
using Aerochat.Server.Moderation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aerochat.Server.Tests;

public sealed class MessageDeletionTests
{
    private static readonly Guid Author=Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid Member=Guid.Parse("10000000-0000-0000-0000-000000000002");
    private static readonly Guid Outsider=Guid.Parse("10000000-0000-0000-0000-000000000003");
    private static readonly Guid Conversation=Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly Guid Message=Guid.Parse("30000000-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset Created=DateTimeOffset.Parse("2026-01-01T00:00:00Z");
    private static string PathTo(Guid? id=null)=>$"/conversations/{Conversation}/messages/{id??Message}";
    private static async Task Seed(ApiWebApplicationFactory f,int count=1)
    {
        await f.SeedAsync(db=>
        {
            foreach(var id in new[]{Author,Member,Outsider})db.Users.Add(new ExternalUserEntity{Id=id,Provider="github",ProviderUserId=id.ToString(),DisplayName="fixture",CreatedAt=Created,UpdatedAt=Created});
            db.Conversations.Add(new ConversationEntity{Id=Conversation,Kind="group",CreatedAt=Created,Participants={new ParticipantEntity{UserId=Author,JoinedAt=Created},new ParticipantEntity{UserId=Member,JoinedAt=Created}}});
            for(int i=0;i<count;i++)db.Messages.Add(new MessageEntity{Id=i==0?Message:Guid.NewGuid(),ConversationId=Conversation,AuthorId=Author,Body="private-body",Kind="message",RefPayloadJson="{\"url\":\"private-ref\"}",CreatedAt=Created,EditedAt=Created.AddMinutes(1)});
            return Task.CompletedTask;
        });
    }
    private static HttpClient Auth(ApiWebApplicationFactory f,Guid id)
    {
        var c=f.CreateClient();c.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",f.Services.GetRequiredService<SessionService>().Issue(new Identity("github",id.ToString(),"fixture")));return c;
    }
    private static async Task<T> Read<T>(ApiWebApplicationFactory f,Func<ChatDb,Task<T>> read)
    {await using var scope=f.Services.CreateAsyncScope();return await read(scope.ServiceProvider.GetRequiredService<ChatDb>());}
    [Test]
    public async Task Author_delete_erases_payload_preserves_identity_history_and_is_idempotent()
    {
        using var f=new ApiWebApplicationFactory();await Seed(f);using var c=Auth(f,Author);
        using var first=await c.DeleteAsync(PathTo());Assert.That(first.StatusCode,Is.EqualTo(HttpStatusCode.NoContent));
        using var second=await c.DeleteAsync(PathTo());Assert.That(second.StatusCode,Is.EqualTo(HttpStatusCode.NoContent));
        var m=await Read(f,db=>db.Messages.AsNoTracking().SingleAsync());
        Assert.Multiple(()=>{Assert.That(m.Body,Is.Empty);Assert.That(m.RefPayloadJson,Is.Null);Assert.That(m.DeletedAt,Is.Not.Null);Assert.That(m.AuthorId,Is.EqualTo(Author));Assert.That(m.Id,Is.EqualTo(Message));Assert.That(m.CreatedAt,Is.EqualTo(Created));Assert.That(m.EditedAt,Is.EqualTo(Created.AddMinutes(1)));});
        var rows=await Read(f,db=>db.Set<ModerationAuditRecord>().ToArrayAsync());Assert.That(rows,Has.Length.EqualTo(1));
        Assert.That(rows[0].ActorId,Is.EqualTo(Author));Assert.That(rows[0].TargetId,Is.EqualTo(Message));Assert.That(rows[0].Action,Is.EqualTo("message.delete"));Assert.That(rows[0].ConversationId,Is.EqualTo(Conversation));Assert.That(rows[0].ReasonCode,Is.Null);
        string history=await c.GetStringAsync($"/conversations/{Conversation}/messages");Assert.That(history,Does.Not.Contain("private-body").And.Not.Contain("private-ref"));
        using var doc=JsonDocument.Parse(history);Assert.That(doc.RootElement.GetProperty("items")[0].GetProperty("deletedAt").ValueKind,Is.EqualTo(JsonValueKind.String));
    }
    [TestCase(false)] [TestCase(true)]
    public async Task Unauthenticated_and_non_author_are_rejected_without_audit(bool authenticated)
    {
        using var f=new ApiWebApplicationFactory();await Seed(f);using var c=authenticated?Auth(f,Member):f.CreateClient();using var response=await c.DeleteAsync(PathTo());
        Assert.That(response.StatusCode,Is.EqualTo(authenticated?HttpStatusCode.Forbidden:HttpStatusCode.Unauthorized));
        Assert.That(await Read(f,db=>db.Set<ModerationAuditRecord>().CountAsync()),Is.Zero);Assert.That((await Read(f,db=>db.Messages.SingleAsync())).Body,Is.EqualTo("private-body"));
    }
    [Test]
    public async Task Operator_can_delete_without_membership_and_uses_separate_quota()
    {
        using var f=new ApiWebApplicationFactory();
        using var configured=f.WithWebHostBuilder(b=>b.ConfigureServices(services=>
        {
            services.RemoveAll<ModerationSettings>();services.AddSingleton(ModerationSettings.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"Moderation:OperatorIds:0",Outsider.ToString()}}).Build()));
        }));
        using var c=configured.CreateClient();
        // Initialize and seed the configured host's own database scope.
        await using(var scope=configured.Services.CreateAsyncScope())
        {
            var db=scope.ServiceProvider.GetRequiredService<ChatDb>();
            db.Users.AddRange(new ExternalUserEntity{Id=Author,Provider="github",ProviderUserId=Author.ToString(),DisplayName="a",CreatedAt=Created,UpdatedAt=Created},new ExternalUserEntity{Id=Outsider,Provider="github",ProviderUserId=Outsider.ToString(),DisplayName="o",CreatedAt=Created,UpdatedAt=Created});
            db.Conversations.Add(new ConversationEntity{Id=Conversation,Kind="group",CreatedAt=Created});
            db.Messages.Add(new MessageEntity{Id=Message,ConversationId=Conversation,AuthorId=Author,Body="private-body",Kind="message",CreatedAt=Created});await db.SaveChangesAsync();
        }
        c.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",configured.Services.GetRequiredService<SessionService>().Issue(new Identity("github",Outsider.ToString(),"fixture")));
        var normal=configured.Services.GetRequiredService<Aerochat.Server.Hardening.FixedWindowRateLimiter>();for(int i=0;i<30;i++)normal.TryAcquire(Outsider.ToString("N"));
        using var response=await c.DeleteAsync(PathTo());Assert.That(response.StatusCode,Is.EqualTo(HttpStatusCode.NoContent));
        var quota=configured.Services.GetRequiredService<ModerationQuota>();for(int i=0;i<29;i++)Assert.That(quota.TryAcquire(Outsider).Allowed,Is.True);Assert.That(quota.TryAcquire(Outsider).Allowed,Is.False);
    }
    [Test]
    public async Task Audit_trigger_failure_rolls_back_prior_update_and_publishes_nothing()
    {
        using var f=new ApiWebApplicationFactory();await Seed(f);
        await f.SeedAsync(db=>{db.Database.ExecuteSqlRaw("CREATE TRIGGER reject_delete_audit BEFORE INSERT ON moderation_audit BEGIN SELECT RAISE(ABORT,'fixture'); END;");return Task.CompletedTask;});
        var hub=f.Services.GetRequiredService<GatewayHub>();var sink=new Sink(Member,"member");hub.Register(sink);sink.Events.Clear();using var c=Auth(f,Author);
        try { using var response=await c.DeleteAsync(PathTo());Assert.That((int)response.StatusCode,Is.GreaterThanOrEqualTo(500)); }
        catch(DbUpdateException error) { Assert.That(error.InnerException,Is.TypeOf<Microsoft.Data.Sqlite.SqliteException>()); }
        Assert.That((await Read(f,db=>db.Messages.AsNoTracking().SingleAsync())).Body,Is.EqualTo("private-body"));
        Assert.That(await Read(f,db=>db.Set<ModerationAuditRecord>().CountAsync()),Is.Zero);Assert.That(sink.Events,Is.Empty);
    }
    [Test]
    public async Task Replay_is_tombstoned_and_deletion_fanout_only_reaches_current_members()
    {
        using var f=new ApiWebApplicationFactory();await Seed(f);var hub=f.Services.GetRequiredService<GatewayHub>();
        var before=hub.Publish("presence.updated",JsonSerializer.SerializeToElement(new{status="online"}),[Member,Outsider]);
        var old=hub.Publish("message.created",new MessageCreatedData(Conversation,new GatewayMessageData(Message,Conversation,Author,"private-body","message",new Dictionary<string,object>{{"url","private-ref"}},Created,null,null)),[Author,Member,Outsider]);
        var member=new Sink(Member,"member");var outsider=new Sink(Outsider,"outsider");hub.Register(member);hub.Register(outsider);member.Events.Clear();outsider.Events.Clear();
        using var c=Auth(f,Author);using var response=await c.DeleteAsync(PathTo());Assert.That(response.StatusCode,Is.EqualTo(HttpStatusCode.NoContent));Assert.That(member.Events.Single().Type,Is.EqualTo("message.deleted"));Assert.That(outsider.Events,Is.Empty);
        var replay=hub.Register(new Sink(Member,"new-member"),before.EventId);Assert.That(replay.ReplayedEvents.Select(e=>e.EventId),Does.Contain(old.EventId));
        foreach(var record in replay.ReplayedEvents)Assert.That(GatewayJson.Serialize(record.Envelope),Does.Not.Contain("private-body").And.Not.Contain("private-ref"));
        Assert.That(hub.Register(new Sink(Outsider,"new-outsider"),before.EventId).ReplayedEvents,Is.Empty);
    }
    [Test]
    public async Task Repeated_deletes_produce_one_audit_and_one_event()
    {
        using var f=new ApiWebApplicationFactory();await Seed(f);using var c=Auth(f,Author);var hub=f.Services.GetRequiredService<GatewayHub>();var sink=new Sink(Member,"member");hub.Register(sink);sink.Events.Clear();
        for(int i=0;i<8;i++){using var response=await c.DeleteAsync(PathTo());Assert.That(response.StatusCode,Is.EqualTo(HttpStatusCode.NoContent));}
        Assert.That(await Read(f,db=>db.Set<ModerationAuditRecord>().CountAsync()),Is.EqualTo(1));Assert.That(sink.Events.Count(e=>e.Type=="message.deleted"),Is.EqualTo(1));
    }
    [Test]
    public async Task Author_quota_rejects_before_change_and_repeat_is_free()
    {
        using var f=new ApiWebApplicationFactory{RateLimit=1};await Seed(f,2);using var c=Auth(f,Author);using var first=await c.DeleteAsync(PathTo());Assert.That(first.StatusCode,Is.EqualTo(HttpStatusCode.NoContent));using var repeat=await c.DeleteAsync(PathTo());Assert.That(repeat.StatusCode,Is.EqualTo(HttpStatusCode.NoContent));
        var other=await Read(f,db=>db.Messages.Where(m=>m.Id!=Message).Select(m=>m.Id).SingleAsync());using var denied=await c.DeleteAsync(PathTo(other));Assert.That(denied.StatusCode,Is.EqualTo(HttpStatusCode.TooManyRequests));Assert.That(denied.Headers.RetryAfter,Is.Not.Null);Assert.That(await Read(f,db=>db.Set<ModerationAuditRecord>().CountAsync()),Is.EqualTo(1));
    }
    private sealed class Sink(Guid user,string connection):IGatewaySink
    {
        public Guid UserId=>user;public string ConnectionId=>connection;public CancellationToken Disconnected=>CancellationToken.None;
        internal List<GatewayEnvelope> Events=[];public bool TryEnqueue(GatewayEnvelope e){Events.Add(e);return true;}public void Abort(GatewayAbortReason reason){}public void Complete(){}
    }
}
