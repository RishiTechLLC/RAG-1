namespace Rag.Core.Chunking;

public interface IChunkerFactory
{
    IChunker Get(ChunkingStrategy strategy);
}

public sealed class ChunkerFactory(IEnumerable<IChunker> chunkers) : IChunkerFactory
{
    private readonly Dictionary<ChunkingStrategy, IChunker> _chunkers = chunkers.ToDictionary(c => c.Strategy);

    public IChunker Get(ChunkingStrategy strategy) =>
        _chunkers.TryGetValue(strategy, out var chunker)
            ? chunker
            : throw new NotSupportedException($"No chunker registered for strategy '{strategy}'.");
}
