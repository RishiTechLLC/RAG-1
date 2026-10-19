namespace Rag.Core.Chunking;

/// <summary>
/// Tries the coarsest separator first and only descends to finer separators for pieces that are
/// still too large, so natural boundaries are preserved wherever possible.
/// </summary>
public sealed class RecursiveChunker : IChunker
{
    private static readonly string[] Separators = ["\n\n", "\n", ". ", " "];

    public ChunkingStrategy Strategy => ChunkingStrategy.Recursive;

    public Task<IReadOnlyList<TextChunk>> ChunkAsync(string text, ChunkingOptions options, CancellationToken cancellationToken = default)
    {
        var normalized = TextUtilities.Normalize(text);
        return Task.FromResult(TextUtilities.ToChunks(Split(normalized, 0, options)));
    }

    internal static List<string> Split(string text, int separatorIndex, ChunkingOptions options)
    {
        if (text.Length <= options.ChunkSize)
        {
            return [text];
        }

        if (separatorIndex >= Separators.Length)
        {
            return TextUtilities.Window(text, options.ChunkSize, options.Overlap).ToList();
        }

        var separator = Separators[separatorIndex];
        var parts = text.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length <= 1)
        {
            return Split(text, separatorIndex + 1, options);
        }

        // Re-split any part that is still too big with the next (finer) separator, then pack.
        var units = new List<string>();
        foreach (var part in parts)
        {
            if (part.Length > options.ChunkSize)
            {
                units.AddRange(Split(part, separatorIndex + 1, options));
            }
            else
            {
                units.Add(separator == ". " && !part.EndsWith('.') ? part + "." : part);
            }
        }

        var joiner = separator == ". " ? " " : separator;
        return TextUtilities.Pack(units, joiner, options.ChunkSize, options.Overlap);
    }
}
