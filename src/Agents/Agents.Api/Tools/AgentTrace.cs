using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Agents.Api.Tools;

/// <summary>One step taken while answering: an agent handing off to another agent, or an agent calling a tool.</summary>
public sealed record TraceStep(string Caller, string Target, string Kind, string Input, long DurationMs, bool Succeeded, DateTimeOffset StartedAt);

/// <summary>
/// Collects <see cref="TraceStep"/>s for the current request. Uses <see cref="AsyncLocal{T}"/> so nested
/// agent -> tool calls (which run in the same async flow) all land in the same trace.
/// </summary>
public static class AgentTrace
{
    private static readonly AsyncLocal<ConcurrentQueue<TraceStep>?> Current = new();
    private static readonly AsyncLocal<string?> CurrentAgent = new();
    private static readonly AsyncLocal<StrongBox<int>?> KnowledgePassages = new();

    public static ConcurrentQueue<TraceStep> Begin()
    {
        var steps = new ConcurrentQueue<TraceStep>();
        Current.Value = steps;
        CurrentAgent.Value = AgentNames.Orchestrator;
        KnowledgePassages.Value = new StrongBox<int>(0);
        return steps;
    }

    /// <summary>Passages the knowledge base returned during this request (the semantic cache only stores grounded answers).</summary>
    public static int KnowledgePassagesFound => KnowledgePassages.Value?.Value ?? 0;

    internal static void RecordKnowledgePassages(int count)
    {
        if (KnowledgePassages.Value is { } box)
        {
            Interlocked.Add(ref box.Value, count);
        }
    }

    internal static string Caller => CurrentAgent.Value ?? "unknown";

    internal static void Record(TraceStep step) => Current.Value?.Enqueue(step);

    internal static IDisposable EnterAgent(string agent)
    {
        var previous = CurrentAgent.Value;
        CurrentAgent.Value = agent;
        return new Restore(() => CurrentAgent.Value = previous);
    }

    private sealed class Restore(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}

/// <summary>Wraps any <see cref="AIFunction"/> (MCP tool, HTTP tool or agent-as-tool) and records it in <see cref="AgentTrace"/>.</summary>
public sealed class TracingFunction(AIFunction inner, string kind, string? enteredAgent = null) : DelegatingAIFunction(inner)
{
    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var caller = AgentTrace.Caller;
        var input = Truncate(JsonSerializer.Serialize(arguments.ToDictionary(a => a.Key, a => a.Value)), 300);
        var startedAt = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        using var scope = enteredAgent is null ? null : AgentTrace.EnterAgent(enteredAgent);
        try
        {
            var result = await base.InvokeCoreAsync(arguments, cancellationToken);
            AgentTrace.Record(new TraceStep(caller, Name, kind, input, stopwatch.ElapsedMilliseconds, true, startedAt));
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AgentTrace.Record(new TraceStep(caller, Name, kind, input, stopwatch.ElapsedMilliseconds, false, startedAt));

            // Let the model see the failure and recover instead of failing the whole chat turn.
            return $"Tool '{Name}' failed: {ex.Message}";
        }
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max] + "…";
}
