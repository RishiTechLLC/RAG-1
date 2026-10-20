namespace Rag.Core;

/// <summary>Azure Blob Storage container that holds the source documents.</summary>
public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    /// <summary>Connection string (Azurite locally, or a key-based string). Takes precedence over <see cref="AccountUri"/>.</summary>
    public string? ConnectionString { get; set; }

    /// <summary>e.g. https://mystorage.blob.core.windows.net — used with managed identity / Entra ID.</summary>
    public string? AccountUri { get; set; }

    public string ContainerName { get; set; } = "documents";

    /// <summary>Local folder whose files are uploaded on startup if missing (local development only).</summary>
    public string? SeedFolder { get; set; }
}

/// <summary>Azure AI Search service that stores the chunk vectors.</summary>
public sealed class SearchOptions
{
    public const string SectionName = "Search";

    public string Endpoint { get; set; } = string.Empty;

    /// <summary>Optional admin key. When empty, Entra ID (DefaultAzureCredential) is used.</summary>
    public string? ApiKey { get; set; }

    public string IndexName { get; set; } = "rag-chunks";
}

/// <summary>Azure OpenAI (or Azure AI Foundry) deployments used for embeddings and answers.</summary>
public sealed class AzureOpenAIOptions
{
    public const string SectionName = "AzureOpenAI";

    /// <summary>e.g. https://my-openai.openai.azure.com/</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>Optional key. When empty, Entra ID (DefaultAzureCredential) is used.</summary>
    public string? ApiKey { get; set; }

    public string ChatDeployment { get; set; } = "gpt-5-mini";

    public string EmbeddingDeployment { get; set; } = "text-embedding-3-small";

    /// <summary>Vector size of <see cref="EmbeddingDeployment"/> (1536 for text-embedding-3-small).</summary>
    public int EmbeddingDimensions { get; set; } = 1536;
}
