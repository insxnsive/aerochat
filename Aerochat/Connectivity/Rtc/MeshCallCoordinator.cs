using System.Text;
using System.Threading.Channels;

namespace Aerochat.Connectivity.Rtc;

internal enum MeshCallState { Idle, Joining, InCall, Ended }

// One explicit group voice session in one conversation. Gateway and engine callbacks only enqueue
// work; a single worker applies it under _serial, as JoinAsync and LeaveAsync do. Nothing outside
// tests constructs this yet: group calls stay disabled in the UI.
internal sealed class MeshCallCoordinator:IAsyncDisposable
{
    internal const int MaxPendingIce=64,MaxPendingIceBytes=64*1024,MaxQueuedEvents=256,MaxQueuedBytes=256*1024;
    private static readonly TimeSpan LeaveBudget=TimeSpan.FromSeconds(2);

    private sealed class Peer(Guid remote,MeshPeerEdge edge,bool offerer,RtcPeerEngine engine)
    {
        internal Guid Remote=>remote;internal MeshPeerEdge Edge=>edge;internal bool Offerer=>offerer;internal RtcPeerEngine Engine=>engine;
        internal bool OfferSent,RemoteReady;internal int PendingBytes;internal readonly List<string> PendingIce=[];
        internal EventHandler<RtcIceCandidate>? OnIce;internal EventHandler<RtcPeerState>? OnState;
    }
    private abstract class Work{internal int Bytes;}
    private sealed class SignalWork(MeshSignalEvent signal):Work{internal MeshSignalEvent Signal=>signal;}
    private sealed class RosterWork(MeshRoomSnapshot room):Work{internal MeshRoomSnapshot Room=>room;}
    private sealed class ResyncWork:Work{}
    private sealed class OverflowWork:Work{}
    private sealed class IceWork(Peer peer,string candidate):Work{internal Peer Peer=>peer;internal string Candidate=>candidate;}
    private sealed class FailedWork(Peer peer):Work{internal Peer Peer=>peer;}

    private readonly Guid _conversation;
    private readonly IMeshRoomClient _rooms;
    private readonly IMeshEventSource _events;
    private readonly Func<MeshAudioSession> _sessionFactory;
    private readonly TurnCredentialProvider? _turn;
    private readonly SemaphoreSlim _serial=new(1,1);
    private readonly Channel<Work> _queue=Channel.CreateUnbounded<Work>(new UnboundedChannelOptions{SingleReader=true});
    private readonly Dictionary<Guid,Peer> _peers=[];
    private readonly HashSet<Guid> _failedEdges=[];
    private readonly Task _worker;
    private MeshAudioSession? _session;
    private MeshRoomSnapshot? _room;
    private Guid _self;
    private volatile bool _muted=true;
    private int _queuedCount,_queuedBytes,_overflow,_disposed;
    private Task? _dispose;

    internal MeshCallCoordinator(Guid conversation,IMeshRoomClient rooms,IMeshEventSource events,Func<MeshAudioSession> sessionFactory,TurnCredentialProvider? turn=null)
    {
        MeshRoomWire.RequireId(conversation);
        _conversation=conversation;_rooms=rooms??throw new ArgumentNullException(nameof(rooms));
        _events=events??throw new ArgumentNullException(nameof(events));_sessionFactory=sessionFactory??throw new ArgumentNullException(nameof(sessionFactory));_turn=turn;
        _events.MeshSignalReceived+=OnSignal;_events.MeshRosterReceived+=OnRoster;_events.MeshResyncRequired+=OnResync;
        _worker=Task.Run(RunAsync);
    }

    internal MeshCallState State{get;private set;}
    internal int PeerCount{get{lock(_peers)return _peers.Count;}}
    internal RtcPeerEngine? EngineFor(Guid remote){lock(_peers)return _peers.TryGetValue(remote,out var peer)?peer.Engine:null;}

    // Mute is owned here and applied only through the session: engine.Mute/Unmute keep their own flag,
    // which replacement resets, and RtcPeerEngine.Unmute returns early when that flag is false.
    internal void SetMuted(bool muted){_muted=muted;Volatile.Read(ref _session)?.SetMuted(muted);}

