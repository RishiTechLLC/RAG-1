namespace Agents.Api.SemanticCache;

/// <summary>Used when no Redis connection string is configured or SemanticCache:Enabled is false.</summary>
public sealed class DisabledSemanticCache : ISemanticCache
{
    public bool IsEnabled => false;

    public Task<CachedAnswer?> LookupAsync(string agent, string question, CancellationToken cancellationToken) => Task.FromResult<CachedAnswer?>(null);

    public Task StoreAsync(string agent, string question, string answer, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<SemanticCacheStats> GetStatsAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new SemanticCacheStats(false, false, 0, 0, 0, 0, TimeSpan.Zero));

    public Task<long> ClearAsync(CancellationToken cancellationToken) => Task.FromResult(0L);
}
