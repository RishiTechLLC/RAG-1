using Agents.Api.Tools;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agents.Api;

public static class AgentNames
{
    public const string Orchestrator = "orchestrator";
    public const string Knowledge = "knowledge_agent";
    public const string CampusData = "campus_data_agent";
    public const string Writer = "writer_agent";
}

public sealed record AgentInfo(string Name, string Description, IReadOnlyList<string> Tools);

public sealed record AgentSet(
    AIAgent Orchestrator,
    IReadOnlyDictionary<string, AIAgent> Specialists,
    IReadOnlyList<AgentInfo> Info,
    bool Degraded);

/// <summary>
/// Builds the multi-agent system with Microsoft Agent Framework:
/// <code>
///   orchestrator ──► knowledge_agent   ──► search_knowledge_base  (Rag.Api → Azure AI Search vectors)
///                ├─► campus_data_agent ──► MCP tools              (MCP server → Students API/SQL, Blob, calendar, weather)
///                └─► writer_agent                                 (pure LLM: drafts, summaries, rewrites)
/// </code>
/// Specialists are exposed to the orchestrator as function tools (the "agent-as-tool" pattern), so the
/// orchestrator can call one, several, or chain them (e.g. fetch data, then ask the writer to draft an email).
/// </summary>
public sealed class AgentCatalog(
    IChatClient chatClient,
    KnowledgeBaseTools knowledgeBase,
    McpToolProvider mcp,
    ILoggerFactory loggerFactory,
    IServiceProvider services,
    TimeProvider time)
{
    private static readonly TimeSpan DegradedRetryInterval = TimeSpan.FromSeconds(30);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger _logger = loggerFactory.CreateLogger<AgentCatalog>();
    private AgentSet? _agents;
    private DateTimeOffset _rebuildAfter = DateTimeOffset.MaxValue;

    public async Task<AgentSet> GetAsync(CancellationToken cancellationToken)
    {
        if (_agents is not null && time.GetUtcNow() < _rebuildAfter)
        {
            return _agents;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_agents is null || time.GetUtcNow() >= _rebuildAfter)
            {
                _agents = await BuildAsync(cancellationToken);
                _rebuildAfter = _agents.Degraded ? time.GetUtcNow() + DegradedRetryInterval : DateTimeOffset.MaxValue;
            }

            return _agents;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<AgentSet> BuildAsync(CancellationToken cancellationToken)
    {
        // --- Knowledge agent: retrieval over the vector database -----------------------------------
        var searchTool = new TracingFunction(
            AIFunctionFactory.Create(knowledgeBase.SearchKnowledgeBase, name: "search_knowledge_base"), "tool");

        var knowledge = CreateAgent(
            AgentNames.Knowledge,
            "Answers questions from the organisation's documents (policies, handbooks, manuals, course material) using vector search.",
            """
            You are the knowledge-base specialist. ALWAYS call search_knowledge_base before answering,
            rewriting the question into a focused search query. You may search more than once with different
            queries. Answer only from the returned passages and cite them like [1], [2] including the source
            document name. If nothing relevant is found, say so plainly - never invent facts.
            """,
            [searchTool]);

        // --- Campus data agent: live/structured data through the MCP server -------------------------
        IList<AITool> mcpTools;
        var degraded = false;
        try
        {
            mcpTools = (await mcp.GetToolsAsync(cancellationToken))
                .Select(t => (AITool)new TracingFunction(t, "mcp-tool"))
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "MCP server unavailable; campus_data_agent will run without tools and retry in {Interval}", DegradedRetryInterval);
            mcpTools = [];
            degraded = true;
        }

        var campusData = CreateAgent(
            AgentNames.CampusData,
            "Looks up structured, live data via MCP: student records, enrollments and grades, course rosters, major statistics, stored files, the academic calendar and weather.",
            degraded
                ? "You are the campus data specialist, but your data tools are temporarily unavailable. Tell the user the student/campus data service is unreachable right now and to try again shortly."
                : """
                  You are the campus data specialist. Use your tools to fetch exact data - student profiles,
                  enrollments and grades, course rosters, major statistics, the academic calendar, stored files
                  and weather. Chain tools when needed (e.g. search_students to find an id, then
                  get_student_enrollments). Report exact values from tool results; never guess records.
                  Present lists as compact Markdown tables. Treat student data as confidential: only return
                  what was asked for.
                  """,
            mcpTools);

        // --- Writer agent: another agent the orchestrator can delegate to -------------------------
        var writer = CreateAgent(
            AgentNames.Writer,
            "Writes and transforms text: emails, announcements, summaries, reports, bullet lists, tone changes. Give it all the facts it needs in the request.",
            """
            You are a professional writer. Produce clear, well-structured text from the facts you are given.
            Do not add facts that were not provided. Use Markdown formatting where it helps readability.
            """,
            []);

        // --- Orchestrator: routes the user's request to the right specialist(s) ----------------------
        var specialists = new Dictionary<string, AIAgent>
        {
            [AgentNames.Knowledge] = knowledge,
            [AgentNames.CampusData] = campusData,
            [AgentNames.Writer] = writer,
        };

        var delegationTools = specialists.Values
            .Select(agent => (AITool)new TracingFunction(agent.AsAIFunction(), "agent", agent.Name))
            .ToList();

        var orchestrator = CreateAgent(
            AgentNames.Orchestrator,
            "Routes each request to the right specialist agent(s) and composes the final answer.",
            """
            You are the orchestrator of a university assistant. You do not have data yourself; you delegate:
            - knowledge_agent: questions answered by documents (policies, procedures, handbooks, FAQs, course content).
            - campus_data_agent: questions about specific students, grades, enrollments, courses, rosters,
              statistics, files in storage, academic calendar dates, or weather.
            - writer_agent: drafting or rewriting text (emails, announcements, summaries). Pass it the facts
              gathered from the other agents.
            Rules:
            1. Pick the minimum set of agents needed. Call several (in sequence) when a request spans sources,
               e.g. "email student S2026005 their grades" = campus_data_agent, then writer_agent.
            2. Pass each agent a complete, self-contained request (it cannot see this conversation).
            3. Answer greetings and questions about your own capabilities directly without calling agents.
            4. Keep citations like [1] that the knowledge agent returns. Do not invent data.
            5. Reply in concise Markdown.
            """,
            delegationTools);

        var info = new List<AgentInfo>
        {
            new(AgentNames.Orchestrator, orchestrator.Description!, specialists.Keys.ToList()),
            new(AgentNames.Knowledge, knowledge.Description!, ["search_knowledge_base"]),
            new(AgentNames.CampusData, campusData.Description!, mcpTools.Select(t => t.Name).ToList()),
            new(AgentNames.Writer, writer.Description!, []),
        };

        return new AgentSet(orchestrator, specialists, info, degraded);
    }

    private ChatClientAgent CreateAgent(string name, string description, string instructions, IList<AITool> tools) =>
        new(chatClient, instructions, name, description, tools, loggerFactory, services);
}
