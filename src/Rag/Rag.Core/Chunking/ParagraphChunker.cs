namespace Rag.Core.Chunking;

public sealed class ParagraphChunker : IChunker
{
    public ChunkingStrategy Strategy => ChunkingStrategy.Paragraph;

    public Task<IReadOnlyList<TextChunk>> ChunkAsync(string text, ChunkingOptions options, CancellationToken cancellationToken = default)
    {
        var paragraphs = TextUtilities.SplitParagraphs(TextUtilities.Normalize(text));
        var packed = TextUtilities.Pack(paragraphs, "\n\n", options.ChunkSize, options.Overlap);
        return Task.FromResult(TextUtilities.ToChunks(packed));
    }
}
