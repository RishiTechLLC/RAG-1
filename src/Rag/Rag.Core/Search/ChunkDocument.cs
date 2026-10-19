using System.Text.Json.Serialization;

namespace Rag.Core.Search;

/// <summary>Shape of a document in the Azure AI Search index (one per chunk per strategy).</summary>
public sealed class ChunkDocument
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("section")]
    public string? Section { get; set; }

    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    [JsonPropertyName("sourceUri")]
    public string SourceUri { get; set; } = string.Empty;

    [JsonPropertyName("chunkIndex")]
    public int ChunkIndex { get; set; }

    [JsonPropertyName("strategy")]
    public string Strategy { get; set; } = string.Empty;

    [JsonPropertyName("indexedAt")]
    public DateTimeOffset IndexedAt { get; set; }

    [JsonPropertyName("contentVector")]
    public ReadOnlyMemory<float> ContentVector { get; set; }
}

/// <summary>A search hit returned to callers (no vector).</summary>
public sealed record SearchHit(
    string Id,
    string Content,
    string Title,
    string? Section,
    string Source,
    string SourceUri,
    int ChunkIndex,
    string Strategy,
    double Score,
    double? RerankerScore);
