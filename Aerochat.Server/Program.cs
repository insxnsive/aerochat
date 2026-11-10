using Aerochat.Server;

var builder = WebApplication.CreateBuilder(args);

// Hosting request-start/finish records include raw query strings. /ws authenticates
// via a query token, so this category must not reach any logging provider.
builder.Services.PostConfigure<LoggerFilterOptions>(options =>
{
    const string category = "Microsoft.AspNetCore.Hosting.Diagnostics";
    string?[] providers = options.Rules.Select(rule => rule.ProviderName)
        .Append(null).Distinct(StringComparer.Ordinal).ToArray();
    foreach (string? provider in providers)
    {
        options.Rules.Add(new LoggerFilterRule(provider, category, LogLevel.None, null));
    }
});

ServerComposition.ConfigureBuilder(builder);

var app = builder.Build();
await ServerComposition.ConfigureAppAsync(app);
ServerComposition.MapEndpoints(app);
app.Run();

public partial class Program { }
