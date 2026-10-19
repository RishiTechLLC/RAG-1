using Azure;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Rag.Core.Chunking;

namespace Rag.Core.Search;

public sealed class AzureAISearchIndexService(
    SearchIndexClient indexClient,
    IEmbeddingGenerator<string, Embedding<float>> embeddings,
    IOptions<SearchOptions> searchOptions,
    IOptions<AzureOpenAIOptions> aiOptions,
    ILogger<AzureAISearchIndexService> logger) : ISearchIndexService
{
    private const string VectorProfile = "vector-profile";
    private const string HnswConfig = "hnsw-config";
    private const string SemanticConfig = "semantic-config";
    private const int UploadBatchSize = 500;

    private readonly string _indexName = searchOptions.Value.IndexName;
    private SearchClient Client => indexClient.GetSearchClient(_indexName);

    public async Task EnsureIndexAsync(bool recreate = false, CancellationToken cancellationToken = default)
    {
        if (recreate)
        {
            logger.LogWarning("Deleting index {Index}", _indexName);
            try
            {
                await indexClient.DeleteIndexAsync(_indexName, cancellationToken);
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
            }
        }

        var index = new SearchIndex(_indexName)
        {
            Fields =
            {
                new SimpleField("id", SearchFieldDataType.String) { IsKey = true, IsFilterable = true },
                new SearchableField("content") { AnalyzerName = LexicalAnalyzerName.EnMicrosoft },
                new SearchableField("title") { IsFilterable = true, IsSortable = true },
                new SearchableField("section"),
                new SimpleField("source", SearchFieldDataType.String) { IsFilterable = true, IsFacetable = true },
                new SimpleField("sourceUri", SearchFieldDataType.String),
                new SimpleField("chunkIndex", SearchFieldDataType.Int32) { IsFilterable = true, IsSortable = true },
                new SimpleField("strategy", SearchFieldDataType.String) { IsFilterable = true, IsFacetable = true },
                new SimpleField("indexedAt", SearchFieldDataType.DateTimeOffset) { IsFilterable = true, IsSortable = true },
                new VectorSearchField("contentVector", aiOptions.Value.EmbeddingDimensions, VectorProfile),
            },
            VectorSearch = new VectorSearch
            {
                Algorithms = { new HnswAlgorithmConfiguration(HnswConfig) },
                Profiles = { new VectorSearchProfile(VectorProfile, HnswConfig) },
            },
            SemanticSearch = new SemanticSearch
            {
                DefaultConfigurationName = SemanticConfig,
                Configurations =
                {
                    new SemanticConfiguration(SemanticConfig, new SemanticPrioritizedFields
                    {
                        TitleField = new SemanticField("title"),
                        ContentFields = { new SemanticField("content") },
                        KeywordsFields = { new SemanticField("section") },
                    }),
                },
            },
        };

        await indexClient.CreateOrUpdateIndexAsync(index, cancellationToken: cancellationToken);
        logger.LogInformation("Index {Index} is ready", _indexName);
    }

    public async Task UploadAsync(IReadOnlyList<ChunkDocument> documents, CancellationToken cancellationToken = default)
    {
        foreach (var batch in documents.Chunk(UploadBatchSize))
        {
            var response = await Client.MergeOrUploadDocumentsAsync(batch, cancellationToken: cancellationToken);
            var failed = response.Value.Results.Where(r => !r.Succeeded).ToList();
            if (failed.Count > 0)
            {
                throw new InvalidOperationException(
                    $"{failed.Count} chunk(s) failed to index. First error: {failed[0].Key}: {failed[0].ErrorMessage}");
            }
        }
    }

    public async Task<int> DeleteChunksAsync(string source, ChunkingStrategy strategy, CancellationToken cancellationToken = default)
    {
        var options = new Azure.Search.Documents.SearchOptions
        {
            Filter = SearchFilter.Create($"source eq {source} and strategy eq {strategy.ToString()}"),
            Size = 1000,
            Select = { "id" },
        };

        var ids = new List<string>();
        var results = await Client.SearchAsync<ChunkDocument>("*", options, cancellationToken);
        await foreach (var result in results.Value.GetResultsAsync().WithCancellation(cancellationToken))
        {
            ids.Add(result.Document.Id);
        }

        if (ids.Count > 0)
        {
            await Client.DeleteDocumentsAsync("id", ids, cancellationToken: cancellationToken);
        }

        return ids.Count;
    }

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(string query, int top = 5, ChunkingStrategy? strategy = null, CancellationToken cancellationToken = default)
    {
        var embedding = await embeddings.GenerateAsync(query, cancellationToken: cancellationToken);

        var options = new Azure.Search.Documents.SearchOptions
        {
            Size = top,
            QueryType = SearchQueryType.Semantic,
            SemanticSearch = new SemanticSearchOptions { SemanticConfigurationName = SemanticConfig },
            VectorSearch = new VectorSearchOptions
            {
                Queries =
                {
                    new VectorizedQuery(embedding.Vector) { KNearestNeighborsCount = Math.Max(top * 5, 50), Fields = { "contentVector" } },
                },
            },
            Select = { "id", "content", "title", "section", "source", "sourceUri", "chunkIndex", "strategy" },
        };

        if (strategy is not null)
        {
            options.Filter = SearchFilter.Create($"strategy eq {strategy.Value.ToString()}");
        }

        var response = await Client.SearchAsync<ChunkDocument>(query, options, cancellationToken);
        var hits = new List<SearchHit>();
        await foreach (var result in response.Value.GetResultsAsync().WithCancellation(cancellationToken))
        {
            var d = result.Document;
            hits.Add(new SearchHit(d.Id, d.Content, d.Title, d.Section, d.Source, d.SourceUri, d.ChunkIndex, d.Strategy,
                result.Score ?? 0, result.SemanticSearch?.RerankerScore));
        }

        return hits;
    }

    public async Task<IReadOnlyDictionary<string, long>> CountByStrategyAsync(CancellationToken cancellationToken = default)
    {
        var options = new Azure.Search.Documents.SearchOptions { Size = 0, Facets = { "strategy,count:20" } };
        var response = await Client.SearchAsync<ChunkDocument>("*", options, cancellationToken);
        return response.Value.Facets.TryGetValue("strategy", out var facets)
            ? facets.ToDictionary(f => f.Value?.ToString() ?? "unknown", f => f.Count ?? 0)
            : new Dictionary<string, long>();
    }
}
