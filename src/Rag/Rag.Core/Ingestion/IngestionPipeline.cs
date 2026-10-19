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
    Task<IngestionResult> RunAsync(IngestionRequest request, CancellationToken cancellationToken = default);
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
            var chunks = await chunker.ChunkAsync(document.Text, options, cancellationToken);
            var deleted = request.RecreateIndex ? 0 : await index.DeleteChunksAsync(document.Name, request.Strategy, cancellationToken);

            var indexed = new List<ChunkDocument>(chunks.Count);
            foreach (var batch in chunks.Chunk(EmbeddingBatchSize))
            {
                var vectors = await embeddings.GenerateAsync(batch.Select(c => c.Content), cancellationToken: cancellationToken);
                indexed.AddRange(batch.Zip(vectors, (chunk, vector) => new ChunkDocument
                {
                    Id = CreateId(document.Name, request.Strategy, chunk.Index),
                    Content = chunk.Content,
                    Title = Path.GetFileNameWithoutExtension(document.Name),
                    Section = chunk.Section,
                    Source = document.Name,
                    SourceUri = document.Uri,
                    ChunkIndex = chunk.Index,
                    Strategy = request.Strategy.ToString(),
                    IndexedAt = DateTimeOffset.UtcNow,
                    ContentVector = vector.Vector,
                }));
            }

            await index.UploadAsync(indexed, cancellationToken);
            details.Add(new DocumentIngestionResult(document.Name, indexed.Count, deleted));
            logger.LogInformation("Indexed {Document}: {Chunks} chunks ({Deleted} stale chunks removed)", document.Name, indexed.Count, deleted);
        }

        stopwatch.Stop();
        var result = new IngestionResult(request.Strategy, details.Count, details.Sum(d => d.Chunks), stopwatch.Elapsed, details);
        logger.LogInformation("Ingestion finished: {Documents} documents, {Chunks} chunks in {Duration}", result.Documents, result.Chunks, result.Duration);
        return result;
    }

    /// <summary>Search keys only allow letters, digits, '_', '-' and '='; hash the blob name to stay within that set.</summary>
    internal static string CreateId(string source, ChunkingStrategy strategy, int chunkIndex)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)))[..24].ToLowerInvariant();
        return $"{hash}-{strategy.ToString().ToLowerInvariant()}-{chunkIndex:D5}";
    }
}
