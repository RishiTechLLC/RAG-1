using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Rag.Core.Chunking;
using Rag.Core.Documents;
using Rag.Core.Ingestion;

namespace Rag.Functions;

/// <summary>
/// Runs the RAG pipeline for every file that is uploaded (or overwritten) in the documents container:
/// download -> extract text -> chunk -> embed -> upsert into Azure AI Search.
/// The chunking strategy comes from the blob's "strategy" metadata if present, otherwise Ingestion:Strategy.
/// Failures are retried by the Functions runtime (5 attempts) and then moved to the
/// "webjobs-blobtrigger-poison" queue.
/// </summary>
public sealed class DocumentIngestionFunction(
    IIngestionPipeline pipeline,
    IConfiguration configuration,
    ILogger<DocumentIngestionFunction> logger)
{
    internal const string StrategyMetadataKey = "strategy";

    [Function(nameof(IngestUploadedDocument))]
    public async Task IngestUploadedDocument(
        [BlobTrigger("%DocumentsContainer%/{name}", Connection = "DocumentsStorage")] BlobClient blob,
        string name,
        CancellationToken cancellationToken)
    {
        if (!DocumentTextExtractor.IsSupported(name))
        {
            logger.LogInformation("Skipping {Blob}: unsupported file type", name);
            return;
        }

        var properties = (await blob.GetPropertiesAsync(cancellationToken: cancellationToken)).Value;
        var strategy = ResolveStrategy(properties.Metadata, configuration["Ingestion:Strategy"]);

        string text;
        var download = await blob.DownloadStreamingAsync(cancellationToken: cancellationToken);
        await using (var content = download.Value.Content)
        {
            text = await DocumentTextExtractor.ExtractAsync(content, name, cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            logger.LogWarning("Skipping {Blob}: no text could be extracted (scanned PDF or empty file?)", name);
            return;
        }

        logger.LogInformation("Ingesting {Blob} ({Length} characters) with the {Strategy} strategy", name, text.Length, strategy);
        var document = new SourceDocument(name, blob.Uri.ToString(), properties.ContentType ?? "application/octet-stream", text, properties.LastModified);
        await pipeline.IngestDocumentAsync(document, strategy, cancellationToken);
    }

    internal static ChunkingStrategy ResolveStrategy(IDictionary<string, string> metadata, string? configured)
    {
        if (metadata.TryGetValue(StrategyMetadataKey, out var fromBlob) && ChunkingStrategyInfo.TryParse(fromBlob, out var blobStrategy))
        {
            return blobStrategy;
        }

        return ChunkingStrategyInfo.TryParse(configured, out var configuredStrategy) ? configuredStrategy : ChunkingStrategy.Markdown;
    }
}
