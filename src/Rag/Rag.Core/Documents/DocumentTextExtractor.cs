using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml.Packaging;
using UglyToad.PdfPig;

namespace Rag.Core.Documents;

/// <summary>Turns supported file formats into plain text (PDF, Word, Markdown, HTML, text, CSV, JSON).</summary>
public static partial class DocumentTextExtractor
{
    private static readonly HashSet<string> PlainTextExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".txt", ".md", ".markdown", ".csv", ".json", ".xml", ".yaml", ".yml", ".log" };

    [GeneratedRegex(@"<(script|style)[^>]*>.*?</\1>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ScriptOrStyle();

    [GeneratedRegex(@"<br\s*/?>|</(p|div|h[1-6]|li|tr)>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockEnd();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tag();

    public static bool IsSupported(string fileName)
    {
        var ext = Path.GetExtension(fileName);
        return PlainTextExtensions.Contains(ext)
            || ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".docx", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".html", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".htm", StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<string> ExtractAsync(Stream content, string fileName, CancellationToken cancellationToken = default)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();

        // PdfPig and OpenXml need a seekable stream.
        await using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        buffer.Position = 0;

        return ext switch
        {
            ".pdf" => ExtractPdf(buffer),
            ".docx" => ExtractDocx(buffer),
            ".html" or ".htm" => ExtractHtml(Encoding.UTF8.GetString(buffer.ToArray())),
            _ => Encoding.UTF8.GetString(buffer.ToArray()),
        };
    }

    private static string ExtractPdf(Stream stream)
    {
        using var pdf = PdfDocument.Open(stream);
        var sb = new StringBuilder();
        foreach (var page in pdf.GetPages())
        {
            sb.AppendLine(page.Text);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string ExtractDocx(Stream stream)
    {
        using var doc = WordprocessingDocument.Open(stream, isEditable: false);
        var body = doc.MainDocumentPart?.Document?.Body;
        if (body is null)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        foreach (var paragraph in body.Descendants<DocumentFormat.OpenXml.Wordprocessing.Paragraph>())
        {
            sb.AppendLine(paragraph.InnerText);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    internal static string ExtractHtml(string html)
    {
        var text = ScriptOrStyle().Replace(html, string.Empty);
        text = BlockEnd().Replace(text, "\n\n");
        text = Tag().Replace(text, string.Empty);
        return WebUtility.HtmlDecode(text);
    }
}
