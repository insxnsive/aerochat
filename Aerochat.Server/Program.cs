using Aerochat.Server;

var builder = WebApplication.CreateBuilder(args);
ServerComposition.ConfigureBuilder(builder);

var app = builder.Build();
await ServerComposition.ConfigureAppAsync(app);
ServerComposition.MapEndpoints(app);
app.Run();

public partial class Program { }
