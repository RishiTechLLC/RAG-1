using System.ComponentModel;
using System.Text;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using ModelContextProtocol.Server;

namespace Mcp.Server.Tools;

/// <summary>Data source #2: Azure Blob Storage (the same container the RAG pipeline ingests).</summary>
[McpServerToolType]
public sealed class DocumentStorageTools(BlobContainerClient container)
{
    private const int MaxCharacters = 8000;

    [McpServerTool(Name = "list_documents", ReadOnly = true), Description("List files in the document storage container with size and last-modified date.")]
    public async Task<IReadOnlyList<object>> ListDocuments(
        [Description("Optional folder/prefix, e.g. 'policies/'.")] string? prefix = null,
        CancellationToken cancellationToken = default)
    {
        var items = new List<object>();
        await foreach (var blob in container.GetBlobsAsync(new GetBlobsOptions { Prefix = prefix }, cancellationToken))
        {
            items.Add(new { name = blob.Name, sizeBytes = blob.Properties.ContentLength, lastModified = blob.Properties.LastModified });
            if (items.Count >= 200)
            {
                break;
            }
        }

        return items;
    }

    [McpServerTool(Name = "read_text_document", ReadOnly = true), Description("Read the raw contents of a text-based file (txt, md, csv, json) from storage. Output is truncated to 8000 characters. For semantic questions over documents prefer the knowledge-base search instead.")]
    public async Task<string> ReadTextDocument(
        [Description("Exact blob name as returned by list_documents.")] string name,
        CancellationToken cancellationToken = default)
    {
        var blob = container.GetBlobClient(name);
        if (!await blob.ExistsAsync(cancellationToken))
        {
            return $"Document '{name}' was not found.";
        }

        var content = await blob.DownloadContentAsync(cancellationToken);
        var text = Encoding.UTF8.GetString(content.Value.Content.ToArray());
        return text.Length <= MaxCharacters ? text : text[..MaxCharacters] + "\n...[truncated]";
    }
}
