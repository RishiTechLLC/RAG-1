using Microsoft.Extensions.Configuration;

// Local orchestration for the whole platform: `dotnet run --project src/Aspire/RagPlatform.AppHost`
// (or F5 in Visual Studio / VS Code / Rider). Starts the Blob Storage emulator, SQL Server (container or
// your own server), all four .NET services and the Angular app, wires their URLs together, and opens the
// Aspire dashboard with logs, traces and metrics. Breakpoints work in every .NET project because they run
// as normal processes. No Docker needed when you use your own SQL Server and the default npm Azurite.
//
// Azure OpenAI and Azure AI Search have no local emulators, so point them at real (dev) resources:
//   cd src/Aspire/RagPlatform.AppHost
//   dotnet user-secrets set "Azure:OpenAI:Endpoint" "https://<your-aoai>.openai.azure.com/"
//   dotnet user-secrets set "Azure:Search:Endpoint" "https://<your-search>.search.windows.net"
//   (optional) dotnet user-secrets set "Azure:OpenAI:ApiKey" "<key>" / "Azure:Search:ApiKey" "<key>"
// Without keys, your `az login` / Visual Studio identity is used (needs the RBAC roles in docs/local-development.md).

var builder = DistributedApplication.CreateBuilder(args);
var config = builder.Configuration;

// ---- Infrastructure ---------------------------------------------------------------------------
// SQL: set ConnectionStrings:StudentsDb in the AppHost user secrets to use your own (on-prem) SQL Server:
//   dotnet user-secrets set "ConnectionStrings:StudentsDb" "Server=MYSERVER;Database=students;Trusted_Connection=True;TrustServerCertificate=True"
// Otherwise a SQL Server container is started.
var useExternalSql = !string.IsNullOrWhiteSpace(config.GetConnectionString("StudentsDb"));
IResourceBuilder<IResourceWithConnectionString> studentsDb = useExternalSql
    ? builder.AddConnectionString("StudentsDb")
    : builder.AddSqlServer("sql", builder.AddParameter("sql-password", secret: true))
        .WithDataVolume()
        .WithLifetime(ContainerLifetime.Persistent)
        .AddDatabase("StudentsDb", databaseName: "students");

// Blob Storage for documents. Pick with LocalStorage:Mode in appsettings.json / user secrets:
//   "Npm"       (default) Azurite emulator run with npx - needs Node.js only, no Docker
//   "Container" Azurite in Docker
//   "Azure"     a real storage account: set Azure:Storage:AccountUri (Entra ID, needs the
//               "Storage Blob Data Contributor" role) or Azure:Storage:ConnectionString
var storageMode = config["LocalStorage:Mode"] ?? "Npm";
IResourceBuilder<IResource>? storageDependency = null;
object storageConnection; // string or ReferenceExpression, passed to the apps as Storage__ConnectionString
var storageAccountUri = config["Azure:Storage:AccountUri"];

switch (storageMode.ToLowerInvariant())
{
    case "azure":
        storageConnection = config["Azure:Storage:ConnectionString"] ?? string.Empty;
        if (string.IsNullOrWhiteSpace(storageAccountUri) && string.IsNullOrWhiteSpace((string)storageConnection))
        {
            throw new InvalidOperationException("LocalStorage:Mode is Azure: set Azure:Storage:AccountUri or Azure:Storage:ConnectionString.");
        }
        break;

    case "container":
        var storage = builder.AddAzureStorage("storage")
            .RunAsEmulator(emulator => emulator.WithDataVolume().WithLifetime(ContainerLifetime.Persistent));
        storage.AddBlobContainer("documents");
        var blobs = storage.AddBlobs("blobs");
        storageConnection = blobs.Resource.ConnectionStringExpression;
        storageDependency = blobs;
        break;

    default:
        var azuriteArgs = new[]
        {
            "--yes", "--package", "azurite@3", "azurite-blob",
            "--blobHost", "127.0.0.1", "--blobPort", "10000",
            "--location", Path.Combine(builder.AppHostDirectory, ".azurite"),
            "--skipApiVersionCheck", "--loose",
        };
        storageDependency = OperatingSystem.IsWindows()
            ? builder.AddExecutable("azurite", "cmd", builder.AppHostDirectory, ["/c", "npx", .. azuriteArgs])
            : builder.AddExecutable("azurite", "npx", builder.AppHostDirectory, azuriteArgs);
        storageConnection = "UseDevelopmentStorage=true"; // Azurite's well-known local account on port 10000
        break;
}

IResourceBuilder<ProjectResource> WithDocumentStorage(IResourceBuilder<ProjectResource> project)
{
    project.WithEnvironment("Storage__ContainerName", "documents");
    if (!string.IsNullOrWhiteSpace(storageAccountUri) && storageMode.Equals("azure", StringComparison.OrdinalIgnoreCase))
    {
        project.WithEnvironment("Storage__AccountUri", storageAccountUri);
        project.WithEnvironment("Storage__ConnectionString", string.Empty);
    }
    else if (storageConnection is ReferenceExpression expression)
    {
        project.WithEnvironment("Storage__ConnectionString", expression);
    }
    else
    {
        project.WithEnvironment("Storage__ConnectionString", (string)storageConnection);
    }

    if (storageDependency is not null)
    {
        project.WaitFor(storageDependency);
    }

    return project;
}

var samplesFolder = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "..", "..", "samples", "documents"));

// ---- Project 1: RAG API (Blob -> chunking -> Azure AI Search) -----------------------------------
var ragApi = WithDocumentStorage(builder.AddProject<Projects.Rag_Api>("rag-api"))
    .WithEnvironment("Storage__SeedFolder", samplesFolder)
    .WithAzureSettings(config);

// ---- Project 2: Students API (SQL) + MCP server -----------------------------------------------
var studentsApi = builder.AddProject<Projects.Students_Api>("students-api")
    .WithReference(studentsDb);
if (!useExternalSql)
{
    studentsApi.WaitFor(studentsDb); // wait for the container; an external server is assumed to be up
}

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
