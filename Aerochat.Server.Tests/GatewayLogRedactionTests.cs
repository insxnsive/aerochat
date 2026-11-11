using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using Aerochat.Server.Auth;
using Aerochat.Server.Data;
using Aerochat.Server.Data.Entities;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aerochat.Server.Tests;

public sealed class GatewayLogRedactionTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task Websocket_upgrade_does_not_log_session_token(bool providerOverride)
    {
        const string category = "Microsoft.AspNetCore.Hosting.Diagnostics";
        var capture = new RecordingProvider();
        string token;
        using (var source = new ApiWebApplicationFactory())
        using (var factory = source.WithWebHostBuilder(builder => builder.ConfigureLogging(logging =>
        {
            logging.AddProvider(capture);
            logging.AddFilter(category, LogLevel.Information);
            logging.AddFilter("Aerochat.RedactionProbe", LogLevel.Information);
            if (providerOverride)
            {
                logging.AddFilter<RecordingProvider>(category, LogLevel.Information);
            }
        })))
        {
            using (IServiceScope scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ChatDb>();
                DateTimeOffset now = DateTimeOffset.UtcNow;
                db.Users.Add(new ExternalUserEntity
                {
                    Id = Guid.NewGuid(),
                    Provider = "github",
                    ProviderUserId = "log-redaction",
                    DisplayName = "Log test",
                    CreatedAt = now,
                    UpdatedAt = now
                });
                await db.SaveChangesAsync();
            }

            token = factory.Services.GetRequiredService<SessionService>()
                .Issue(new Identity("github", "log-redaction", "Log test"));
            factory.Services.GetRequiredService<ILoggerFactory>()
                .CreateLogger("Aerochat.RedactionProbe").LogInformation("redaction-capture-active");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var client = factory.Server.CreateWebSocketClient();
            client.ConfigureRequest = request => request.Headers.Authorization = "Bearer " + token;
            using WebSocket socket = await client.ConnectAsync(new Uri("ws://localhost/ws"), timeout.Token);
            var buffer = new byte[8192];
            WebSocketReceiveResult ready = await socket.ReceiveAsync(
                new ArraySegment<byte>(buffer), timeout.Token);
            Assert.That(ready.MessageType, Is.EqualTo(WebSocketMessageType.Text));
            Assert.That(Encoding.UTF8.GetString(buffer, 0, ready.Count), Does.Contain("gateway.ready"));
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", timeout.Token);
        }

        // Host disposed first: include request-finish and shutdown logs in the snapshot.
        string[] emitted = capture.Entries.ToArray();
        Assert.That(
            emitted.Any(entry => entry.Contains("redaction-capture-active", StringComparison.Ordinal)),
            Is.True);
        Assert.That(
            emitted.Any(entry => entry.Contains(token, StringComparison.Ordinal)
                || entry.Contains(Uri.EscapeDataString(token), StringComparison.Ordinal)),
            Is.False,
            "Session token appeared in captured server logs");
    }

    [Test]
    public async Task Rejected_websocket_request_does_not_log_session_token()
    {
        const string category = "Microsoft.AspNetCore.Hosting.Diagnostics";
        var capture = new RecordingProvider();
        string token;
        using (var source = new ApiWebApplicationFactory())
        using (var factory = source.WithWebHostBuilder(builder => builder.ConfigureLogging(logging =>
        {
            logging.AddProvider(capture);
            logging.AddFilter(category, LogLevel.Information);
            logging.AddFilter("Aerochat.RedactionProbe", LogLevel.Information);
        })))
        {
            using (IServiceScope scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ChatDb>();
                DateTimeOffset now = DateTimeOffset.UtcNow;
                db.Users.Add(new ExternalUserEntity
                {
                    Id = Guid.NewGuid(),
                    Provider = "github",
                    ProviderUserId = "log-redaction-rejected",
                    DisplayName = "Log test",
                    CreatedAt = now,
                    UpdatedAt = now
                });
                await db.SaveChangesAsync();
            }

            token = factory.Services.GetRequiredService<SessionService>()
                .Issue(new Identity("github", "log-redaction-rejected", "Log test"));
            factory.Services.GetRequiredService<ILoggerFactory>()
                .CreateLogger("Aerochat.RedactionProbe").LogInformation("redaction-capture-active");
            using HttpClient client = factory.CreateClient();
            using HttpResponseMessage response = await client.GetAsync(
                "/ws?token=" + Uri.EscapeDataString(token));
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        }

        string[] emitted = capture.Entries.ToArray();
        Assert.That(
            emitted.Any(entry => entry.Contains("redaction-capture-active", StringComparison.Ordinal)),
            Is.True);
        Assert.That(
            emitted.Any(entry => entry.Contains(token, StringComparison.Ordinal)
                || entry.Contains(Uri.EscapeDataString(token), StringComparison.Ordinal)),
            Is.False,
            "Session token appeared in captured server logs");
    }

    [Test]
    public async Task Loopback_host_does_not_log_session_token()
    {
        var capture = new RecordingProvider();
        string token;
        using (var fixture = await LoopbackServerFixture.StartAsync(configureLogging: logging =>
        {
            logging.AddProvider(capture);
            logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Information);
            logging.AddFilter<RecordingProvider>("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Information);
            logging.AddFilter("Aerochat.RedactionProbe", LogLevel.Information);
        }))
        {
            await fixture.SeedAsync(db =>
            {
                DateTimeOffset now = DateTimeOffset.UtcNow;
                db.Users.Add(new ExternalUserEntity
                {
                    Id = Guid.NewGuid(),
                    Provider = "github",
                    ProviderUserId = "loopback-redaction",
                    DisplayName = "Loopback log test",
                    CreatedAt = now,
                    UpdatedAt = now
                });
                return Task.CompletedTask;
            });
            token = fixture.Services.GetRequiredService<SessionService>()
                .Issue(new Identity("github", "loopback-redaction", "Loopback log test"));
            fixture.Services.GetRequiredService<ILoggerFactory>()
                .CreateLogger("Aerochat.RedactionProbe").LogInformation("redaction-capture-active");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var socket = new ClientWebSocket();
            socket.Options.SetRequestHeader("Authorization", "Bearer " + token);
            var uri = new UriBuilder(fixture.BaseUrl)
            {
                Scheme = "ws",
                Path = "/ws"
            };
            await socket.ConnectAsync(uri.Uri, timeout.Token);
            var buffer = new byte[8192];
            WebSocketReceiveResult ready = await socket.ReceiveAsync(
                new ArraySegment<byte>(buffer), timeout.Token);
            Assert.That(ready.MessageType, Is.EqualTo(WebSocketMessageType.Text));
            Assert.That(Encoding.UTF8.GetString(buffer, 0, ready.Count), Does.Contain("gateway.ready"));
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", timeout.Token);
        }

        // Fixture stopped/disposed first, including request-finish logs.
        string[] emitted = capture.Entries.ToArray();
        Assert.That(
            emitted.Any(entry => entry.Contains("redaction-capture-active", StringComparison.Ordinal)),
            Is.True);
        Assert.That(
            emitted.Any(entry => entry.Contains(token, StringComparison.Ordinal)
                || entry.Contains(Uri.EscapeDataString(token), StringComparison.Ordinal)),
            Is.False,
            "Session token appeared in captured server logs");
    }

    private sealed class RecordingProvider : ILoggerProvider, ISupportExternalScope
    {
        internal ConcurrentQueue<string> Entries { get; } = new();
        private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();

        public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

        public ILogger CreateLogger(string categoryName) => new Recorder(this, categoryName);

        public void Dispose()
        {
        }

        private sealed class Recorder(RecordingProvider owner, string category) : ILogger
        {
            public bool IsEnabled(LogLevel level) => true;

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull =>
                owner._scopes.Push(state);

            public void Log<TState>(
                LogLevel level,
                EventId id,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                owner.Entries.Enqueue(category + " " + formatter(state, exception));
                if (exception is not null)
                {
                    owner.Entries.Enqueue(exception.ToString());
                }

                if (state is IEnumerable<KeyValuePair<string, object?>> fields)
                {
                    foreach (KeyValuePair<string, object?> field in fields)
                    {
                        owner.Entries.Enqueue(field.Key + "=" + field.Value);
                    }
                }

                owner._scopes.ForEachScope(
                    (scope, entries) => entries.Enqueue(scope?.ToString() ?? ""), owner.Entries);
            }
        }
    }
}
