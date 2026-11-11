using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using Aerochat.Connectivity.Rtc;
using SIPSorcery.Net;

namespace Aerochat.VisualShell.Tests;

public sealed class TurnClientTests
{
    private const string Good = "{\"iceServers\":[{\"urls\":[\"turn:relay.invalid:3478\"],\"username\":\"fixture-user\",\"credential\":\"fixture-credential\"}],\"ttl\":60}";
    private sealed class Handler : HttpMessageHandler
    {
        internal int Count;
        internal Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Reply = (r, c) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Good) });
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken c)
        { Count++; Assert.That(r.RequestUri!.AbsolutePath, Is.EqualTo("/turn/credentials"));
          Assert.That(r.Headers.Authorization?.ToString(), Is.EqualTo("Bearer fixture-token")); return Reply(r,c); }
    }
    private sealed class Clock : TimeProvider
    {
        internal long Seconds;
        internal Timer? Pending;
        public override long TimestampFrequency => 1;
        public override long GetTimestamp() => Seconds;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            Pending = new Timer(callback, state);
        internal void Advance(int seconds)
        {
            Seconds += seconds;
            if (Pending is { Due: var due } && due != Timeout.InfiniteTimeSpan && seconds >= due.TotalSeconds)
            { Pending.Due = Timeout.InfiniteTimeSpan; Pending.Callback(Pending.State); }
        }
        internal sealed class Timer(TimerCallback callback, object? state) : ITimer
        {
            internal TimerCallback Callback = callback; internal object? State = state;
            internal TimeSpan Due = Timeout.InfiniteTimeSpan;
            public bool Change(TimeSpan dueTime, TimeSpan period) { Due=dueTime; return true; }
            public void Dispose() => Due=Timeout.InfiniteTimeSpan;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
    private static TurnCredentialProvider Create(Handler h, Clock? clock=null) => new(new HttpClient(h),
        new Uri("https://chat.invalid/"), _ => Task.FromResult<string?>("fixture-token"), clock);

    [Test]
    public async Task Cache_refreshes_at_eighty_percent_and_last_lease_stops_polling()
    {
        var h=new Handler(); var clock=new Clock(); using var p=Create(h,clock);
        var a=await p.AcquireAsync(); using(var b=await p.AcquireAsync()) Assert.That(h.Count, Is.EqualTo(1));
        Assert.That(clock.Pending!.Due, Is.EqualTo(TimeSpan.FromSeconds(48)));
        clock.Advance(48); await p.RefreshTask;
        Assert.That(h.Count, Is.EqualTo(2)); a.Dispose(); clock.Advance(60);
        Assert.That(h.Count, Is.EqualTo(2));
    }
    [Test]
    public async Task Engine_receives_initial_ICE_and_hangup_releases_lease()
    {
        var h=new Handler(); var clock=new Clock(); using var p=Create(h,clock);
        await using var engine=new RtcPeerEngine(false); engine.UseTurn(p); await engine.InitializeAsync();
        var peer=(RTCPeerConnection)typeof(RtcPeerEngine).GetField("_peer",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(engine)!;
        var ice=peer.getConfiguration().iceServers;
        Assert.That(ice.Single().credential, Is.EqualTo("fixture-credential"));
        Assert.That(ice.Single().urls, Is.EqualTo("turn:relay.invalid:3478"));
        await engine.Hangup(); Assert.That(clock.Pending!.Due, Is.EqualTo(Timeout.InfiniteTimeSpan));
    }
    [TestCase("{\"iceServers\":[],\"ttl\":0}", true)]
    [TestCase("{\"iceServers\":[],\"ttl\":60}", false)]
    [TestCase("{\"iceServers\":null,\"ttl\":60}", false)]
    [TestCase("{}", false)]
    [TestCase("not json", false)]
    [TestCase("{\"iceServers\":[{\"urls\":[\"https://evil.invalid\"],\"username\":\"u\",\"credential\":\"c\"}],\"ttl\":60}", false)]
    public async Task Response_shapes_are_checked(string body,bool valid)
    {
        var h=new Handler { Reply=(r,c)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(body)}) };
        using var p=Create(h);
        if(valid) { using var lease=await p.AcquireAsync(); Assert.That(lease.Value.IceServers,Is.Empty); }
        else Assert.That((await Catch(p)).Message,Is.EqualTo("TURN credentials are unavailable."));
    }
    private static async Task<Exception> Catch(TurnCredentialProvider p)
    { try { using var lease=await p.AcquireAsync(); Assert.Fail("Expected failure"); } catch(Exception e) { return e; } throw new Exception(); }
    [Test]
    public async Task Oversize_is_rejected_and_non_success_body_is_not_parsed()
    {
        var h=new Handler{Reply=(r,c)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(new string(' ',32769)+Good)})};
        using var p=Create(h); Assert.That((await Catch(p)).Message,Is.EqualTo("TURN credentials are unavailable."));
        h.Reply=(r,c)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized){Content=new StringContent("private-error")});
        Assert.That((await Catch(p)).ToString(),Does.Not.Contain("private-error"));
    }
    [Test]
    public async Task Concurrent_acquisition_uses_one_fetch_and_late_disposal_cannot_install()
    {
        var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish=new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var h=new Handler{Reply=(r,c)=>{started.TrySetResult();return finish.Task;}};
        using var p=Create(h);var first=p.AcquireAsync();await started.Task;var second=p.AcquireAsync();
        finish.SetResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(Good)});
        using var a=await first;using var b=await second;Assert.That(h.Count,Is.EqualTo(1));
    }
    [Test]
    public async Task Rate_limit_does_not_extend_old_credentials_or_retry_early()
    {
        var h=new Handler();var clock=new Clock();using var p=Create(h,clock);using var a=await p.AcquireAsync();
        h.Reply=(r,c)=>{var v=new HttpResponseMessage(HttpStatusCode.TooManyRequests);v.Headers.RetryAfter=new(TimeSpan.FromSeconds(120));return Task.FromResult(v);};
        clock.Advance(48);await p.RefreshTask;clock.Seconds=61;
        Assert.That((await Catch(p)).Message,Is.EqualTo("TURN credentials are unavailable."));Assert.That(h.Count,Is.EqualTo(2));
        Assert.That(clock.Pending!.Due,Is.EqualTo(TimeSpan.FromSeconds(120)));
    }
    [Test]
    public async Task Disposed_provider_rejects_late_response()
    {
        var finish=new TaskCompletionSource<HttpResponseMessage>();var h=new Handler{Reply=(r,c)=>finish.Task};
        var p=Create(h);var a=p.AcquireAsync();p.Dispose();finish.SetResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(Good)});
        Assert.That((await CatchTask(a)).Message,Is.EqualTo("TURN credentials are unavailable."));
    }
    private static async Task<Exception> CatchTask(Task<TurnCredentialProvider.Lease> task)
    {try { using var l=await task;return new Exception("unexpected success");}catch(Exception e){return e;}}
    [Test]
    public async Task Identity_change_during_fetch_and_caller_cancellation_reject_results()
    {
        string? token="fixture-token";var finish=new TaskCompletionSource<HttpResponseMessage>();
        var h=new Handler{Reply=(r,c)=>finish.Task};using var p=new TurnCredentialProvider(new HttpClient(h),
            new Uri("https://chat.invalid/"),_=>Task.FromResult(token));
        var pending=p.AcquireAsync();token=null;finish.SetResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(Good)});
        Assert.That((await CatchTask(pending)).Message,Is.EqualTo("TURN credentials are unavailable."));
        using var ct=new CancellationTokenSource();ct.Cancel();
        Assert.That(await CatchTask(p.AcquireAsync(ct.Token)),Is.InstanceOf<OperationCanceledException>());
    }
}
