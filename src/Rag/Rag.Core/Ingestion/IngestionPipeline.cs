using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Rag.Core.Chunking;
using Rag.Core.Documents;
using Rag.Core.Search;

namespace Rag.Core.Ingestion;

public interface IIngestionPipeline
{
    /// <summary>Ingests every document in the container (optionally under a prefix) with one strategy.</summary>
    Task<IngestionResult> RunAsync(IngestionRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Ingests a single, already-downloaded document (used by the blob trigger). Replaces any chunks the
    /// document previously had for the same strategy, so re-uploading a file updates it in the index.
    /// </summary>
    Task<DocumentIngestionResult> IngestDocumentAsync(SourceDocument document, ChunkingStrategy strategy, CancellationToken cancellationToken = default);
}

/// <summary>Blob Storage -> text extraction -> chunking (selected strategy) -> embeddings -> Azure AI Search.</summary>
public sealed class IngestionPipeline(
    IDocumentSource documents,
    IChunkerFactory chunkers,
    IEmbeddingGenerator<string, Embedding<float>> embeddings,
    ISearchIndexService index,
    IOptions<ChunkingOptions> defaultChunking,
    ILogger<IngestionPipeline> logger) : IIngestionPipeline
{
    private const int EmbeddingBatchSize = 64;
    private volatile bool _indexEnsured;

    public async Task<DocumentIngestionResult> IngestDocumentAsync(SourceDocument document, ChunkingStrategy strategy, CancellationToken cancellationToken = default)
    {
        var options = new ChunkingOptions
        {
            ChunkSize = defaultChunking.Value.ChunkSize,
            Overlap = defaultChunking.Value.Overlap,
            SemanticBreakpointPercentile = defaultChunking.Value.SemanticBreakpointPercentile,
        };
        options.Validate();

        if (!_indexEnsured)
        {
            await index.EnsureIndexAsync(recreate: false, cancellationToken);
            _indexEnsured = true;
        }

        return await IngestAsync(document, strategy, chunkers.Get(strategy), options, deleteStale: true, cancellationToken);
    }

    public async Task<IngestionResult> RunAsync(IngestionRequest request, CancellationToken cancellationToken = default)
    {
        var options = new ChunkingOptions
        {
            ChunkSize = request.ChunkSize ?? defaultChunking.Value.ChunkSize,
            Overlap = request.Overlap ?? defaultChunking.Value.Overlap,
            SemanticBreakpointPercentile = defaultChunking.Value.SemanticBreakpointPercentile,
        };
        options.Validate();

        var stopwatch = Stopwatch.StartNew();
        var chunker = chunkers.Get(request.Strategy);
        logger.LogInformation(
            "Ingesting with strategy {Strategy} (size {Size}, overlap {Overlap}, prefix '{Prefix}')",
            request.Strategy, options.ChunkSize, options.Overlap, request.Prefix ?? string.Empty);

        await index.EnsureIndexAsync(request.RecreateIndex, cancellationToken);

        var details = new List<DocumentIngestionResult>();
        await foreach (var document in documents.GetDocumentsAsync(request.Prefix, cancellationToken))
        {
            details.Add(await IngestAsync(document, request.Strategy, chunker, options, deleteStale: !request.RecreateIndex, cancellationToken));
        }

        stopwatch.Stop();
        var result = new IngestionResult(request.Strategy, details.Count, details.Sum(d => d.Chunks), stopwatch.Elapsed, details);
        logger.LogInformation("Ingestion finished: {Documents} documents, {Chunks} chunks in {Duration}", result.Documents, result.Chunks, result.Duration);
        return result;
    }

    private async Task<DocumentIngestionResult> IngestAsync(
        SourceDocument document, ChunkingStrategy strategy, IChunker chunker, ChunkingOptions options, bool deleteStale, CancellationToken cancellationToken)
    {
        var chunks = await chunker.ChunkAsync(document.Text, options, cancellationToken);
        var deleted = deleteStale ? await index.DeleteChunksAsync(document.Name, strategy, cancellationToken) : 0;

        var indexed = new List<ChunkDocument>(chunks.Count);
        foreach (var batch in chunks.Chunk(EmbeddingBatchSize))
        {
            var vectors = await embeddings.GenerateAsync(batch.Select(c => c.Content), cancellationToken: cancellationToken);
            indexed.AddRange(batch.Zip(vectors, (chunk, vector) => new ChunkDocument
            {
                Id = CreateId(document.Name, strategy, chunk.Index),
                Content = chunk.Content,
                Title = Path.GetFileNameWithoutExtension(document.Name),
                Section = chunk.Section,
                Source = document.Name,
                SourceUri = document.Uri,
                ChunkIndex = chunk.Index,
                Strategy = strategy.ToString(),
                IndexedAt = DateTimeOffset.UtcNow,
                ContentVector = vector.Vector,
            }));
        }

        await index.UploadAsync(indexed, cancellationToken);
        logger.LogInformation("Indexed {Document} with {Strategy}: {Chunks} chunks ({Deleted} stale chunks removed)",
            document.Name, strategy, indexed.Count, deleted);
        return new DocumentIngestionResult(document.Name, indexed.Count, deleted);
    }

    /// <summary>Search keys only allow letters, digits, '_', '-' and '='; hash the blob name to stay within that set.</summary>
    internal static string CreateId(string source, ChunkingStrategy strategy, int chunkIndex)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)))[..24].ToLowerInvariant();
        return $"{hash}-{strategy.ToString().ToLowerInvariant()}-{chunkIndex:D5}";
    }
}
