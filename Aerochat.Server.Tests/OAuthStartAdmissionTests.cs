using System.Net;
using System.Text.Json;
using Aerochat.Server.Auth.OAuth;
using Aerochat.Server.Hardening;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aerochat.Server.Tests;

public sealed class OAuthStartAdmissionTests
{
    private const string ValidReturnUri = "http://127.0.0.1:4321/oauth/callback";
    private const string StartPath = "/auth/google/start";
    private const string UppercaseStartPath = "/AUTH/google/START";

    [TestCase("per_ip")]
    [TestCase("global")]
    public async Task OAuth_start_admission_rejects_exhaustion_before_allocating_state(string scope)
    {
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-08-25T12:00:00Z"));
        int storeCapacity = scope == "per_ip" ? 6 : 101;

        using var factory = new ApiWebApplicationFactory { Clock = clock };
        using WebApplicationFactory<Program> configured = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Auth:Google:ClientId", "test-google-client");
            builder.UseSetting("Auth:Google:ClientSecret", "test-google-secret");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<OAuthFlowStore>();
                services.AddSingleton(new OAuthFlowStore(clock, storeCapacity, storeCapacity));
            });
        });

        TestServer server = configured.Server;

        async Task<HttpContext> StartAsync(
            string path,
            IPAddress address,
            string spoofedForwardingValue)
        {
            return await server.SendAsync(context =>
            {
                context.Request.Method = HttpMethods.Get;
                context.Request.Scheme = "http";
                context.Request.Path = path;
                context.Request.QueryString =
                    new QueryString($"?returnUri={Uri.EscapeDataString(ValidReturnUri)}");
                context.Connection.RemoteIpAddress = address;
                context.Request.Headers["X-Forwarded-For"] = spoofedForwardingValue;
                context.Request.Headers["Forwarded"] = $"for={spoofedForwardingValue}";
                context.Request.Headers["X-Real-IP"] = spoofedForwardingValue;
            });
        }

        static async Task<string> ReadBodyAsync(HttpContext context)
        {
            using var reader = new StreamReader(context.Response.Body);
            return await reader.ReadToEndAsync();
        }

        static void AssertRedirect(HttpContext context)
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status302Found));
            Assert.That(context.Response.Headers.Location.ToString(), Is.Not.Empty);
            Assert.That(
                context.Response.Headers.Location.ToString(),
                Does.Contain("state="));
        }

        static async Task AssertAdmissionRejectedAsync(HttpContext context)
        {
            Assert.That(
                context.Response.StatusCode,
                Is.EqualTo(StatusCodes.Status429TooManyRequests));
            Assert.That(context.Response.Headers.Location.ToString(), Is.Empty);
            Assert.That(
                context.Response.Headers["Retry-After"].ToString(),
                Is.EqualTo("60"));

            string body = await ReadBodyAsync(context);
            using JsonDocument document = JsonDocument.Parse(body);
            Assert.That(
                document.RootElement.GetProperty("error").GetString(),
                Is.EqualTo("rate_limited"));
        }

        if (scope == "per_ip")
        {
            IPAddress callerAddress = IPAddress.Parse("203.0.113.7");
            for (int attempt = 0; attempt < 5; attempt++)
            {
                AssertRedirect(await StartAsync(
                    StartPath,
                    callerAddress,
                    $"198.51.100.{attempt}"));
            }

            await AssertAdmissionRejectedAsync(await StartAsync(
                UppercaseStartPath,
                callerAddress,
                "192.0.2.200"));

            clock.Advance(TimeSpan.FromSeconds(60));

            AssertRedirect(await StartAsync(
                StartPath,
                callerAddress,
                "192.0.2.201"));
            return;
        }

        for (int index = 0; index < 100; index++)
        {
            IPAddress address = new(
            [
                100,
                64,
                (byte)(index / 256),
                (byte)(index % 256)
            ]);
            AssertRedirect(await StartAsync(
                StartPath,
                address,
                $"198.51.100.{index % 256}"));
        }

        IPAddress blockedAddress = IPAddress.Parse("203.0.113.99");
        await AssertAdmissionRejectedAsync(await StartAsync(
            UppercaseStartPath,
            blockedAddress,
            "192.0.2.1"));

        clock.Advance(TimeSpan.FromSeconds(60));

        AssertRedirect(await StartAsync(
            StartPath,
            blockedAddress,
            "192.0.2.2"));
    }

    [Test]
    public void Simultaneous_acquisitions_from_one_address_never_exceed_per_address_limit()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-08-25T12:00:00Z"));
        var limiter = new OAuthStartRateLimiter(clock);
        IPAddress address = IPAddress.Parse("203.0.113.10");
        int admitted = 0;

        Parallel.For(
            0,
            64,
            _ =>
            {
                if (limiter.TryAcquire(address).Allowed)
                {
                    Interlocked.Increment(ref admitted);
                }
            });

        Assert.That(admitted, Is.EqualTo(OAuthStartRateLimiter.PerIpLimit));
    }

    [Test]
    public void Simultaneous_acquisitions_from_distinct_addresses_never_exceed_global_limit()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-08-25T12:00:00Z"));
        var limiter = new OAuthStartRateLimiter(clock);
        int admitted = 0;

        Parallel.For(
            0,
            300,
            index =>
            {
                IPAddress address = new(
                [
                    100,
                    64,
                    (byte)(index / 256),
                    (byte)(index % 256)
                ]);
                if (limiter.TryAcquire(address).Allowed)
                {
                    Interlocked.Increment(ref admitted);
                }
            });

        Assert.That(admitted, Is.EqualTo(OAuthStartRateLimiter.GlobalLimit));
    }

    [Test]
    public void Rejected_address_does_not_consume_global_slots_for_another_address()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-08-25T12:00:00Z"));
        var limiter = new OAuthStartRateLimiter(clock);
        IPAddress blocked = IPAddress.Parse("203.0.113.20");

        for (int attempt = 0; attempt < OAuthStartRateLimiter.PerIpLimit; attempt++)
        {
            Assert.That(limiter.TryAcquire(blocked).Allowed, Is.True);
        }

        for (int attempt = 0; attempt < 5; attempt++)
        {
            Assert.That(limiter.TryAcquire(blocked).Allowed, Is.False);
        }

        int admitted = 0;
        for (int index = 0; index < OAuthStartRateLimiter.GlobalLimit - OAuthStartRateLimiter.PerIpLimit; index++)
        {
            IPAddress other = new(
            [
                100,
                65,
                (byte)(index / 256),
                (byte)(index % 256)
            ]);
            if (limiter.TryAcquire(other).Allowed)
            {
                admitted++;
            }
        }

        Assert.That(admitted, Is.EqualTo(95));
        Assert.That(
            limiter.TryAcquire(IPAddress.Parse("203.0.113.21")).Allowed,
            Is.False);
    }

    [Test]
    public void Rollover_recovers_at_the_exact_window_boundary_without_sleeping()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-08-25T12:00:00Z"));
        var limiter = new OAuthStartRateLimiter(clock);
        IPAddress address = IPAddress.Parse("203.0.113.30");

        for (int attempt = 0; attempt < OAuthStartRateLimiter.PerIpLimit; attempt++)
        {
            Assert.That(limiter.TryAcquire(address).Allowed, Is.True);
        }

        Assert.That(limiter.TryAcquire(address).Allowed, Is.False);

        clock.Advance(OAuthStartRateLimiter.Window - TimeSpan.FromMilliseconds(1));
        Assert.That(limiter.TryAcquire(address).Allowed, Is.False);

        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.That(limiter.TryAcquire(address).Allowed, Is.True);
    }

    [Test]
    public void Mapped_and_native_ipv4_share_identity_and_missing_addresses_share_one_bucket()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-08-25T12:00:00Z"));
        var limiter = new OAuthStartRateLimiter(clock);
        IPAddress native = IPAddress.Parse("127.0.0.1");
        IPAddress mapped = IPAddress.Parse("::ffff:127.0.0.1");

        for (int attempt = 0; attempt < OAuthStartRateLimiter.PerIpLimit; attempt++)
        {
            Assert.That(limiter.TryAcquire(native).Allowed, Is.True);
        }

        Assert.That(limiter.TryAcquire(mapped).Allowed, Is.False);

        var unknownLimiter = new OAuthStartRateLimiter(clock);
        for (int attempt = 0; attempt < OAuthStartRateLimiter.PerIpLimit; attempt++)
        {
            Assert.That(unknownLimiter.TryAcquire(null).Allowed, Is.True);
        }

        Assert.That(unknownLimiter.TryAcquire(null).Allowed, Is.False);
        Assert.That(unknownLimiter.TryAcquire(native).Allowed, Is.True);
    }
}
