using System.IO;
using System.Text;
using System.Text.Json;

namespace Aerochat.Connectivity.Rtc;

// A validated mesh.signal addressed to this client. The server sets From from the
// authenticated sender; the coordinator still checks self, room, edge and roster before use.
internal sealed class MeshSignalEvent(Guid conversation,Guid roomId,Guid edgeId,Guid from,Guid to,string action,string? sdp,string? candidate):EventArgs
{
    internal Guid Conversation{get;}=conversation;
    internal Guid RoomId{get;}=roomId;
    internal Guid EdgeId{get;}=edgeId;
    internal Guid From{get;}=from;
    internal Guid To{get;}=to;
    internal string Action{get;}=action;
    internal string? Sdp{get;}=sdp;
    internal string? Candidate{get;}=candidate;
    public override string ToString()=>"MeshSignalEvent { [redacted] }";
}

// A validated mesh.roster: the server's whole room snapshot after a join, leave or disconnect.
internal sealed class MeshRosterEvent(Guid conversation,MeshRoomSnapshot room):EventArgs
{
    internal Guid Conversation{get;}=conversation;
    internal MeshRoomSnapshot Room{get;}=room;
}

// Implemented by GatewayClient. Handlers run on the gateway receive loop and must only
// hand work to the coordinator's own queue; they must not wait on HTTP, media or disposal.
internal interface IMeshEventSource
{
    event EventHandler<MeshSignalEvent>? MeshSignalReceived;
    event EventHandler<MeshRosterEvent>? MeshRosterReceived;
    event EventHandler<MeshResyncEventArgs>? MeshResyncRequired;
}

internal enum MeshResyncReason { RosterRejected, Reconnected }

// Raised when mesh state may have been missed: a mesh.roster frame failed validation (replay will not
// resend it), or the gateway reconnected (live mesh.signal frames are never replayed). The coordinator
// answers with a fresh ReadAsync. Like the other mesh events, handlers must only enqueue.
internal sealed class MeshResyncEventArgs(MeshResyncReason reason):EventArgs
{
    internal MeshResyncReason Reason{get;}=reason;
}

internal static class MeshGatewayProtocol
{
    private const int MaxFieldBytes=64*1024;

    internal static bool TryParseSignal(JsonElement data,out MeshSignalEvent? signal)
    {
        signal=null;
        if(!MeshRoomWire.IsObject(data)||!TryId(data,"conversationId",out var conversation)||!TryId(data,"roomId",out var room)
            ||!TryId(data,"edgeId",out var edge)||!TryId(data,"from",out var from)||!TryId(data,"to",out var to)||from==to
            ||!data.TryGetProperty("action",out var actionValue)||actionValue.ValueKind!=JsonValueKind.String)return false;
        string action=actionValue.GetString()!;
        if(action is not ("offer" or "answer" or "ice")||!TryText(data,"sdp",out string? sdp)||!TryText(data,"candidate",out string? candidate))return false;
        bool ice=action=="ice";
        if(string.IsNullOrWhiteSpace(ice?candidate:sdp)||(ice?sdp:candidate) is not null)return false;
        signal=new(conversation,room,edge,from,to,action,sdp,candidate);
        return true;
    }

    internal static bool TryParseRoster(JsonElement data,out MeshRosterEvent? roster)
    {
        roster=null;
        if(!MeshRoomWire.IsObject(data)||!TryId(data,"conversationId",out var conversation)||!data.TryGetProperty("room",out var room))return false;
        try
        {
            roster=new(conversation,MeshRoomWire.Parse(room));
            return true;
        }
        catch(InvalidDataException)
        {
            return false;
        }
    }

    private static bool TryId(JsonElement data,string name,out Guid id)
    {
        id=Guid.Empty;
        return data.TryGetProperty(name,out var value)&&value.ValueKind==JsonValueKind.String
            &&(Guid.TryParseExact(value.GetString(),"D",out id)||Guid.TryParseExact(value.GetString(),"N",out id))&&id!=Guid.Empty;
    }

    // Absent or null means not supplied; a supplied value must be a string within the server's per-field limit.
    private static bool TryText(JsonElement data,string name,out string? text)
    {
        text=null;
        if(!data.TryGetProperty(name,out var value)||value.ValueKind==JsonValueKind.Null)return true;
        if(value.ValueKind!=JsonValueKind.String)return false;
        text=value.GetString();
        return Encoding.UTF8.GetByteCount(text!)<=MaxFieldBytes;
    }
}
