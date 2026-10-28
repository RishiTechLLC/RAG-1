using Agents.Api.SemanticCache;
using Agents.Api.Tools;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
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
    long? OutputTokens,
    bool Cached = false);

/// <summary>
/// Runs a chat turn against the orchestrator (or a specific specialist) and keeps conversation sessions.
/// The first question of a conversation is looked up in the semantic cache; grounded document answers are
/// stored there. Answers that used MCP (student records, live data) are never cached.
/// </summary>
public sealed class ChatService(AgentCatalog catalog, IMemoryCache sessions, ISemanticCache cache, ILogger<ChatService> logger)
{
    private const string CacheTool = "semantic_cache";
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
        var prefixKey = sessionKey + ":prefix";
        var started = TimeProvider.System.GetTimestamp();

        // Only a conversation's opening question is self-contained enough to answer from the cache.
        var isFirstTurn = !sessions.TryGetValue(sessionKey, out AgentSession? session) && !sessions.TryGetValue(prefixKey, out object? _);
        if (isFirstTurn && cache.IsEnabled)
        {
            var hit = await cache.LookupAsync(agentName, request.Message, cancellationToken);
            if (hit is not null)
            {
                // Remember the exchange so a follow-up question still has its context.
                sessions.Set(prefixKey, new List<ChatMessage>
                {
                    new(ChatRole.User, request.Message),
                    new(ChatRole.Assistant, hit.Answer),
                }, SessionLifetime);

                var elapsed = (long)TimeProvider.System.GetElapsedTime(started).TotalMilliseconds;
                var step = new TraceStep(agentName, CacheTool, "cache",
                    $"{{\"similarity\":{hit.Similarity:F3},\"cachedQuestion\":\"{Escape(hit.Question)}\"}}", elapsed, true, DateTimeOffset.UtcNow);
                return new ChatResponse(conversationId, hit.Answer, agentName, [step], elapsed, 0, 0, Cached: true);
            }
        }

        if (session is null)
        {
            session = await agent.CreateSessionAsync(cancellationToken);
            sessions.Set(sessionKey, session, SessionLifetime);
        }

        var steps = AgentTrace.Begin();
        using var agentScope = AgentTrace.EnterAgent(agentName);

        AgentResponse response;
        if (sessions.TryGetValue(prefixKey, out List<ChatMessage>? prefix) && prefix is not null)
        {
            // The conversation started with a cached answer: replay it so the agent sees the earlier turn.
            sessions.Remove(prefixKey);
            response = await agent.RunAsync([.. prefix, new ChatMessage(ChatRole.User, request.Message)], session, cancellationToken: cancellationToken);
        }
        else
        {
            response = await agent.RunAsync(request.Message, session, cancellationToken: cancellationToken);
        }

        var orderedSteps = steps.OrderBy(s => s.StartedAt).ToArray();
        if (isFirstTurn && cache.IsEnabled && IsCacheable(response.Text, orderedSteps))
        {
            await cache.StoreAsync(agentName, request.Message, response.Text, cancellationToken);
            logger.LogInformation("Stored answer in the semantic cache for {Agent}", agentName);
        }

        return new ChatResponse(
            conversationId,
            response.Text,
            agentName,
            orderedSteps,
            (long)TimeProvider.System.GetElapsedTime(started).TotalMilliseconds,
            response.Usage?.InputTokenCount,
            response.Usage?.OutputTokenCount);
    }

    /// <summary>
    /// Cache only answers grounded in the document knowledge base: every step succeeded, no MCP/campus data was
    /// used (live and personal data must not be served to other users), and the search actually found passages
    /// (so "I couldn't find that" isn't cached and outlive a later ingestion).
    /// </summary>
    internal static bool IsCacheable(string reply, IReadOnlyList<TraceStep> steps) =>
        !string.IsNullOrWhiteSpace(reply)
        && AgentTrace.KnowledgePassagesFound > 0
        && steps.All(s => s.Succeeded)
        && !steps.Any(s => s.Kind == "mcp-tool" || s.Target == AgentNames.CampusData);

    private static string Escape(string value) =>
        System.Text.Json.JsonEncodedText.Encode(value.Length > 200 ? value[..200] + "…" : value).ToString();

    public void Reset(string conversationId)
    {
        foreach (var agent in AgentAliases.Values.Distinct())
        {
            sessions.Remove($"{conversationId}:{agent}");
            sessions.Remove($"{conversationId}:{agent}:prefix");
        }
    }
}
