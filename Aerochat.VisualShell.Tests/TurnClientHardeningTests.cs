using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Aerochat.Connectivity.Rtc;

namespace Aerochat.VisualShell.Tests;

public sealed class TurnClientHardeningTests
{
    private const string Good = "{\"iceServers\":[{\"urls\":[\"turn:relay.invalid:3478\"],\"username\":\"user-secret\",\"credential\":\"credential-secret\"}],\"ttl\":60}";
    private sealed class Clock : TimeProvider
    {
        internal long Seconds;
        internal Timer? Pending;
        public override long TimestampFrequency => 1;
        public override long GetTimestamp() => Seconds;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddSeconds(Seconds);
        public override ITimer CreateTimer(TimerCallback cb, object? state, TimeSpan due, TimeSpan period) => Pending = new Timer(cb,state);
        internal void Fire()
        {
            var timer = Pending!;
            Seconds += (long)timer.Due.TotalSeconds;
            timer.Due = Timeout.InfiniteTimeSpan; timer.Callback(timer.State);
        }
        internal sealed class Timer(TimerCallback cb, object? state) : ITimer
        {
            internal readonly TimerCallback Callback=cb; internal readonly object? State=state;
            internal TimeSpan Due=Timeout.InfiniteTimeSpan;
            public bool Change(TimeSpan due, TimeSpan period)
            {
                if (due.TotalMilliseconds > uint.MaxValue-1) throw new ArgumentOutOfRangeException(nameof(due));
                Due=due; return true;
            }
            public void Dispose() => Due=Timeout.InfiniteTimeSpan;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
    private sealed class Handler : HttpMessageHandler
    {
        internal Func<HttpResponseMessage> Reply=()=>Ok();
        internal int Count;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        { Count++; return Task.FromResult(Reply()); }
    }
    private static HttpResponseMessage Ok(string body=Good) => new(HttpStatusCode.OK){Content=new StringContent(body)};
    private static TurnCredentialProvider Create(Handler h,Clock clock,Func<CancellationToken,Task<string?>>? token=null) =>
        new(new HttpClient(h),new Uri("https://chat.invalid/"),token??(_=>Task.FromResult<string?>("token-a")),clock);
    [TestCase(false)]
    [TestCase(true)]
    public async Task Huge_retry_after_is_capped_without_faulting_timer(bool date)
    {
        var h=new Handler();var clock=new Clock();using var p=Create(h,clock);using var lease=await p.AcquireAsync();
        h.Reply=()=>{var r=new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            r.Headers.RetryAfter=date?new RetryConditionHeaderValue(DateTimeOffset.MaxValue):new RetryConditionHeaderValue(TimeSpan.FromSeconds(int.MaxValue));return r;};
        clock.Fire(); await p.RefreshTask;
        Assert.That(clock.Pending!.Due,Is.EqualTo(TimeSpan.FromSeconds(300)));
        h.Reply=()=>Ok();clock.Fire();await p.RefreshTask;
        Assert.That(h.Count,Is.EqualTo(3));Assert.That(clock.Pending.Due,Is.EqualTo(TimeSpan.FromSeconds(48)));
    }
    [TestCase(-5)]
    [TestCase(0)]
    [TestCase(1)]
    public async Task Short_retry_after_has_a_safe_floor(int seconds)
    {
        var h=new Handler();var clock=new Clock();using var p=Create(h,clock);using var lease=await p.AcquireAsync();
        h.Reply=()=>{var r=new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            r.Headers.TryAddWithoutValidation("Retry-After",seconds.ToString(System.Globalization.CultureInfo.InvariantCulture));return r;};
        clock.Fire();await p.RefreshTask;
        Assert.That(clock.Pending!.Due,Is.EqualTo(TimeSpan.FromSeconds(5)));
        h.Reply=()=>Ok();clock.Fire();await p.RefreshTask;Assert.That(h.Count,Is.EqualTo(3));
    }
    [Test]
    public async Task Generation_change_clears_retry_and_backoff()
    {
        string? token="token-a";var clock=new Clock();var h=new Handler();using var p=Create(h,clock,_=>Task.FromResult<string?>(token));
        using var a=await p.AcquireAsync();
        h.Reply=()=>{var r=new HttpResponseMessage(HttpStatusCode.TooManyRequests);r.Headers.RetryAfter=new(TimeSpan.FromSeconds(300));return r;};
        clock.Fire();await p.RefreshTask;
        token="token-b";h.Reply=()=>Ok();using var b=await p.AcquireAsync();Assert.That(h.Count,Is.EqualTo(3));
        h.Reply=()=>new(HttpStatusCode.ServiceUnavailable);clock.Fire();await p.RefreshTask;
        Assert.That(clock.Pending!.Due,Is.EqualTo(TimeSpan.FromSeconds(5)));
    }
    [Test]
    public async Task Failure_backoff_grows_caps_and_resets_on_success_and_last_release()
    {
        var h=new Handler();var clock=new Clock();using var p=Create(h,clock);var lease=await p.AcquireAsync();
        h.Reply=()=>new(HttpStatusCode.ServiceUnavailable);
        foreach(int expected in new[]{5,10,20,40,80,160,300,300})
        {clock.Fire();await p.RefreshTask;Assert.That(clock.Pending!.Due,Is.EqualTo(TimeSpan.FromSeconds(expected)));}
        h.Reply=()=>Ok();clock.Fire();await p.RefreshTask;
        h.Reply=()=>new(HttpStatusCode.ServiceUnavailable);clock.Fire();await p.RefreshTask;
        Assert.That(clock.Pending!.Due,Is.EqualTo(TimeSpan.FromSeconds(5)));
        lease.Dispose();h.Reply=()=>Ok();using var next=await p.AcquireAsync();
        Assert.That(clock.Pending.Due,Is.EqualTo(TimeSpan.FromSeconds(48)));
    }
    [Test]
    public void Record_formatting_redacts_credentials_and_username()
    {
        var server=new TurnIceServer(["turn:relay.invalid"],"user-secret","credential-secret");
        var response=new TurnResponse([server],60);
        foreach(string text in new[]{server.ToString(),$"{server}",response.ToString()})
        {Assert.That(text,Does.Not.Contain("user-secret"));Assert.That(text,Does.Not.Contain("credential-secret"));}
    }
    [TestCase("turn:relay.invalid",true)]
    [TestCase("TURNS:relay.invalid:65535?transport=tcp",true)]
    [TestCase("turn:[::1]:3478?transport=udp",true)]
    [TestCase("turn:127.0.0.1:1",true)]
    [TestCase("turn:",false)]
    [TestCase("turn://relay.invalid",false)]
    [TestCase("turn:user@relay.invalid",false)]
    [TestCase("turn:relay.invalid/path",false)]
    [TestCase("turn:relay.invalid#fragment",false)]
    [TestCase("turn:relay.invalid:0",false)]
    [TestCase("turn:relay.invalid:65536",false)]
    [TestCase("turn:relay.invalid?transport=ws",false)]
    [TestCase("turn:[::::]",false)]
    [TestCase("turn:bad_host",false)]
    [TestCase("turn:relay.invalid\n",false)]
    [TestCase("overlong",false)]
    public async Task Urls_follow_server_grammar(string url,bool valid)
    {
        if(url=="overlong") url="turn:"+new string('a',2048)+".invalid";
        var body=JsonSerializer.Serialize(new{iceServers=new[]{new{urls=new[]{url},username="user",credential="credential"}},ttl=60});
        var h=new Handler{Reply=()=>Ok(body)};using var p=Create(h,new Clock());
        if(valid){using var lease=await p.AcquireAsync();Assert.That(lease.Value.IceServers[0].Urls[0],Is.EqualTo(url));}
        else Assert.That(Assert.ThrowsAsync<InvalidOperationException>(async()=>{using var lease=await p.AcquireAsync();})!.Message,Is.EqualTo("TURN credentials are unavailable."));
    }
}
