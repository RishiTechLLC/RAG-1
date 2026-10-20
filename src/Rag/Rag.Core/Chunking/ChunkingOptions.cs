namespace Rag.Core.Chunking;

public sealed class ChunkingOptions
{
    public const string SectionName = "Chunking";

    /// <summary>Target maximum chunk length in characters (~4 characters per token).</summary>
    public int ChunkSize { get; set; } = 1200;

    /// <summary>Characters shared between consecutive chunks (SlidingWindow, Recursive, Sentence, Paragraph).</summary>
    public int Overlap { get; set; } = 200;

    /// <summary>
    /// Semantic strategy: a break is placed where the cosine distance between neighbouring sentences
    /// is above this percentile of all distances in the document (0-100).
    /// </summary>
    public double SemanticBreakpointPercentile { get; set; } = 90;

    public void Validate()
    {
        if (ChunkSize < 100)
        {
            throw new ArgumentOutOfRangeException(nameof(ChunkSize), ChunkSize, "Chunk size must be at least 100 characters.");
        }

        if (Overlap < 0 || Overlap >= ChunkSize)
        {
            throw new ArgumentOutOfRangeException(nameof(Overlap), Overlap, "Overlap must be >= 0 and smaller than the chunk size.");
        }

        if (SemanticBreakpointPercentile is <= 0 or >= 100)
        {
            throw new ArgumentOutOfRangeException(nameof(SemanticBreakpointPercentile), SemanticBreakpointPercentile, "Percentile must be between 0 and 100.");
        }
    }
}
