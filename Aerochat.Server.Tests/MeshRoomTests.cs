using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Aerochat.Server.Auth;
using Aerochat.Server.Calls;
using Aerochat.Server.Data.Entities;
using Aerochat.Server.Gateway;
using Microsoft.Extensions.DependencyInjection;

namespace Aerochat.Server.Tests;

public sealed class MeshRoomTests
{
    private static Guid User(int n)=>Guid.Parse(n.ToString("x32"));
    private static MeshRooms Registry(int n=8)
    {var r=new MeshRooms();for(int i=1;i<=n;i++)r.SetPresence(User(i),true);return r;}
    [Test]
    public void Four_people_including_self_have_six_links_and_fifth_is_rejected()
    {
        var r=Registry();var c=Guid.NewGuid();var first=r.Join(c,User(1),null).Snapshot;
        for(int i=2;i<=4;i++)Assert.That(r.Join(c,User(i),first.RoomId).Error,Is.Null);
        var full=r.Read(c);Assert.That(full.Participants,Has.Length.EqualTo(4));Assert.That(full.Edges,Has.Length.EqualTo(6));
        Assert.That(r.Join(c,User(5),first.RoomId).Error,Is.EqualTo("mesh_full"));
        Assert.That(r.Join(c,User(1),first.RoomId).Error,Is.Null);
    }
    [Test]
    public async Task Concurrent_final_slot_is_atomic()
    {
        var r=Registry();var c=Guid.NewGuid();var id=r.Join(c,User(1),null).Snapshot.RoomId;
        r.Join(c,User(2),id);r.Join(c,User(3),id);
        using var barrier=new Barrier(5);
        var tasks=Enumerable.Range(4,5).Select(i=>Task.Factory.StartNew(()=>{barrier.SignalAndWait();return r.Join(c,User(i),id);},CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default)).ToArray();
        var results=await Task.WhenAll(tasks);Assert.That(results.Count(x=>x.Error is null),Is.EqualTo(1));Assert.That(r.Read(c).Participants,Has.Length.EqualTo(4));
    }
    [Test]
    public void Room_and_edge_generations_reject_replacement_and_offer_owner_is_canonical()
    {
        var r=Registry();var c=Guid.NewGuid();var id=r.Join(c,User(2),null).Snapshot.RoomId!.Value;
        var edge=r.Join(c,User(1),id).Snapshot.Edges.Single();Assert.That(edge.Offerer,Is.EqualTo(User(1)));
        Assert.That(r.Signal(c,User(2),User(1),id,edge.Id,"offer"),Is.EqualTo("mesh_invalid_state"));
        Assert.That(r.Signal(c,User(1),User(2),id,edge.Id,"offer"),Is.Null);
        Assert.That(r.Signal(c,User(2),User(1),id,edge.Id,"answer"),Is.Null);
        Assert.That(r.Signal(c,User(1),User(2),id,edge.Id,"ice"),Is.Null);
        r.Leave(c,User(1),id);var replacement=r.Join(c,User(1),id).Snapshot.Edges.Single();Assert.That(replacement.Id,Is.Not.EqualTo(edge.Id));
        Assert.That(r.Signal(c,User(1),User(2),id,edge.Id,"ice"),Is.EqualTo("mesh_stale_edge"));
        r.SetPresence(User(1),false);r.SetPresence(User(2),false);r.SetPresence(User(1),true);
        var next=r.Join(c,User(1),null).Snapshot.RoomId;Assert.That(next,Is.Not.EqualTo(id));
        Assert.That(r.Leave(c,User(1),id).Error,Is.EqualTo("mesh_stale_room"));
    }
    [Test]
    public void Signals_cannot_cross_edges_or_address_self_or_unjoined_users()
    {
        var r=Registry();var c=Guid.NewGuid();var id=r.Join(c,User(1),null).Snapshot.RoomId!.Value;
        var edge=r.Join(c,User(2),id).Snapshot.Edges.Single();r.Join(c,User(3),id);
        Assert.That(r.Signal(c,User(1),User(3),id,edge.Id,"offer"),Is.EqualTo("mesh_stale_edge"));
        Assert.That(r.Signal(c,User(1),User(1),id,edge.Id,"offer"),Is.EqualTo("mesh_not_participant"));
        Assert.That(r.Signal(c,User(1),User(4),id,edge.Id,"offer"),Is.EqualTo("mesh_not_participant"));
    }
    [Test]
    public async Task Capability_reply_names_the_caller_as_self()
    {
        using var f=new ApiWebApplicationFactory();var c=Guid.NewGuid();
        await f.SeedAsync(db=>
        {
            db.Conversations.Add(new(){Id=c,Kind="group",CreatedAt=DateTimeOffset.UnixEpoch});
            for(int i=1;i<=2;i++)
            {
                db.Users.Add(new(){Id=User(i),Provider="github",ProviderUserId="self"+i,DisplayName="fixture",CreatedAt=DateTimeOffset.UnixEpoch,UpdatedAt=DateTimeOffset.UnixEpoch});
                db.Participants.Add(new(){ConversationId=c,UserId=User(i),JoinedAt=DateTimeOffset.UnixEpoch});
            }
            return Task.CompletedTask;
        });
        foreach(int n in new[]{1,2})
        {
            using var client=f.CreateClient();
            client.DefaultRequestHeaders.Authorization=new("Bearer",f.Services.GetRequiredService<SessionService>().Issue(new Identity("github","self"+n,"fixture")));
            using var reply=await client.GetAsync($"/conversations/{c}/mesh");
            Assert.That(reply.StatusCode,Is.EqualTo(HttpStatusCode.OK));
            using var json=JsonDocument.Parse(await reply.Content.ReadAsStringAsync());var root=json.RootElement;
            Assert.That(root.TryGetProperty("self",out var self),Is.True,"capability reply has no self");
            Assert.That(self.GetString(),Is.EqualTo(User(n).ToString("D")));
            Assert.That(root.GetProperty("version").GetInt32(),Is.EqualTo(1));
            Assert.That(root.GetProperty("maxParticipants").GetInt32(),Is.EqualTo(4));
            Assert.That(root.GetProperty("room").GetProperty("roomId").ValueKind,Is.EqualTo(JsonValueKind.Null));
        }
    }
    [Test]
    public async Task HTTP_auth_capability_membership_and_addressed_delivery()
    {
        using var f=new ApiWebApplicationFactory();var c=Guid.NewGuid();
        await f.SeedAsync(db=>
        {
            db.Conversations.Add(new(){Id=c,Kind="group",CreatedAt=DateTimeOffset.UnixEpoch});
            for(int i=1;i<=4;i++)
            {
                db.Users.Add(new(){Id=User(i),Provider="github",ProviderUserId="mesh"+i,DisplayName="fixture",CreatedAt=DateTimeOffset.UnixEpoch,UpdatedAt=DateTimeOffset.UnixEpoch});
                if(i<4)db.Participants.Add(new(){ConversationId=c,UserId=User(i),JoinedAt=DateTimeOffset.UnixEpoch});
            }
            return Task.CompletedTask;
        });
        using var a=f.CreateClient();using var b=f.CreateClient();using var outsider=f.CreateClient();
        string path=$"/conversations/{c}/mesh";
        using(var unauth=await a.GetAsync(path))Assert.That(unauth.StatusCode,Is.EqualTo(HttpStatusCode.Unauthorized));
        void Auth(HttpClient client,int n)=>client.DefaultRequestHeaders.Authorization=new("Bearer",f.Services.GetRequiredService<SessionService>().Issue(new Identity("github","mesh"+n,"fixture")));
        Auth(a,1);Auth(b,2);Auth(outsider,4);
        using(var denied=await outsider.GetAsync(path))Assert.That(denied.StatusCode,Is.EqualTo(HttpStatusCode.Forbidden));
        using(var legacy=await a.PostAsJsonAsync(path+"/join",new{version=0}))Assert.That(legacy.StatusCode,Is.EqualTo(HttpStatusCode.Conflict));
        var hub=f.Services.GetRequiredService<GatewayHub>();var sa=new Sink(User(1));var sb=new Sink(User(2));var sc=new Sink(User(3));hub.Register(sa);hub.Register(sb);hub.Register(sc);
        using var joinA=await a.PostAsJsonAsync(path+"/join",new{version=1});Assert.That(joinA.StatusCode,Is.EqualTo(HttpStatusCode.OK));
        var room=JsonDocument.Parse(await joinA.Content.ReadAsStringAsync()).RootElement.GetProperty("roomId").GetGuid();
        using var joinB=await b.PostAsJsonAsync(path+"/join",new{version=1,roomId=room});Assert.That(joinB.StatusCode,Is.EqualTo(HttpStatusCode.OK));
        var edge=JsonDocument.Parse(await joinB.Content.ReadAsStringAsync()).RootElement.GetProperty("edges")[0].GetProperty("id").GetGuid();
        sa.Events.Clear();sb.Events.Clear();sc.Events.Clear();
        using var offer=await a.PostAsJsonAsync(path+"/signal",new{version=1,roomId=room,edgeId=edge,to=User(2),action="offer",sdp="fixture-sdp"});
        Assert.That(offer.StatusCode,Is.EqualTo(HttpStatusCode.OK));Assert.That(sa.Events,Is.Empty);Assert.That(sc.Events,Is.Empty);Assert.That(sb.Events,Has.Count.EqualTo(1));
        Assert.That(JsonSerializer.Serialize(sb.Events[0]),Does.Contain("mesh.signal"));
    }
    private sealed class Sink(Guid user):IGatewaySink
    {
        public Guid UserId=>user;public string ConnectionId=>user.ToString();public CancellationToken Disconnected=>CancellationToken.None;
        internal List<GatewayEnvelope> Events=[];
        public bool TryEnqueue(GatewayEnvelope envelope){Events.Add(envelope);return true;}
        public void Abort(GatewayAbortReason reason){}
        public void Complete(){}
    }
    [Test]
    public void Disconnect_removes_edges_and_notifier_publishes_current_roster()
    {
        var rooms=Registry();var c=Guid.NewGuid();var id=rooms.Join(c,User(1),null).Snapshot.RoomId;
        rooms.Join(c,User(2),id);var hub=new GatewayHub(new GatewayOptions());var sink=new Sink(User(1));hub.Register(sink);sink.Events.Clear();
        rooms.SetPresence(User(2),false);new MeshRosterNotifier(rooms,hub).Flush();
        Assert.That(rooms.Read(c).Edges,Is.Empty);Assert.That(sink.Events,Has.Count.EqualTo(1));
        Assert.That(JsonSerializer.Serialize(sink.Events[0]),Does.Contain("mesh.roster"));
    }
}
