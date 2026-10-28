using System.Runtime.InteropServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using NRedisStack;
using NRedisStack.RedisStackCommands;
using NRedisStack.Search;
using NRedisStack.Search.Literals.Enums;
using StackExchange.Redis;

namespace Agents.Api.SemanticCache;

public sealed record CachedAnswer(string Question, string Answer, double Similarity, DateTimeOffset CreatedAt);

public sealed record SemanticCacheStats(bool Enabled, bool Connected, long Entries, long Hits, long Misses, double SimilarityThreshold, TimeSpan TimeToLive);

public interface ISemanticCache
{
    bool IsEnabled { get; }

    /// <summary>Returns the cached answer of the most similar earlier question for this agent, if similar enough.</summary>
    Task<CachedAnswer?> LookupAsync(string agent, string question, CancellationToken cancellationToken);

    Task StoreAsync(string agent, string question, string answer, CancellationToken cancellationToken);

    Task<SemanticCacheStats> GetStatsAsync(CancellationToken cancellationToken);

    Task<long> ClearAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Semantic cache on Redis vector search: each entry is a HASH ({prefix}:entry:{guid}) holding the question,
/// the answer, the agent and the question's embedding, with a TTL. A lookup embeds the new question and runs a
/// KNN-1 query filtered by agent; cosine similarity at or above the threshold is a hit.
/// Any Redis failure is logged and treated as a miss, so chat keeps working when Redis is down.
/// </summary>
public sealed class RedisSemanticCache(
    IConnectionMultiplexer redis,
    IEmbeddingGenerator<string, Embedding<float>> embeddings,
    IOptions<SemanticCacheOptions> options,
    IOptions<AzureOpenAIOptions> aiOptions,
    ILogger<RedisSemanticCache> logger) : ISemanticCache
{
    private readonly SemanticCacheOptions _options = options.Value;
    private readonly SemaphoreSlim _indexGate = new(1, 1);
    private bool _indexReady;
    private long _hits;
    private long _misses;

    private string IndexName => $"{_options.KeyPrefix}-idx";
    private string EntryPrefix => $"{_options.KeyPrefix}:entry:";

    public bool IsEnabled => _options.Enabled;

    public async Task<CachedAnswer?> LookupAsync(string agent, string question, CancellationToken cancellationToken)
    {
        try
        {
            if (!await EnsureIndexAsync())
            {
                return null;
            }

            var vector = await EmbedAsync(question, cancellationToken);
            var query = new Query($"(@agent:{{{Tag(agent)}}})=>[KNN 1 @embedding $vec AS distance]")
                .AddParam("vec", vector)
                .SetSortBy("distance")
                .ReturnFields("question", "answer", "createdAt", "distance")
                .Limit(0, 1)
                .Dialect(2);

            var result = await redis.GetDatabase().FT().SearchAsync(IndexName, query);
            var doc = result.Documents.FirstOrDefault();
            if (doc is null)
            {
                Interlocked.Increment(ref _misses);
                return null;
            }

            // Redis reports cosine *distance* (1 - similarity).
            var similarity = 1 - (double)doc["distance"];
            if (similarity < _options.SimilarityThreshold)
            {
                Interlocked.Increment(ref _misses);
                logger.LogDebug("Semantic cache miss for {Agent}: best similarity {Similarity:F3}", agent, similarity);
                return null;
            }

            Interlocked.Increment(ref _hits);
            return new CachedAnswer(
                (string)doc["question"]!,
                (string)doc["answer"]!,
                similarity,
                DateTimeOffset.FromUnixTimeSeconds((long)doc["createdAt"]));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Semantic cache lookup failed; continuing without the cache");
            return null;
        }
    }

    public async Task StoreAsync(string agent, string question, string answer, CancellationToken cancellationToken)
    {
        try
        {
            if (!await EnsureIndexAsync())
            {
                return;
            }

            var vector = await EmbedAsync(question, cancellationToken);
            var key = EntryPrefix + Guid.NewGuid().ToString("n");
            var db = redis.GetDatabase();
            await db.HashSetAsync(key,
            [
                new HashEntry("agent", Tag(agent)),
                new HashEntry("question", question),
                new HashEntry("answer", answer),
                new HashEntry("createdAt", DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
                new HashEntry("embedding", vector),
            ]);
            await db.KeyExpireAsync(key, _options.TimeToLive);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Semantic cache store failed");
        }
    }

    public async Task<SemanticCacheStats> GetStatsAsync(CancellationToken cancellationToken)
    {
        long entries = 0;
        var connected = redis.IsConnected;
        if (connected && await EnsureIndexAsync())
        {
            try
            {
                entries = (await redis.GetDatabase().FT().InfoAsync(IndexName)).NumDocs;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not read semantic cache index info");
            }
        }

        return new SemanticCacheStats(_options.Enabled, connected, entries,
            Interlocked.Read(ref _hits), Interlocked.Read(ref _misses), _options.SimilarityThreshold, _options.TimeToLive);
    }

    public async Task<long> ClearAsync(CancellationToken cancellationToken)
    {
        var stats = await GetStatsAsync(cancellationToken);
        try
        {
            // DD = also delete the documents; the index is recreated on the next request.
            await redis.GetDatabase().FT().DropIndexAsync(IndexName, dd: true);
        }
        catch (RedisServerException ex) when (ex.Message.Contains("Unknown index", StringComparison.OrdinalIgnoreCase)
                                             || ex.Message.Contains("no such index", StringComparison.OrdinalIgnoreCase))
        {
        }

        _indexReady = false;
        return stats.Entries;
    }

    private async Task<bool> EnsureIndexAsync()
    {
        if (_indexReady)
        {
            return true;
        }

        if (!redis.IsConnected)
        {
            return false;
        }

        await _indexGate.WaitAsync();
        try
        {
            if (_indexReady)
            {
                return true;
            }

            var ft = redis.GetDatabase().FT();
            try
            {
                await ft.InfoAsync(IndexName);
            }
            catch (RedisServerException)
            {
                var schema = new Schema()
                    .AddTagField("agent")
                    .AddVectorField("embedding", Schema.VectorField.VectorAlgo.HNSW, new Dictionary<string, object>
                    {
                        ["TYPE"] = "FLOAT32",
                        ["DIM"] = aiOptions.Value.EmbeddingDimensions,
                        ["DISTANCE_METRIC"] = "COSINE",
                    });
                await ft.CreateAsync(IndexName, new FTCreateParams().On(IndexDataType.HASH).Prefix(EntryPrefix), schema);
                logger.LogInformation("Created semantic cache index {Index}", IndexName);
            }

            _indexReady = true;
            return true;
        }
        finally
        {
            _indexGate.Release();
        }
    }

    private async Task<byte[]> EmbedAsync(string text, CancellationToken cancellationToken)
    {
        var embedding = await embeddings.GenerateAsync(Normalize(text), cancellationToken: cancellationToken);
        return MemoryMarshal.AsBytes(embedding.Vector.Span).ToArray();
    }

    private static string Normalize(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    /// <summary>TAG values are matched literally; keep them to letters and digits so no escaping is needed.</summary>
    private static string Tag(string value) => new(value.Where(char.IsLetterOrDigit).ToArray());
}
