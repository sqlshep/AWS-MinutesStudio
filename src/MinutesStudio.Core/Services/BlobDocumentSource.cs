using Azure.Storage.Blobs;
using Microsoft.Extensions.Options;
using MinutesStudio.Core.Configuration;

namespace MinutesStudio.Core.Services;

/// <summary>
/// Document source backed by an Azure Blob Storage container. Also provides an uploader that seeds
/// the container from the local samples folder (used by the "Upload samples to Blob" action).
/// </summary>
public interface IBlobDocumentSource : IDocumentSource
{
    /// <summary>Uploads every PDF from <paramref name="localFolder"/> into the container. Returns the file names uploaded.</summary>
    Task<IReadOnlyList<string>> UploadFromFolderAsync(string localFolder, CancellationToken ct = default);

    /// <summary>Uploads a single PDF (e.g. picked from the user's machine) into the container.</summary>
    Task UploadAsync(string fileName, Stream content, CancellationToken ct = default);

    /// <summary>Downloads a blob's bytes as a seekable stream (used for inline preview).</summary>
    Task<Stream> DownloadAsync(string fileName, CancellationToken ct = default);
}

public sealed class BlobDocumentSource : IBlobDocumentSource
{
    private readonly BlobContainerClient _container;

    public BlobDocumentSource(IOptions<AzureBlobOptions> options)
    {
        var opts = options.Value;
        if (!opts.IsConfigured)
            throw new InvalidOperationException(
                "AzureBlob:ConnectionString is not configured. Set it via user-secrets or app settings.");

        var serviceClient = new BlobServiceClient(opts.ConnectionString);
        _container = serviceClient.GetBlobContainerClient(opts.ContainerName);
    }

    public string Description => $"Blob container '{_container.Name}'";

    public async Task<IReadOnlyList<SourceDocumentRef>> ListAsync(CancellationToken ct = default)
    {
        await _container.CreateIfNotExistsAsync(cancellationToken: ct);

        var refs = new List<SourceDocumentRef>();
        await foreach (var item in _container.GetBlobsAsync(cancellationToken: ct))
        {
            if (!item.Name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) continue;

            var name = item.Name;
            refs.Add(new SourceDocumentRef(name, async token =>
            {
                var response = await _container.GetBlobClient(name).DownloadStreamingAsync(cancellationToken: token);
                return response.Value.Content;
            }));
        }

        return refs.OrderBy(r => r.FileName, StringComparer.Ordinal).ToList();
    }

    public async Task<IReadOnlyList<string>> UploadFromFolderAsync(string localFolder, CancellationToken ct = default)
    {
        if (!Directory.Exists(localFolder))
            throw new DirectoryNotFoundException($"Samples folder not found: {Path.GetFullPath(localFolder)}");

        await _container.CreateIfNotExistsAsync(cancellationToken: ct);

        var uploaded = new List<string>();
        foreach (var file in Directory.GetFiles(localFolder, "*.pdf").OrderBy(f => f))
        {
            var name = Path.GetFileName(file);
            await using var stream = File.OpenRead(file);
            await _container.GetBlobClient(name).UploadAsync(stream, overwrite: true, ct);
            uploaded.Add(name);
        }

        return uploaded;
    }

    public async Task UploadAsync(string fileName, Stream content, CancellationToken ct = default)
    {
        await _container.CreateIfNotExistsAsync(cancellationToken: ct);
        await _container.GetBlobClient(Path.GetFileName(fileName)).UploadAsync(content, overwrite: true, ct);
    }

    public async Task<Stream> DownloadAsync(string fileName, CancellationToken ct = default)
    {
        var response = await _container.GetBlobClient(fileName).DownloadContentAsync(ct);
        return response.Value.Content.ToStream();
    }
}
