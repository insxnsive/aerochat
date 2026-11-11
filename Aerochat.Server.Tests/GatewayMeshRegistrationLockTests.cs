using System.Text.Json;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using Aerochat.Server.Auth;
using Aerochat.Server.Calls;
using Aerochat.Server.Data.Entities;
using Aerochat.Server.Gateway;
using Microsoft.Extensions.DependencyInjection;

namespace Aerochat.Server.Tests;

public sealed class GatewayMeshRegistrationLockTests
{
    [Test]
    public async Task Oversized_signal_is_rejected_without_waiting_for_delivery_lock()
    {
        using var factory=new ApiWebApplicationFactory();var user=Guid.NewGuid();var other=Guid.NewGuid();var conversation=Guid.NewGuid();var now=DateTimeOffset.UtcNow;
        await factory.SeedAsync(db=>
        {
            foreach(var id in new[]{user,other})db.Users.Add(new ExternalUserEntity{Id=id,Provider="github",ProviderUserId=id.ToString(),DisplayName="fixture",CreatedAt=now,UpdatedAt=now});
            db.Conversations.Add(new ConversationEntity{Id=conversation,Kind="group",CreatedAt=now,Participants={new ParticipantEntity{UserId=user,JoinedAt=now},new ParticipantEntity{UserId=other,JoinedAt=now}}});return Task.CompletedTask;
        });
        using var client=factory.CreateClient();client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",factory.Services.GetRequiredService<SessionService>().Issue(new Identity("github",user.ToString(),"fixture")));
        var hub=factory.Services.GetRequiredService<GatewayHub>();var delivery=typeof(GatewayHub).GetField("_deliveryGate",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(hub)!;
        using var held=new ManualResetEventSlim();using var release=new ManualResetEventSlim();
        var holder=Task.Run(()=>{lock(delivery){held.Set();if(!release.Wait(TimeSpan.FromSeconds(5)))throw new TimeoutException("Probe delivery release timed out.");}});
        Task<HttpResponseMessage>? response=null;
        try
        {
            Assert.That(held.Wait(TimeSpan.FromSeconds(5)),Is.True);
            response=client.PostAsJsonAsync($"/conversations/{conversation}/mesh/signal",new{version=1,roomId=Guid.NewGuid(),edgeId=Guid.NewGuid(),to=other,action="ice",candidate=new string('\u0001',60000)});
            using var result=await response.WaitAsync(TimeSpan.FromSeconds(1));Assert.That(result.StatusCode,Is.EqualTo(HttpStatusCode.BadRequest));
        }
        finally
        {
            release.Set();await holder.WaitAsync(TimeSpan.FromSeconds(5));
            if(response is not null)using(await response.WaitAsync(TimeSpan.FromSeconds(5))){}
        }
    }
    [Test]
    public async Task Cancelled_registration_can_leave_while_mesh_publication_is_paused_before_queue()
    {
        var hub=new GatewayHub(new GatewayOptions());var user=Guid.NewGuid();
        using var visible=new ManualResetEventSlim();using var releaseToken=new ManualResetEventSlim();using var registrationFinished=new ManualResetEventSlim();
        var sink=new CancelledSink(user,visible,releaseToken);
        Task<GatewayRegistrationResult> registration=Task.Run(()=>
        {try{return hub.Register(sink);}finally{registrationFinished.Set();}});
        var rooms=new MeshRooms();var other=Guid.NewGuid();var conversation=Guid.NewGuid();rooms.SetPresence(user,true);rooms.SetPresence(other,true);
        var room=rooms.Join(conversation,user,null).Snapshot.RoomId!.Value;rooms.Join(conversation,other,room);rooms.SetPresence(other,false);
        var notifier=new MeshRosterNotifier(rooms,hub);
        Task? publish=null;
        try
        {
            Assert.That(visible.Wait(TimeSpan.FromSeconds(5)),Is.True,"Registration must be visible while its state gate is held.");
            hub.BeforeQueueInternalForTesting=envelope=>
            {
                releaseToken.Set();
                if(!registrationFinished.Wait(TimeSpan.FromSeconds(1)))
                    throw new TimeoutException("Registration cannot remove itself while mesh publication retains the hub gate.");
            };
            publish=Task.Run(notifier.Flush);
            Assert.DoesNotThrowAsync(async()=>await Task.WhenAll(registration,publish).WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.That((await registration).Registered,Is.False);
        }
        finally
        {
            releaseToken.Set();
            // The probe throws before QueueInternal if the cycle is present. Releasing
            // the outer locks lets registration finish, so the failing RED never hangs.
            try {await registration.WaitAsync(TimeSpan.FromSeconds(5));}catch{ }
            if(publish is not null)try{await publish.WaitAsync(TimeSpan.FromSeconds(5));}catch{ }
            hub.BeforeQueueInternalForTesting=null;
        }
    }
    private sealed class CancelledSink(Guid user,ManualResetEventSlim visible,ManualResetEventSlim release):IGatewaySink
    {
        private int _reads;
        public Guid UserId=>user;public string ConnectionId=>"cancelled-registration";
        public CancellationToken Disconnected
        {
            get
            {
                if(Interlocked.Increment(ref _reads)==1)
                {
                    visible.Set();
                    if(!release.Wait(TimeSpan.FromSeconds(5)))throw new TimeoutException("Probe token release timed out.");
                }
                return new CancellationToken(true);
            }
        }
        public bool TryEnqueue(GatewayEnvelope envelope)=>true;
        public void Abort(GatewayAbortReason reason){}
        public void Complete(){}
    }
}
