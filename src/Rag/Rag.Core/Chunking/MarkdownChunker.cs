using System.Text;
using System.Text.RegularExpressions;

namespace Rag.Core.Chunking;

/// <summary>
/// Splits on Markdown headings. Each chunk carries its heading path (e.g. "Guide > Setup > Azure")
/// both as <see cref="TextChunk.Section"/> and as a prefix in the text, which improves retrieval.
/// Sections that exceed the chunk size are split further with the recursive strategy.
/// Plain text without headings degrades gracefully to recursive chunking.
/// </summary>
public sealed partial class MarkdownChunker : IChunker
{
    [GeneratedRegex(@"^(#{1,6})\s+(.+?)\s*#*\s*$")]
    private static partial Regex Heading();

    public ChunkingStrategy Strategy => ChunkingStrategy.Markdown;

    public Task<IReadOnlyList<TextChunk>> ChunkAsync(string text, ChunkingOptions options, CancellationToken cancellationToken = default)
    {
        var normalized = TextUtilities.Normalize(text);
        var sections = SplitSections(normalized);
        var chunks = new List<TextChunk>();

        foreach (var (path, body) in sections)
        {
            if (string.IsNullOrWhiteSpace(body))
            {
                continue;
            }

            var prefix = string.IsNullOrEmpty(path) ? string.Empty : path + "\n\n";
            var budget = Math.Max(100, options.ChunkSize - prefix.Length);
            var subOptions = new ChunkingOptions
            {
                ChunkSize = budget,
                Overlap = Math.Min(options.Overlap, budget / 2),
                SemanticBreakpointPercentile = options.SemanticBreakpointPercentile,
            };

            foreach (var piece in RecursiveChunker.Split(body.Trim(), 0, subOptions))
            {
                chunks.Add(new TextChunk(prefix + piece, chunks.Count, string.IsNullOrEmpty(path) ? null : path));
            }
        }

        return Task.FromResult<IReadOnlyList<TextChunk>>(chunks);
    }

    private static List<(string Path, string Body)> SplitSections(string text)
    {
        var result = new List<(string, string)>();
        var headings = new string?[7];
        var body = new StringBuilder();
        var currentPath = string.Empty;
        var inCodeFence = false;

        foreach (var line in text.Split('\n'))
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inCodeFence = !inCodeFence;
            }

            var match = inCodeFence ? Match.Empty : Heading().Match(line);
            if (!match.Success)
            {
                body.AppendLine(line);
                continue;
            }

            result.Add((currentPath, body.ToString()));
            body.Clear();

            var level = match.Groups[1].Value.Length;
            headings[level] = match.Groups[2].Value.Trim();
            for (var i = level + 1; i < headings.Length; i++)
            {
                headings[i] = null;
            }

            currentPath = string.Join(" > ", headings.Where(h => h is not null));
        }

        result.Add((currentPath, body.ToString()));
        return result;
    }
}
