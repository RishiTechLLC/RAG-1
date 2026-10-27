using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Rag.Core;

var builder = FunctionsApplication.CreateBuilder(args);

// The blob trigger reads its connection from "DocumentsStorage" (a connection string, or
// DocumentsStorage__blobServiceUri / __queueServiceUri for Entra ID) and the container from
// "DocumentsContainer". Reuse those for Rag.Core's own storage client so they are configured once.
var config = builder.Configuration;
var fallbacks = new Dictionary<string, string?>();
if (string.IsNullOrWhiteSpace(config["Storage:ConnectionString"]) && string.IsNullOrWhiteSpace(config["Storage:AccountUri"]))
{
    if (config["DocumentsStorage"] is { Length: > 0 } connectionString)
    {
        fallbacks["Storage:ConnectionString"] = connectionString;
    }
    else if (config["DocumentsStorage:blobServiceUri"] is { Length: > 0 } blobServiceUri)
    {
        fallbacks["Storage:AccountUri"] = blobServiceUri;
    }
}

if (string.IsNullOrWhiteSpace(config["Storage:ContainerName"]) && config["DocumentsContainer"] is { Length: > 0 } container)
{
    fallbacks["Storage:ContainerName"] = container;
}

config.AddInMemoryCollection(fallbacks);
builder.Services.AddRagCore(config);

builder.Build().Run();