    internal async Task JoinAsync(CancellationToken token=default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed)!=0,this);
        await _serial.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if(State is MeshCallState.Joining or MeshCallState.InCall)throw new InvalidOperationException("Already in a group call.");
            State=MeshCallState.Joining;_failedEdges.Clear();Interlocked.Exchange(ref _overflow,0);
            MeshRoomSnapshot joined;
            try
            {
                var capability=await _rooms.ReadAsync(_conversation,token).ConfigureAwait(false);
                _self=capability.Self;
                joined=await _rooms.JoinAsync(_conversation,capability.Room.RoomId,token).ConfigureAwait(false);
            }
            catch {State=MeshCallState.Idle;throw;}
            _room=joined;var session=_sessionFactory();session.SetMuted(_muted);Volatile.Write(ref _session,session);
            State=MeshCallState.InCall;
            await ReconcileAsync(joined).ConfigureAwait(false);
        }
        finally {_serial.Release();}
    }

    internal async Task LeaveAsync()
    {
        await _serial.WaitAsync().ConfigureAwait(false);
        try {await EndAsync().ConfigureAwait(false);}
        finally {_serial.Release();}
    }

    private void OnSignal(object? sender,MeshSignalEvent signal)
    {if(signal.Conversation==_conversation)Enqueue(new SignalWork(signal){Bytes=256+Size(signal.Sdp)+Size(signal.Candidate)});}
    private void OnRoster(object? sender,MeshRosterEvent roster)
    {if(roster.Conversation==_conversation)Enqueue(new RosterWork(roster.Room){Bytes=1024});}
    private void OnResync(object? sender,MeshResyncEventArgs resync)=>Enqueue(new ResyncWork{Bytes=64});
    private static int Size(string? text)=>text is null?0:Encoding.UTF8.GetByteCount(text);

    // Runs on gateway and engine threads: never waits. Past the bound the call ends rather than
    // processing an incomplete stream of signals.
    private void Enqueue(Work work)
    {
        if(Volatile.Read(ref _disposed)!=0)return;
        int count=Interlocked.Increment(ref _queuedCount),bytes=Interlocked.Add(ref _queuedBytes,work.Bytes);
        if(count>MaxQueuedEvents||bytes>MaxQueuedBytes)
        {
            Interlocked.Decrement(ref _queuedCount);Interlocked.Add(ref _queuedBytes,-work.Bytes);
            if(Interlocked.Exchange(ref _overflow,1)==0)_queue.Writer.TryWrite(new OverflowWork());
            return;
        }
        if(!_queue.Writer.TryWrite(work)){Interlocked.Decrement(ref _queuedCount);Interlocked.Add(ref _queuedBytes,-work.Bytes);}
    }

    private async Task RunAsync()
    {
        await foreach(var work in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if(work is not OverflowWork){Interlocked.Decrement(ref _queuedCount);Interlocked.Add(ref _queuedBytes,-work.Bytes);}
            await _serial.WaitAsync().ConfigureAwait(false);
            try {await ApplyAsync(work).ConfigureAwait(false);}
            catch { /* Every handler contains its own failures; the worker must survive. */ }
            finally {_serial.Release();}
        }
    }

    private Task ApplyAsync(Work work)=>work switch
    {
        OverflowWork=>EndAsync(),
        _ when State!=MeshCallState.InCall=>Task.CompletedTask,
        SignalWork s=>HandleSignalAsync(s.Signal),
        RosterWork r=>ReconcileAsync(r.Room),
        ResyncWork=>ResyncAsync(),
        IceWork i=>SendIceAsync(i.Peer,i.Candidate),
        FailedWork f=>FailPeerAsync(f.Peer),
        _=>Task.CompletedTask
    };

    private bool IsCurrent(Peer peer){lock(_peers)return State==MeshCallState.InCall&&_peers.TryGetValue(peer.Remote,out var current)&&ReferenceEquals(current,peer);}
    private Peer[] Peers(){lock(_peers)return _peers.Values.ToArray();}

    private async Task ReconcileAsync(MeshRoomSnapshot room)
    {
        if(State!=MeshCallState.InCall)return;
        // A replaced room or a roster without self means this session is over; no automatic rejoin.
        if(room.RoomId!=_room!.RoomId||!room.Participants.Contains(_self)){await EndAsync().ConfigureAwait(false);return;}
        _room=room;
        var wanted=room.Edges.Where(e=>e.Offerer==_self||e.Answerer==_self).ToDictionary(e=>e.Offerer==_self?e.Answerer:e.Offerer);
        foreach(var peer in Peers())
            if(!wanted.TryGetValue(peer.Remote,out var edge)||edge.Id!=peer.Edge.Id)await RemoveAsync(peer).ConfigureAwait(false);
        foreach(var (remote,edge) in wanted)
        {
            if(State!=MeshCallState.InCall)return;
            bool present;lock(_peers)present=_peers.ContainsKey(remote);
            if(!present&&!_failedEdges.Contains(edge.Id))await AddAsync(remote,edge).ConfigureAwait(false);
        }
    }

    private async Task AddAsync(Guid remote,MeshPeerEdge edge)
    {
        RtcPeerEngine engine;
        try {engine=await _session!.AddPeerAsync(default,_turn).ConfigureAwait(false);}
        catch(Exception error)
        {
            if(IsSessionLost(error))await EndAsync().ConfigureAwait(false);else _failedEdges.Add(edge.Id);
            return;
        }
        var peer=new Peer(remote,edge,edge.Offerer==_self,engine);
        peer.OnIce=(_,candidate)=>Enqueue(new IceWork(peer,candidate.Candidate){Bytes=256+Size(candidate.Candidate)});
        peer.OnState=(_,state)=>{if(state is RtcPeerState.Failed or RtcPeerState.Disconnected)Enqueue(new FailedWork(peer){Bytes=64});};
        engine.IceCandidateReady+=peer.OnIce;engine.StateChanged+=peer.OnState;
        lock(_peers)_peers[remote]=peer;
        if(!peer.Offerer)return;
        try
        {
            _session!.SetMuted(_muted);
            string offer=await engine.StartCall().ConfigureAwait(false);
            if(!IsCurrent(peer))return;
            await _rooms.SignalAsync(_conversation,new(_room!.RoomId!.Value,edge.Id,remote,"offer",Sdp:offer)).ConfigureAwait(false);
            peer.OfferSent=true;
        }
        catch(Exception error){await OnPeerErrorAsync(peer,error).ConfigureAwait(false);}
    }

    private async Task HandleSignalAsync(MeshSignalEvent signal)
    {
        var room=_room!;
        // The gateway parser only rejects from==to; everything else is checked against this session.
        if(signal.RoomId!=room.RoomId||signal.To!=_self||!room.Participants.Contains(signal.From))return;
        var edge=room.Edges.FirstOrDefault(e=>e.Id==signal.EdgeId);
        if(edge is null||!((edge.Offerer==signal.From&&edge.Answerer==_self)||(edge.Answerer==signal.From&&edge.Offerer==_self)))return;
        Peer? peer;lock(_peers)_peers.TryGetValue(signal.From,out peer);
        if(peer is null||peer.Edge.Id!=edge.Id)return;
        try
        {
            switch(signal.Action)
            {
                case "offer" when !peer.Offerer&&!peer.RemoteReady:
                    _session!.SetMuted(_muted);
                    string answer=await peer.Engine.AcceptOffer(signal.Sdp!).ConfigureAwait(false);
                    if(!IsCurrent(peer))return;
                    peer.RemoteReady=true;DrainIce(peer);
                    await _rooms.SignalAsync(_conversation,new(room.RoomId!.Value,edge.Id,peer.Remote,"answer",Sdp:answer)).ConfigureAwait(false);
                    break;
                case "answer" when peer.Offerer&&peer.OfferSent&&!peer.RemoteReady:
                    _session!.SetMuted(_muted);
                    await peer.Engine.ApplyAnswer(signal.Sdp!).ConfigureAwait(false);
                    if(!IsCurrent(peer))return;
                    peer.RemoteReady=true;DrainIce(peer);
                    break;
                case "ice":
                    if(peer.RemoteReady){peer.Engine.AddIceCandidate(new(signal.Candidate!));break;}
                    peer.PendingIce.Add(signal.Candidate!);peer.PendingBytes+=Size(signal.Candidate);
                    if(peer.PendingIce.Count>MaxPendingIce||peer.PendingBytes>MaxPendingIceBytes)await FailPeerAsync(peer).ConfigureAwait(false);
                    break;
            }
        }
        catch(Exception error){await OnPeerErrorAsync(peer,error).ConfigureAwait(false);}
    }

    private static void DrainIce(Peer peer)
    {
        foreach(string candidate in peer.PendingIce)peer.Engine.AddIceCandidate(new(candidate));
        peer.PendingIce.Clear();peer.PendingBytes=0;
    }

    private async Task SendIceAsync(Peer peer,string candidate)
    {
        if(!IsCurrent(peer))return;
        try {await _rooms.SignalAsync(_conversation,new(_room!.RoomId!.Value,peer.Edge.Id,peer.Remote,"ice",Candidate:candidate)).ConfigureAwait(false);}
        catch(Exception error){await OnPeerErrorAsync(peer,error).ConfigureAwait(false);}
    }

    private async Task ResyncAsync()
    {
        MeshCapability capability;
        try {capability=await _rooms.ReadAsync(_conversation).ConfigureAwait(false);}
        catch { return; /* Keep the current state; the next roster or resync tries again. */ }
        if(capability.Self!=_self){await EndAsync().ConfigureAwait(false);return;}
        await ReconcileAsync(capability.Room).ConfigureAwait(false);
    }

    // A device timeout closes the whole session (AddPeerAsync throws after it): end the call, no restart.
    private static bool IsSessionLost(Exception error)=>error is TimeoutException or ObjectDisposedException;

    private async Task OnPeerErrorAsync(Peer peer,Exception error)
    {
        if(IsSessionLost(error))await EndAsync().ConfigureAwait(false);
        else await FailPeerAsync(peer).ConfigureAwait(false);
    }

    // Failure removes only this edge, and the same server edge is not negotiated again.
    private async Task FailPeerAsync(Peer peer)
    {
        if(!IsCurrent(peer))return;
        _failedEdges.Add(peer.Edge.Id);
        await RemoveAsync(peer).ConfigureAwait(false);
    }

    private async Task RemoveAsync(Peer peer)
    {
        Unhook(peer);
        lock(_peers)if(_peers.TryGetValue(peer.Remote,out var current)&&ReferenceEquals(current,peer))_peers.Remove(peer.Remote);
        try {if(_session is { } session)await session.RemovePeerAsync(peer.Engine).ConfigureAwait(false);}
        catch { /* The peer is gone from the map either way. */ }
    }

    private static void Unhook(Peer peer){peer.Engine.IceCandidateReady-=peer.OnIce;peer.Engine.StateChanged-=peer.OnState;}

    // Invalidate first, then stop media, then best-effort leave with a short budget.
    private async Task EndAsync()
    {
        var session=_session;var room=_room?.RoomId;
        if(session is null&&room is null)return;
        State=MeshCallState.Ended;Volatile.Write(ref _session,null);_room=null;
        foreach(var peer in Peers())Unhook(peer);
        lock(_peers)_peers.Clear();
        if(session is not null)try {await session.DisposeAsync().ConfigureAwait(false);}catch { }
        if(room is Guid id)
        {
            using var budget=new CancellationTokenSource(LeaveBudget);
            try {await _rooms.LeaveAsync(_conversation,id,budget.Token).ConfigureAwait(false);}catch { }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock(_queue)
        {
            if(_dispose is null)
            {
                Volatile.Write(ref _disposed,1);
                _events.MeshSignalReceived-=OnSignal;_events.MeshRosterReceived-=OnRoster;_events.MeshResyncRequired-=OnResync;
                _queue.Writer.TryComplete();_dispose=FinishDisposeAsync();
            }
            return new(_dispose);
        }
    }

    private async Task FinishDisposeAsync()
    {
        await _worker.ConfigureAwait(false);
        await _serial.WaitAsync().ConfigureAwait(false);
        try {await EndAsync().ConfigureAwait(false);}
        finally {_serial.Release();}
    }
}
