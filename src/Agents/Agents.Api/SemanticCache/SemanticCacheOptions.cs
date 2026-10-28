namespace Agents.Api.SemanticCache;

public sealed class SemanticCacheOptions
{
    public const string SectionName = "SemanticCache";

    /// <summary>Turns the cache on. It also needs ConnectionStrings:redis (Redis Stack or Redis 8 with the query engine).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Minimum cosine similarity (0-1) between a new question and a cached one to count as the same question.
    /// 0.92 matches rephrasings ("What's the late policy?" vs "What is the late submission policy?")
    /// without matching different questions on the same topic.
    /// </summary>
    public double SimilarityThreshold { get; set; } = 0.92;

    /// <summary>How long a cached answer lives. Re-ingesting documents doesn't invalidate it; flush with DELETE /api/cache.</summary>
    public TimeSpan TimeToLive { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Redis key prefix and index name, so several environments can share one Redis.</summary>
    public string KeyPrefix { get; set; } = "semcache";
}
