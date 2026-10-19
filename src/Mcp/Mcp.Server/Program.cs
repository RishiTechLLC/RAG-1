using Azure.Identity;
using Azure.Storage.Blobs;
using Mcp.Server.Sources;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// --- Data sources -----------------------------------------------------------------------------
builder.Services.AddHttpClient<StudentsApiClient>(http =>
    http.BaseAddress = new Uri(builder.Configuration["StudentsApi:Url"]
        ?? throw new InvalidOperationException("StudentsApi:Url is not configured.")));

builder.Services.AddHttpClient<WeatherClient>();
builder.Services.AddSingleton<AcademicCalendar>();
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddSingleton(_ =>
{
    var connectionString = builder.Configuration["Storage:ConnectionString"];
    var service = !string.IsNullOrWhiteSpace(connectionString)
        ? new BlobServiceClient(connectionString)
        : new BlobServiceClient(new Uri(builder.Configuration["Storage:AccountUri"]
            ?? throw new InvalidOperationException("Storage:ConnectionString or Storage:AccountUri must be configured.")),
            new DefaultAzureCredential());
    return service.GetBlobContainerClient(builder.Configuration["Storage:ContainerName"] ?? "documents");
});

// --- MCP server (Streamable HTTP, stateless so it scales out on App Service) -------------------
builder.Services
    .AddMcpServer(options => options.ServerInfo = new() { Name = "campus-data", Version = "1.0.0" })
    .WithHttpTransport(options => options.Stateless = true)
    .WithToolsFromAssembly();

var app = builder.Build();

app.MapDefaultEndpoints();

// Optional shared-secret protection. Set Mcp:ApiKey (App Service setting / user secret) to require
// clients to send "X-Api-Key". For production prefer Entra ID (App Service Authentication).
var apiKey = app.Configuration["Mcp:ApiKey"];
if (!string.IsNullOrWhiteSpace(apiKey))
{
    app.UseWhen(ctx => ctx.Request.Path.StartsWithSegments("/mcp"), branch => branch.Use(async (ctx, next) =>
    {
        if (!ctx.Request.Headers.TryGetValue("X-Api-Key", out var provided) || provided != apiKey)
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        await next();
    }));
}

app.MapMcp("/mcp");
app.MapGet("/", () => "Campus data MCP server. Endpoint: /mcp (Streamable HTTP).");

app.Run();
