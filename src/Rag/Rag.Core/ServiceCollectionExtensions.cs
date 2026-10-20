using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.Search.Documents.Indexes;
using Azure.Storage.Blobs;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Rag.Core.AI;
using Rag.Core.Chunking;
using Rag.Core.Documents;
using Rag.Core.Ingestion;
using Rag.Core.Search;

namespace Rag.Core;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers storage, Azure AI Search, Azure OpenAI (embeddings + chat), every chunking strategy and the
    /// ingestion pipeline. Credentials: keys when configured, otherwise <see cref="DefaultAzureCredential"/>
    /// (managed identity in Azure; Visual Studio / az CLI sign-in locally).
    /// </summary>
    public static IServiceCollection AddRagCore(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<StorageOptions>(configuration.GetSection(StorageOptions.SectionName));
        services.Configure<SearchOptions>(configuration.GetSection(SearchOptions.SectionName));
        services.Configure<AzureOpenAIOptions>(configuration.GetSection(AzureOpenAIOptions.SectionName));
        services.Configure<ChunkingOptions>(configuration.GetSection(ChunkingOptions.SectionName));

        services.AddSingleton<TokenCredential>(_ => new DefaultAzureCredential(new DefaultAzureCredentialOptions
        {
            // Lets you pin a user-assigned managed identity via AZURE_CLIENT_ID.
            ManagedIdentityClientId = configuration["AZURE_CLIENT_ID"],
        }));

        services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<StorageOptions>>().Value;
            var service = !string.IsNullOrWhiteSpace(options.ConnectionString)
                ? new BlobServiceClient(options.ConnectionString)
                : new BlobServiceClient(
                    new Uri(options.AccountUri ?? throw new InvalidOperationException("Storage:ConnectionString or Storage:AccountUri must be configured.")),
                    sp.GetRequiredService<TokenCredential>());
            return service.GetBlobContainerClient(options.ContainerName);
        });

        services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<SearchOptions>>().Value;
            if (string.IsNullOrWhiteSpace(options.Endpoint))
            {
                throw new InvalidOperationException("Search:Endpoint is not configured.");
            }

            return string.IsNullOrWhiteSpace(options.ApiKey)
                ? new SearchIndexClient(new Uri(options.Endpoint), sp.GetRequiredService<TokenCredential>())
                : new SearchIndexClient(new Uri(options.Endpoint), new AzureKeyCredential(options.ApiKey));
        });

        services.AddSingleton(sp => AzureOpenAIClientFactory.Create(
            sp.GetRequiredService<IOptions<AzureOpenAIOptions>>().Value,
            sp.GetRequiredService<TokenCredential>()));

        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<AzureOpenAIOptions>>().Value;
            return sp.GetRequiredService<OpenAI.OpenAIClient>()
                .GetEmbeddingClient(options.EmbeddingDeployment)
                .AsIEmbeddingGenerator(options.EmbeddingDimensions);
        });

        services.AddSingleton<IChatClient>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<AzureOpenAIOptions>>().Value;
            return sp.GetRequiredService<OpenAI.OpenAIClient>()
                .GetChatClient(options.ChatDeployment)
                .AsIChatClient();
        });

        services.AddSingleton<IChunker, FixedSizeChunker>();
        services.AddSingleton<IChunker, SlidingWindowChunker>();
        services.AddSingleton<IChunker, SentenceChunker>();
        services.AddSingleton<IChunker, ParagraphChunker>();
        services.AddSingleton<IChunker, RecursiveChunker>();
        services.AddSingleton<IChunker, MarkdownChunker>();
        services.AddSingleton<IChunker, SemanticChunker>();
        services.AddSingleton<IChunkerFactory, ChunkerFactory>();

        services.AddSingleton<IDocumentSource, BlobDocumentSource>();
        services.AddSingleton<DocumentUploader>();
        services.AddSingleton<ISearchIndexService, AzureAISearchIndexService>();
        services.AddSingleton<IIngestionPipeline, IngestionPipeline>();
        services.AddSingleton<RagAnswerService>();

        return services;
    }
}
