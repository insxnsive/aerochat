namespace Aerochat.Server.Calls;

public sealed record MeshEdge(Guid Id, Guid Offerer, Guid Answerer);
public sealed record MeshSnapshot(Guid? RoomId, Guid[] Participants, MeshEdge[] Edges);
public sealed record MeshResult(string? Error, MeshSnapshot Snapshot);

// Process-local, like CallRegistry. All roster and edge transitions use one lock.
// No method here calls the gateway; endpoint lock order is gateway then registry.
public sealed class MeshRooms
{
    private sealed class Room
    {
        internal Guid Id=Guid.NewGuid();
        internal HashSet<Guid> Users=[];
        internal Dictionary<Guid,(MeshEdge Edge,int Phase,int IceCount)> Edges=[];
    }
    private readonly object _gate=new();
    private readonly Dictionary<Guid,Room> _rooms=[];
    private readonly HashSet<Guid> _online=[];
    private readonly HashSet<Guid> _dirty=[];
    public void SetPresence(Guid user,bool online)
    {
        lock(_gate)
        {
            if(online) {_online.Add(user);return;} _online.Remove(user);
            foreach(var pair in _rooms.ToArray())
                if(pair.Value.Users.Contains(user)) { Remove(pair.Key,pair.Value,user); _dirty.Add(pair.Key); }
        }
    }
    private static MeshSnapshot Snapshot(Room? r)=>r is null ? new(null,[],[]) :
        new(r.Id,r.Users.OrderBy(u=>u.ToString("N"),StringComparer.Ordinal).ToArray(),r.Edges.Values.Select(e=>e.Edge).ToArray());
    public MeshSnapshot Read(Guid conversation) {lock(_gate)return Snapshot(_rooms.GetValueOrDefault(conversation));}
    public (Guid Conversation,MeshSnapshot Snapshot)[] DrainChanges()
    {
        lock(_gate) { var values=_dirty.Select(c=>(c,Snapshot(_rooms.GetValueOrDefault(c)))).ToArray();_dirty.Clear();return values; }
    }
    public MeshResult Join(Guid conversation,Guid actor,Guid? expectedRoom)
    {
        lock(_gate)
        {
            var r=_rooms.GetValueOrDefault(conversation);
            MeshResult Fail(string e)=>new(e,Snapshot(r));
            if(!_online.Contains(actor))return Fail("mesh_gateway_required");
            if(r is null)
            {
                if(expectedRoom is not null)return Fail("mesh_stale_room");
                _rooms[conversation]=r=new();
            }
            else if(expectedRoom!=r.Id)return Fail("mesh_stale_room");
            if(r.Users.Contains(actor))return new(null,Snapshot(r));
            if(r.Users.Count>=4)return Fail("mesh_full");
            foreach(var other in r.Users)
            {
                bool first=string.CompareOrdinal(actor.ToString("N"),other.ToString("N"))<0;
                var edge=new MeshEdge(Guid.NewGuid(),first?actor:other,first?other:actor);r.Edges.Add(edge.Id,(edge,0,0));
            }
            r.Users.Add(actor);return new(null,Snapshot(r));
        }
    }
    public MeshResult Leave(Guid conversation,Guid actor,Guid room)
    {
        lock(_gate)
        {
            var r=_rooms.GetValueOrDefault(conversation);
            if(r?.Id!=room)return new("mesh_stale_room",Snapshot(r));
            Remove(conversation,r,actor);return new(null,Snapshot(_rooms.GetValueOrDefault(conversation)));
        }
    }
    private void Remove(Guid conversation,Room r,Guid actor)
    {
        if(!r.Users.Remove(actor))return;
        foreach(var edge in r.Edges.Values.Where(e=>e.Edge.Offerer==actor||e.Edge.Answerer==actor).ToArray())r.Edges.Remove(edge.Edge.Id);
        if(r.Users.Count==0)_rooms.Remove(conversation);
    }
    public string? Signal(Guid conversation,Guid actor,Guid to,Guid room,Guid edgeId,string action)
    {
        lock(_gate)
        {
            var r=_rooms.GetValueOrDefault(conversation);
            if(r?.Id!=room)return "mesh_stale_room";
            if(actor==to||!r.Users.Contains(actor)||!r.Users.Contains(to))return "mesh_not_participant";
            if(!r.Edges.TryGetValue(edgeId,out var entry)||
                !((entry.Edge.Offerer==actor&&entry.Edge.Answerer==to)||(entry.Edge.Answerer==actor&&entry.Edge.Offerer==to)))return "mesh_stale_edge";
            if(action=="offer"&&entry.Phase==0&&actor==entry.Edge.Offerer)r.Edges[edgeId]=(entry.Edge,1,entry.IceCount);
            else if(action=="answer"&&entry.Phase==1&&actor==entry.Edge.Answerer)r.Edges[edgeId]=(entry.Edge,2,entry.IceCount);
            else if(action!="ice"||entry.Phase==0)return "mesh_invalid_state";
            else if(entry.IceCount>=128)return "mesh_ice_limit";
            else r.Edges[edgeId]=(entry.Edge,entry.Phase,entry.IceCount+1);
            return null;
        }
    }
}
