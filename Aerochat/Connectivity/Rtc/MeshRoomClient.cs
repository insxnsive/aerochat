using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Aerochat.Connectivity.Auth;

namespace Aerochat.Connectivity.Rtc;

internal sealed record MeshPeerEdge(Guid Id,Guid Offerer,Guid Answerer);
internal sealed record MeshRoomSnapshot(Guid? RoomId,IReadOnlyList<Guid> Participants,IReadOnlyList<MeshPeerEdge> Edges);
internal sealed record MeshOutgoingSignal(Guid RoomId,Guid EdgeId,Guid To,string Action,string? Sdp=null,string? Candidate=null)
{ public override string ToString()=>"MeshOutgoingSignal { [redacted] }"; }
internal sealed class MeshRequestException(HttpStatusCode status,string? code,TimeSpan? retryAfter)
    :HttpRequestException("The mesh request failed.",null,status)
{
    internal string? Code {get;}=code;
    internal TimeSpan? RetryAfter {get;}=retryAfter;
}
internal interface IMeshRoomClient
{
    Task<MeshRoomSnapshot> ReadAsync(Guid conversation,CancellationToken token=default);
    Task<MeshRoomSnapshot> JoinAsync(Guid conversation,Guid? room,CancellationToken token=default);
    Task<MeshRoomSnapshot> LeaveAsync(Guid conversation,Guid room,CancellationToken token=default);
    Task SignalAsync(Guid conversation,MeshOutgoingSignal signal,CancellationToken token=default);
}
internal sealed class MeshRoomClient:IMeshRoomClient
{
    private readonly HttpClient _http;private readonly Uri _server;private readonly Func<CancellationToken,Task<string?>> _token;
    private readonly TimeProvider _clock;private readonly TimeSpan _timeout;
    private static readonly HashSet<string> Codes=["mesh_upgrade_required","mesh_group_required","mesh_stale_room","mesh_stale_edge","mesh_not_participant","mesh_invalid_state","mesh_full","mesh_gateway_required","mesh_ice_limit","rate_limited"];
    internal MeshRoomClient(HttpClient http,Uri server,Func<CancellationToken,Task<string?>> token,TimeProvider? clock=null,TimeSpan? timeout=null)
    {
        ArgumentNullException.ThrowIfNull(server);
        if(!server.IsAbsoluteUri||(!string.Equals(server.Scheme,"https",StringComparison.OrdinalIgnoreCase)
            &&!(string.Equals(server.Scheme,"http",StringComparison.OrdinalIgnoreCase)&&OAuthAuthClient.HasExactNumericLoopbackHost(server)))
            ||server.UserInfo.Length!=0||server.Query.Length!=0||server.Fragment.Length!=0)throw new ArgumentException("Invalid mesh server URI.",nameof(server));
        _http=http??throw new ArgumentNullException(nameof(http));_token=token??throw new ArgumentNullException(nameof(token));
        _server=new Uri(server.GetLeftPart(UriPartial.Path).TrimEnd('/')+"/");_clock=clock??TimeProvider.System;_timeout=timeout??TimeSpan.FromSeconds(10);
        if(_timeout<=TimeSpan.Zero||_timeout>TimeSpan.FromSeconds(10))throw new ArgumentOutOfRangeException(nameof(timeout));
    }
    public async Task<MeshRoomSnapshot> ReadAsync(Guid conversation,CancellationToken token=default)
    {
        var json=await SendAsync(conversation,null,null,true,token).ConfigureAwait(false);
        if(!MeshRoomWire.IsObject(json)||!json.TryGetProperty("version",out var version)||version.ValueKind!=JsonValueKind.Number||!version.TryGetInt32(out int v)||v!=1
            ||!json.TryGetProperty("maxParticipants",out var max)||max.ValueKind!=JsonValueKind.Number||!max.TryGetInt32(out int m)||m!=4||!json.TryGetProperty("room",out var room))throw MeshRoomWire.Invalid();
        return MeshRoomWire.Parse(room);
    }
    public async Task<MeshRoomSnapshot> JoinAsync(Guid conversation,Guid? room,CancellationToken token=default)
    {
        if(room==Guid.Empty)throw new ArgumentException("Invalid mesh room.",nameof(room));
        return MeshRoomWire.Parse(await SendAsync(conversation,"join",new{version=1,roomId=room},true,token).ConfigureAwait(false));
    }
    public async Task<MeshRoomSnapshot> LeaveAsync(Guid conversation,Guid room,CancellationToken token=default)
    {
        MeshRoomWire.RequireId(room);
        return MeshRoomWire.Parse(await SendAsync(conversation,"leave",new{version=1,roomId=room},true,token).ConfigureAwait(false));
    }
    public async Task SignalAsync(Guid conversation,MeshOutgoingSignal signal,CancellationToken token=default)
    {
        MeshRoomWire.RequireId(signal.RoomId);MeshRoomWire.RequireId(signal.EdgeId);MeshRoomWire.RequireId(signal.To);
        bool ice=signal.Action=="ice";
        if(signal.Action is not ("ice" or "offer" or "answer")||string.IsNullOrWhiteSpace(ice?signal.Candidate:signal.Sdp)
            ||(ice?signal.Sdp is not null:signal.Candidate is not null)||new[]{signal.Sdp,signal.Candidate}.Any(s=>s is not null&&Encoding.UTF8.GetByteCount(s)>64*1024))throw new ArgumentException("Invalid mesh signal.",nameof(signal));
        var body=new{version=1,roomId=signal.RoomId,edgeId=signal.EdgeId,to=signal.To,action=signal.Action,sdp=signal.Sdp,candidate=signal.Candidate};
        if(JsonSerializer.SerializeToUtf8Bytes(body).Length>256*1024-256)throw new ArgumentException("Mesh signal exceeds its byte limit.",nameof(signal));
        await SendAsync(conversation,"signal",body,false,token).ConfigureAwait(false);
    }
    private async Task<JsonElement> SendAsync(Guid conversation,string? action,object? body,bool readRoom,CancellationToken token)
    {
        MeshRoomWire.RequireId(conversation);token.ThrowIfCancellationRequested();using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(_timeout);
        try
        {
            string? auth=await _token(deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);
            if(string.IsNullOrWhiteSpace(auth)||auth.Any(char.IsControl))throw new InvalidOperationException("No cached authentication token is available.");
            using var request=new HttpRequestMessage(action is null?HttpMethod.Get:HttpMethod.Post,new Uri(_server,$"conversations/{conversation:D}/mesh"+(action is null?"":"/"+action)));
            request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",auth);if(body is not null)request.Content=JsonContent.Create(body);
            Task<HttpResponseMessage> headers=_http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,deadline.Token);
            HttpResponseMessage received;
            try {received=await headers.WaitAsync(deadline.Token).ConfigureAwait(false);}
            catch {_=DisposeLateResponseAsync(headers);throw;}
            using var response=received;
            if(!response.IsSuccessStatusCode)
            {
                string? code=null;TimeSpan? retry=null;
                if(response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.TooManyRequests)
                {
                    var error=await ReadJsonAsync(response,4096,deadline.Token).ConfigureAwait(false);
                    if(MeshRoomWire.IsObject(error)&&error.TryGetProperty("error",out var value)&&value.ValueKind==JsonValueKind.String&&Codes.Contains(value.GetString()!))code=value.GetString();
                }
                if(response.StatusCode==HttpStatusCode.TooManyRequests&&response.Headers.RetryAfter is { } header)
                {
                    double seconds=header.Delta?.TotalSeconds??(header.Date-_clock.GetUtcNow())?.TotalSeconds??1;
                    retry=TimeSpan.FromSeconds(Math.Clamp(seconds,1,300));
                }
                throw new MeshRequestException(response.StatusCode,code,retry);
            }
            return readRoom?await ReadJsonAsync(response,16*1024,deadline.Token).ConfigureAwait(false):default;
        }
        catch(OperationCanceledException)when(!token.IsCancellationRequested){throw new TimeoutException("The mesh request timed out.");}
        catch(JsonException){throw MeshRoomWire.Invalid();}
    }
    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response,int cap,CancellationToken token)
    {
        Task<JsonElement> pending=BoundedJsonResponse.ReadAsync<JsonElement>(response,cap,token);
        try {return await pending.WaitAsync(token).ConfigureAwait(false);}
        catch {_=pending.ContinueWith(t=>{_ = t.Exception;},CancellationToken.None,TaskContinuationOptions.OnlyOnFaulted,TaskScheduler.Default);throw;}
    }
    private static async Task DisposeLateResponseAsync(Task<HttpResponseMessage> pending)
    {try{using var response=await pending.ConfigureAwait(false);}catch{ }}
}
internal static class MeshRoomWire
{
    internal static InvalidDataException Invalid()=>new("Invalid mesh response.");
    internal static void RequireId(Guid id){if(id==Guid.Empty)throw new ArgumentException("A nonempty mesh id is required.");}
    internal static bool IsObject(JsonElement value)=>value.ValueKind==JsonValueKind.Object&&value.EnumerateObject().Select(p=>p.Name).Distinct(StringComparer.Ordinal).Count()==value.EnumerateObject().Count();
    private static Guid Id(JsonElement value)
    {
        if(value.ValueKind!=JsonValueKind.String||!(Guid.TryParseExact(value.GetString(),"D",out var id)||Guid.TryParseExact(value.GetString(),"N",out id))||id==Guid.Empty)throw Invalid();return id;
    }
    internal static MeshRoomSnapshot Parse(JsonElement value)
    {
        if(!IsObject(value)||!value.TryGetProperty("roomId",out var room)||!value.TryGetProperty("participants",out var users)||users.ValueKind!=JsonValueKind.Array||users.GetArrayLength()>4
            ||!value.TryGetProperty("edges",out var edges)||edges.ValueKind!=JsonValueKind.Array||edges.GetArrayLength()>6)throw Invalid();
        Guid? roomId=room.ValueKind==JsonValueKind.Null?null:Id(room);var participants=users.EnumerateArray().Select(Id).ToArray();
        if(participants.Distinct().Count()!=participants.Length||(roomId is null)!=(participants.Length==0)||edges.GetArrayLength()!=participants.Length*(participants.Length-1)/2)throw Invalid();
        var parsed=new List<MeshPeerEdge>();var pairs=new HashSet<(Guid,Guid)>();var ids=new HashSet<Guid>();
        foreach(var edge in edges.EnumerateArray())
        {
            if(!IsObject(edge)||!edge.TryGetProperty("id",out var id)||!edge.TryGetProperty("offerer",out var offerer)||!edge.TryGetProperty("answerer",out var answerer))throw Invalid();
            var item=new MeshPeerEdge(Id(id),Id(offerer),Id(answerer));
            if(!ids.Add(item.Id)||!participants.Contains(item.Offerer)||!participants.Contains(item.Answerer)||string.CompareOrdinal(item.Offerer.ToString("N"),item.Answerer.ToString("N"))>=0||!pairs.Add((item.Offerer,item.Answerer)))throw Invalid();parsed.Add(item);
        }
        return new(roomId,Array.AsReadOnly(participants),parsed.AsReadOnly());
    }
}
