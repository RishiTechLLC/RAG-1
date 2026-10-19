using Microsoft.Extensions.AI;
using Rag.Core.Chunking;
using Rag.Core.Ingestion;

namespace Rag.Core.Tests;

public class ChunkerTests
{
    private static readonly ChunkingOptions Options = new() { ChunkSize = 200, Overlap = 40 };

    private static string SampleText()
    {
        var paragraphs = Enumerable.Range(1, 8).Select(p =>
            string.Join(" ", Enumerable.Range(1, 5).Select(s => $"Paragraph {p} sentence {s} talks about topic {p}.")));
        return string.Join("\n\n", paragraphs);
    }

    public static TheoryData<IChunker> DeterministicChunkers() =>
    [
        new FixedSizeChunker(),
        new SlidingWindowChunker(),
        new SentenceChunker(),
        new ParagraphChunker(),
        new RecursiveChunker(),
        new MarkdownChunker(),
    ];

    [Theory]
    [MemberData(nameof(DeterministicChunkers))]
    public async Task Chunks_respect_max_size_and_are_sequential(IChunker chunker)
    {
        var chunks = await chunker.ChunkAsync(SampleText(), Options, TestContext.Current.CancellationToken);

        Assert.NotEmpty(chunks);
        Assert.All(chunks, c => Assert.InRange(c.Content.Length, 1, Options.ChunkSize));
        Assert.Equal(Enumerable.Range(0, chunks.Count), chunks.Select(c => c.Index));
    }

    [Theory]
    [MemberData(nameof(DeterministicChunkers))]
    public async Task Every_word_survives_chunking(IChunker chunker)
    {
        var text = SampleText();
        var chunks = await chunker.ChunkAsync(text, Options, TestContext.Current.CancellationToken);
        var joined = string.Join(" ", chunks.Select(c => c.Content));

        // Fixed-size windows can cut a word in two, so compare on full sentences for the others.
        if (chunker.Strategy is ChunkingStrategy.FixedSize or ChunkingStrategy.SlidingWindow)
        {
            Assert.Contains("Paragraph 8", joined);
            return;
        }

        foreach (var p in Enumerable.Range(1, 8))
        {
            Assert.Contains($"Paragraph {p} sentence 5 talks about topic {p}", joined);
        }
    }

    [Fact]
    public async Task Sliding_window_overlaps_consecutive_chunks()
    {
        var text = new string('a', 100) + new string('b', 100) + new string('c', 100);
        var chunks = await new SlidingWindowChunker().ChunkAsync(text, new ChunkingOptions { ChunkSize = 150, Overlap = 50 }, TestContext.Current.CancellationToken);

        Assert.True(chunks.Count >= 3);
        Assert.Equal(chunks[0].Content[^50..], chunks[1].Content[..50]);
    }

    [Fact]
    public async Task Fixed_size_has_no_overlap()
    {
        var text = new string('x', 450);
        var chunks = await new FixedSizeChunker().ChunkAsync(text, new ChunkingOptions { ChunkSize = 200, Overlap = 50 }, TestContext.Current.CancellationToken);

        Assert.Equal([200, 200, 50], chunks.Select(c => c.Content.Length));
    }

    [Fact]
    public async Task Paragraph_chunker_never_splits_a_small_paragraph()
    {
        var chunks = await new ParagraphChunker().ChunkAsync(SampleText(), new ChunkingOptions { ChunkSize = 500, Overlap = 0 }, TestContext.Current.CancellationToken);

        Assert.All(chunks, c => Assert.StartsWith("Paragraph", c.Content));
        Assert.All(chunks, c => Assert.EndsWith(".", c.Content));
    }

    [Fact]
    public async Task Markdown_chunker_keeps_heading_path()
    {
        const string markdown =
            """
            # Handbook
            Intro text.

            ## Leave
            You get 25 days of leave.

            ### Sick leave
            Sick leave is unlimited with a doctor's note.

            ## Expenses
            Submit receipts within 30 days.
            """;

        var chunks = await new MarkdownChunker().ChunkAsync(markdown, Options, TestContext.Current.CancellationToken);

        var sick = Assert.Single(chunks, c => c.Content.Contains("unlimited"));
        Assert.Equal("Handbook > Leave > Sick leave", sick.Section);
        Assert.StartsWith("Handbook > Leave > Sick leave", sick.Content);

        var expenses = Assert.Single(chunks, c => c.Content.Contains("receipts"));
        Assert.Equal("Handbook > Expenses", expenses.Section);
    }

    [Fact]
    public async Task Markdown_chunker_ignores_hashes_inside_code_fences()
    {
        const string markdown = "# Title\n\n```bash\n# not a heading\necho hi\n```\n\nAfter code.";

        var chunks = await new MarkdownChunker().ChunkAsync(markdown, Options, TestContext.Current.CancellationToken);

        Assert.All(chunks, c => Assert.Equal("Title", c.Section));
    }

    [Fact]
    public async Task Semantic_chunker_splits_where_topic_changes()
    {
        const string text =
            "Cats purr when happy. Cats sleep a lot. Cats chase mice. " +
            "Rockets need fuel. Rockets reach orbit. Rockets are loud.";

        var chunks = await new SemanticChunker(new KeywordEmbeddingGenerator())
            .ChunkAsync(text, new ChunkingOptions { ChunkSize = 1000, Overlap = 0, SemanticBreakpointPercentile = 80 }, TestContext.Current.CancellationToken);

        Assert.Equal(2, chunks.Count);
        Assert.DoesNotContain("Rocket", chunks[0].Content);
        Assert.DoesNotContain("Cat", chunks[1].Content);
    }

    [Theory]
    [InlineData("recursive", ChunkingStrategy.Recursive)]
    [InlineData("SLIDING-WINDOW", ChunkingStrategy.SlidingWindow)]
    [InlineData("fixed_size", ChunkingStrategy.FixedSize)]
    public void Strategy_names_parse_case_insensitively(string input, ChunkingStrategy expected)
    {
        Assert.True(ChunkingStrategyInfo.TryParse(input, out var actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("")]
    [InlineData("3")]
    [InlineData("unknown")]
    public void Invalid_strategy_names_are_rejected(string input) =>
        Assert.False(ChunkingStrategyInfo.TryParse(input, out _));

    [Fact]
    public void Chunk_ids_are_valid_search_keys_and_stable()
    {
        var id = IngestionPipeline.CreateId("folder/My File (1).pdf", ChunkingStrategy.Semantic, 7);

        Assert.Matches("^[a-z0-9_=-]+$", id);
        Assert.Equal(id, IngestionPipeline.CreateId("folder/My File (1).pdf", ChunkingStrategy.Semantic, 7));
        Assert.NotEqual(id, IngestionPipeline.CreateId("folder/My File (1).pdf", ChunkingStrategy.Recursive, 7));
    }

    [Fact]
    public void Options_validation_rejects_overlap_larger_than_size() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new ChunkingOptions { ChunkSize = 200, Overlap = 200 }.Validate());

    /// <summary>Fake embeddings: one dimension per topic keyword, enough to make topic shifts measurable.</summary>
    private sealed class KeywordEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
        {
            var result = new GeneratedEmbeddings<Embedding<float>>(values.Select(v => new Embedding<float>(new[]
            {
                (float)CountOf(v, "Cat"),
                (float)CountOf(v, "Rocket"),
                0.01f,
            })));
            return Task.FromResult(result);
        }

        private static int CountOf(string text, string word) =>
            (text.Length - text.Replace(word, string.Empty, StringComparison.Ordinal).Length) / word.Length;

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
