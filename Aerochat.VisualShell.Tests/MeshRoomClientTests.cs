using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aerochat.Connectivity.Rtc;

namespace Aerochat.VisualShell.Tests;

public sealed class MeshRoomClientTests
{
    private static readonly Guid A=Guid.Parse("00000000-0000-0000-0000-000000000001"),B=Guid.Parse("00000000-0000-0000-0000-000000000002"),Room=Guid.NewGuid(),Edge=Guid.NewGuid(),Conversation=Guid.NewGuid();
    private static string Snapshot=>JsonSerializer.Serialize(new{roomId=Room,participants=new[]{A,B},edges=new[]{new{id=Edge,offerer=A,answerer=B}}});
    private static string Capability=>"{\"version\":1,\"maxParticipants\":4,\"room\":"+Snapshot+"}";
    private sealed class Handler:HttpMessageHandler
    {
        internal int Requests;internal Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> Reply=(r,t)=>Task.FromResult(Ok(Capability));
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken t){Requests++;return Reply(r,t);}
    }
    private static HttpResponseMessage Ok(string body)=>new(HttpStatusCode.OK){Content=new StringContent(body,Encoding.UTF8,"application/json")};
    private static MeshRoomClient Client(HttpClient http,TimeSpan? timeout=null)=>new(http,new Uri("https://chat.invalid/prefix"),_=>Task.FromResult<string?>("fixture-token"),timeout:timeout);
    [Test]
    public async Task Routes_bodies_bearer_and_bare_snapshot_replies_match_the_server()
    {
        var h=new Handler();using var http=new HttpClient(h);int tokens=0;var client=new MeshRoomClient(http,new Uri("https://chat.invalid/prefix"),_=>Task.FromResult<string?>("fixture-"+(++tokens)));
        h.Reply=async(r,t)=>
        {
            Assert.That(r.Headers.Authorization!.Scheme,Is.EqualTo("Bearer"));Assert.That(r.Headers.Authorization.Parameter,Is.EqualTo("fixture-"+tokens));Assert.That(r.RequestUri!.AbsolutePath,Does.StartWith($"/prefix/conversations/{Conversation:D}/mesh"));
            if(r.Method==HttpMethod.Get)return Ok(Capability);
            using var body=JsonDocument.Parse(await r.Content!.ReadAsStringAsync(t));Assert.That(body.RootElement.GetProperty("version").GetInt32(),Is.EqualTo(1));
            if(r.RequestUri.AbsolutePath.EndsWith("/join"))Assert.That(body.RootElement.GetProperty("roomId").ValueKind,Is.EqualTo(JsonValueKind.Null));
            else Assert.That(body.RootElement.GetProperty("roomId").GetGuid(),Is.EqualTo(Room));return Ok(Snapshot);
        };
        var read=await client.ReadAsync(Conversation);var join=await client.JoinAsync(Conversation,null);var leave=await client.LeaveAsync(Conversation,Room);
        Assert.That(read.RoomId,Is.EqualTo(Room));Assert.That(join.Edges.Single().Offerer,Is.EqualTo(A));Assert.That(leave.Participants,Has.Count.EqualTo(2));Assert.That(tokens,Is.EqualTo(3));Assert.That(http.DefaultRequestHeaders.Authorization,Is.Null);
        Assert.Throws<NotSupportedException>(()=>((IList<Guid>)read.Participants).Add(Guid.NewGuid()));
    }
    [TestCase("version")][TestCase("version-type")][TestCase("capacity")][TestCase("duplicate-user")][TestCase("reversed-edge")][TestCase("unknown-user")][TestCase("zero-room")][TestCase("missing-edge")][TestCase("extra-edge")][TestCase("null-room")][TestCase("braced-id")]
    public void Invalid_capabilities_and_roster_graphs_are_rejected(string mode)
    {
        var json=JsonNode.Parse(Capability)!.AsObject();var room=json["room"]!;
        switch(mode)
        {
            case "version":json["version"]=2;break;case "version-type":json["version"]="1";break;case "capacity":json["maxParticipants"]=5;break;
            case "duplicate-user":room["participants"]![1]=A.ToString();break;
            case "reversed-edge":room["edges"]![0]!["offerer"]=B.ToString();room["edges"]![0]!["answerer"]=A.ToString();break;
            case "unknown-user":room["edges"]![0]!["answerer"]=Guid.NewGuid().ToString();break;
            case "zero-room":room["roomId"]=Guid.Empty.ToString();break;
            case "missing-edge":room["edges"]=new JsonArray();break;
            case "extra-edge":room["edges"]!.AsArray().Add(room["edges"]![0]!.DeepClone());break;
            case "null-room":room["roomId"]=null;break;
            case "braced-id":room["participants"]![0]=A.ToString("B");break;
        }
        var h=new Handler{Reply=(r,t)=>Task.FromResult(Ok(json.ToJsonString()))};using var http=new HttpClient(h);var client=Client(http);Assert.ThrowsAsync<InvalidDataException>(async()=>await client.ReadAsync(Conversation));
    }
    [Test]
    public async Task Empty_room_and_N_guid_forms_are_accepted_but_duplicate_properties_are_not()
    {
        var h=new Handler{Reply=(r,t)=>Task.FromResult(Ok("{\"version\":1,\"maxParticipants\":4,\"room\":{\"roomId\":null,\"participants\":[],\"edges\":[]}}"))};using var http=new HttpClient(h);var client=Client(http);Assert.That((await client.ReadAsync(Conversation)).RoomId,Is.Null);
        h.Reply=(r,t)=>Task.FromResult(Ok(Capability.Replace(A.ToString(),A.ToString("N"))));Assert.That((await client.ReadAsync(Conversation)).Participants[0],Is.EqualTo(A));
        h.Reply=(r,t)=>Task.FromResult(Ok(Capability.Replace("\"version\":1","\"version\":1,\"version\":1")));Assert.ThrowsAsync<InvalidDataException>(async()=>await client.ReadAsync(Conversation));
    }
    private sealed class Bomb:HttpContent
    {
        internal bool Disposed;internal readonly TaskCompletionSource Closed=new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override bool TryComputeLength(out long length){length=0;return false;}
        protected override Task SerializeToStreamAsync(Stream stream,TransportContext? context)=>throw new InvalidOperationException("Response body must not be read.");
        protected override void Dispose(bool disposing){Disposed=true;base.Dispose(disposing);Closed.TrySetResult();}
    }
    [Test]
    public async Task Signal_reads_headers_only_disposes_response_and_omits_sender_identity()
    {
        var bomb=new Bomb();var h=new Handler{Reply=async(r,t)=>
        {
            Assert.That(r.RequestUri!.AbsolutePath,Does.EndWith("/mesh/signal"));using var json=JsonDocument.Parse(await r.Content!.ReadAsStringAsync(t));var d=json.RootElement;
            Assert.That(d.TryGetProperty("from",out _),Is.False);Assert.That(d.GetProperty("to").GetGuid(),Is.EqualTo(B));Assert.That(d.GetProperty("edgeId").GetGuid(),Is.EqualTo(Edge));Assert.That(d.GetProperty("version").GetInt32(),Is.EqualTo(1));return new(HttpStatusCode.OK){Content=bomb};
        }};using var http=new HttpClient(h);await Client(http).SignalAsync(Conversation,new(Room,Edge,B,"ice",Candidate:"fixture-candidate"));Assert.That(bomb.Disposed,Is.True);
        Assert.That(new MeshOutgoingSignal(Room,Edge,B,"offer",Sdp:"private-sdp").ToString(),Does.Not.Contain("private-sdp"));
    }
    [Test]
    public void Invalid_ids_and_overlarge_signals_never_send()
    {
        var h=new Handler();using var http=new HttpClient(h);var client=Client(http);
        Assert.ThrowsAsync<ArgumentException>(async()=>await client.ReadAsync(Guid.Empty));Assert.ThrowsAsync<ArgumentException>(async()=>await client.JoinAsync(Conversation,Guid.Empty));
        foreach(var signal in new[]{new MeshOutgoingSignal(Room,Edge,B,"ice",Candidate:new string('a',65537)),new(Room,Edge,B,"ice",Candidate:new string('\u0001',60000)),new(Room,Edge,B,"unknown",Sdp:"fixture"),new(Room,Guid.Empty,B,"offer",Sdp:"fixture")})Assert.ThrowsAsync<ArgumentException>(async()=>await client.SignalAsync(Conversation,signal));
        Assert.That(h.Requests,Is.Zero);
    }
    [TestCase("http://localhost/")][TestCase("http://127.0.0.1.evil.invalid/")][TestCase("https://user:pass@chat.invalid/")][TestCase("https://chat.invalid/?token=fixture")][TestCase("https://chat.invalid/#fragment")]
    public void Unsafe_server_bases_are_rejected(string server)
    {using var http=new HttpClient(new Handler());Assert.Throws<ArgumentException>(()=>new MeshRoomClient(http,new Uri(server),_=>Task.FromResult<string?>("fixture")));}
    [TestCase(false)][TestCase(true)]
    public void Request_deadline_and_caller_cancellation_are_distinct(bool cancelled)
    {
        var h=new Handler{Reply=async(r,t)=>{await Task.Delay(Timeout.Infinite,t);return Ok(Capability);}};using var http=new HttpClient(h);var client=Client(http,TimeSpan.FromMilliseconds(100));using var stop=new CancellationTokenSource();
        if(cancelled){stop.Cancel();Assert.ThrowsAsync<OperationCanceledException>(async()=>await client.ReadAsync(Conversation,stop.Token));Assert.That(h.Requests,Is.Zero);}
        else Assert.ThrowsAsync<TimeoutException>(async()=>await client.ReadAsync(Conversation));
    }
    [Test]
    public void Errors_keep_only_known_codes_clamp_retry_and_do_not_retry_automatically()
    {
        var h=new Handler{Reply=(r,t)=>{var response=new HttpResponseMessage(HttpStatusCode.TooManyRequests){Content=new StringContent("{\"error\":\"rate_limited\"}")};response.Headers.RetryAfter=new RetryConditionHeaderValue(TimeSpan.FromDays(100));return Task.FromResult(response);}};
        using var http=new HttpClient(h);var client=Client(http);var error=Assert.ThrowsAsync<MeshRequestException>(async()=>await client.ReadAsync(Conversation))!;Assert.That(error.Code,Is.EqualTo("rate_limited"));Assert.That(error.RetryAfter,Is.EqualTo(TimeSpan.FromMinutes(5)));Assert.That(h.Requests,Is.EqualTo(1));
        h.Reply=(r,t)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict){Content=new StringContent("{\"error\":\"private-sdp\"}")});error=Assert.ThrowsAsync<MeshRequestException>(async()=>await client.ReadAsync(Conversation))!;Assert.That(error.Code,Is.Null);Assert.That(error.ToString(),Does.Not.Contain("private-sdp"));
    }
    [Test]
    public void Room_and_error_bodies_have_separate_byte_limits()
    {
        var h=new Handler{Reply=(r,t)=>Task.FromResult(Ok(Capability.PadRight(16385)))};using var http=new HttpClient(h);var client=Client(http);Assert.ThrowsAsync<InvalidDataException>(async()=>await client.ReadAsync(Conversation));
        h.Reply=(r,t)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict){Content=new StringContent("{\"error\":\"mesh_stale_room\"}".PadRight(4097))});
        var error=Assert.ThrowsAsync<MeshRequestException>(async()=>await client.ReadAsync(Conversation))!;Assert.That(error.StatusCode,Is.EqualTo(HttpStatusCode.Conflict));Assert.That(error.Code,Is.Null);
    }
    private sealed class CountingStream(byte[] bytes):MemoryStream(bytes)
    {
        internal int ReadCount;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken token=default)
        {int count=await base.ReadAsync(buffer,token);ReadCount+=count;return count;}
    }
    private sealed class UnknownBody:HttpContent
    {
        internal readonly CountingStream Data=new(Encoding.UTF8.GetBytes(Capability.PadRight(16385)));
        protected override bool TryComputeLength(out long length){length=0;return false;}
        protected override Task<Stream> CreateContentReadStreamAsync()=>Task.FromResult<Stream>(Data);
        protected override Task SerializeToStreamAsync(Stream stream,TransportContext? context)=>throw new InvalidOperationException("Streaming path required.");
    }
    [Test]
    public void Unknown_length_room_response_reads_only_the_cap_plus_one()
    {
        var body=new UnknownBody();var h=new Handler{Reply=(r,t)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=body})};using var http=new HttpClient(h);
        Assert.ThrowsAsync<InvalidDataException>(async()=>await Client(http).ReadAsync(Conversation));Assert.That(body.Data.ReadCount,Is.EqualTo(16385));
    }
    [Test]
    public async Task Deadline_bounds_a_header_handler_that_ignores_cancellation_and_disposes_late_reply()
    {
        var reply=new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);var bomb=new Bomb();var h=new Handler{Reply=(r,t)=>reply.Task};using var http=new HttpClient(h);
        var request=Client(http,TimeSpan.FromMilliseconds(100)).ReadAsync(Conversation);
        try{Assert.ThrowsAsync<TimeoutException>(async()=>await request.WaitAsync(TimeSpan.FromSeconds(3)));Assert.That(request.IsCompleted,Is.True);}
        finally {reply.TrySetResult(new(HttpStatusCode.OK){Content=bomb});await bomb.Closed.Task.WaitAsync(TimeSpan.FromSeconds(3));try{await request.WaitAsync(TimeSpan.FromSeconds(3));}catch{ }}
    }
    [Test]
    public async Task Caller_cancellation_during_request_stays_cancellation()
    {
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var h=new Handler{Reply=async(r,t)=>{entered.TrySetResult();await Task.Delay(Timeout.Infinite,t);return Ok(Capability);}};
        using var http=new HttpClient(h);using var stop=new CancellationTokenSource();Task request=Client(http).ReadAsync(Conversation,stop.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));stop.Cancel();Assert.CatchAsync<OperationCanceledException>(async()=>await request.WaitAsync(TimeSpan.FromSeconds(3)));
    }
    [TestCase(false)][TestCase(true)]
    public void Retry_dates_are_clamped_to_one_second_or_five_minutes(bool future)
    {
        var h=new Handler{Reply=(r,t)=>{var response=new HttpResponseMessage(HttpStatusCode.TooManyRequests){Content=new StringContent("{\"error\":\"rate_limited\"}")};response.Headers.RetryAfter=new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddDays(future?100:-100));return Task.FromResult(response);}};
        using var http=new HttpClient(h);var error=Assert.ThrowsAsync<MeshRequestException>(async()=>await Client(http).ReadAsync(Conversation))!;Assert.That(error.RetryAfter,Is.EqualTo(TimeSpan.FromSeconds(future?300:1)));
    }
    [TestCase(HttpStatusCode.Conflict)][TestCase(HttpStatusCode.TooManyRequests)]
    public void Malformed_error_bodies_still_report_status_and_clamped_retry(HttpStatusCode status)
    {
        var h=new Handler{Reply=(r,t)=>{var response=new HttpResponseMessage(status){Content=new StringContent("{\"error\":")};response.Headers.RetryAfter=new RetryConditionHeaderValue(TimeSpan.FromDays(1));return Task.FromResult(response);}};
        using var http=new HttpClient(h);var error=Assert.ThrowsAsync<MeshRequestException>(async()=>await Client(http).ReadAsync(Conversation))!;
        Assert.That(error.StatusCode,Is.EqualTo(status));Assert.That(error.Code,Is.Null);
        Assert.That(error.RetryAfter,Is.EqualTo(status==HttpStatusCode.TooManyRequests?TimeSpan.FromMinutes(5):null));
    }
    [Test]
    public void Signal_leaves_envelope_headroom_and_rejects_a_null_signal()
    {
        var h=new Handler();using var http=new HttpClient(h);var client=Client(http);
        Assert.ThrowsAsync<ArgumentNullException>(async()=>await client.SignalAsync(Conversation,null!));
        int Size(string candidate)=>JsonSerializer.SerializeToUtf8Bytes(new{version=1,roomId=Room,edgeId=Edge,to=B,action="ice",sdp=(string?)null,candidate}).Length;
        string candidate=new string('\u0001',(256*1024-300-Size(""))/6);
        candidate+=new string('a',256*1024-300-Size(candidate));
        Assert.That(Size(candidate),Is.EqualTo(256*1024-300));
        Assert.ThrowsAsync<ArgumentException>(async()=>await client.SignalAsync(Conversation,new(Room,Edge,B,"ice",Candidate:candidate)));
        Assert.That(h.Requests,Is.Zero);
    }
    [TestCase("join-other")][TestCase("join-empty")][TestCase("leave-other")]
    public void Join_and_leave_replies_must_name_the_requested_room(string mode)
    {
        string other=JsonSerializer.Serialize(new{roomId=Guid.NewGuid(),participants=new[]{A,B},edges=new[]{new{id=Edge,offerer=A,answerer=B}}});
        string reply=mode=="join-empty"?"{\"roomId\":null,\"participants\":[],\"edges\":[]}":other;
        var h=new Handler{Reply=(r,t)=>Task.FromResult(Ok(reply))};using var http=new HttpClient(h);var client=Client(http);
        if(mode=="leave-other")Assert.ThrowsAsync<InvalidDataException>(async()=>await client.LeaveAsync(Conversation,Room));
        else Assert.ThrowsAsync<InvalidDataException>(async()=>await client.JoinAsync(Conversation,mode=="join-empty"?null:Room));
    }
    [Test]
    public async Task Leaving_the_last_seat_may_return_a_closed_room()
    {
        var h=new Handler{Reply=(r,t)=>Task.FromResult(Ok("{\"roomId\":null,\"participants\":[],\"edges\":[]}"))};using var http=new HttpClient(h);
        Assert.That((await Client(http).LeaveAsync(Conversation,Room)).RoomId,Is.Null);
    }
}
