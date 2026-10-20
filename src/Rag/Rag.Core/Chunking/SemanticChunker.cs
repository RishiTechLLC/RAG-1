using System.Numerics.Tensors;
using Microsoft.Extensions.AI;

namespace Rag.Core.Chunking;

/// <summary>
/// Semantic chunking: embeds every sentence (with a small neighbour window for stability),
/// measures the cosine distance between consecutive sentences and starts a new chunk wherever the
/// distance exceeds the configured percentile. Chunks are still capped at the chunk size.
/// </summary>
public sealed class SemanticChunker(IEmbeddingGenerator<string, Embedding<float>> embeddings) : IChunker
{
    private const int EmbeddingBatchSize = 64;

    public ChunkingStrategy Strategy => ChunkingStrategy.Semantic;

    public async Task<IReadOnlyList<TextChunk>> ChunkAsync(string text, ChunkingOptions options, CancellationToken cancellationToken = default)
    {
        var sentences = TextUtilities.SplitSentences(TextUtilities.Normalize(text).Replace('\n', ' '));
        if (sentences.Count <= 2)
        {
            return TextUtilities.ToChunks(TextUtilities.Pack(sentences, " ", options.ChunkSize, 0));
        }

        // Embed each sentence together with its neighbours to smooth out very short sentences.
        var windows = sentences
            .Select((_, i) => TextUtilities.JoinWithSpace(sentences.Skip(Math.Max(0, i - 1)).Take(i == 0 ? 2 : 3)))
            .ToList();

        var vectors = new List<ReadOnlyMemory<float>>(windows.Count);
        foreach (var batch in windows.Chunk(EmbeddingBatchSize))
        {
            var generated = await embeddings.GenerateAsync(batch, cancellationToken: cancellationToken);
            vectors.AddRange(generated.Select(e => e.Vector));
        }

        var distances = new double[sentences.Count - 1];
        for (var i = 0; i < distances.Length; i++)
        {
            distances[i] = 1 - TensorPrimitives.CosineSimilarity(vectors[i].Span, vectors[i + 1].Span);
        }

        var threshold = Percentile(distances, options.SemanticBreakpointPercentile);
        return TextUtilities.ToChunks(Group(sentences, distances, threshold, options.ChunkSize));
    }

    internal static List<string> Group(IReadOnlyList<string> sentences, IReadOnlyList<double> distances, double threshold, int maxSize)
    {
        var groups = new List<string>();
        var current = new List<string> { sentences[0] };
        var length = sentences[0].Length;

        for (var i = 1; i < sentences.Count; i++)
        {
            var sentence = sentences[i];
            var topicShift = distances[i - 1] > threshold;
            if (topicShift || length + 1 + sentence.Length > maxSize)
            {
                groups.AddRange(TextUtilities.Pack(current, " ", maxSize, 0));
                current = [];
                length = 0;
            }

            current.Add(sentence);
            length += sentence.Length + (current.Count > 1 ? 1 : 0);
        }

        groups.AddRange(TextUtilities.Pack(current, " ", maxSize, 0));
        return groups;
    }

    internal static double Percentile(IReadOnlyList<double> values, double percentile)
    {
        var sorted = values.Order().ToArray();
        var rank = percentile / 100 * (sorted.Length - 1);
        var lower = (int)Math.Floor(rank);
        var upper = (int)Math.Ceiling(rank);
        return sorted[lower] + (sorted[upper] - sorted[lower]) * (rank - lower);
    }
}
