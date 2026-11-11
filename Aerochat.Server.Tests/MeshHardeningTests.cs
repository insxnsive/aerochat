using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Aerochat.Server.Auth;
using Aerochat.Server.Calls;
using Aerochat.Server.Gateway;
using Aerochat.Server.Hardening;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aerochat.Server.Tests;

public sealed class MeshHardeningTests
{
    private sealed class Clock:TimeProvider
    {internal DateTimeOffset Now=DateTimeOffset.UtcNow.AddMinutes(1);public override DateTimeOffset GetUtcNow()=>Now;}
    [Test]
    public void Signal_budget_is_six_hundred_per_user_and_resets_at_sixty_seconds()
    {
        var clock=new Clock();var quota=new MeshSignalRateLimiter(clock);
        for(int i=0;i<600;i++)Assert.That(quota.TryAcquire("a").Allowed,Is.True);
        var denied=quota.TryAcquire("a");Assert.That(denied.Allowed,Is.False);Assert.That(denied.RetryAfter,Is.EqualTo(TimeSpan.FromSeconds(60)));
        Assert.That(quota.TryAcquire("b").Allowed,Is.True);clock.Now=clock.Now.AddSeconds(60);Assert.That(quota.TryAcquire("a").Allowed,Is.True);
    }
    [TestCase("GET","mesh")]
    [TestCase("POST","mesh/join")]
    [TestCase("POST","mesh/leave")]
    [TestCase("GET","MESH/")]
    [TestCase("POST","MESH/JOIN/")]
    public async Task Mesh_control_routes_use_the_normal_budget(string method,string suffix)
    {
        var clock=new Clock();using var factory=new ApiWebApplicationFactory{Clock=clock,RateLimit=1,RateLimitWindowSeconds=60};
        var id=Guid.NewGuid();await Seed(factory,id);using var client=Auth(factory);string path=$"/conversations/{Guid.NewGuid()}/{suffix}";
        async Task<HttpResponseMessage> Send()=>method=="GET"?await client.GetAsync(path):await client.PostAsJsonAsync(path,new{version=1});
        using var first=await Send();Assert.That(first.StatusCode,Is.EqualTo(HttpStatusCode.NotFound));
        using var denied=await Send();Assert.That(denied.StatusCode,Is.EqualTo(HttpStatusCode.TooManyRequests));
        Assert.That(await denied.Content.ReadAsStringAsync(),Is.EqualTo("{\"error\":\"rate_limited\"}"));Assert.That(denied.Headers.RetryAfter?.Delta,Is.EqualTo(TimeSpan.FromSeconds(60)));
        clock.Now=clock.Now.AddSeconds(60);using var reset=await Send();Assert.That(reset.StatusCode,Is.EqualTo(HttpStatusCode.NotFound));
    }
    private static Task Seed(ApiWebApplicationFactory f,Guid id)=>f.SeedAsync(db=>
    {db.Users.Add(new(){Id=id,Provider="github",ProviderUserId="mesh-hardening",DisplayName="fixture",CreatedAt=DateTimeOffset.UtcNow,UpdatedAt=DateTimeOffset.UtcNow});return Task.CompletedTask;});
    private static HttpClient Auth(ApiWebApplicationFactory f)
    {var c=f.CreateClient();c.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",f.Services.GetRequiredService<SessionService>().Issue(new Identity("github","mesh-hardening","fixture")));return c;}
    [Test]
    public async Task Signal_has_its_own_budget_and_near_misses_do_not_consume_it()
    {
        var clock=new Clock();using var f=new ApiWebApplicationFactory{Clock=clock,RateLimit=1,RateLimitWindowSeconds=60};var id=Guid.NewGuid();await Seed(f,id);using var c=Auth(f);
        var quota=f.Services.GetService<MeshSignalRateLimiter>();Assert.That(quota,Is.Not.Null);
        string prefix=$"/conversations/{Guid.NewGuid()}/mesh";
        using(var miss=await c.GetAsync(prefix+"/signal"))Assert.That(miss.StatusCode,Is.EqualTo(HttpStatusCode.MethodNotAllowed));
        using(var control=await c.GetAsync(prefix))Assert.That(control.StatusCode,Is.EqualTo(HttpStatusCode.NotFound));
        using(var control=await c.GetAsync(prefix))Assert.That(control.StatusCode,Is.EqualTo(HttpStatusCode.TooManyRequests));
        for(int i=0;i<599;i++)Assert.That(quota!.TryAcquire(id.ToString("N")).Allowed,Is.True);
        using(var last=await c.PostAsJsonAsync(prefix+"/signal",new{version=1}))Assert.That(last.StatusCode,Is.EqualTo(HttpStatusCode.NotFound));
        using(var limited=await c.PostAsJsonAsync($"/conversations/{Guid.NewGuid()}/MESH/SIGNAL/",new{version=1}))
        {Assert.That(limited.StatusCode,Is.EqualTo(HttpStatusCode.TooManyRequests));Assert.That(limited.Headers.RetryAfter?.Delta,Is.EqualTo(TimeSpan.FromSeconds(60)));}
        clock.Now=clock.Now.AddSeconds(60);using var reset=await c.PostAsJsonAsync(prefix+"/signal",new{version=1});Assert.That(reset.StatusCode,Is.EqualTo(HttpStatusCode.NotFound));
    }
    [Test]
    public async Task ICE_count_is_atomic_per_edge_across_both_senders_and_new_edges_reset()
    {
        var rooms=new MeshRooms();var a=Guid.Parse("00000000-0000-0000-0000-000000000001");var b=Guid.Parse("00000000-0000-0000-0000-000000000002");var conversation=Guid.NewGuid();rooms.SetPresence(a,true);rooms.SetPresence(b,true);
        var room=rooms.Join(conversation,a,null).Snapshot.RoomId!.Value;var edge=rooms.Join(conversation,b,room).Snapshot.Edges.Single();
        Assert.That(rooms.Signal(conversation,a,b,room,edge.Id,"ice"),Is.EqualTo("mesh_invalid_state"));
        Assert.That(rooms.Signal(conversation,a,b,room,edge.Id,"offer"),Is.Null);Assert.That(rooms.Signal(conversation,b,a,room,edge.Id,"answer"),Is.Null);
        var results=await Task.WhenAll(Enumerable.Range(0,160).Select(i=>Task.Run(()=>i%2==0?rooms.Signal(conversation,a,b,room,edge.Id,"ice"):rooms.Signal(conversation,b,a,room,edge.Id,"ice"))));
        Assert.That(results.Count(r=>r is null),Is.EqualTo(128));Assert.That(results.Count(r=>r=="mesh_ice_limit"),Is.EqualTo(32));
        rooms.Leave(conversation,b,room);var next=rooms.Join(conversation,b,room).Snapshot.Edges.Single();
        Assert.That(rooms.Signal(conversation,a,b,room,edge.Id,"ice"),Is.EqualTo("mesh_stale_edge"));Assert.That(rooms.Signal(conversation,a,b,room,next.Id,"offer"),Is.Null);Assert.That(rooms.Signal(conversation,a,b,room,next.Id,"ice"),Is.Null);
    }
    private sealed class Sink(Guid user,string connection):IGatewaySink
    {
        public Guid UserId=>user;public string ConnectionId=>connection;public CancellationToken Disconnected=>CancellationToken.None;
        internal List<GatewayEnvelope> Events=[];public bool TryEnqueue(GatewayEnvelope envelope){Events.Add(envelope);return true;}public void Abort(GatewayAbortReason reason){}public void Complete(){}
    }
    [Test]
    public void Signals_deliver_live_but_do_not_enter_replay_or_displace_history()
    {
        var user=Guid.NewGuid();var other=Guid.NewGuid();var hub=new GatewayHub(new GatewayOptions{InstanceId="mesh-test",ReplayCapacity=2});var live=new Sink(user,"live");var outsider=new Sink(other,"other");hub.Register(live);hub.Register(outsider);live.Events.Clear();outsider.Events.Clear();
        var first=hub.Publish("message.created",JsonSerializer.SerializeToElement(new{body="before"}),[user]);
        for(int i=0;i<10;i++)hub.Publish("mesh.signal",JsonSerializer.SerializeToElement(new{sdp="fixture-sdp"}),[user]);
        var last=hub.Publish("message.created",JsonSerializer.SerializeToElement(new{body="after"}),[user]);
        Assert.That(hub.RetainedEventCount,Is.EqualTo(2));Assert.That(live.Events.Count,Is.EqualTo(12));Assert.That(outsider.Events,Is.Empty);
        var replay=new Sink(user,"reconnected");var result=hub.Register(replay,first.EventId);
        Assert.That(result.Status,Is.EqualTo(GatewayReplayStatus.Replayed));Assert.That(result.ReplayedEvents.Select(e=>e.Type),Is.EqualTo(new[]{"message.created"}));Assert.That(result.ReplayedEvents.Single().EventId,Is.EqualTo(last.EventId));
    }
    private sealed class Logger:ILogger
    {
        internal readonly List<string> Messages=[];internal Exception? Exception;
        public IDisposable? BeginScope<TState>(TState state) where TState:notnull=>null;public bool IsEnabled(LogLevel level)=>true;
        public void Log<TState>(LogLevel level,EventId id,TState state,Exception? exception,Func<TState,Exception?,string> formatter){Exception=exception;Messages.Add(formatter(state,exception));}
    }
    [Test]
    public void Notifier_failure_is_contained_type_only_and_next_tick_runs()
    {
        var logger=new Logger();int calls=0;
        Assert.DoesNotThrow(()=>MeshRosterNotifier.RunTick(()=>{calls++;throw new InvalidOperationException("private-sdp-and-token");},logger));
        MeshRosterNotifier.RunTick(()=>calls++,logger);Assert.That(calls,Is.EqualTo(2));Assert.That(logger.Messages,Has.Count.EqualTo(1));
        Assert.That(logger.Messages[0],Does.Contain("InvalidOperationException").And.Not.Contain("private-sdp-and-token"));Assert.That(logger.Exception,Is.Null);
    }
}
