using Amazon;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;
using MinutesStudio.Core.Configuration;

namespace MinutesStudio.Core.Services;

/// <summary>
/// Document source backed by an Amazon S3 bucket. Also provides an uploader that seeds the bucket
/// from the local samples folder (used by the "Upload samples" action) and single-file uploads.
/// </summary>
public interface IBlobDocumentSource : IDocumentSource
{
    /// <summary>Uploads every PDF from <paramref name="localFolder"/> into the bucket. Returns the file names uploaded.</summary>
    Task<IReadOnlyList<string>> UploadFromFolderAsync(string localFolder, CancellationToken ct = default);

    /// <summary>Uploads a single PDF (e.g. picked from the user's machine) into the bucket.</summary>
    Task UploadAsync(string fileName, Stream content, CancellationToken ct = default);

    /// <summary>Downloads an object's bytes as a seekable stream (used for inline preview).</summary>
    Task<Stream> DownloadAsync(string fileName, CancellationToken ct = default);
}

public sealed class S3DocumentSource : IBlobDocumentSource
{
    private readonly IAmazonS3 _s3;
    private readonly string _bucket;
    private readonly string _prefix;

    public S3DocumentSource(IOptions<S3Options> options)
    {
        var opts = options.Value;
        if (!opts.IsConfigured)
            throw new InvalidOperationException(
                "S3:BucketName is not configured. Set it via user-secrets or app settings.");

        _bucket = opts.BucketName;
        _prefix = opts.Prefix?.Trim('/') ?? string.Empty;

        var region = RegionEndpoint.GetBySystemName(
            string.IsNullOrWhiteSpace(opts.Region) ? "us-east-1" : opts.Region);
        _s3 = new AmazonS3Client(region);
    }

    public string Description => string.IsNullOrEmpty(_prefix)
        ? $"S3 bucket '{_bucket}'"
        : $"S3 bucket '{_bucket}/{_prefix}'";

    public async Task<IReadOnlyList<SourceDocumentRef>> ListAsync(CancellationToken ct = default)
    {
        var refs = new List<SourceDocumentRef>();
        var request = new ListObjectsV2Request
        {
            BucketName = _bucket,
            Prefix = string.IsNullOrEmpty(_prefix) ? null : _prefix + "/"
        };

        ListObjectsV2Response response;
        do
        {
            response = await _s3.ListObjectsV2Async(request, ct);
            foreach (var obj in response.S3Objects)
            {
                if (!obj.Key.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) continue;

                var key = obj.Key;
                var name = NameFromKey(key);
                refs.Add(new SourceDocumentRef(name, async token =>
                {
                    var get = await _s3.GetObjectAsync(_bucket, key, token);
                    return get.ResponseStream;
                }));
            }

            request.ContinuationToken = response.NextContinuationToken;
        }
        while (response.IsTruncated == true);

        return refs.OrderBy(r => r.FileName, StringComparer.Ordinal).ToList();
    }

    public async Task<IReadOnlyList<string>> UploadFromFolderAsync(string localFolder, CancellationToken ct = default)
    {
        if (!Directory.Exists(localFolder))
            throw new DirectoryNotFoundException($"Samples folder not found: {Path.GetFullPath(localFolder)}");

        var uploaded = new List<string>();
        foreach (var file in Directory.GetFiles(localFolder, "*.pdf").OrderBy(f => f))
        {
            var name = Path.GetFileName(file);
            await using var stream = File.OpenRead(file);
            await PutAsync(name, stream, ct);
            uploaded.Add(name);
        }

        return uploaded;
    }

    public Task UploadAsync(string fileName, Stream content, CancellationToken ct = default) =>
        PutAsync(Path.GetFileName(fileName), content, ct);

    public async Task<Stream> DownloadAsync(string fileName, CancellationToken ct = default)
    {
        var get = await _s3.GetObjectAsync(_bucket, KeyFor(Path.GetFileName(fileName)), ct);

        // Buffer into a seekable MemoryStream so inline PDF preview can use HTTP range processing.
        var buffer = new MemoryStream();
        await get.ResponseStream.CopyToAsync(buffer, ct);
        buffer.Position = 0;
        return buffer;
    }

    private async Task PutAsync(string name, Stream content, CancellationToken ct)
    {
        // S3 needs a known length; buffer non-seekable streams (e.g. browser uploads) first.
        Stream body = content;
        MemoryStream? owned = null;
        if (!content.CanSeek)
        {
            owned = new MemoryStream();
            await content.CopyToAsync(owned, ct);
            owned.Position = 0;
            body = owned;
        }

        try
        {
            await _s3.PutObjectAsync(new PutObjectRequest
            {
                BucketName = _bucket,
                Key = KeyFor(name),
                InputStream = body,
                ContentType = "application/pdf",
                AutoCloseStream = false
            }, ct);
        }
        finally
        {
            owned?.Dispose();
        }
    }

    private string KeyFor(string name) =>
        string.IsNullOrEmpty(_prefix) ? name : $"{_prefix}/{name}";

    private string NameFromKey(string key) =>
        string.IsNullOrEmpty(_prefix) || !key.StartsWith(_prefix + "/", StringComparison.Ordinal)
            ? key
            : key[(_prefix.Length + 1)..];
}
