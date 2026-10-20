namespace Rag.Core.Chunking;

/// <summary>A piece of a document ready to be embedded.</summary>
/// <param name="Content">The chunk text.</param>
/// <param name="Index">Zero-based position of the chunk within its document.</param>
/// <param name="Section">Optional heading path (Markdown strategy).</param>
public sealed record TextChunk(string Content, int Index, string? Section = null);
