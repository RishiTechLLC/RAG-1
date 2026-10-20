using Rag.Core.Chunking;

namespace Rag.Core.Ingestion;

public sealed record IngestionRequest(
    ChunkingStrategy Strategy,
    string? Prefix = null,
    int? ChunkSize = null,
    int? Overlap = null,
    bool RecreateIndex = false);

public sealed record DocumentIngestionResult(string Document, int Chunks, int DeletedChunks);

public sealed record IngestionResult(
    ChunkingStrategy Strategy,
    int Documents,
    int Chunks,
    TimeSpan Duration,
    IReadOnlyList<DocumentIngestionResult> Details);
