using System.Text;
using System.Text.RegularExpressions;

namespace Rag.Core.Chunking;

internal static partial class TextUtilities
{
    [GeneratedRegex(@"(?<=[.!?])\s+(?=[\p{Lu}\p{N}""'(\[])")]
    private static partial Regex SentenceBoundary();

    [GeneratedRegex(@"\n\s*\n")]
    private static partial Regex ParagraphBoundary();

    [GeneratedRegex(@"[ \t]+")]
    private static partial Regex HorizontalWhitespace();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ExcessBlankLines();

    /// <summary>Normalizes line endings and whitespace so every strategy sees the same input.</summary>
    public static string Normalize(string text)
    {
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        normalized = HorizontalWhitespace().Replace(normalized, " ");
        normalized = ExcessBlankLines().Replace(normalized, "\n\n");
        return normalized.Trim();
    }

    public static IReadOnlyList<string> SplitSentences(string text) =>
        SentenceBoundary().Split(text)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();

    public static IReadOnlyList<string> SplitParagraphs(string text) =>
        ParagraphBoundary().Split(text)
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToList();

    /// <summary>Hard split into windows of <paramref name="size"/> characters, stepping by size - overlap.</summary>
    public static IEnumerable<string> Window(string text, int size, int overlap)
    {
        var step = Math.Max(1, size - overlap);
        for (var start = 0; start < text.Length; start += step)
        {
            var length = Math.Min(size, text.Length - start);
            var piece = text.Substring(start, length).Trim();
            if (piece.Length > 0)
            {
                yield return piece;
            }

            if (start + length >= text.Length)
            {
                yield break;
            }
        }
    }

    /// <summary>
    /// Greedily packs units (sentences, paragraphs, ...) into chunks no longer than <paramref name="size"/>.
    /// Trailing units of the previous chunk (up to <paramref name="overlap"/> characters) are repeated at the
    /// start of the next chunk. Units that are themselves too large are hard-split with <see cref="Window"/>.
    /// </summary>
    public static List<string> Pack(IEnumerable<string> units, string separator, int size, int overlap)
    {
        var chunks = new List<string>();
        var current = new List<string>();
        var currentLength = 0;

        void Flush()
        {
            if (current.Count == 0)
            {
                return;
            }

            chunks.Add(string.Join(separator, current));

            // Carry over trailing units as overlap for the next chunk.
            var carried = new List<string>();
            var carriedLength = 0;
            for (var i = current.Count - 1; i >= 0 && overlap > 0; i--)
            {
                var add = current[i].Length + (carried.Count > 0 ? separator.Length : 0);
                if (carriedLength + add > overlap)
                {
                    break;
                }

                carried.Insert(0, current[i]);
                carriedLength += add;
            }

            current = carried;
            currentLength = carriedLength;
        }

        foreach (var unit in units)
        {
            if (unit.Length > size)
            {
                Flush();
                current.Clear();
                currentLength = 0;
                chunks.AddRange(Window(unit, size, overlap));
                continue;
            }

            var added = unit.Length + (current.Count > 0 ? separator.Length : 0);
            if (currentLength + added > size && current.Count > 0)
            {
                var before = current.Count;
                Flush();

                // If the overlap alone leaves no room for this unit, drop the overlap.
                if (currentLength + unit.Length + separator.Length > size || current.Count == before)
                {
                    current.Clear();
                    currentLength = 0;
                }

                added = unit.Length + (current.Count > 0 ? separator.Length : 0);
            }

            current.Add(unit);
            currentLength += added;
        }

        if (current.Count > 0)
        {
            chunks.Add(string.Join(separator, current));
        }

        return chunks;
    }

    public static IReadOnlyList<TextChunk> ToChunks(IEnumerable<string> pieces, string? section = null) =>
        pieces.Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .Select((p, i) => new TextChunk(p, i, section))
            .ToList();

    public static string JoinWithSpace(IEnumerable<string> parts)
    {
        var sb = new StringBuilder();
        foreach (var part in parts)
        {
            if (sb.Length > 0)
            {
                sb.Append(' ');
            }

            sb.Append(part);
        }

        return sb.ToString();
    }
}
