using System.Text.Json;
using Aerochat.Server.Gateway;

namespace Aerochat.Server.Calls;

// Presence callbacks run under the hub lock and must never publish. Drain only
// after acquiring that lock through its normal route, outside the callback.
public sealed class MeshRosterNotifier(MeshRooms rooms,GatewayHub gateway,ILogger<MeshRosterNotifier>? logger=null):BackgroundService
{
    public void Flush()=>gateway.WithDeliveryLock(()=>
    {
        foreach(var change in rooms.DrainChanges())
            gateway.Publish("mesh.roster",JsonSerializer.SerializeToElement(new{conversationId=change.Conversation,room=change.Snapshot},new JsonSerializerOptions(JsonSerializerDefaults.Web)),change.Snapshot.Participants);
        return true;
    });
    internal static void RunTick(Action flush,ILogger logger)
    {
        try { flush(); }
        catch(Exception exception)
        {
            logger.LogWarning("Mesh roster refresh failed: {ExceptionType}",exception.GetType().Name);
        }
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer=new PeriodicTimer(TimeSpan.FromSeconds(1));
        while(await timer.WaitForNextTickAsync(stoppingToken))
            RunTick(Flush,logger??Microsoft.Extensions.Logging.Abstractions.NullLogger<MeshRosterNotifier>.Instance);
    }
}
