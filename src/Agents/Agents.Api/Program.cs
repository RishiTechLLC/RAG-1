using Agents.Api;
using Agents.Api.Tools;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.Configure<AzureOpenAIOptions>(builder.Configuration.GetSection(AzureOpenAIOptions.SectionName));
builder.Services.Configure<DownstreamOptions>(builder.Configuration.GetSection("Downstream"));

builder.Services.AddSingleton<TokenCredential>(_ => new DefaultAzureCredential(new DefaultAzureCredentialOptions
{
    ManagedIdentityClientId = builder.Configuration["AZURE_CLIENT_ID"],
}));

builder.Services.AddSingleton<IChatClient>(sp =>
    new ChatClientBuilder(ChatClientFactory.Create(
            sp.GetRequiredService<IOptions<AzureOpenAIOptions>>().Value,
            sp.GetRequiredService<TokenCredential>()))
        .UseOpenTelemetry(sourceName: "Agents.Api", configure: o => o.EnableSensitiveData = builder.Environment.IsDevelopment())
        .Build(sp));

builder.Services.AddHttpClient<KnowledgeBaseTools>((sp, http) =>
    http.BaseAddress = new Uri(sp.GetRequiredService<IOptions<DownstreamOptions>>().Value.RagApiUrl.TrimEnd('/') + "/"));

builder.Services.AddSingleton<McpToolProvider>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<AgentCatalog>();
builder.Services.AddSingleton<ChatService>();
builder.Services.AddMemoryCache();
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();

var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(allowedOrigins)
    .AllowAnyHeader()
    .AllowAnyMethod()));

var app = builder.Build();

app.UseExceptionHandler();
app.UseCors();
app.MapOpenApi();
app.MapDefaultEndpoints();

var api = app.MapGroup("/api").WithTags("Chat");

api.MapPost("/chat", async (ChatRequest request, ChatService chat, CancellationToken ct) =>
    {
        if (string.IsNullOrWhiteSpace(request.Message))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["message"] = ["Message is required."] });
        }

        if (request.Agent is not null && !ChatService.AgentAliases.ContainsKey(request.Agent))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["agent"] = [$"Unknown agent. Use one of: {string.Join(", ", ChatService.AgentAliases.Keys)}"],
            });
        }

        return Results.Ok(await chat.ChatAsync(request, ct));
    })
    .WithSummary("Send a message. The orchestrator routes it to the knowledge (vector DB), campus data (MCP) or writer agents.");

api.MapDelete("/chat/{conversationId}", (string conversationId, ChatService chat) =>
    {
        chat.Reset(conversationId);
        return Results.NoContent();
    })
    .WithSummary("Forget a conversation's history.");

api.MapGet("/agents", async (AgentCatalog catalog, CancellationToken ct) =>
    {
        var agents = await catalog.GetAsync(ct);
        return new { degraded = agents.Degraded, agents = agents.Info };
    })
    .WithSummary("Lists the agents and the tools each one can use.");

app.Run();
