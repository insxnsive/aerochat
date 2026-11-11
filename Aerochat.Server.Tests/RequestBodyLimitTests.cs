using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Aerochat.Server.Tests;

public sealed class RequestBodyLimitTests
{
    [TestCase(true, false, false)]
    [TestCase(true, false, true)]
    [TestCase(true, true, false)]
    [TestCase(true, true, true)]
    [TestCase(false, false, false)]
    [TestCase(false, false, true)]
    [TestCase(false, true, false)]
    [TestCase(false, true, true)]
    public async Task Known_and_chunked_bodies_obey_exact_wire_boundary(bool auth, bool chunked, bool over)
    {
        await using var server = await LoopbackServerFixture.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(server.BaseUrl) };
        int limit = auth ? 16 * 1024 : 2 * 1024 * 1024;
        string prefix = auth ? "{\"code\":\"invalid\"}" : "{\"sdp\":\"x\"}";
        byte[] bytes = Encoding.UTF8.GetBytes(prefix + new string(' ', limit + (over ? 1 : 0) - prefix.Length));
        using var content = new BodyContent(bytes, chunked);
        using var response = await client.PostAsync(auth ? "/auth/session/exchange"
            : "/conversations/11111111-1111-1111-1111-111111111111/call/offer", content);
        Assert.That(response.StatusCode, Is.EqualTo(over ? HttpStatusCode.RequestEntityTooLarge
            : auth ? HttpStatusCode.BadRequest : HttpStatusCode.Unauthorized));
        if (over) Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo("{\"error\":\"request_too_large\"}"));
    }

    [Test]
    public void Kestrel_has_explicit_global_limit()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Configuration["ConnectionStrings:Chat"] = "Data Source=:memory:";
        ServerComposition.ConfigureBuilder(builder);
        using var services = builder.Services.BuildServiceProvider();
        Assert.That(services.GetRequiredService<IOptions<KestrelServerOptions>>().Value.Limits.MaxRequestBodySize,
            Is.EqualTo(2 * 1024 * 1024));
    }

    [Test]
    public async Task All_three_maximal_call_fields_fit_even_when_JSON_escaped()
    {
        string field = new('\0', 64 * 1024);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new { sdp = field, candidate = field, reason = field });
        Assert.That(bytes.Length, Is.GreaterThan(256 * 1024).And.LessThan(2 * 1024 * 1024));
        await using var server = await LoopbackServerFixture.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(server.BaseUrl) };
        using var content = new BodyContent(bytes, true);
        using var response = await client.PostAsync("/conversations/11111111-1111-1111-1111-111111111111/call/offer", content);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    private sealed class BodyContent : HttpContent
    {
        private readonly byte[] _bytes;
        private readonly bool _chunked;
        public BodyContent(byte[] bytes, bool chunked)
        {
            _bytes = bytes; _chunked = chunked;
            Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }
        protected override bool TryComputeLength(out long length) { length = _bytes.Length; return !_chunked; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(_bytes).AsTask();
    }
}
