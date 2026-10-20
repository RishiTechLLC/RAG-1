namespace Rag.Core.Documents;

/// <summary>A document downloaded from storage with its extracted plain text.</summary>
public sealed record SourceDocument(
    string Name,
    string Uri,
    string ContentType,
    string Text,
    DateTimeOffset? LastModified);
