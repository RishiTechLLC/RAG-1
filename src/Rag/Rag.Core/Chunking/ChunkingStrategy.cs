namespace Rag.Core.Chunking;

/// <summary>The chunking strategies that can be selected when running ingestion.</summary>
public enum ChunkingStrategy
{
    /// <summary>Fixed number of characters, no overlap. Fast and predictable.</summary>
    FixedSize,

    /// <summary>Fixed number of characters with an overlapping window so context is not cut mid-thought.</summary>
    SlidingWindow,

    /// <summary>Packs whole sentences into chunks up to the size limit.</summary>
    Sentence,

    /// <summary>Packs whole paragraphs (blank-line separated) into chunks up to the size limit.</summary>
    Paragraph,

    /// <summary>LangChain-style recursive split: paragraphs, then lines, then sentences, then words.</summary>
    Recursive,

    /// <summary>Splits Markdown on headings and keeps the heading path with each chunk.</summary>
    Markdown,

    /// <summary>Embeds each sentence and starts a new chunk where the meaning shifts (embedding distance spikes).</summary>
    Semantic,
}

public static class ChunkingStrategyInfo
{
    public static IReadOnlyDictionary<ChunkingStrategy, string> Descriptions { get; } = new Dictionary<ChunkingStrategy, string>
    {
        [ChunkingStrategy.FixedSize] = "Fixed-size character windows, no overlap",
        [ChunkingStrategy.SlidingWindow] = "Fixed-size character windows with overlap",
        [ChunkingStrategy.Sentence] = "Whole sentences packed up to the chunk size",
        [ChunkingStrategy.Paragraph] = "Whole paragraphs packed up to the chunk size",
        [ChunkingStrategy.Recursive] = "Recursive split on paragraphs > lines > sentences > words",
        [ChunkingStrategy.Markdown] = "Heading-aware split that keeps the section path",
        [ChunkingStrategy.Semantic] = "Embedding-based split where the topic changes",
    };

    public static bool TryParse(string? value, out ChunkingStrategy strategy)
    {
        strategy = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Replace("-", string.Empty).Replace("_", string.Empty);
        if (int.TryParse(normalized, out _))
        {
            // Reject bare numbers so "--strategy 3" doesn't silently map to an enum ordinal.
            return false;
        }

        return Enum.TryParse(normalized, ignoreCase: true, out strategy) && Enum.IsDefined(strategy);
    }
}
