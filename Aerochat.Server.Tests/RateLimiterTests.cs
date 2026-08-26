using Aerochat.Server.Hardening;

namespace Aerochat.Server.Tests;

public sealed class RateLimiterTests
{
    [Test]
    public void Allows_limit_then_rejects_until_window_expires()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-08-25T12:00:00Z"));
        var limiter = new FixedWindowRateLimiter(2, TimeSpan.FromMinutes(1), clock);

        Assert.Multiple(() =>
        {
            Assert.That(limiter.TryAcquire("user-1").Allowed, Is.True);
            Assert.That(limiter.TryAcquire("user-1").Allowed, Is.True);
            Assert.That(limiter.TryAcquire("user-1").Allowed, Is.False);
        });

        clock.Advance(TimeSpan.FromMinutes(1));

        Assert.That(limiter.TryAcquire("user-1").Allowed, Is.True);
    }

    [Test]
    public void Keys_are_independent_and_retry_after_is_positive()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-08-25T12:00:00Z"));
        var limiter = new FixedWindowRateLimiter(1, TimeSpan.FromMinutes(1), clock);

        Assert.That(limiter.TryAcquire("user-1").Allowed, Is.True);
        RateLimitDecision rejected = limiter.TryAcquire("user-1");

        Assert.Multiple(() =>
        {
            Assert.That(rejected.Allowed, Is.False);
            Assert.That(rejected.RetryAfter, Is.EqualTo(TimeSpan.FromMinutes(1)));
            Assert.That(limiter.TryAcquire("user-2").Allowed, Is.True);
        });
    }
}
