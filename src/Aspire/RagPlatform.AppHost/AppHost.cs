using Microsoft.Extensions.Configuration;

// Local orchestration for the whole platform: `dotnet run --project src/Aspire/RagPlatform.AppHost`
// (or F5 in Visual Studio / VS Code / Rider). Starts SQL Server + Azurite containers, all four .NET
// services and the Angular app, wires their URLs together, and opens the Aspire dashboard with logs,
// traces and metrics. Breakpoints work in every .NET project because they run as normal processes.
//
// Azure OpenAI and Azure AI Search have no local emulators, so point them at real (dev) resources:
//   cd src/Aspire/RagPlatform.AppHost
//   dotnet user-secrets set "Azure:OpenAI:Endpoint" "https://<your-aoai>.openai.azure.com/"
//   dotnet user-secrets set "Azure:Search:Endpoint" "https://<your-search>.search.windows.net"
//   (optional) dotnet user-secrets set "Azure:OpenAI:ApiKey" "<key>" / "Azure:Search:ApiKey" "<key>"
// Without keys, your `az login` / Visual Studio identity is used (needs the RBAC roles in docs/local-development.md).

var builder = DistributedApplication.CreateBuilder(args);
var config = builder.Configuration;

// ---- Infrastructure (containers) ------------------------------------------------------------
var sqlPassword = builder.AddParameter("sql-password", secret: true);
var studentsDb = builder.AddSqlServer("sql", sqlPassword)
    .WithDataVolume()
    .WithLifetime(ContainerLifetime.Persistent)
    .AddDatabase("StudentsDb", databaseName: "students");

var storage = builder.AddAzureStorage("storage")
    .RunAsEmulator(emulator => emulator.WithDataVolume().WithLifetime(ContainerLifetime.Persistent));
var blobs = storage.AddBlobs("blobs");
storage.AddBlobContainer("documents");

var samplesFolder = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "..", "..", "samples", "documents"));

// ---- Project 1: RAG API (Blob -> chunking -> Azure AI Search) -----------------------------------
var ragApi = builder.AddProject<Projects.Rag_Api>("rag-api")
    .WithEnvironment("Storage__ConnectionString", blobs.Resource.ConnectionStringExpression)
    .WithEnvironment("Storage__ContainerName", "documents")
    .WithEnvironment("Storage__SeedFolder", samplesFolder)
    .WithAzureSettings(config)
    .WaitFor(blobs);

// ---- Project 2: Students API (SQL) + MCP server -----------------------------------------------
var studentsApi = builder.AddProject<Projects.Students_Api>("students-api")
    .WithReference(studentsDb)
    .WaitFor(studentsDb);

var mcpServer = builder.AddProject<Projects.Mcp_Server>("mcp-server")
    .WithEnvironment("StudentsApi__Url", studentsApi.GetEndpoint("http"))
    .WithEnvironment("Storage__ConnectionString", blobs.Resource.ConnectionStringExpression)
    .WithEnvironment("Storage__ContainerName", "documents")
    .WaitFor(studentsApi)
    .WaitFor(blobs);

// ---- Multi-agent API (Microsoft Agent Framework) ----------------------------------------------
var agentsApi = builder.AddProject<Projects.Agents_Api>("agents-api")
    .WithEnvironment("Downstream__RagApiUrl", ragApi.GetEndpoint("http"))
    .WithEnvironment("Downstream__McpServerUrl", mcpServer.GetEndpoint("http"))
    .WithEnvironment("Cors__AllowedOrigins__0", "http://localhost:4200")
    .WithAzureSettings(config)
    .WaitFor(ragApi)
    .WaitFor(mcpServer);

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
