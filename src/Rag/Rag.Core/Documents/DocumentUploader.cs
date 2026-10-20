using Azure.Storage.Blobs;
using Microsoft.Extensions.Logging;

namespace Rag.Core.Documents;

/// <summary>Uploads local files into the documents container (sample data for local dev, or a CLI --upload).</summary>
public sealed class DocumentUploader(BlobContainerClient container, ILogger<DocumentUploader> logger)
{
    public async Task<int> UploadFolderAsync(string folder, bool overwrite, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(folder))
        {
            throw new DirectoryNotFoundException($"Folder '{folder}' does not exist.");
        }

        await container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);

        var uploaded = 0;
        foreach (var path in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            if (!DocumentTextExtractor.IsSupported(path))
            {
                continue;
            }

            var name = Path.GetRelativePath(folder, path).Replace('\\', '/');
            var blob = container.GetBlobClient(name);
            if (!overwrite && await blob.ExistsAsync(cancellationToken))
            {
                continue;
            }

            await blob.UploadAsync(path, overwrite: true, cancellationToken);
            uploaded++;
            logger.LogInformation("Uploaded {Blob}", name);
        }

        return uploaded;
    }
}
