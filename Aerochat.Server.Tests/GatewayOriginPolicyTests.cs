using Aerochat.Server.Hardening;

namespace Aerochat.Server.Tests;

public sealed class GatewayOriginPolicyTests
{
    [Test]
    public void Empty_configuration_allows_present_origin_for_dev_default()
    {
        Assert.That(GatewayOriginPolicy.IsAllowed("https://any.example", new HashSet<string>()), Is.True);
    }

    [Test]
    public void Present_origin_must_match_configured_origin()
    {
        var allowed = new HashSet<string>(["https://allowed.example"], StringComparer.OrdinalIgnoreCase);

        Assert.Multiple(() =>
        {
            Assert.That(GatewayOriginPolicy.IsAllowed(null, allowed), Is.True);
            Assert.That(GatewayOriginPolicy.IsAllowed("https://allowed.example", allowed), Is.True);
            Assert.That(GatewayOriginPolicy.IsAllowed("https://blocked.example", allowed), Is.False);
        });
    }
}
