namespace Rag.Core.Documents;

public interface IDocumentSource
{
    /// <summary>Streams the documents (optionally filtered by blob name prefix) with extracted text.</summary>
    IAsyncEnumerable<SourceDocument> GetDocumentsAsync(string? prefix = null, CancellationToken cancellationToken = default);

    /// <summary>Lists the document names without downloading them.</summary>
    IAsyncEnumerable<string> ListDocumentNamesAsync(string? prefix = null, CancellationToken cancellationToken = default);
}
