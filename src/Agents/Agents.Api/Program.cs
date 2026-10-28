using Agents.Api;
using Agents.Api.SemanticCache;
using Agents.Api.Tools;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.Configure<AzureOpenAIOptions>(builder.Configuration.GetSection(AzureOpenAIOptions.SectionName));
builder.Services.Configure<DownstreamOptions>(builder.Configuration.GetSection("Downstream"));

builder.Services.AddSingleton<TokenCredential>(_ => new DefaultAzureCredential(new DefaultAzureCredentialOptions
{
    ManagedIdentityClientId = builder.Configuration["AZURE_CLIENT_ID"],
}));

builder.Services.AddSingleton(sp => ChatClientFactory.CreateOpenAIClient(
    sp.GetRequiredService<IOptions<AzureOpenAIOptions>>().Value,
    sp.GetRequiredService<TokenCredential>()));

builder.Services.AddSingleton<IChatClient>(sp =>
    new ChatClientBuilder(sp.GetRequiredService<OpenAI.OpenAIClient>()
            .GetChatClient(sp.GetRequiredService<IOptions<AzureOpenAIOptions>>().Value.ChatDeployment)
            .AsIChatClient())
        .UseOpenTelemetry(sourceName: "Agents.Api", configure: o => o.EnableSensitiveData = builder.Environment.IsDevelopment())
        .Build(sp));

builder.Services.AddHttpClient<KnowledgeBaseTools>((sp, http) =>
    http.BaseAddress = new Uri(sp.GetRequiredService<IOptions<DownstreamOptions>>().Value.RagApiUrl.TrimEnd('/') + "/"));

// ---- Semantic cache (Redis Stack / Redis 8 vector search) -----------------------------------
// Enabled when ConnectionStrings:redis is set, e.g. "localhost:6379" for a local container.
builder.Services.Configure<SemanticCacheOptions>(builder.Configuration.GetSection(SemanticCacheOptions.SectionName));
var redisConnectionString = builder.Configuration.GetConnectionString("redis");
if (!string.IsNullOrWhiteSpace(redisConnectionString) && builder.Configuration.GetValue("SemanticCache:Enabled", true))
{
    builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
    {
        var redisOptions = ConfigurationOptions.Parse(redisConnectionString);
        redisOptions.AbortOnConnectFail = false; // start even if Redis is down; the cache just misses until it's back
        return ConnectionMultiplexer.Connect(redisOptions);
    });
    builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp =>
    {
        var ai = sp.GetRequiredService<IOptions<AzureOpenAIOptions>>().Value;
        return sp.GetRequiredService<OpenAI.OpenAIClient>().GetEmbeddingClient(ai.EmbeddingDeployment).AsIEmbeddingGenerator(ai.EmbeddingDimensions);
    });
    builder.Services.AddSingleton<ISemanticCache, RedisSemanticCache>();
}
else
{
    builder.Services.AddSingleton<ISemanticCache, DisabledSemanticCache>();
}

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

api.MapGet("/cache/stats", (ISemanticCache cache, CancellationToken ct) => cache.GetStatsAsync(ct))
    .WithTags("Cache")
    .WithSummary("Semantic cache status: entries, hits/misses since start, threshold and TTL.");

api.MapDelete("/cache", async (ISemanticCache cache, CancellationToken ct) => Results.Ok(new { removed = await cache.ClearAsync(ct) }))
    .WithTags("Cache")
    .WithSummary("Removes every cached answer (e.g. after re-ingesting changed documents).");

api.MapGet("/agents", async (AgentCatalog catalog, CancellationToken ct) =>
    {
        var agents = await catalog.GetAsync(ct);
        return new { degraded = agents.Degraded, agents = agents.Info };
    })
    .WithSummary("Lists the agents and the tools each one can use.");

app.Run();
