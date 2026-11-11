using System.Net;
using System.Reflection;
using Aerochat.Connectivity.Rtc;

namespace Aerochat.VisualShell.Tests;

public sealed class MeshAudioHardeningTests
{
    private sealed class Capture:IMeshCapture
    {
        internal int Starts,Closes;internal volatile bool Open;internal Task? StartWait,CloseWait;public event Action<uint,byte[]>? Sample;
        internal readonly TaskCompletionSource ClosedAfterStart=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task StartAsync(){Starts++;if(StartWait is not null)await StartWait;Open=true;}
        public Task CloseAsync(){Closes++;bool wasOpen=Open;Open=false;if(wasOpen)ClosedAfterStart.TrySetResult();return CloseWait??Task.CompletedTask;}
        internal void Emit()=>Sample?.Invoke(160,new byte[160]);
    }
    private sealed class Playback:IMeshPlayback
    {
        internal int Closes;internal Task? StartWait,CloseWait;
        public Task StartAsync()=>StartWait??Task.CompletedTask;
        public Task CloseAsync(){Closes++;return CloseWait??Task.CompletedTask;}
        public void Receive(RtcAudioPacket packet)=>throw new InvalidOperationException("close-race fixture");
    }
    private static IRtcAudioEndpoint Audio(RtcPeerEngine peer)=>(IRtcAudioEndpoint)typeof(RtcPeerEngine).GetField("_audio",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(peer)!;
    [Test]
    public async Task Initial_mute_and_owner_unmute_survive_peer_replacement_and_capture_reopens_lazily()
    {
        var captures=new List<Capture>();await using var owner=new MeshAudioSession(()=>{var capture=new Capture();captures.Add(capture);return capture;},()=>new Playback());
        var peer=await owner.AddPeerAsync();var audio=Audio(peer);int packets=0;audio.EncodedSample+=(_,_)=>packets++;
        await audio.StartAsync();captures[0].Emit();Assert.That(packets,Is.Zero);
        owner.SetMuted(false);captures[0].Emit();Assert.That(packets,Is.EqualTo(1));owner.SetMuted(true);
        await owner.RemovePeerAsync(peer);Assert.That(captures[0].Closes,Is.EqualTo(1));
        var replacement=await owner.AddPeerAsync();var next=Audio(replacement);next.EncodedSample+=(_,_)=>packets++;
        Assert.That(captures,Has.Count.EqualTo(1));await next.StartAsync();Assert.That(captures,Has.Count.EqualTo(2));captures[1].Emit();Assert.That(packets,Is.EqualTo(1));
        owner.SetMuted(false);captures[1].Emit();Assert.That(packets,Is.EqualTo(2));
    }
    [TestCase("capture-start")][TestCase("playback-start")][TestCase("playback-close")][TestCase("capture-close")]
    public async Task Hung_device_operations_are_bounded_fail_closed_and_late_start_is_cleaned(string mode)
    {
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var capture=new Capture();var playback=new Playback();
        if(mode=="capture-start")capture.StartWait=release.Task;if(mode=="playback-start")playback.StartWait=release.Task;
        var owner=new MeshAudioSession(()=>capture,()=>playback,deviceTimeout:TimeSpan.FromMilliseconds(100));
        var peer=await owner.AddPeerAsync();var audio=Audio(peer);int packets=0;audio.EncodedSample+=(_,_)=>packets++;
        try
        {
            if(mode.EndsWith("start"))
                Assert.ThrowsAsync<TimeoutException>(async()=>await audio.StartAsync().WaitAsync(TimeSpan.FromSeconds(3)));
            else
            {
                await audio.StartAsync();if(mode=="playback-close")playback.CloseWait=release.Task;else capture.CloseWait=release.Task;
                Assert.ThrowsAsync<TimeoutException>(async()=>await owner.RemovePeerAsync(peer).WaitAsync(TimeSpan.FromSeconds(3)));
            }
            owner.SetMuted(false);capture.Emit();Assert.That(packets,Is.Zero);
            Assert.ThrowsAsync<ObjectDisposedException>(async()=>await owner.AddPeerAsync());
        }
        finally
        {
            release.TrySetResult();
            try{await owner.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));}catch(TimeoutException){ }
        }
        Assert.That(capture.Closes,Is.GreaterThanOrEqualTo(1));Assert.That(playback.Closes,Is.GreaterThanOrEqualTo(1));
    }
    [Test]
    public async Task Timed_out_capture_that_opens_late_is_closed_again()
    {
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var capture=new Capture{StartWait=release.Task};
        var owner=new MeshAudioSession(()=>capture,()=>new Playback(),deviceTimeout:TimeSpan.FromMilliseconds(100));var peer=await owner.AddPeerAsync();
        try
        {
            Assert.ThrowsAsync<TimeoutException>(async()=>await Audio(peer).StartAsync().WaitAsync(TimeSpan.FromSeconds(3)));
            release.TrySetResult();await capture.ClosedAfterStart.Task.WaitAsync(TimeSpan.FromSeconds(3));Assert.That(capture.Open,Is.False);
        }
        finally {release.TrySetResult();try{await owner.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));}catch(TimeoutException){ }}
    }
    [Test]
    public async Task Playback_close_race_is_ignored_on_receive_thread()
    {
        await using var owner=new MeshAudioSession(()=>new Capture(),()=>new Playback());var peer=await owner.AddPeerAsync();var audio=Audio(peer);await audio.StartAsync();
        Assert.DoesNotThrow(()=>audio.Receive(new RtcAudioPacket(new IPEndPoint(IPAddress.Loopback,5000),1,1,160,0,false,new byte[160])));
    }
}
