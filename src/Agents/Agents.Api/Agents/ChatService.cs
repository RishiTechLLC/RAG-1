using Agents.Api.Tools;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Caching.Memory;

namespace Agents.Api;

public sealed record ChatRequest(string Message, string? ConversationId = null, string? Agent = null);

public sealed record ChatResponse(
    string ConversationId,
    string Reply,
    string Agent,
    IReadOnlyList<TraceStep> Steps,
    long DurationMs,
    long? InputTokens,
    long? OutputTokens);

/// <summary>Runs a chat turn against the orchestrator (or a specific specialist) and keeps conversation sessions.</summary>
public sealed class ChatService(AgentCatalog catalog, IMemoryCache sessions)
{
    private static readonly MemoryCacheEntryOptions SessionLifetime = new() { SlidingExpiration = TimeSpan.FromMinutes(30) };

    public static readonly IReadOnlyDictionary<string, string> AgentAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["auto"] = AgentNames.Orchestrator,
        [AgentNames.Orchestrator] = AgentNames.Orchestrator,
        ["knowledge"] = AgentNames.Knowledge,
        [AgentNames.Knowledge] = AgentNames.Knowledge,
        ["campus"] = AgentNames.CampusData,
        [AgentNames.CampusData] = AgentNames.CampusData,
        ["writer"] = AgentNames.Writer,
        [AgentNames.Writer] = AgentNames.Writer,
    };

    public async Task<ChatResponse> ChatAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        var agentName = AgentAliases.GetValueOrDefault(request.Agent ?? "auto", AgentNames.Orchestrator);
        var agents = await catalog.GetAsync(cancellationToken);
        var agent = agentName == AgentNames.Orchestrator ? agents.Orchestrator : agents.Specialists[agentName];

        var conversationId = string.IsNullOrWhiteSpace(request.ConversationId) ? Guid.NewGuid().ToString("n") : request.ConversationId;
        var sessionKey = $"{conversationId}:{agentName}";
        if (!sessions.TryGetValue(sessionKey, out AgentSession? session) || session is null)
        {
            session = await agent.CreateSessionAsync(cancellationToken);
            sessions.Set(sessionKey, session, SessionLifetime);
        }

        var steps = AgentTrace.Begin();
        using var _ = AgentTrace.EnterAgent(agentName);
        var started = TimeProvider.System.GetTimestamp();

        var response = await agent.RunAsync(request.Message, session, cancellationToken: cancellationToken);

        return new ChatResponse(
            conversationId,
            response.Text,
            agentName,
            steps.OrderBy(s => s.StartedAt).ToArray(),
            (long)TimeProvider.System.GetElapsedTime(started).TotalMilliseconds,
            response.Usage?.InputTokenCount,
            response.Usage?.OutputTokenCount);
    }

    public void Reset(string conversationId)
    {
        foreach (var agent in AgentAliases.Values.Distinct())
        {
            sessions.Remove($"{conversationId}:{agent}");
        }
    }
}
