using Rag.Core.Chunking;

namespace Rag.Core.Search;

public interface ISearchIndexService
{
    Task EnsureIndexAsync(bool recreate = false, CancellationToken cancellationToken = default);

    Task UploadAsync(IReadOnlyList<ChunkDocument> documents, CancellationToken cancellationToken = default);

    /// <summary>Removes previously indexed chunks of a document for one strategy, so re-ingestion doesn't leave stale chunks.</summary>
    Task<int> DeleteChunksAsync(string source, ChunkingStrategy strategy, CancellationToken cancellationToken = default);

    /// <summary>Hybrid (keyword + vector) search with semantic re-ranking, optionally filtered to one chunking strategy.</summary>
    Task<IReadOnlyList<SearchHit>> SearchAsync(string query, int top = 5, ChunkingStrategy? strategy = null, CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<string, long>> CountByStrategyAsync(CancellationToken cancellationToken = default);
}
