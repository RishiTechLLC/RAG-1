using Microsoft.Extensions.Options;
using Rag.Core.Chunking;
using Rag.Core.Ingestion;
using Rag.Core.Search;

namespace Rag.Api;

public sealed class AutoIngestOptions
{
    public const string SectionName = "Rag:AutoIngest";

    /// <summary>Ingest on startup when the search index is empty. On in Development, off otherwise.</summary>
    public bool Enabled { get; set; }

    public string Strategy { get; set; } = nameof(ChunkingStrategy.Markdown);
}

/// <summary>
/// Makes a fresh local environment chat-ready: after startup, if the search index has no chunks yet,
/// runs the ingestion pipeline once with the configured strategy. Failures are logged, never fatal.
/// </summary>
public sealed class AutoIngestionService(
    IServiceProvider services,
    IOptions<AutoIngestOptions> options,
    ILogger<AutoIngestionService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            return;
        }

        if (!ChunkingStrategyInfo.TryParse(options.Value.Strategy, out var strategy))
        {
            logger.LogWarning("Auto-ingestion skipped: unknown strategy '{Strategy}' in {Section}:Strategy", options.Value.Strategy, AutoIngestOptions.SectionName);
            return;
        }

        try
        {
            var index = services.GetRequiredService<ISearchIndexService>();
            var counts = await index.CountByStrategyAsync(stoppingToken);
            if (counts.Values.Sum() > 0)
            {
                logger.LogInformation("Auto-ingestion skipped: index already has chunks ({Counts})",
                    string.Join(", ", counts.Select(c => $"{c.Key}={c.Value}")));
                return;
            }

            logger.LogInformation("Search index is empty; auto-ingesting documents with the {Strategy} strategy", strategy);
            var result = await services.GetRequiredService<IIngestionPipeline>()
                .RunAsync(new IngestionRequest(strategy), stoppingToken);
            logger.LogInformation("Auto-ingestion done: {Documents} documents, {Chunks} chunks in {Duration}",
                result.Documents, result.Chunks, result.Duration);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Auto-ingestion failed. Check the Search:Endpoint / AzureOpenAI settings and your RBAC roles, then call POST /api/ingest manually.");
        }
    }
}
