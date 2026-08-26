using System.Data.Common;
using Aerochat.Server.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aerochat.Server.Tests;

public sealed class ApiWebApplicationFactory : WebApplicationFactory<Program>
{
    private static readonly byte[] TestSigningKey =
    [
        0x10, 0x21, 0x32, 0x43, 0x54, 0x65, 0x76, 0x87,
        0x98, 0xA9, 0xBA, 0xCB, 0xDC, 0xED, 0xFE, 0x0F,
        0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88,
        0x99, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF, 0x00
    ];

    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public List<int> MessageQueryLimits { get; } = [];

    public string? TenorApiKey { get; set; }

    public Func<HttpRequestMessage, HttpResponseMessage>? TenorRequestHandler { get; set; }

    public TimeProvider? Clock { get; set; }

    public int? RateLimit { get; set; }

    public int? RateLimitWindowSeconds { get; set; }

    public ApiWebApplicationFactory()
    {
        _connection.Open();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Chat", "Data Source=:memory:");
        builder.UseSetting("Auth:SessionSigningKey", Convert.ToBase64String(TestSigningKey));
        if (RateLimit is not null)
        {
            builder.UseSetting("RateLimit:Limit", RateLimit.Value.ToString());
        }

        if (RateLimitWindowSeconds is not null)
        {
            builder.UseSetting("RateLimit:WindowSeconds", RateLimitWindowSeconds.Value.ToString());
        }
        if (TenorApiKey is not null)
        {
            builder.UseSetting("Tenor:ApiKey", TenorApiKey);
        }

        builder.ConfigureServices(services =>
        {
            if (Clock is not null)
            {
                TimeProvider clock = Clock;
                services.RemoveAll<TimeProvider>();
                services.AddSingleton(clock);
            }

            services.RemoveAll<DbContextOptions<ChatDb>>();
            services.RemoveAll<ChatDb>();
            services.AddDbContext<ChatDb>(options => options
                .UseSqlite(_connection)
                .AddInterceptors(new MessageQueryLimitInterceptor(MessageQueryLimits)));

            if (TenorRequestHandler is not null)
            {
                Func<HttpRequestMessage, HttpResponseMessage> handler = TenorRequestHandler;
                services.AddHttpClient<Aerochat.Server.Gifs.TenorProxyService>()
                    .ConfigurePrimaryHttpMessageHandler(() => new StubHttpMessageHandler(handler));
            }
        });
    }

    public async Task SeedAsync(Func<ChatDb, Task> seed)
    {
        await using AsyncServiceScope scope = Services.CreateAsyncScope();
        ChatDb db = scope.ServiceProvider.GetRequiredService<ChatDb>();
        await seed(db);
        await db.SaveChangesAsync();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _connection.Dispose();
        }

        base.Dispose(disposing);
    }
}

internal sealed class StubHttpMessageHandler(
    Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken) =>
        Task.FromResult(handler(request));
}

internal sealed class MessageQueryLimitInterceptor(ICollection<int> limits) : DbCommandInterceptor
{
    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result)
    {
        Capture(command);
        return base.ReaderExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Capture(command);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    private void Capture(DbCommand command)
    {
        if (!command.CommandText.Contains("FROM \"messages\"", StringComparison.Ordinal))
        {
            return;
        }

        foreach (DbParameter parameter in command.Parameters)
        {
            if (parameter.Value is int value)
            {
                limits.Add(value);
            }
        }
    }
}
