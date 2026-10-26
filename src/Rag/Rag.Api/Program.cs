using System.Text.Json.Serialization;
using Rag.Api;
using Rag.Core;
using Rag.Core.Chunking;
using Rag.Core.Documents;
using Rag.Core.Ingestion;
using Rag.Core.Search;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddRagCore(builder.Configuration);
builder.Services.Configure<AutoIngestOptions>(builder.Configuration.GetSection(AutoIngestOptions.SectionName));
builder.Services.AddHostedService<AutoIngestionService>();
// Return enums by name ("strategy": "FixedSize") instead of their number.
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();

var app = builder.Build();

app.UseExceptionHandler();
app.MapOpenApi();
app.MapDefaultEndpoints();

var api = app.MapGroup("/api").WithTags("RAG");

api.MapGet("/strategies", () => ChunkingStrategyInfo.Descriptions
        .Select(kv => new { name = kv.Key.ToString(), description = kv.Value }))
    .WithSummary("Lists the chunking strategies that can be used for ingestion and search filtering.");

api.MapGet("/documents", async (IDocumentSource source, string? prefix, CancellationToken ct) =>
        await source.ListDocumentNamesAsync(prefix, ct).ToListAsync(ct))
    .WithSummary("Lists the supported documents in the storage container.");

api.MapPost("/ingest", async (IngestRequestDto body, IIngestionPipeline pipeline, CancellationToken ct) =>
    {
        if (!ChunkingStrategyInfo.TryParse(body.Strategy, out var strategy))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["strategy"] = [$"Unknown strategy. Valid values: {string.Join(", ", Enum.GetNames<ChunkingStrategy>())}"],
            });
        }

        var result = await pipeline.RunAsync(
            new IngestionRequest(strategy, body.Prefix, body.ChunkSize, body.Overlap, body.RecreateIndex), ct);
        return Results.Ok(result);
    })
    .WithSummary("Ingests documents from Blob Storage into Azure AI Search using the chosen chunking strategy.");

api.MapPost("/search", async (SearchRequestDto body, ISearchIndexService search, IConfiguration config, CancellationToken ct) =>
    {
        var strategy = ResolveStrategy(body.Strategy, config);
        var hits = await search.SearchAsync(body.Query, body.Top ?? config.GetValue("Rag:DefaultTop", 5), strategy, ct);
        return Results.Ok(hits);
    })
    .WithSummary("Hybrid vector + keyword search with semantic ranking. Optionally filter by chunking strategy.");

api.MapPost("/ask", async (SearchRequestDto body, RagAnswerService rag, IConfiguration config, CancellationToken ct) =>
    {
        var strategy = ResolveStrategy(body.Strategy, config);
        var answer = await rag.AskAsync(body.Query, body.Top ?? config.GetValue("Rag:DefaultTop", 5), strategy, ct);
        return Results.Ok(answer);
    })
    .WithSummary("Retrieval-augmented answer with numbered citations.");

api.MapGet("/stats", async (ISearchIndexService search, CancellationToken ct) => await search.CountByStrategyAsync(ct))
    .WithSummary("Number of indexed chunks per chunking strategy.");

app.Run();

static ChunkingStrategy? ResolveStrategy(string? requested, IConfiguration config)
{
    var value = string.IsNullOrWhiteSpace(requested) ? config["Rag:DefaultStrategy"] : requested;
    return ChunkingStrategyInfo.TryParse(value, out var strategy) ? strategy : null;
}

internal sealed record IngestRequestDto(string Strategy, string? Prefix, int? ChunkSize, int? Overlap, bool RecreateIndex = false);

internal sealed record SearchRequestDto(string Query, int? Top, string? Strategy);
