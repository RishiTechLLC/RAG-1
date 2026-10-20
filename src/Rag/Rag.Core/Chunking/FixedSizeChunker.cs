namespace Rag.Core.Chunking;

public sealed class FixedSizeChunker : IChunker
{
    public ChunkingStrategy Strategy => ChunkingStrategy.FixedSize;

    public Task<IReadOnlyList<TextChunk>> ChunkAsync(string text, ChunkingOptions options, CancellationToken cancellationToken = default)
    {
        var normalized = TextUtilities.Normalize(text);
        return Task.FromResult(TextUtilities.ToChunks(TextUtilities.Window(normalized, options.ChunkSize, overlap: 0)));
    }
}
