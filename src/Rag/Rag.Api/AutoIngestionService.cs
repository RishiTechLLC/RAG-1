using Microsoft.Extensions.Options;
using Rag.Core.Chunking;
using Rag.Core.Documents;
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
/// Makes a fresh local environment chat-ready, in the background so the API starts immediately:
///   1. Development only: uploads Storage:SeedFolder (samples/documents) to the container, retrying while
///      the storage emulator is still starting (npx downloads Azurite on first run).
///   2. If the search index has no chunks yet, runs the ingestion pipeline once with the configured strategy.
/// Failures are logged, never fatal.
/// </summary>
public sealed class AutoIngestionService(
    IServiceProvider services,
    IOptions<AutoIngestOptions> options,
    IConfiguration configuration,
    IHostEnvironment environment,
    ILogger<AutoIngestionService> logger) : BackgroundService
{
    private static readonly TimeSpan StorageWaitTimeout = TimeSpan.FromMinutes(3);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (environment.IsDevelopment() && configuration["Storage:SeedFolder"] is { Length: > 0 } folder)
        {
            if (!await SeedSampleDocumentsAsync(Path.GetFullPath(Path.Combine(environment.ContentRootPath, folder)), stoppingToken))
            {
                return;
            }
        }

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

    /// <returns>false when storage never became reachable (ingestion would fail too).</returns>
    private async Task<bool> SeedSampleDocumentsAsync(string folder, CancellationToken stoppingToken)
    {
        if (!Directory.Exists(folder))
        {
            return true;
        }

        var uploader = services.GetRequiredService<DocumentUploader>();
        var deadline = DateTimeOffset.UtcNow + StorageWaitTimeout;
        while (true)
        {
            try
            {
                var uploaded = await uploader.UploadFolderAsync(folder, overwrite: false, stoppingToken);
                logger.LogInformation("Seeded {Count} sample document(s) from {Folder}", uploaded, folder);
                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && DateTimeOffset.UtcNow < deadline)
            {
                logger.LogInformation("Blob storage not reachable yet ({Message}); retrying in 5s", ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not upload sample documents: blob storage unreachable. Check the Storage settings / the azurite resource.");
                return false;
            }
        }
    }
}
