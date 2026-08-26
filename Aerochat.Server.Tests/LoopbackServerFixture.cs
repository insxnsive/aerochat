using Aerochat.Server.Data;
using Aerochat.Server.Gateway;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aerochat.Server.Tests;

/// <summary>
/// Hosts the real server pipeline (Kestrel, WebSockets, gateway hub, SQLite) on a
/// numeric loopback endpoint so tests exercise genuine sockets instead of TestServer.
/// </summary>
public sealed class LoopbackServerFixture : IAsyncDisposable, IDisposable
{
    internal static readonly byte[] TestSigningKey =
    [
        0x21, 0x43, 0x65, 0x87, 0xA9, 0xCB, 0xED, 0x0F,
        0x11, 0x33, 0x55, 0x77, 0x99, 0xBB, 0xDD, 0xFF,
        0x01, 0x23, 0x45, 0x67, 0x89, 0xAB, 0xCD, 0xEF,
        0xBA, 0xDC, 0xFE, 0x98, 0x76, 0x54, 0x32, 0x10
    ];

    private readonly SqliteConnection _connection;
    private readonly WebApplication _app;

    private LoopbackServerFixture(WebApplication app, string baseUrl, SqliteConnection connection)
    {
        _app = app;
        BaseUrl = baseUrl.TrimEnd('/');
        _connection = connection;
    }

    public string BaseUrl { get; }

    public IServiceProvider Services => _app.Services;

    public static async Task<LoopbackServerFixture> StartAsync(
        string? instanceId = null,
        int? replayCapacity = null,
        int? queueCapacity = null,
        string? allowedOrigins = null)
    {
        var options = new WebApplicationOptions { EnvironmentName = "Testing" };
        var builder = WebApplication.CreateBuilder(options);
        builder.Configuration["urls"] = "http://127.0.0.1:0";
        builder.Configuration["Auth:SessionSigningKey"] = Convert.ToBase64String(TestSigningKey);
        if (instanceId is not null)
        {
            builder.Configuration["Gateway:InstanceId"] = instanceId;
        }

        if (replayCapacity is not null)
        {
            builder.Configuration["Gateway:ReplayCapacity"] =
                replayCapacity.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (queueCapacity is not null)
        {
            builder.Configuration["Gateway:QueueCapacity"] =
                queueCapacity.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (allowedOrigins is not null)
        {
            builder.Configuration["Gateway:AllowedOrigins"] = allowedOrigins;
        }

        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        try
        {
            builder.Services.RemoveAll<DbContextOptions<ChatDb>>();
            builder.Services.RemoveAll<ChatDb>();
            builder.Services.AddDbContext<ChatDb>(dbOptions => dbOptions.UseSqlite(connection));

            ServerComposition.ConfigureBuilder(builder);
            WebApplication app = builder.Build();
            await ServerComposition.ConfigureAppAsync(app);
            ServerComposition.MapEndpoints(app);
            await app.StartAsync();

            string baseUrl = app.Urls.FirstOrDefault(url => url.StartsWith("http://127.0.0.1", StringComparison.Ordinal))
                ?? throw new InvalidOperationException("Kestrel did not bind a numeric loopback endpoint.");
            return new LoopbackServerFixture(app, baseUrl, connection);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public async Task SeedAsync(Func<ChatDb, Task> seed)
    {
        await using AsyncServiceScope scope = Services.CreateAsyncScope();
        ChatDb db = scope.ServiceProvider.GetRequiredService<ChatDb>();
        await seed(db);
        await db.SaveChangesAsync();
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _app.StopAsync(timeout.Token);
        }
        finally
        {
            await _app.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
