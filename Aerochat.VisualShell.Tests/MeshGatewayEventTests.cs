using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aerochat.Connectivity;
using Aerochat.Connectivity.Rtc;

namespace Aerochat.VisualShell.Tests;

public sealed class MeshGatewayEventTests
{
    private static readonly Guid A=Guid.Parse("00000000-0000-0000-0000-000000000001"),B=Guid.Parse("00000000-0000-0000-0000-000000000002"),Conversation=Guid.NewGuid(),Room=Guid.NewGuid(),Edge=Guid.NewGuid();

    private static JsonObject SignalData(string action="offer")=>new()
    {
        ["conversationId"]=Conversation.ToString(),["roomId"]=Room.ToString(),["edgeId"]=Edge.ToString(),["from"]=A.ToString(),["to"]=B.ToString(),
        ["action"]=action,["sdp"]=action=="ice"?null:"fixture-sdp",["candidate"]=action=="ice"?"fixture-candidate":null
    };
    private static JsonObject RosterData()=>new()
    {
        ["conversationId"]=Conversation.ToString(),
        ["room"]=new JsonObject{["roomId"]=Room.ToString(),["participants"]=new JsonArray(A.ToString(),B.ToString()),
            ["edges"]=new JsonArray(new JsonObject{["id"]=Edge.ToString(),["offerer"]=A.ToString(),["answerer"]=B.ToString()})}
    };
    private static string Frame(string type,string id,JsonNode data)=>new JsonObject{["t"]=type,["eventId"]=id,["d"]=data}.ToJsonString();

    [TestCase("valid",true)][TestCase("ice",true)][TestCase("n-form",true)]
    [TestCase("action",false)][TestCase("self",false)][TestCase("ice-with-sdp",false)][TestCase("offer-without-sdp",false)]
    [TestCase("oversized",false)][TestCase("braced",false)][TestCase("empty-id",false)][TestCase("duplicate",false)][TestCase("sdp-type",false)]
    public void Mesh_signal_parser_accepts_only_well_formed_addressed_signals(string mode,bool accepted)
    {
        var data=SignalData(mode=="ice"?"ice":"offer");
        switch(mode)
        {
            case "n-form":data["from"]=A.ToString("N");break;
            case "action":data["action"]="renegotiate";break;
            case "self":data["to"]=A.ToString();break;
            case "ice-with-sdp":data["action"]="ice";data["candidate"]="fixture-candidate";break;
            case "offer-without-sdp":data["sdp"]=null;break;
            case "oversized":data["sdp"]=new string('a',64*1024+1);break;
            case "braced":data["edgeId"]=Edge.ToString("B");break;
            case "empty-id":data["roomId"]=Guid.Empty.ToString();break;
            case "sdp-type":data["sdp"]=5;break;
        }
        string json=data.ToJsonString();
        if(mode=="duplicate")json=json.Replace("\"action\":","\"action\":\"offer\",\"action\":");
        using var document=JsonDocument.Parse(json);
        Assert.That(MeshGatewayProtocol.TryParseSignal(document.RootElement,out var signal),Is.EqualTo(accepted));
        if(accepted)Assert.That(signal!.From,Is.EqualTo(A));
    }

    [Test]
    public void Mesh_roster_parser_reuses_room_validation_and_signals_redact_payloads()
    {
        using var valid=JsonDocument.Parse(RosterData().ToJsonString());
        Assert.That(MeshGatewayProtocol.TryParseRoster(valid.RootElement,out var roster),Is.True);
        Assert.That(roster!.Room.Edges.Single().Id,Is.EqualTo(Edge));
        var reversed=RosterData();reversed["room"]!["edges"]![0]!["offerer"]=B.ToString();reversed["room"]!["edges"]![0]!["answerer"]=A.ToString();
        using var invalid=JsonDocument.Parse(reversed.ToJsonString());
        Assert.That(MeshGatewayProtocol.TryParseRoster(invalid.RootElement,out _),Is.False);
        Assert.That(new MeshSignalEvent(Conversation,Room,Edge,A,B,"offer","private-sdp",null).ToString(),Does.Not.Contain("private-sdp"));
    }

    [Test]
    public async Task Gateway_client_delivers_only_valid_mesh_events_to_mesh_subscribers()
    {
        var socket=new FrameSocket(Frame("mesh.signal","gateway:1",SignalData()),Frame("mesh.signal","gateway:2",SignalData("renegotiate")),Frame("mesh.roster","gateway:3",RosterData()));
        var signals=new List<MeshSignalEvent>();var rosters=new List<MeshRosterEvent>();
        var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var client=new GatewayClient(()=>socket,jitter:null,delay:(_,_)=>Task.CompletedTask);
        IMeshEventSource source=client;
        source.MeshSignalReceived+=(_,signal)=>signals.Add(signal);
        source.MeshRosterReceived+=(_,update)=>{rosters.Add(update);done.TrySetResult();};
        try
        {
            await client.ConnectAsync(new Uri("wss://gateway.example"),"token").WaitAsync(TimeSpan.FromSeconds(5));
            await done.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Multiple(()=>
            {
                Assert.That(signals,Has.Count.EqualTo(1));
                Assert.That(signals[0].From,Is.EqualTo(A));Assert.That(signals[0].To,Is.EqualTo(B));
                Assert.That(signals[0].Action,Is.EqualTo("offer"));Assert.That(signals[0].Sdp,Is.EqualTo("fixture-sdp"));
                Assert.That(rosters.Single().Room.RoomId,Is.EqualTo(Room));Assert.That(rosters.Single().Room.Participants,Has.Count.EqualTo(2));
                Assert.That(client.LastEventId,Is.EqualTo("gateway:3"));
            });
        }
        finally
        {
            socket.Abort();
        }
    }

