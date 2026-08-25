using Aerochat.Server.Auth;

namespace Aerochat.Server.Tests;

public sealed class SessionServiceTests
{
    private static readonly byte[] TestSigningKey =
    [
        0x10, 0x21, 0x32, 0x43, 0x54, 0x65, 0x76, 0x87,
        0x98, 0xA9, 0xBA, 0xCB, 0xDC, 0xED, 0xFE, 0x0F,
        0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88,
        0x99, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF, 0x00
    ];

    [Test]
    public void Issue_and_validate_roundtrip()
    {
        var svc = new SessionService(TestSigningKey, TimeProvider.System);
        var token = svc.Issue(new Identity("github", "12345", "nate"));

        var claims = svc.Validate(token);

        Assert.That(claims, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(claims!.Provider, Is.EqualTo("github"));
            Assert.That(claims.ProviderUserId, Is.EqualTo("12345"));
            Assert.That(claims.DisplayName, Is.EqualTo("nate"));
        });
    }

    [Test]
    public void Expired_token_is_rejected()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var svc = new SessionService(TestSigningKey, clock);
        var token = svc.Issue(new Identity("github", "12345", "nate"), TimeSpan.FromSeconds(300));

        clock.Advance(TimeSpan.FromSeconds(301));

        Assert.That(svc.Validate(token), Is.Null);
    }

    [Test]
    public void Tampered_token_is_rejected()
    {
        var svc = new SessionService(TestSigningKey, TimeProvider.System);
        var token = svc.Issue(new Identity("github", "12345", "nate"));
        var segments = token.Split('.');
        var payload = segments[1].ToCharArray();
        int middle = payload.Length / 2;
        payload[middle] = payload[middle] == 'a' ? 'b' : 'a';
        var tamperedToken = string.Join('.', segments[0], new string(payload), segments[2]);

        Assert.That(svc.Validate(tamperedToken), Is.Null);
    }

    [Test]
    public void Token_signed_with_different_key_is_rejected()
    {
        var serviceWithKeyA = new SessionService(TestSigningKey, TimeProvider.System);
        var serviceWithKeyB = new SessionService(
            [
                0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08,
                0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F, 0x10,
                0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18,
                0x19, 0x1A, 0x1B, 0x1C, 0x1D, 0x1E, 0x1F, 0x20
            ],
            TimeProvider.System);
        var token = serviceWithKeyA.Issue(new Identity("github", "12345", "nate"));

        Assert.That(serviceWithKeyB.Validate(token), Is.Null);
    }
}

internal sealed class MutableTimeProvider : TimeProvider
{
    private DateTimeOffset _utcNow;

    public MutableTimeProvider(DateTimeOffset utcNow)
    {
        _utcNow = utcNow;
    }

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public void Advance(TimeSpan amount) => _utcNow += amount;
}
