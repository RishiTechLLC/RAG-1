using Microsoft.Extensions.Configuration;

// Local orchestration for the whole platform: `dotnet run --project src/Aspire/RagPlatform.AppHost`
// (or F5 in Visual Studio / VS Code / Rider). Starts all four .NET services and the Angular app as normal
// processes, wires their URLs together, and opens the Aspire dashboard with logs, traces and metrics.
// No Docker: every backing service is one you already have.
//
// Configure once with user secrets (cd src/Aspire/RagPlatform.AppHost):
//   dotnet user-secrets set "ConnectionStrings:StudentsDb" "Server=MYSERVER;Database=students;Trusted_Connection=True;TrustServerCertificate=True"
//   dotnet user-secrets set "Azure:Storage:AccountUri" "https://<account>.blob.core.windows.net/"   (or Azure:Storage:ConnectionString)
//   dotnet user-secrets set "Azure:OpenAI:Endpoint" "https://<your-aoai>.openai.azure.com/"
//   dotnet user-secrets set "Azure:Search:Endpoint" "https://<your-search>.search.windows.net"
// Without keys/connection strings with keys, your `az login` / Visual Studio identity is used
// (needs the RBAC roles in docs/local-development.md).

var builder = DistributedApplication.CreateBuilder(args);
var config = builder.Configuration;

// ---- Backing services (your own SQL Server + Azure Storage account) ----------------------------
static string Require(string? value, string key, string example) =>
    !string.IsNullOrWhiteSpace(value)
        ? value
        : throw new InvalidOperationException(
            $"'{key}' is not configured. In src/Aspire/RagPlatform.AppHost run: dotnet user-secrets set \"{key}\" \"{example}\"");

Require(config.GetConnectionString("StudentsDb"), "ConnectionStrings:StudentsDb",
    "Server=MYSERVER;Database=students;Trusted_Connection=True;TrustServerCertificate=True");
var studentsDb = builder.AddConnectionString("StudentsDb");

var storageAccountUri = config["Azure:Storage:AccountUri"];
var storageConnectionString = config["Azure:Storage:ConnectionString"];
if (string.IsNullOrWhiteSpace(storageAccountUri))
{
    Require(storageConnectionString, "Azure:Storage:AccountUri", "https://<account>.blob.core.windows.net/");
}

var storageContainer = config["Azure:Storage:ContainerName"] is { Length: > 0 } name ? name : "documents";

// Points a service at the Azure Storage account: Entra ID (AccountUri) wins over a connection string.
IResourceBuilder<ProjectResource> WithDocumentStorage(IResourceBuilder<ProjectResource> project)
{
    project.WithEnvironment("Storage__ContainerName", storageContainer);
    if (!string.IsNullOrWhiteSpace(storageAccountUri))
    {
        project.WithEnvironment("Storage__AccountUri", storageAccountUri);
        project.WithEnvironment("Storage__ConnectionString", string.Empty);
    }
    else
    {
        project.WithEnvironment("Storage__ConnectionString", storageConnectionString!);
    }

    return project;
}

// Sample documents are uploaded to the container on startup if missing (Development only).
// Set Azure:Storage:UploadSampleDocuments=false to keep your container untouched.
var samplesFolder = config.GetValue("Azure:Storage:UploadSampleDocuments", true)
    ? Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "..", "..", "samples", "documents"))
    : string.Empty;

// ---- Project 1: RAG API (Blob -> chunking -> Azure AI Search) -----------------------------------
var ragApi = WithDocumentStorage(builder.AddProject<Projects.Rag_Api>("rag-api"))
    .WithEnvironment("Storage__SeedFolder", samplesFolder)
    .WithAzureSettings(config);

// ---- Project 2: Students API (SQL) + MCP server -----------------------------------------------
var studentsApi = builder.AddProject<Projects.Students_Api>("students-api")
    .WithReference(studentsDb);

var mcpServer = WithDocumentStorage(builder.AddProject<Projects.Mcp_Server>("mcp-server"))
    .WithEnvironment("StudentsApi__Url", studentsApi.GetEndpoint("http"))
    .WaitFor(studentsApi);

