using System.Reflection;
using Aerochat.Server.Gateway;

namespace Aerochat.Server.Tests;

public sealed class GatewayPresenceLockOrderTests
{
    [Test]
    public void Presence_operation_waits_for_delivery_before_entering_its_callback()
    {
        var hub=new GatewayHub(new GatewayOptions());
        var delivery=typeof(GatewayHub).GetField("_deliveryGate",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(hub)!;
        using var started=new ManualResetEventSlim();
        Task<bool>? operation=null;
        Monitor.Enter(delivery);
        try
        {
            operation=Task.Run(()=>{started.Set();return hub.WithPresenceLock(()=>true);});
            Assert.That(started.Wait(TimeSpan.FromSeconds(5)),Is.True);
            Assert.That(operation.Wait(TimeSpan.FromSeconds(1)),Is.False,
                "Presence work entered while another thread held delivery: Publish can take the opposite lock order.");
        }
        finally
        {
            Monitor.Exit(delivery);
            if(operation is not null)Assert.That(operation.Wait(TimeSpan.FromSeconds(5)),Is.True);
        }
    }
}
