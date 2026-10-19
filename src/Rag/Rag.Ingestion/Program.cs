using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rag.Core;
using Rag.Core.Chunking;
using Rag.Core.Documents;
using Rag.Core.Ingestion;

// Usage:
//   dotnet run --project src/Rag/Rag.Ingestion -- --strategy Recursive [--prefix policies/] [--chunk-size 1000] [--overlap 150] [--recreate-index]
//   dotnet run --project src/Rag/Rag.Ingestion -- --upload samples/documents --strategy Markdown
//   dotnet run --project src/Rag/Rag.Ingestion -- --list-strategies
// Omitting --strategy in an interactive terminal shows a menu.

var switchMappings = new Dictionary<string, string>
{
    ["--strategy"] = "Ingestion:Strategy",
    ["-s"] = "Ingestion:Strategy",
    ["--prefix"] = "Ingestion:Prefix",
    ["--chunk-size"] = "Ingestion:ChunkSize",
    ["--overlap"] = "Ingestion:Overlap",
    ["--upload"] = "Ingestion:UploadFolder",
};

if (args.Contains("--help") || args.Contains("-h"))
{
    PrintHelp();
    return 0;
}

if (args.Contains("--list-strategies"))
{
    PrintStrategies();
    return 0;
}

var recreateIndex = args.Contains("--recreate-index");
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    // Read appsettings.json from the output folder no matter where the tool is launched from.
    ContentRootPath = AppContext.BaseDirectory,
});
builder.Configuration.AddCommandLine(args.Where(a => a != "--recreate-index").ToArray(), switchMappings);
builder.Services.AddRagCore(builder.Configuration);

using var host = builder.Build();
var config = host.Services.GetRequiredService<IConfiguration>();

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

if (config["Ingestion:UploadFolder"] is { Length: > 0 } uploadFolder)
{
    var uploaded = await host.Services.GetRequiredService<DocumentUploader>()
        .UploadFolderAsync(Path.GetFullPath(uploadFolder), overwrite: true, cts.Token);
    Console.WriteLine($"Uploaded {uploaded} file(s) from {uploadFolder}");
}

var strategyValue = config["Ingestion:Strategy"];
ChunkingStrategy strategy;
if (string.IsNullOrWhiteSpace(strategyValue))
{
    if (Console.IsInputRedirected)
    {
        Console.Error.WriteLine("No --strategy given. Use --list-strategies to see the options.");
        return 1;
    }

    strategy = PromptForStrategy();
}
else if (!ChunkingStrategyInfo.TryParse(strategyValue, out strategy))
{
    Console.Error.WriteLine($"Unknown strategy '{strategyValue}'.");
    PrintStrategies();
    return 1;
}

var request = new IngestionRequest(
    strategy,
    config["Ingestion:Prefix"],
    int.TryParse(config["Ingestion:ChunkSize"], out var size) ? size : null,
    int.TryParse(config["Ingestion:Overlap"], out var overlap) ? overlap : null,
    recreateIndex);

var pipeline = host.Services.GetRequiredService<IIngestionPipeline>();
var result = await pipeline.RunAsync(request, cts.Token);

Console.WriteLine();
Console.WriteLine($"Strategy  : {result.Strategy}");
Console.WriteLine($"Documents : {result.Documents}");
Console.WriteLine($"Chunks    : {result.Chunks}");
Console.WriteLine($"Duration  : {result.Duration:mm\\:ss\\.fff}");
foreach (var d in result.Details)
{
    Console.WriteLine($"  {d.Document,-50} {d.Chunks,5} chunks");
}

return 0;

static ChunkingStrategy PromptForStrategy()
{
    var values = Enum.GetValues<ChunkingStrategy>();
    Console.WriteLine("Select a chunking strategy:");
    for (var i = 0; i < values.Length; i++)
    {
        Console.WriteLine($"  {i + 1}. {values[i],-14} {ChunkingStrategyInfo.Descriptions[values[i]]}");
    }

    while (true)
    {
        Console.Write($"Choice [1-{values.Length}]: ");
        var input = Console.ReadLine();
        if (int.TryParse(input, out var choice) && choice >= 1 && choice <= values.Length)
        {
            return values[choice - 1];
        }

        if (ChunkingStrategyInfo.TryParse(input, out var named))
        {
            return named;
        }
    }
}

static void PrintStrategies()
{
    Console.WriteLine("Available chunking strategies:");
    foreach (var (strategy, description) in ChunkingStrategyInfo.Descriptions)
    {
        Console.WriteLine($"  {strategy,-14} {description}");
    }
}

static void PrintHelp()
{
    Console.WriteLine(
        """
        RAG ingestion: Azure Blob Storage -> chunking -> embeddings -> Azure AI Search

        Options:
          -s, --strategy <name>   Chunking strategy (see --list-strategies). Prompts when omitted.
          --prefix <path>         Only ingest blobs whose name starts with this prefix.
          --chunk-size <chars>    Override Chunking:ChunkSize.
          --overlap <chars>       Override Chunking:Overlap.
          --upload <folder>       Upload a local folder to the storage container before ingesting.
          --recreate-index        Drop and recreate the search index first.
          --list-strategies       Print the strategies and exit.

        Any setting can also be passed as --Section:Key=value, e.g. --Search:IndexName=my-index
        """);
}
