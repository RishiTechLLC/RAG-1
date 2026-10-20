using System.Text;
using Microsoft.Extensions.AI;
using Rag.Core.Chunking;

namespace Rag.Core.Search;

public sealed record RagAnswer(string Answer, IReadOnlyList<SearchHit> Sources);

/// <summary>Classic RAG: retrieve the top chunks, then ask the chat model to answer only from them with citations.</summary>
public sealed class RagAnswerService(ISearchIndexService search, IChatClient chat)
{
    private const string SystemPrompt =
        """
        You answer questions using ONLY the numbered sources provided.
        Cite sources inline like [1] or [2][3]. If the sources do not contain the answer,
        say you could not find it in the knowledge base. Be concise.
        """;

    public async Task<RagAnswer> AskAsync(string question, int top = 5, ChunkingStrategy? strategy = null, CancellationToken cancellationToken = default)
    {
        var hits = await search.SearchAsync(question, top, strategy, cancellationToken);
        if (hits.Count == 0)
        {
            return new RagAnswer("I could not find anything relevant in the knowledge base.", hits);
        }

        var context = new StringBuilder();
        for (var i = 0; i < hits.Count; i++)
        {
            context.AppendLine($"[{i + 1}] {hits[i].Title}{(hits[i].Section is { } s ? $" ({s})" : string.Empty)}");
            context.AppendLine(hits[i].Content);
            context.AppendLine();
        }

        var response = await chat.GetResponseAsync(
            [
                new ChatMessage(ChatRole.System, SystemPrompt),
                new ChatMessage(ChatRole.User, $"Sources:\n{context}\nQuestion: {question}"),
            ],
            cancellationToken: cancellationToken);

        return new RagAnswer(response.Text, hits);
    }
}