// ---- Multi-agent API (Microsoft Agent Framework) ----------------------------------------------
var agentsApi = builder.AddProject<Projects.Agents_Api>("agents-api")
    .WithEnvironment("Downstream__RagApiUrl", ragApi.GetEndpoint("http"))
    .WithEnvironment("Downstream__McpServerUrl", mcpServer.GetEndpoint("http"))
    .WithEnvironment("Cors__AllowedOrigins__0", "http://localhost:4200")
    .WithAzureSettings(config)
    .WaitFor(ragApi)
    .WaitFor(mcpServer);

// ---- RAG ingestion Functions app (blob trigger: indexes every file uploaded to the container) ----
// Needs Azure Functions Core Tools v4 (Visual Studio "Azure development" workload, or
// `winget install Microsoft.Azure.FunctionsCoreTools`). Disable with RagFunctions:Enabled=false.
if (config.GetValue("RagFunctions:Enabled", true))
{
    var ragFunctions = builder.AddProject<Projects.Rag_Functions>("rag-functions")
        .WithEnvironment("FUNCTIONS_WORKER_RUNTIME", "dotnet-isolated")
        .WithEnvironment("DocumentsContainer", storageContainer)
        .WithEnvironment("Ingestion__Strategy", config["RagFunctions:Strategy"] is { Length: > 0 } strategy ? strategy : "Markdown")
        .WithAzureSettings(config);

    if (!string.IsNullOrWhiteSpace(storageAccountUri))
    {
        // Entra ID (your az login / Visual Studio account): the Functions runtime and the trigger get
        // identity-based connections derived from the blob URL. Your account needs Storage Blob Data Owner,
        // Storage Queue Data Contributor and Storage Table Data Contributor on the storage account.
        var blobUri = new Uri(storageAccountUri).GetLeftPart(UriPartial.Authority) + "/";
        var queueUri = blobUri.Replace(".blob.", ".queue.", StringComparison.OrdinalIgnoreCase);
        var tableUri = blobUri.Replace(".blob.", ".table.", StringComparison.OrdinalIgnoreCase);
        ragFunctions
            .WithEnvironment("AzureWebJobsStorage__blobServiceUri", blobUri)
            .WithEnvironment("AzureWebJobsStorage__queueServiceUri", queueUri)
            .WithEnvironment("AzureWebJobsStorage__tableServiceUri", tableUri)
            .WithEnvironment("DocumentsStorage__blobServiceUri", blobUri)
            .WithEnvironment("DocumentsStorage__queueServiceUri", queueUri);
    }
    else
    {
        ragFunctions
            .WithEnvironment("AzureWebJobsStorage", storageConnectionString!)
            .WithEnvironment("DocumentsStorage", storageConnectionString!);
    }
}

// ---- Front end (Angular dev server with /api proxied to agents-api) ---------------------------
builder.AddJavaScriptApp("chat-ui", "../../Frontend/chat-ui", "start")
    .WithHttpEndpoint(port: 4200, targetPort: 4200, isProxied: false)
    .WithReference(agentsApi)
    .WithEnvironment("AGENTS_API_URL", agentsApi.GetEndpoint("http"))
    .WaitFor(agentsApi)
    .WithExternalHttpEndpoints();

builder.Build().Run();

internal static class ResourceBuilderExtensions
{
    /// <summary>Forwards Azure OpenAI / AI Search settings from the AppHost's user secrets to a service.</summary>
    public static IResourceBuilder<ProjectResource> WithAzureSettings(this IResourceBuilder<ProjectResource> resource, IConfiguration config)
    {
        void Forward(string source, string target)
        {
            var value = config[source];
            if (!string.IsNullOrWhiteSpace(value))
            {
                resource.WithEnvironment(target, value);
            }
        }

        Forward("Azure:OpenAI:Endpoint", "AzureOpenAI__Endpoint");
        Forward("Azure:OpenAI:ApiKey", "AzureOpenAI__ApiKey");
        Forward("Azure:OpenAI:ChatDeployment", "AzureOpenAI__ChatDeployment");
        Forward("Azure:OpenAI:EmbeddingDeployment", "AzureOpenAI__EmbeddingDeployment");
        Forward("Azure:Search:Endpoint", "Search__Endpoint");
        Forward("Azure:Search:ApiKey", "Search__ApiKey");
        Forward("Azure:Search:IndexName", "Search__IndexName");
        return resource;
    }
}
