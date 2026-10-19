namespace Rag.Core.Chunking;

public sealed class SlidingWindowChunker : IChunker
{
    public ChunkingStrategy Strategy => ChunkingStrategy.SlidingWindow;

    public Task<IReadOnlyList<TextChunk>> ChunkAsync(string text, ChunkingOptions options, CancellationToken cancellationToken = default)
    {
        var normalized = TextUtilities.Normalize(text);
        return Task.FromResult(TextUtilities.ToChunks(TextUtilities.Window(normalized, options.ChunkSize, options.Overlap)));
    }
}
