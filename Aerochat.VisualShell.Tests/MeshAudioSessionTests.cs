using System.Net;
using System.Reflection;
using Aerochat.Connectivity.Rtc;
using SIPSorceryMedia.Abstractions;

namespace Aerochat.VisualShell.Tests;

public sealed class MeshAudioSessionTests
{
    private sealed class Capture:IMeshCapture
    {
        internal int Starts,Closes;public event Action<uint,byte[]>? Sample;
        public Task StartAsync(){Starts++;return Task.CompletedTask;}
        public Task CloseAsync(){Closes++;return Task.CompletedTask;}
        internal void Emit()=>Sample?.Invoke(160,new byte[160]);
    }
    private sealed class Playback(bool failClose=false):IMeshPlayback
    {
        internal int Starts,Closes;internal readonly List<uint> Timestamps=[];
        internal TaskCompletionSource? ReleaseClose;internal readonly TaskCompletionSource CloseStarted=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task StartAsync(){Starts++;return Task.CompletedTask;}
        public Task CloseAsync(){Closes++;CloseStarted.TrySetResult();return failClose?Task.FromException(new InvalidOperationException("fixture")):ReleaseClose?.Task??Task.CompletedTask;}
        public void Receive(RtcAudioPacket packet)=>Timestamps.Add(packet.Timestamp);
    }
    private static IRtcAudioEndpoint Audio(RtcPeerEngine peer)=>(IRtcAudioEndpoint)typeof(RtcPeerEngine).GetField("_audio",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(peer)!;
    private static RtcAudioPacket Packet(uint timestamp)=>new(new IPEndPoint(IPAddress.Loopback,5000),1,1,timestamp,0,false,new byte[160]);
    [Test]
    public async Task Three_real_peer_connections_share_one_capture_and_keep_playback_separate()
    {
        int captures=0;var capture=new Capture();var sinks=new List<Playback>();
        await using var owner=new MeshAudioSession(()=>{captures++;return capture;},()=>{var sink=new Playback();sinks.Add(sink);return sink;},initialMuted:false);
        var peers=new List<RtcPeerEngine>();var remotes=new List<RtcPeerEngine>();
        try
        {
            for(int i=0;i<3;i++)
            {
                var peer=await owner.AddPeerAsync();peers.Add(peer);var remote=new RtcPeerEngine(()=>null,()=>null);remotes.Add(remote);
                string offer=await peer.StartCall();string answer=await remote.AcceptOffer(offer);await peer.ApplyAnswer(answer);
                Assert.That(offer,Does.Contain("PCMU/8000"));Assert.That(Audio(peer).SourceFormats,Has.Count.EqualTo(1));
            }
            Assert.That(peers.Select(p=>p.CurrentPeer).Distinct().Count(),Is.EqualTo(3));Assert.That(captures,Is.EqualTo(1));Assert.That(capture.Starts,Is.EqualTo(1));Assert.That(sinks.Select(s=>s.Starts),Is.EqualTo(new[]{1,1,1}));
            var deliveries=new int[3];for(int i=0;i<3;i++){int slot=i;Audio(peers[i]).EncodedSample+=(_,_)=>deliveries[slot]++;}
            capture.Emit();Assert.That(deliveries,Is.EqualTo(new[]{1,1,1}));
            Audio(peers[0]).Receive(Packet(100));Audio(peers[1]).Receive(Packet(200));Audio(peers[2]).Receive(Packet(300));
            Assert.That(sinks.Select(s=>s.Timestamps.Single()),Is.EqualTo(new uint[]{100,200,300}));
            peers[0].Mute();capture.Emit();Assert.That(deliveries,Is.EqualTo(new[]{1,1,1}));peers[0].Unmute();
            await owner.RemovePeerAsync(peers[0]);capture.Emit();Assert.That(deliveries,Is.EqualTo(new[]{1,2,2}));Assert.That(sinks[0].Closes,Is.EqualTo(1));Assert.That(capture.Closes,Is.Zero);
            await owner.DisposeAsync();capture.Emit();Assert.That(deliveries,Is.EqualTo(new[]{1,2,2}));Assert.That(capture.Closes,Is.EqualTo(1));Assert.That(sinks.Select(s=>s.Closes),Is.EqualTo(new[]{1,1,1}));
        }
        finally {foreach(var remote in remotes)await remote.DisposeAsync();}
    }
    [Test]
    public async Task Cancelled_admission_creates_no_peer_or_devices_and_capacity_can_be_reused()
    {
        int captures=0,sinks=0;await using var owner=new MeshAudioSession(()=>{captures++;return new Capture();},()=>{sinks++;return new Playback();});
        using var stop=new CancellationTokenSource();stop.Cancel();Assert.ThrowsAsync<OperationCanceledException>(async()=>await owner.AddPeerAsync(stop.Token));Assert.That(sinks,Is.Zero);Assert.That(captures,Is.Zero);
        var first=await owner.AddPeerAsync();var second=await owner.AddPeerAsync();var third=await owner.AddPeerAsync();
        Assert.ThrowsAsync<InvalidOperationException>(async()=>await owner.AddPeerAsync());await owner.RemovePeerAsync(second);var replacement=await owner.AddPeerAsync();Assert.That(replacement,Is.Not.SameAs(second));Assert.That(sinks,Is.EqualTo(4));Assert.That(captures,Is.Zero);
    }
    [Test]
    public async Task Dispose_attempts_every_sink_and_capture_even_if_one_sink_fails()
    {
        var capture=new Capture();var sinks=new List<Playback>();var owner=new MeshAudioSession(()=>capture,()=>{var sink=new Playback(sinks.Count==0);sinks.Add(sink);return sink;});
        var first=await owner.AddPeerAsync();await Audio(first).StartAsync();await owner.AddPeerAsync();
        var one=owner.DisposeAsync().AsTask();var two=owner.DisposeAsync().AsTask();Assert.That(two,Is.SameAs(one));
        Assert.ThrowsAsync<InvalidOperationException>(async()=>await one);Assert.That(sinks.Select(s=>s.Closes),Is.EqualTo(new[]{1,1}));Assert.That(capture.Closes,Is.EqualTo(1));Assert.ThrowsAsync<ObjectDisposedException>(async()=>await owner.AddPeerAsync());
    }
    [Test]
    public async Task Concurrent_disposals_wait_for_actual_playback_cleanup()
    {
        var capture=new Capture();var playback=new Playback{ReleaseClose=new(TaskCreationOptions.RunContinuationsAsynchronously)};
        var owner=new MeshAudioSession(()=>capture,()=>playback);var peer=await owner.AddPeerAsync();await Audio(peer).StartAsync();
        Task one=owner.DisposeAsync().AsTask();Task two=owner.DisposeAsync().AsTask();
        try
        {
            await playback.CloseStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(one.IsCompleted,Is.False);Assert.That(two,Is.SameAs(one));
        }
        finally {playback.ReleaseClose.TrySetResult();await Task.WhenAll(one,two).WaitAsync(TimeSpan.FromSeconds(5));}
        Assert.That(capture.Closes,Is.EqualTo(1));Assert.That(playback.Closes,Is.EqualTo(1));
    }
    [Test]
    public async Task Incompatible_codec_is_rejected_and_one_bad_subscriber_does_not_drop_other_peers()
    {
        var capture=new Capture();await using var owner=new MeshAudioSession(()=>capture,()=>new Playback(),initialMuted:false);var first=await owner.AddPeerAsync();var second=await owner.AddPeerAsync();
        var a=Audio(first);var b=Audio(second);Assert.Throws<InvalidOperationException>(()=>a.SetFormat(new AudioFormat(SDPWellKnownMediaFormatsEnum.PCMA)));
        await a.StartAsync();await b.StartAsync();int received=0;a.EncodedSample+=(_,_)=>throw new InvalidOperationException("fixture");b.EncodedSample+=(_,_)=>received++;
        Assert.DoesNotThrow(()=>capture.Emit());Assert.That(received,Is.EqualTo(1));
    }
}
