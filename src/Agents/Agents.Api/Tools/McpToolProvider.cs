using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;

namespace Agents.Api.Tools;

/// <summary>
/// Connects to the MCP server and exposes its tools. Each <see cref="McpClientTool"/> is already an
/// AIFunction, so the Agent Framework can call it directly. Reconnects if the server was unavailable.
/// </summary>
public sealed class McpToolProvider(IOptions<DownstreamOptions> options, ILoggerFactory loggerFactory) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger _logger = loggerFactory.CreateLogger<McpToolProvider>();
    private McpClient? _client;
    private IReadOnlyList<McpClientTool>? _tools;

    public async Task<IReadOnlyList<McpClientTool>> GetToolsAsync(CancellationToken cancellationToken)
    {
        if (_tools is not null)
        {
            return _tools;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_tools is not null)
            {
                return _tools;
            }

            var baseUrl = options.Value.McpServerUrl.TrimEnd('/');
            var transportOptions = new HttpClientTransportOptions
            {
                Endpoint = new Uri($"{baseUrl}/mcp"),
                Name = "campus-data",
            };
            if (!string.IsNullOrWhiteSpace(options.Value.McpApiKey))
            {
                transportOptions.AdditionalHeaders = new Dictionary<string, string> { ["X-Api-Key"] = options.Value.McpApiKey };
            }

            _client = await McpClient.CreateAsync(new HttpClientTransport(transportOptions, loggerFactory), loggerFactory: loggerFactory, cancellationToken: cancellationToken);
            var tools = await _client.ListToolsAsync(cancellationToken: cancellationToken);
            _logger.LogInformation("Connected to MCP server {Endpoint} with {Count} tools: {Tools}",
                transportOptions.Endpoint, tools.Count, string.Join(", ", tools.Select(t => t.Name)));
            return _tools = tools.ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
        {
            await _client.DisposeAsync();
        }

        _gate.Dispose();
    }
}
