namespace Rag.Core.Chunking;

public sealed class SentenceChunker : IChunker
{
    public ChunkingStrategy Strategy => ChunkingStrategy.Sentence;

    public Task<IReadOnlyList<TextChunk>> ChunkAsync(string text, ChunkingOptions options, CancellationToken cancellationToken = default)
    {
        var normalized = TextUtilities.Normalize(text).Replace('\n', ' ');
        var sentences = TextUtilities.SplitSentences(normalized);
        var packed = TextUtilities.Pack(sentences, " ", options.ChunkSize, options.Overlap);
        return Task.FromResult(TextUtilities.ToChunks(packed));
    }
}
