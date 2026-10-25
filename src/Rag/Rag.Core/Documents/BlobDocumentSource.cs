using System.Runtime.CompilerServices;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Logging;

namespace Rag.Core.Documents;

/// <summary>Reads documents from an Azure Blob Storage container.</summary>
public sealed class BlobDocumentSource(BlobContainerClient container, ILogger<BlobDocumentSource> logger) : IDocumentSource
{
    public async IAsyncEnumerable<string> ListDocumentNamesAsync(string? prefix = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var blob in container.GetBlobsAsync(new GetBlobsOptions { Prefix = prefix }, cancellationToken))
        {
            if (DocumentTextExtractor.IsSupported(blob.Name))
            {
                yield return blob.Name;
            }
        }
    }

    public async IAsyncEnumerable<SourceDocument> GetDocumentsAsync(string? prefix = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var blob in container.GetBlobsAsync(new GetBlobsOptions { Prefix = prefix }, cancellationToken))
        {
            if (!DocumentTextExtractor.IsSupported(blob.Name))
            {
                logger.LogInformation("Skipping unsupported file {Blob}", blob.Name);
                continue;
            }

            var client = container.GetBlobClient(blob.Name);
            string text;
            try
            {
                var download = await client.DownloadStreamingAsync(cancellationToken: cancellationToken);
                await using var stream = download.Value.Content;
                text = await DocumentTextExtractor.ExtractAsync(stream, blob.Name, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Failed to read {Blob}; skipping", blob.Name);
                continue;
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                logger.LogInformation("No text extracted from {Blob}; skipping", blob.Name);
                continue;
            }

            yield return new SourceDocument(
                blob.Name,
                client.Uri.ToString(),
                blob.Properties.ContentType ?? "application/octet-stream",
                text,
                blob.Properties.LastModified);
        }
    }
}
