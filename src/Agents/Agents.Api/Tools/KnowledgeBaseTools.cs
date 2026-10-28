using System.ComponentModel;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace Agents.Api.Tools;

/// <summary>
/// Function tool that queries the vector database through Rag.Api. The chunking strategy is deliberately
/// not a tool parameter: the model would guess one and filter out every chunk ingested with another.
/// Set Downstream:KnowledgeBaseStrategy to pin a strategy; leave it empty to search all of them.
/// </summary>
public sealed class KnowledgeBaseTools(HttpClient http, IOptions<DownstreamOptions> options)
{
    [Description("Search the document knowledge base (Azure AI Search vector index) for passages relevant to the query. Returns numbered passages with their source document.")]
    public async Task<string> SearchKnowledgeBase(
        [Description("A focused search query, rewritten from the user's question if needed.")] string query,
        [Description("Number of passages to return (1-10).")] int top = 5,
        CancellationToken cancellationToken = default)
    {
        var strategy = string.IsNullOrWhiteSpace(options.Value.KnowledgeBaseStrategy) ? null : options.Value.KnowledgeBaseStrategy;
        var response = await http.PostAsJsonAsync("api/search", new { query, strategy, top = Math.Clamp(top, 1, 10) }, cancellationToken);
        response.EnsureSuccessStatusCode();
        var hits = await response.Content.ReadFromJsonAsync<List<Hit>>(cancellationToken) ?? [];
        AgentTrace.RecordKnowledgePassages(hits.Count);
        if (hits.Count == 0)
        {
            return strategy is null
                ? "No relevant passages were found in the knowledge base."
                : $"No relevant passages were found in the knowledge base (searched only chunks indexed with the '{strategy}' strategy).";
        }

        var sb = new StringBuilder();
        for (var i = 0; i < hits.Count; i++)
        {
            var h = hits[i];
            sb.AppendLine($"[{i + 1}] source: {h.Source}{(h.Section is null ? string.Empty : $" > {h.Section}")} (strategy: {h.Strategy})");
            sb.AppendLine(h.Content);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private sealed record Hit(
        [property: JsonPropertyName("content")] string Content,
        [property: JsonPropertyName("source")] string Source,
        [property: JsonPropertyName("section")] string? Section,
        [property: JsonPropertyName("strategy")] string Strategy);
}