    [Test]
    public async Task A_frame_that_throws_while_processing_is_dropped_and_the_loop_survives()
    {
        // A lone surrogate escape parses, but JsonElement.GetString then throws InvalidOperationException.
        string poisoned=Frame("mesh.signal","gateway:1",SignalData()).Replace("fixture-sdp","\\ud800");
        var socket=new FrameSocket(poisoned,Frame("mesh.roster","gateway:2",RosterData()));
        var delivered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var client=new GatewayClient(()=>socket,jitter:null,delay:(_,_)=>Task.CompletedTask);
        IMeshEventSource source=client;
        source.MeshRosterReceived+=(_,_)=>delivered.TrySetResult();
        try
        {
            await client.ConnectAsync(new Uri("wss://gateway.example"),"token").WaitAsync(TimeSpan.FromSeconds(5));
            await delivered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(client.LastEventId,Is.EqualTo("gateway:2"));
        }
        finally
        {
            socket.Abort();
        }
    }

    [Test]
    public async Task A_rejected_roster_asks_for_a_resync()
    {
        var invalid=RosterData();invalid["room"]!["edges"]=new JsonArray();
        var socket=new FrameSocket(Frame("mesh.roster","gateway:1",invalid));
        var resync=new TaskCompletionSource<MeshResyncReason>(TaskCreationOptions.RunContinuationsAsynchronously);
        var rosters=0;
        await using var client=new GatewayClient(()=>socket,jitter:null,delay:(_,_)=>Task.CompletedTask);
        IMeshEventSource source=client;
        source.MeshRosterReceived+=(_,_)=>rosters++;
        source.MeshResyncRequired+=(_,e)=>resync.TrySetResult(e.Reason);
        try
        {
            await client.ConnectAsync(new Uri("wss://gateway.example"),"token").WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(await resync.Task.WaitAsync(TimeSpan.FromSeconds(5)),Is.EqualTo(MeshResyncReason.RosterRejected));
            Assert.That(rosters,Is.Zero);
        }
        finally
        {
            socket.Abort();
        }
    }

    [Test]
    public async Task A_reconnect_asks_for_a_resync()
    {
        var sockets=new Queue<IGatewaySocket>(new IGatewaySocket[]{new ClosedSocket(),new FrameSocket()});
        var resync=new TaskCompletionSource<MeshResyncReason>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var client=new GatewayClient(()=>sockets.Count>0?sockets.Dequeue():new FrameSocket(),jitter:null,delay:(_,_)=>Task.CompletedTask);
        IMeshEventSource source=client;
        source.MeshResyncRequired+=(_,e)=>resync.TrySetResult(e.Reason);
        await client.ConnectAsync(new Uri("wss://gateway.example"),"token").WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(await resync.Task.WaitAsync(TimeSpan.FromSeconds(5)),Is.EqualTo(MeshResyncReason.Reconnected));
    }

    private sealed class FrameSocket(params string[] frames):IGatewaySocket
    {
        private readonly TaskCompletionSource _closed=new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _next;
        public WebSocketState State=>WebSocketState.Open;
        public Task ConnectAsync(Uri uri,string bearerToken,CancellationToken cancellationToken)=>Task.CompletedTask;
        public async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer,CancellationToken cancellationToken)
        {
            int index=Interlocked.Increment(ref _next)-1;
            if(index<frames.Length)
            {
                byte[] payload=Encoding.UTF8.GetBytes(frames[index]);
                payload.CopyTo(buffer.AsSpan());
                return new(payload.Length,WebSocketMessageType.Text,true);
            }
            await _closed.Task.ConfigureAwait(false);
            return new(0,WebSocketMessageType.Close,true);
        }
        public void Abort()=>_closed.TrySetResult();
        public void Dispose()=>_closed.TrySetResult();
    }

    // The server closes the connection at once, so the client must reconnect.
    private sealed class ClosedSocket:IGatewaySocket
    {
        public WebSocketState State=>WebSocketState.Open;
        public Task ConnectAsync(Uri uri,string bearerToken,CancellationToken cancellationToken)=>Task.CompletedTask;
        public Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer,CancellationToken cancellationToken)=>
            Task.FromResult(new WebSocketReceiveResult(0,WebSocketMessageType.Close,true));
        public void Abort(){}
        public void Dispose(){}
    }
}
