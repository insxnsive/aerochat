using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Aerochat.Server.Auth;
using Aerochat.Server.Data;
using Aerochat.Server.Data.Entities;
using Aerochat.Server.Gateway;
using Aerochat.Server.Moderation;
using Aerochat.Server.Rest;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Aerochat.Server.Tests;

public sealed class MessageDeletionTests
{
    private static readonly Guid Author=Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid Member=Guid.Parse("10000000-0000-0000-0000-000000000002");
    private static readonly Guid Outsider=Guid.Parse("10000000-0000-0000-0000-000000000003");
    private static readonly Guid Conversation=Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly Guid Message=Guid.Parse("30000000-0000-0000-0000-000000000001");
    private static readonly Guid Erased=Guid.Parse("30000000-0000-0000-0000-000000000002");
    private static readonly Guid OtherConversation=Guid.Parse("20000000-0000-0000-0000-000000000002");
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
        using var response=await c.DeleteAsync(PathTo());Assert.That(response.StatusCode,Is.EqualTo(HttpStatusCode.NoContent));
        // The shared message budget was charged exactly once (by the middleware), not twice.
        var normal=configured.Services.GetRequiredService<Aerochat.Server.Hardening.FixedWindowRateLimiter>();for(int i=0;i<29;i++)Assert.That(normal.TryAcquire(Outsider.ToString("N")).Allowed,Is.True);Assert.That(normal.TryAcquire(Outsider.ToString("N")).Allowed,Is.False);
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
    public async Task Delete_requests_draw_the_shared_budget_including_repeats_and_misses()
    {
        using var f=new ApiWebApplicationFactory{RateLimit=3};await Seed(f,2);using var c=Auth(f,Author);
        using var first=await c.DeleteAsync(PathTo());Assert.That(first.StatusCode,Is.EqualTo(HttpStatusCode.NoContent));
        using var repeat=await c.DeleteAsync(PathTo());Assert.That(repeat.StatusCode,Is.EqualTo(HttpStatusCode.NoContent));
        using var miss=await c.DeleteAsync(PathTo(Guid.NewGuid()));Assert.That(miss.StatusCode,Is.EqualTo(HttpStatusCode.NotFound));
        var other=await Read(f,db=>db.Messages.Where(m=>m.Id!=Message).Select(m=>m.Id).SingleAsync());
        using var denied=await c.DeleteAsync(PathTo(other));Assert.That(denied.StatusCode,Is.EqualTo(HttpStatusCode.TooManyRequests));Assert.That(denied.Headers.RetryAfter,Is.Not.Null);
        Assert.That(await Read(f,db=>db.Set<ModerationAuditRecord>().CountAsync()),Is.EqualTo(1));
        Assert.That((await Read(f,db=>db.Messages.AsNoTracking().SingleAsync(m=>m.Id==other))).Body,Is.EqualTo("private-body"));
    }
    [Test]
    public async Task Sends_and_deletes_share_one_per_user_budget()
    {
        using var f=new ApiWebApplicationFactory{RateLimit=2};await Seed(f);using var c=Auth(f,Author);
        using var sent=await c.PostAsync(SendPath(Conversation),SendBody());Assert.That(sent.StatusCode,Is.EqualTo(HttpStatusCode.Created));
        using var first=await c.DeleteAsync(PathTo());Assert.That(first.StatusCode,Is.EqualTo(HttpStatusCode.NoContent));
        using var repeat=await c.DeleteAsync(PathTo());Assert.That(repeat.StatusCode,Is.EqualTo(HttpStatusCode.TooManyRequests));Assert.That(repeat.Headers.RetryAfter,Is.Not.Null);
    }
    [TestCase("missing")] [TestCase("forbidden")] [TestCase("repeat")] [TestCase("unauthenticated")]
    public async Task Free_paths_do_not_wait_for_the_conversation_gate(string path)
    {
        using var f=new ApiWebApplicationFactory();await Seed(f);
        await f.SeedAsync(db=>{db.Messages.Add(new MessageEntity{Id=Erased,ConversationId=Conversation,AuthorId=Author,Body="",Kind="message",CreatedAt=Created,DeletedAt=Created.AddMinutes(2)});return Task.CompletedTask;});
        var gate=f.Services.GetRequiredService<MessageMutationGate>();using var held=await gate.EnterAsync(Conversation,CancellationToken.None);
        using var c=path=="unauthenticated"?f.CreateClient():Auth(f,path=="forbidden"?Member:Author);
        var target=path=="missing"?Guid.NewGuid():path=="repeat"?Erased:Message;
        var expected=path switch{"missing"=>HttpStatusCode.NotFound,"forbidden"=>HttpStatusCode.Forbidden,"repeat"=>HttpStatusCode.NoContent,_=>HttpStatusCode.Unauthorized};
        using var response=await c.DeleteAsync(PathTo(target)).WaitAsync(TimeSpan.FromSeconds(5));Assert.That(response.StatusCode,Is.EqualTo(expected));
    }
    [TestCase("missing")] [TestCase("forbidden")] [TestCase("repeat")]
    public async Task Free_paths_do_not_take_the_sqlite_write_lock(string path)
    {
        string file=Path.Combine(Path.GetTempPath(),"t18-"+Guid.NewGuid().ToString("N")+".db");string connection=$"Data Source={file};Pooling=False;Default Timeout=2";
        try
        {
            using var f=new ApiWebApplicationFactory();
            using var host=f.WithWebHostBuilder(b=>b.ConfigureServices(services=>{services.RemoveAll<DbContextOptions<ChatDb>>();services.RemoveAll<ChatDb>();services.AddDbContext<ChatDb>(o=>o.UseSqlite(connection));}));
            await SeedHost(host.Services,erased:true);
            using var c=host.CreateClient();c.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",host.Services.GetRequiredService<SessionService>().Issue(new Identity("github",(path=="forbidden"?Member:Author).ToString(),"fixture")));
            var target=path=="missing"?Guid.NewGuid():path=="repeat"?Erased:Message;
            var expected=path switch{"missing"=>HttpStatusCode.NotFound,"forbidden"=>HttpStatusCode.Forbidden,_=>HttpStatusCode.NoContent};
            using var writer=new SqliteConnection($"Data Source={file};Pooling=False");writer.Open();
            using(var begin=writer.CreateCommand()){begin.CommandText="BEGIN IMMEDIATE;";begin.ExecuteNonQuery();}
            try { using var response=await c.DeleteAsync(PathTo(target)).WaitAsync(TimeSpan.FromSeconds(8));Assert.That(response.StatusCode,Is.EqualTo(expected)); }
            finally { using var end=writer.CreateCommand();end.CommandText="ROLLBACK;";end.ExecuteNonQuery(); }
        }
        finally { SqliteConnection.ClearAllPools();try{File.Delete(file);}catch(IOException){} }
    }
    [Test]
    public async Task Gate_serializes_one_conversation_lets_others_through_and_cleans_up()
    {
        var gate=new MessageMutationGate();var a=Guid.NewGuid();var b=Guid.NewGuid();
        var first=await gate.EnterAsync(a,CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));var second=gate.EnterAsync(a,CancellationToken.None);
        using(await gate.EnterAsync(b,CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2))){Assert.That(gate.ActiveSlotCount,Is.EqualTo(2));}
        Assert.That(second.IsCompleted,Is.False);Assert.That(gate.UsersOf(a),Is.EqualTo(2));Assert.That(gate.ActiveSlotCount,Is.EqualTo(1));
        first.Dispose();first.Dispose();
        var held=await second.WaitAsync(TimeSpan.FromSeconds(2));Assert.That(gate.UsersOf(a),Is.EqualTo(1));
        held.Dispose();Assert.That(gate.ActiveSlotCount,Is.Zero);
    }
    [Test]
    public async Task Cancelled_gate_waiter_leaves_no_slot()
    {
        var gate=new MessageMutationGate();var a=Guid.NewGuid();using var held=await gate.EnterAsync(a,CancellationToken.None);using var stop=new CancellationTokenSource();
        var waiter=gate.EnterAsync(a,stop.Token);stop.Cancel();Assert.CatchAsync<OperationCanceledException>(async()=>await waiter);Assert.That(gate.UsersOf(a),Is.EqualTo(1));
        held.Dispose();Assert.That(gate.ActiveSlotCount,Is.Zero);
    }
    [Test]
    public async Task Send_in_another_conversation_is_not_blocked_by_a_held_gate()
    {
        using var f=new ApiWebApplicationFactory();await Seed(f);
        await f.SeedAsync(db=>{db.Conversations.Add(new ConversationEntity{Id=OtherConversation,Kind="group",CreatedAt=Created,Participants={new ParticipantEntity{UserId=Author,JoinedAt=Created}}});return Task.CompletedTask;});
        var gate=f.Services.GetRequiredService<MessageMutationGate>();using var c=Auth(f,Author);
        var held=await gate.EnterAsync(Conversation,CancellationToken.None);var blocked=c.PostAsync(SendPath(Conversation),SendBody());
        try
        {
            await Until(()=>gate.UsersOf(Conversation)==2,"the send never reached the gate");
            using var free=await c.PostAsync(SendPath(OtherConversation),SendBody()).WaitAsync(TimeSpan.FromSeconds(5));Assert.That(free.StatusCode,Is.EqualTo(HttpStatusCode.Created));
            Assert.That(blocked.IsCompleted,Is.False);
        }
        finally { held.Dispose(); }
        using var done=await blocked.WaitAsync(TimeSpan.FromSeconds(5));Assert.That(done.StatusCode,Is.EqualTo(HttpStatusCode.Created));Assert.That(gate.ActiveSlotCount,Is.Zero);
    }
    [Test]
    public async Task Delete_that_loses_a_race_for_the_gate_returns_204_without_audit_or_event()
    {
        using var f=new ApiWebApplicationFactory();await Seed(f);var hub=f.Services.GetRequiredService<GatewayHub>();var sink=new Sink(Member,"member");hub.Register(sink);sink.Events.Clear();
        var gate=f.Services.GetRequiredService<MessageMutationGate>();using var c=Auth(f,Author);
        var held=await gate.EnterAsync(Conversation,CancellationToken.None);var losing=c.DeleteAsync(PathTo());
        try
        {
            await Until(()=>gate.UsersOf(Conversation)==2,"the delete never reached the gate");
            await f.SeedAsync(async db=>{var m=await db.Messages.SingleAsync();m.Body="";m.RefPayloadJson=null;m.DeletedAt=Created.AddDays(1);});
        }
        finally { held.Dispose(); }
        using var response=await losing.WaitAsync(TimeSpan.FromSeconds(5));Assert.That(response.StatusCode,Is.EqualTo(HttpStatusCode.NoContent));
        Assert.That(await Read(f,db=>db.Set<ModerationAuditRecord>().CountAsync()),Is.Zero);Assert.That(sink.Events,Is.Empty);
    }
    [Test]
    public async Task Gateway_failure_after_commit_is_logged_and_the_delete_still_succeeds()
    {
        var logs=new LogCapture();
        using var f=new ApiWebApplicationFactory();
        using var host=f.WithWebHostBuilder(b=>{b.UseSetting("Gateway:MaxFrameBytes","64");b.ConfigureLogging(l=>l.AddProvider(logs));});
        await SeedHost(host.Services,erased:false);
        using var c=host.CreateClient();c.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",host.Services.GetRequiredService<SessionService>().Issue(new Identity("github",Author.ToString(),"fixture")));
        using var response=await c.DeleteAsync(PathTo());Assert.That(response.StatusCode,Is.EqualTo(HttpStatusCode.NoContent));
        await using var scope=host.Services.CreateAsyncScope();var db=scope.ServiceProvider.GetRequiredService<ChatDb>();
        var row=await db.Messages.AsNoTracking().SingleAsync(m=>m.Id==Message);Assert.That(row.Body,Is.Empty);Assert.That(row.DeletedAt,Is.Not.Null);
        Assert.That(await db.Set<ModerationAuditRecord>().CountAsync(),Is.EqualTo(1));
        var errors=logs.Errors.ToArray();Assert.That(errors.Any(e=>e.Contains("Message deletion committed but gateway delivery failed",StringComparison.Ordinal)),Is.True);
        Assert.That(errors.Any(e=>e.Contains("private-body",StringComparison.Ordinal)),Is.False);
    }
    [Test]
    public async Task Commit_does_not_hold_gateway_locks()
    {
        using var f=new ApiWebApplicationFactory();await Seed(f);var hub=f.Services.GetRequiredService<GatewayHub>();
        using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();
        await using(var scope=f.Services.CreateAsyncScope())
        {
            var db=scope.ServiceProvider.GetRequiredService<ChatDb>();
            ((SqliteConnection)db.Database.GetDbConnection()).CreateFunction("hold_commit",()=>{entered.Set();release.Wait(TimeSpan.FromSeconds(10));return 1L;});
            await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER hold_delete_audit BEFORE INSERT ON moderation_audit BEGIN SELECT hold_commit(); END;");
        }
        using var c=Auth(f,Author);var delete=c.DeleteAsync(PathTo());
        try
        {
            Assert.That(entered.Wait(TimeSpan.FromSeconds(10)),Is.True,"the commit never started");
            var publish=Task.Run(()=>hub.Publish("presence.updated",JsonSerializer.SerializeToElement(new{status="online"}),[Member]));
            var register=Task.Run(()=>hub.Register(new Sink(Member,"late-member")));
            Assert.That(Task.WaitAll([publish,register],TimeSpan.FromSeconds(3)),Is.True,"publish or register waited for the database commit");
        }
        finally { release.Set(); }
        using var response=await delete.WaitAsync(TimeSpan.FromSeconds(10));Assert.That(response.StatusCode,Is.EqualTo(HttpStatusCode.NoContent));
    }
    private static string SendPath(Guid conversation)=>$"/conversations/{conversation}/messages";
    private static StringContent SendBody()=>new("{\"body\":\"hello\",\"kind\":\"message\"}",System.Text.Encoding.UTF8,"application/json");
    private static async Task Until(Func<bool> condition,string what)
    {
        for(int i=0;i<200&&!condition();i++)await Task.Delay(25);
        Assert.That(condition(),Is.True,what);
    }
    private static async Task SeedHost(IServiceProvider services,bool erased)
    {
        await using var scope=services.CreateAsyncScope();var db=scope.ServiceProvider.GetRequiredService<ChatDb>();
        foreach(var id in new[]{Author,Member,Outsider})db.Users.Add(new ExternalUserEntity{Id=id,Provider="github",ProviderUserId=id.ToString(),DisplayName="fixture",CreatedAt=Created,UpdatedAt=Created});
        db.Conversations.Add(new ConversationEntity{Id=Conversation,Kind="group",CreatedAt=Created,Participants={new ParticipantEntity{UserId=Author,JoinedAt=Created},new ParticipantEntity{UserId=Member,JoinedAt=Created}}});
        db.Messages.Add(new MessageEntity{Id=Message,ConversationId=Conversation,AuthorId=Author,Body="private-body",Kind="message",RefPayloadJson="{\"url\":\"private-ref\"}",CreatedAt=Created});
        if(erased)db.Messages.Add(new MessageEntity{Id=Erased,ConversationId=Conversation,AuthorId=Author,Body="",Kind="message",CreatedAt=Created,DeletedAt=Created.AddMinutes(2)});
        await db.SaveChangesAsync();
    }
    private sealed class LogCapture:ILoggerProvider,ILogger
    {
        internal readonly List<string> Errors=[];
        public ILogger CreateLogger(string categoryName)=>this;
        public IDisposable? BeginScope<TState>(TState state) where TState:notnull=>null;
        public bool IsEnabled(LogLevel level)=>true;
        public void Log<TState>(LogLevel level,EventId id,TState state,Exception? exception,Func<TState,Exception?,string> formatter)
        {if(level>=LogLevel.Error)lock(Errors)Errors.Add(formatter(state,exception)+(exception?.ToString()??""));}
        public void Dispose(){}
    }
    private sealed class Sink(Guid user,string connection):IGatewaySink
    {
        public Guid UserId=>user;public string ConnectionId=>connection;public CancellationToken Disconnected=>CancellationToken.None;
        internal List<GatewayEnvelope> Events=[];public bool TryEnqueue(GatewayEnvelope e){Events.Add(e);return true;}public void Abort(GatewayAbortReason reason){}public void Complete(){}
    }
}
