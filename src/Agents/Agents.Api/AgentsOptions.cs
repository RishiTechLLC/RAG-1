namespace Agents.Api;

public sealed class AzureOpenAIOptions
{
    public const string SectionName = "AzureOpenAI";

    public string Endpoint { get; set; } = string.Empty;

    public string? ApiKey { get; set; }

    public string ChatDeployment { get; set; } = "gpt-5-mini";
}

public sealed class DownstreamOptions
{
    /// <summary>Base URL of Rag.Api (vector database search).</summary>
    public string RagApiUrl { get; set; } = string.Empty;

    /// <summary>Base URL of the MCP server; the MCP endpoint is {McpServerUrl}/mcp.</summary>
    public string McpServerUrl { get; set; } = string.Empty;

    /// <summary>Optional shared secret sent to the MCP server as X-Api-Key.</summary>
    public string? McpApiKey { get; set; }
}
