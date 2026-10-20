namespace Rag.Core.Chunking;

public interface IChunker
{
    ChunkingStrategy Strategy { get; }

    Task<IReadOnlyList<TextChunk>> ChunkAsync(string text, ChunkingOptions options, CancellationToken cancellationToken = default);
}
