using System.Diagnostics;
using System.Reflection;
using Aerochat.Connectivity.Rtc;

namespace Aerochat.VisualShell.Tests;

public sealed class MeshCallCoordinatorTests
{
    private static readonly Guid A=Guid.Parse("00000000-0000-0000-0000-00000000000a"),B=Guid.Parse("00000000-0000-0000-0000-00000000000b"),C=Guid.Parse("00000000-0000-0000-0000-00000000000c");

    // In-memory stand-in for MeshEndpoints: same canonical offerer rule, roster published to the
    // remaining participants, signals delivered only to the addressed member with the server's from.
    private sealed class Server
    {
        internal readonly Guid Conversation=Guid.NewGuid();
        private readonly object _gate=new();
        private Guid? _room;private readonly List<Guid> _users=[];private readonly List<MeshPeerEdge> _edges=[];
        internal readonly Dictionary<Guid,Events> Events=[];
        internal readonly List<(Guid From,MeshOutgoingSignal Signal)> Signals=[];
        internal Func<Guid,MeshOutgoingSignal,bool> Deliver=(_,_)=>true;
        internal int Leaves;
        internal MeshRoomSnapshot Snapshot(){lock(_gate)return new(_room,_users.OrderBy(u=>u.ToString("N"),StringComparer.Ordinal).ToArray(),_edges.ToArray());}
        internal MeshPeerEdge Edge(Guid x,Guid y){lock(_gate)return _edges.Single(e=>(e.Offerer==x&&e.Answerer==y)||(e.Offerer==y&&e.Answerer==x));}
        internal (Guid From,MeshOutgoingSignal Signal)[] Sent(){lock(_gate)return Signals.ToArray();}
        internal MeshRoomSnapshot Join(Guid user)
        {
            lock(_gate)
            {
                _room??=Guid.NewGuid();
                if(!_users.Contains(user))
                {
                    foreach(var other in _users){bool first=string.CompareOrdinal(user.ToString("N"),other.ToString("N"))<0;_edges.Add(new(Guid.NewGuid(),first?user:other,first?other:user));}
                    _users.Add(user);
                }
            }
            var snapshot=Snapshot();Publish(snapshot);return snapshot;
        }
        internal MeshRoomSnapshot Leave(Guid user,bool publish=true)
        {
            lock(_gate){Leaves++;_users.Remove(user);_edges.RemoveAll(e=>e.Offerer==user||e.Answerer==user);if(_users.Count==0)_room=null;}
            var snapshot=Snapshot();if(publish)Publish(snapshot);return snapshot;
        }
        internal void Publish(MeshRoomSnapshot snapshot){foreach(var user in snapshot.Participants)Events[user].Roster(new MeshRosterEvent(Conversation,snapshot));}
        internal void Signal(Guid from,MeshOutgoingSignal signal)
        {
            lock(_gate)Signals.Add((from,signal));
            if(Deliver(from,signal))Inject(from,signal);
        }
        internal void Inject(Guid from,MeshOutgoingSignal signal,Guid? to=null,Guid? conversation=null)=>
            Events[to??signal.To].Signal(new MeshSignalEvent(conversation??Conversation,signal.RoomId,signal.EdgeId,from,signal.To,signal.Action,signal.Sdp,signal.Candidate));
    }
    private sealed class Events:IMeshEventSource
    {
        public event EventHandler<MeshSignalEvent>? MeshSignalReceived;
        public event EventHandler<MeshRosterEvent>? MeshRosterReceived;
        public event EventHandler<MeshResyncEventArgs>? MeshResyncRequired;
        internal void Signal(MeshSignalEvent e)=>MeshSignalReceived?.Invoke(this,e);
        internal void Roster(MeshRosterEvent e)=>MeshRosterReceived?.Invoke(this,e);
        internal void Resync()=>MeshResyncRequired?.Invoke(this,new MeshResyncEventArgs(MeshResyncReason.Reconnected));
    }
    private sealed class Client(Server server,Guid self):IMeshRoomClient
    {
        internal TaskCompletionSource? HoldRead;
        public async Task<MeshCapability> ReadAsync(Guid conversation,CancellationToken token=default)
        {if(HoldRead is { } hold)await hold.Task;return new(self,server.Snapshot());}
        public Task<MeshRoomSnapshot> JoinAsync(Guid conversation,Guid? room,CancellationToken token=default)=>Task.FromResult(server.Join(self));
        public Task<MeshRoomSnapshot> LeaveAsync(Guid conversation,Guid room,CancellationToken token=default)=>Task.FromResult(server.Leave(self));
        public Task SignalAsync(Guid conversation,MeshOutgoingSignal signal,CancellationToken token=default){server.Signal(self,signal);return Task.CompletedTask;}
    }
    private sealed class Capture(bool hang=false):IMeshCapture
    {
        internal int Closes;public event Action<uint,byte[]>? Sample;
        public Task StartAsync()=>hang?new TaskCompletionSource().Task:Task.CompletedTask;
        public Task CloseAsync(){Closes++;return Task.CompletedTask;}
        internal void Emit()=>Sample?.Invoke(160,new byte[160]);
    }
    private sealed class Playback:IMeshPlayback
    {
        internal int Closes;
        public Task StartAsync()=>Task.CompletedTask;
        public Task CloseAsync(){Closes++;return Task.CompletedTask;}
        public void Receive(RtcAudioPacket packet){}
    }
    private sealed class Member(Server server,Guid self,bool hangCapture=false):IAsyncDisposable
    {
        internal readonly Client Rooms=new(server,self);
        internal readonly Capture Capture=new(hangCapture);
        internal readonly List<Playback> Playbacks=[];
        internal MeshCallCoordinator? Coordinator;
        internal MeshCallCoordinator Start()
        {
            server.Events[self]=new Events();
            return Coordinator=new(server.Conversation,Rooms,server.Events[self],
                ()=>new MeshAudioSession(()=>Capture,()=>{var p=new Playback();lock(Playbacks)Playbacks.Add(p);return p;},deviceTimeout:TimeSpan.FromMilliseconds(300)));
        }
        public async ValueTask DisposeAsync(){if(Coordinator is not null)await Coordinator.DisposeAsync();}
    }
    private static async Task UntilAsync(Func<bool> condition,string what)
    {
        var clock=Stopwatch.StartNew();
        while(!condition()){if(clock.Elapsed>TimeSpan.FromSeconds(20))Assert.Fail("Timed out waiting for: "+what);await Task.Delay(20);}
    }
    private static Task SettleAsync()=>Task.Delay(400);
    private static bool Connected(Member member,Guid remote)=>member.Coordinator!.EngineFor(remote)?.State==RtcPeerState.Connected;
    private static IRtcAudioEndpoint Audio(RtcPeerEngine peer)=>(IRtcAudioEndpoint)typeof(RtcPeerEngine).GetField("_audio",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(peer)!;

    [Test]
    public async Task Two_members_connect_once_through_the_canonical_offerer()
    {
        var server=new Server();await using var a=new Member(server,A);await using var b=new Member(server,B);
        await a.Start().JoinAsync();await b.Start().JoinAsync();
        await UntilAsync(()=>Connected(a,B)&&Connected(b,A),"A and B connected");
        var sent=server.Sent();
        Assert.Multiple(()=>
        {
            Assert.That(sent.Where(s=>s.Signal.Action=="offer").Select(s=>s.From),Is.EqualTo(new[]{A}));
            Assert.That(sent.Where(s=>s.Signal.Action=="answer").Select(s=>s.From),Is.EqualTo(new[]{B}));
            Assert.That(sent.All(s=>s.Signal.EdgeId==server.Edge(A,B).Id),Is.True);
            Assert.That(a.Coordinator!.PeerCount,Is.EqualTo(1));Assert.That(b.Coordinator!.State,Is.EqualTo(MeshCallState.InCall));
        });
    }

    [Test]
    public async Task Three_members_form_a_full_mesh_and_each_pair_has_one_offerer()
    {
        var server=new Server();await using var a=new Member(server,A);await using var b=new Member(server,B);await using var c=new Member(server,C);
        await a.Start().JoinAsync();await b.Start().JoinAsync();await c.Start().JoinAsync();
        await UntilAsync(()=>Connected(a,B)&&Connected(a,C)&&Connected(b,C)&&Connected(b,A)&&Connected(c,A)&&Connected(c,B),"full mesh connected");
        var offers=server.Sent().Where(s=>s.Signal.Action=="offer").ToArray();
        Assert.Multiple(()=>
        {
            Assert.That(offers,Has.Length.EqualTo(3));
            Assert.That(offers.All(o=>server.Snapshot().Edges.Single(e=>e.Id==o.Signal.EdgeId).Offerer==o.From),Is.True);
            Assert.That(new[]{a,b,c}.Select(m=>m.Coordinator!.PeerCount),Is.EqualTo(new[]{2,2,2}));
        });
    }

    [Test]
    public async Task Forged_or_misaddressed_signals_never_reach_a_peer()
    {
        var server=new Server();await using var a=new Member(server,A);await using var b=new Member(server,B);
        server.Deliver=(from,signal)=>signal.Action!="offer";
        await a.Start().JoinAsync();await b.Start().JoinAsync();
        await UntilAsync(()=>server.Sent().Any(s=>s.Signal.Action=="offer"),"A offered");
        var real=server.Sent().Single(s=>s.Signal.Action=="offer").Signal;var room=server.Snapshot().RoomId!.Value;var edge=server.Edge(A,B).Id;
        var garbage=new MeshOutgoingSignal(room,edge,B,"offer",Sdp:"not-sdp");
        server.Inject(A,garbage,conversation:Guid.NewGuid());
        server.Inject(A,garbage with {RoomId=Guid.NewGuid()});
        server.Inject(A,garbage with {To=C},to:B);
        server.Inject(A,garbage with {EdgeId=Guid.NewGuid()});
        server.Inject(C,garbage);
        await SettleAsync();
        Assert.That(b.Coordinator!.PeerCount,Is.EqualTo(1),"a forged signal reached the peer and failed it");
        server.Inject(A,real);
        await UntilAsync(()=>Connected(a,B)&&Connected(b,A),"A and B connected after the real offer");
    }

    [Test]
    public async Task Negotiation_failure_removes_the_peer_through_the_session_and_is_not_retried()
    {
        var server=new Server();await using var a=new Member(server,A);await using var b=new Member(server,B);
        server.Deliver=(from,signal)=>signal.Action!="offer";
        await a.Start().JoinAsync();await b.Start().JoinAsync();
        await UntilAsync(()=>server.Sent().Any(s=>s.Signal.Action=="offer"),"A offered");
        var real=server.Sent().Single(s=>s.Signal.Action=="offer").Signal;
        server.Inject(A,real with {Sdp="not-sdp"});
        await UntilAsync(()=>b.Coordinator!.PeerCount==0,"B removed the failed peer");
        Assert.That(b.Playbacks.Single().Closes,Is.EqualTo(1),"the failed edge was not removed through MeshAudioSession.RemovePeerAsync");
        server.Inject(A,real);server.Publish(server.Snapshot());await SettleAsync();
        Assert.That(b.Coordinator!.PeerCount,Is.Zero,"the failed server edge was negotiated again");
    }

    [Test]
    public async Task Ice_beyond_the_queue_bound_fails_only_that_edge()
    {
        var server=new Server();await using var a=new Member(server,A);await using var b=new Member(server,B);
        server.Deliver=(from,signal)=>signal.Action!="offer"&&signal.Action!="ice";
        await a.Start().JoinAsync();await b.Start().JoinAsync();
        await UntilAsync(()=>b.Coordinator!.PeerCount==1,"B created its answerer peer");
        var ice=new MeshOutgoingSignal(server.Snapshot().RoomId!.Value,server.Edge(A,B).Id,B,"ice",Candidate:"candidate:1 1 udp 1 127.0.0.1 9 typ host");
        for(int i=0;i<MeshCallCoordinator.MaxPendingIce;i++)server.Inject(A,ice);
        await SettleAsync();Assert.That(b.Coordinator!.PeerCount,Is.EqualTo(1),"64 queued candidates are within the bound");
        server.Inject(A,ice);
        await UntilAsync(()=>b.Coordinator!.PeerCount==0,"the 65th candidate failed the edge");
        Assert.That(b.Coordinator!.State,Is.EqualTo(MeshCallState.InCall));
    }

    [TestCase("self-missing")][TestCase("room-replaced")]
    public async Task A_roster_without_self_or_for_another_room_ends_the_call_and_leaves(string mode)
    {
        var server=new Server();await using var a=new Member(server,A);await using var b=new Member(server,B);
        await a.Start().JoinAsync();await b.Start().JoinAsync();
        var current=server.Snapshot();
        var roster=mode=="self-missing"?current with {Participants=new[]{A},Edges=Array.Empty<MeshPeerEdge>()}:current with {RoomId=Guid.NewGuid()};
        server.Events[B].Roster(new MeshRosterEvent(server.Conversation,roster));
        // Ended is set before media stops and the best-effort leave runs, so wait for the leave itself.
        await UntilAsync(()=>b.Coordinator!.State==MeshCallState.Ended&&server.Leaves==1,"B ended the call and left");
        Assert.Multiple(()=>{Assert.That(b.Coordinator!.PeerCount,Is.Zero);Assert.That(server.Leaves,Is.EqualTo(1));});
    }

    [Test]
    public async Task Resync_reconciles_a_member_who_left_without_a_roster_frame()
    {
        var server=new Server();await using var a=new Member(server,A);await using var b=new Member(server,B);await using var c=new Member(server,C);
        await a.Start().JoinAsync();await b.Start().JoinAsync();await c.Start().JoinAsync();
        await UntilAsync(()=>a.Coordinator!.PeerCount==2,"A has two peers");
        server.Leave(C,publish:false);await SettleAsync();
        Assert.That(a.Coordinator!.PeerCount,Is.EqualTo(2));
        server.Events[A].Resync();
        await UntilAsync(()=>a.Coordinator!.PeerCount==1&&a.Coordinator.EngineFor(C) is null,"A dropped C after resync");
    }

    [Test]
    public async Task Mute_is_applied_only_through_the_session()
    {
        var server=new Server();await using var a=new Member(server,A);await using var b=new Member(server,B);
        await a.Start().JoinAsync();await b.Start().JoinAsync();
        await UntilAsync(()=>Connected(a,B)&&Connected(b,A),"A and B connected");
        int sent=0;Audio(a.Coordinator!.EngineFor(B)!).EncodedSample+=(_,_)=>sent++;
        a.Capture.Emit();Assert.That(sent,Is.Zero,"a new call starts muted");
        a.Coordinator!.SetMuted(false);a.Capture.Emit();Assert.That(sent,Is.EqualTo(1));
        a.Coordinator!.SetMuted(true);a.Capture.Emit();Assert.That(sent,Is.EqualTo(1));
    }

    [Test]
    public async Task A_device_timeout_ends_the_call_without_a_restart()
    {
        var server=new Server();await using var a=new Member(server,A);await using var b=new Member(server,B,hangCapture:true);
        await a.Start().JoinAsync();await b.Start().JoinAsync();
        await UntilAsync(()=>b.Coordinator!.State==MeshCallState.Ended,"B ended after its capture start timed out");
        await UntilAsync(()=>a.Coordinator!.PeerCount==0,"A dropped B after B left");
        Assert.Multiple(()=>{Assert.That(server.Leaves,Is.EqualTo(1));Assert.That(a.Coordinator!.State,Is.EqualTo(MeshCallState.InCall));});
    }

    [Test]
    public async Task Gateway_handlers_only_enqueue_and_an_overflow_ends_the_call()
    {
        var server=new Server();await using var b=new Member(server,B);
        await b.Start().JoinAsync();
        b.Rooms.HoldRead=new(TaskCreationOptions.RunContinuationsAsynchronously);
        server.Events[B].Resync();await SettleAsync();
        var signal=new MeshOutgoingSignal(server.Snapshot().RoomId!.Value,Guid.NewGuid(),B,"ice",Candidate:"x");
        var clock=Stopwatch.StartNew();
        for(int i=0;i<MeshCallCoordinator.MaxQueuedEvents+10;i++)server.Inject(A,signal);
        var raised=clock.Elapsed;
        b.Rooms.HoldRead.TrySetResult();
        await UntilAsync(()=>b.Coordinator!.State==MeshCallState.Ended,"the overflow ended the call");
        Assert.That(raised,Is.LessThan(TimeSpan.FromSeconds(1)),"a handler waited on the coordinator");
    }
}
