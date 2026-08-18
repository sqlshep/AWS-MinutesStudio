using System.Text;
using System.Text.Json;
using Amazon;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MinutesStudio.Core.Configuration;
using MinutesStudio.Core.Models;
using OpenSearch.Net;
using OpenSearch.Net.Auth.AwsSigV4;
using HttpMethod = OpenSearch.Net.HttpMethod;

namespace MinutesStudio.Core.Services;

public interface ISearchService
{
    Task EnsureIndexAsync(CancellationToken ct = default);
    Task UploadAsync(IReadOnlyList<DocumentChunk> chunks, CancellationToken ct = default);
    Task<IReadOnlyList<RetrievedChunk>> SearchAsync(string queryText, ReadOnlyMemory<float> queryVector, int topK, CancellationToken ct = default);

    /// <summary>Returns all chunks (optionally for a single source file), ordered by source and chunk index.</summary>
    Task<IReadOnlyList<RetrievedChunk>> GetChunksAsync(string? sourceFile, CancellationToken ct = default);

    /// <summary>Lists the distinct ingested documents (meetings).</summary>
    Task<IReadOnlyList<DocumentInfo>> ListDocumentsAsync(CancellationToken ct = default);

    Task<long> GetDocumentCountAsync(CancellationToken ct = default);

    /// <summary>Creates a fresh, uniquely-named (timestamped) index and makes it the active one.</summary>
    Task ResetIndexAsync(CancellationToken ct = default);

    /// <summary>The name of the index currently being read/written (newest timestamped index), or null if none.</summary>
    Task<string?> GetActiveIndexNameAsync(CancellationToken ct = default);
}

/// <summary>
/// Amazon OpenSearch Serverless (AOSS) vector store. Creates a k-NN + BM25 index, upserts chunks,
/// and runs hybrid retrieval by combining a knn query with a BM25 match query and fusing the two
/// result sets with Reciprocal Rank Fusion (RRF) — AOSS does not support server-side search
/// pipelines, so the fusion is done client-side. This is more robust than either signal alone for
/// short analyst questions, mirroring the original Azure hybrid behavior.
/// </summary>
public sealed class OpenSearchService : ISearchService
{
    /// <summary>RRF damping constant. 60 is the value from the original RRF paper and a common default.</summary>
    private const int RrfK = 60;

    private readonly OpenSearchLowLevelClient _client;
    private readonly int _dimensions;
    private readonly ILogger<OpenSearchService> _logger;

    /// <summary>Configured IndexName is treated as a prefix; each concrete index is "{prefix}-{yyyyMMdd-HHmmss}".</summary>
    private readonly string _prefix;
    private readonly SemaphoreSlim _activeLock = new(1, 1);
    private string? _activeIndex;

    public OpenSearchService(
        IOptions<OpenSearchOptions> searchOptions,
        IOptions<BedrockOptions> bedrockOptions,
        ILogger<OpenSearchService> logger)
    {
        var options = searchOptions.Value;
        _dimensions = bedrockOptions.Value.EmbeddingDimensions;
        _logger = logger;
        _prefix = options.IndexName;

        if (string.IsNullOrWhiteSpace(options.Endpoint))
            throw new InvalidOperationException(
                "OpenSearch:Endpoint is not configured. Set it via user-secrets or app settings.");

        var endpoint = new Uri(options.Endpoint);
        var region = RegionEndpoint.GetBySystemName(
            string.IsNullOrWhiteSpace(options.Region) ? "us-east-1" : options.Region);

        // "es" = managed OpenSearch Service domain, "aoss" = Serverless collection. Auto-detect
        // from the endpoint host when not explicitly configured.
        var serviceCode = string.IsNullOrWhiteSpace(options.ServiceCode)
            ? (endpoint.Host.Contains(".aoss.", StringComparison.OrdinalIgnoreCase)
                ? AwsSigV4HttpConnection.OpenSearchServerlessService
                : AwsSigV4HttpConnection.OpenSearchService)
            : options.ServiceCode;

        var connection = new AwsSigV4HttpConnection(region, service: serviceCode);
        // DisableDirectStreaming so the server's response body is captured even on errors (needed to
        // surface the real reason behind a 403 — IAM/access-policy vs FGAC).
        var config = new ConnectionConfiguration(endpoint, connection).DisableDirectStreaming();
        _client = new OpenSearchLowLevelClient(config);
        _logger.LogInformation("OpenSearch client configured for {Host} (SigV4 service '{Service}').",
            endpoint.Host, serviceCode);
    }

    public async Task<string?> GetActiveIndexNameAsync(CancellationToken ct = default)
    {
        if (_activeIndex is not null) return _activeIndex;

        await _activeLock.WaitAsync(ct);
        try
        {
            if (_activeIndex is not null) return _activeIndex;
            _activeIndex = (await ListMatchingIndexesAsync(ct))
                .OrderByDescending(n => n, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            return _activeIndex;
        }
        finally
        {
            _activeLock.Release();
        }
    }

    /// <summary>
    /// Lists prefix-matching indexes via "GET /_alias" (every index appears as a JSON key), filtering
    /// client-side. Deliberately avoids a "*" wildcard and a query string in the request path: the
    /// AWS SigV4 signer and a managed OpenSearch Service (es) domain disagree on how to encode "*",
    /// and the low-level client rejects query strings in the path.
    /// </summary>
    private async Task<IReadOnlyList<string>> ListMatchingIndexesAsync(CancellationToken ct)
    {
        var response = await _client.DoRequestAsync<StringResponse>(
            HttpMethod.GET, "/_alias", ct);
        EnsureSuccess(response, "list indexes");

        using var doc = JsonDocument.Parse(response.Body);
        return doc.RootElement.EnumerateObject()
            .Select(p => p.Name)
            .Where(name =>
                name.StartsWith(_prefix + "-", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, _prefix, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private async Task<string> CreateNewIndexAsync(CancellationToken ct)
    {
        var name = $"{_prefix}-{DateTime.Now:yyyyMMdd-HHmmss}";
        var body = BuildIndexBody();
        var response = await _client.DoRequestAsync<StringResponse>(
            HttpMethod.PUT, $"/{name}", ct, PostData.String(body));
        EnsureSuccess(response, $"create index '{name}'");

        await _activeLock.WaitAsync(ct);
        try { _activeIndex = name; }
        finally { _activeLock.Release(); }

        _logger.LogInformation("Created search index '{Index}'.", name);
        return name;
    }

    public async Task EnsureIndexAsync(CancellationToken ct = default)
    {
        var active = await GetActiveIndexNameAsync(ct);
        if (active is not null)
        {
            _logger.LogInformation("Using existing search index '{Index}'.", active);
            return;
        }

        await CreateNewIndexAsync(ct);
    }

    public async Task ResetIndexAsync(CancellationToken ct = default)
    {
        var old = await ListMatchingIndexesAsync(ct);
        var created = await CreateNewIndexAsync(ct);

        foreach (var name in old.Where(n => !string.Equals(n, created, StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                var response = await _client.DoRequestAsync<StringResponse>(HttpMethod.DELETE, $"/{name}", ct);
                EnsureSuccess(response, $"delete index '{name}'");
                _logger.LogInformation("Deleted old search index '{Index}'.", name);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not delete old index '{Index}'.", name);
            }
        }
    }

    public async Task UploadAsync(IReadOnlyList<DocumentChunk> chunks, CancellationToken ct = default)
    {
        if (chunks.Count == 0) return;

        var index = await GetActiveIndexNameAsync(ct)
            ?? throw new InvalidOperationException("No search index exists yet. Ingest documents first.");

        const int batchSize = 50;
        for (var i = 0; i < chunks.Count; i += batchSize)
        {
            var batch = chunks.Skip(i).Take(batchSize).ToList();
            var ndjson = BuildBulkBody(index, batch);
            var response = await Retry.OnTransientAsync(
                () => _client.DoRequestAsync<StringResponse>(
                    HttpMethod.POST, "/_bulk", ct, PostData.String(ndjson)), ct: ct);
            EnsureSuccess(response, "bulk upload");
            EnsureNoBulkErrors(response);
        }

        _logger.LogInformation("Uploaded {Count} chunks to '{Index}'.", chunks.Count, index);
    }

    public async Task<IReadOnlyList<RetrievedChunk>> SearchAsync(
        string queryText, ReadOnlyMemory<float> queryVector, int topK, CancellationToken ct = default)
    {
        var active = await GetActiveIndexNameAsync(ct);
        if (active is null) return Array.Empty<RetrievedChunk>();

        // Two independent retrievals fused with RRF (client-side hybrid).
        var vectorHits = await RunSearchAsync(active, BuildKnnQuery(queryVector, topK), ct);
        var keywordHits = await RunSearchAsync(active, BuildKeywordQuery(queryText, topK), ct);

        return FuseWithRrf(vectorHits, keywordHits, topK);
    }

    public async Task<IReadOnlyList<RetrievedChunk>> GetChunksAsync(string? sourceFile, CancellationToken ct = default)
    {
        var active = await GetActiveIndexNameAsync(ct);
        if (active is null) return Array.Empty<RetrievedChunk>();

        var hits = await RunSearchAsync(active, BuildFetchAllQuery(sourceFile), ct);

        return hits
            .OrderBy(c => c.MeetingDate ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(c => c.SourceFile, StringComparer.Ordinal)
            .ThenBy(c => c.ChunkIndex)
            .ToList();
    }

    public async Task<IReadOnlyList<DocumentInfo>> ListDocumentsAsync(CancellationToken ct = default)
    {
        var chunks = await GetChunksAsync(null, ct);
        return chunks
            .GroupBy(c => c.SourceFile)
            .Select(g => new DocumentInfo
            {
                SourceFile = g.Key,
                Title = g.First().Title,
                MeetingDate = g.First().MeetingDate,
                ChunkCount = g.Count()
            })
            .OrderBy(d => d.MeetingDate ?? string.Empty, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<long> GetDocumentCountAsync(CancellationToken ct = default)
    {
        var active = await GetActiveIndexNameAsync(ct);
        if (active is null) return 0;

        const string body = "{\"size\":0,\"track_total_hits\":true,\"query\":{\"match_all\":{}}}";
        var response = await _client.DoRequestAsync<StringResponse>(
            HttpMethod.POST, $"/{active}/_search", ct, PostData.String(body));
        if (!response.Success) return 0;

        using var doc = JsonDocument.Parse(response.Body);
        return doc.RootElement.GetProperty("hits").GetProperty("total").GetProperty("value").GetInt64();
    }

    // ---- query bodies -------------------------------------------------------

    private static string BuildKnnQuery(ReadOnlyMemory<float> vector, int topK)
    {
        var sb = new StringBuilder();
        sb.Append("{\"size\":").Append(topK)
          .Append(",\"_source\":").Append(SourceFields)
          .Append(",\"query\":{\"knn\":{\"contentVector\":{\"vector\":[");
        var span = vector.Span;
        for (var i = 0; i < span.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(span[i].ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        sb.Append("],\"k\":").Append(topK).Append("}}}}");
        return sb.ToString();
    }

    private static string BuildKeywordQuery(string queryText, int topK) =>
        $"{{\"size\":{topK},\"_source\":{SourceFields}," +
        $"\"query\":{{\"match\":{{\"content\":{JsonSerializer.Serialize(queryText)}}}}}}}";

    private static string BuildFetchAllQuery(string? sourceFile)
    {
        var query = string.IsNullOrWhiteSpace(sourceFile)
            ? "{\"match_all\":{}}"
            : $"{{\"term\":{{\"sourceFile\":{JsonSerializer.Serialize(sourceFile)}}}}}";
        return $"{{\"size\":1000,\"_source\":{SourceFields},\"query\":{query}}}";
    }

    private const string SourceFields =
        "[\"id\",\"content\",\"sourceFile\",\"title\",\"meetingDate\",\"chunkIndex\"]";

    private async Task<List<RetrievedChunk>> RunSearchAsync(string index, string body, CancellationToken ct)
    {
        var response = await Retry.OnTransientAsync(
            () => _client.DoRequestAsync<StringResponse>(
                HttpMethod.POST, $"/{index}/_search", ct, PostData.String(body)), ct: ct);
        EnsureSuccess(response, "search");
        return ParseHits(response.Body);
    }

    private static List<RetrievedChunk> ParseHits(string json)
    {
        var results = new List<RetrievedChunk>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("hits", out var hitsRoot)) return results;
        if (!hitsRoot.TryGetProperty("hits", out var hits)) return results;

        foreach (var hit in hits.EnumerateArray())
        {
            var src = hit.GetProperty("_source");
            results.Add(new RetrievedChunk
            {
                Id = GetString(src, "id"),
                Content = GetString(src, "content"),
                SourceFile = GetString(src, "sourceFile"),
                Title = GetString(src, "title"),
                MeetingDate = src.TryGetProperty("meetingDate", out var md) && md.ValueKind == JsonValueKind.String
                    ? md.GetString()
                    : null,
                ChunkIndex = src.TryGetProperty("chunkIndex", out var ci) && ci.ValueKind == JsonValueKind.Number
                    ? ci.GetInt32()
                    : 0,
                Score = hit.TryGetProperty("_score", out var sc) && sc.ValueKind == JsonValueKind.Number
                    ? sc.GetDouble()
                    : 0d
            });
        }

        return results;
    }

    /// <summary>Reciprocal Rank Fusion of two ranked result lists, keyed by chunk id.</summary>
    private static IReadOnlyList<RetrievedChunk> FuseWithRrf(
        IReadOnlyList<RetrievedChunk> a, IReadOnlyList<RetrievedChunk> b, int topK)
    {
        var scores = new Dictionary<string, double>();
        var byId = new Dictionary<string, RetrievedChunk>();

        void Accumulate(IReadOnlyList<RetrievedChunk> list)
        {
            for (var rank = 0; rank < list.Count; rank++)
            {
                var chunk = list[rank];
                scores[chunk.Id] = scores.GetValueOrDefault(chunk.Id) + 1.0 / (RrfK + rank + 1);
                byId.TryAdd(chunk.Id, chunk);
            }
        }

        Accumulate(a);
        Accumulate(b);

        return scores
            .OrderByDescending(kv => kv.Value)
            .Take(topK)
            .Select(kv =>
            {
                var c = byId[kv.Key];
                return new RetrievedChunk
                {
                    Id = c.Id,
                    Content = c.Content,
                    SourceFile = c.SourceFile,
                    Title = c.Title,
                    MeetingDate = c.MeetingDate,
                    ChunkIndex = c.ChunkIndex,
                    Score = kv.Value
                };
            })
            .ToList();
    }

    // ---- index + bulk bodies ------------------------------------------------

    private string BuildIndexBody() =>
        "{" +
            "\"settings\":{\"index.knn\":true}," +
            "\"mappings\":{\"properties\":{" +
                "\"id\":{\"type\":\"keyword\"}," +
                "\"content\":{\"type\":\"text\"}," +
                "\"sourceFile\":{\"type\":\"keyword\"}," +
                "\"title\":{\"type\":\"text\",\"fields\":{\"raw\":{\"type\":\"keyword\"}}}," +
                "\"meetingDate\":{\"type\":\"keyword\"}," +
                "\"chunkIndex\":{\"type\":\"integer\"}," +
                "\"pageStart\":{\"type\":\"integer\"}," +
                "\"pageEnd\":{\"type\":\"integer\"}," +
                "\"contentVector\":{\"type\":\"knn_vector\",\"dimension\":" + _dimensions +
                    ",\"method\":{\"name\":\"hnsw\",\"engine\":\"faiss\",\"space_type\":\"l2\"}}" +
            "}}" +
        "}";

    private static string BuildBulkBody(string index, IReadOnlyList<DocumentChunk> chunks)
    {
        var sb = new StringBuilder();
        foreach (var c in chunks)
        {
            // Custom _id gives idempotent upserts (re-ingesting a file replaces its chunks).
            sb.Append("{\"index\":{\"_index\":").Append(JsonSerializer.Serialize(index))
              .Append(",\"_id\":").Append(JsonSerializer.Serialize(c.Id)).Append("}}\n");

            var source = JsonSerializer.Serialize(new
            {
                id = c.Id,
                content = c.Content,
                sourceFile = c.SourceFile,
                title = c.Title,
                meetingDate = c.MeetingDate,
                chunkIndex = c.ChunkIndex,
                pageStart = c.PageStart,
                pageEnd = c.PageEnd,
                contentVector = c.ContentVector.ToArray()
            });
            sb.Append(source).Append('\n');
        }

        return sb.ToString();
    }

    // ---- helpers ------------------------------------------------------------

    private static string GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static void EnsureSuccess(StringResponse response, string action)
    {
        if (response.Success) return;
        // Prefer the server's response body (contains the real authz reason); fall back to the
        // low-level exception message. Body is populated because DisableDirectStreaming is on.
        var body = string.IsNullOrWhiteSpace(response.Body) ? null : response.Body.Trim();
        var detail = body ?? response.OriginalException?.Message ?? "unknown error";
        throw new InvalidOperationException(
            $"OpenSearch request failed ({action}, HTTP {(int)response.HttpStatusCode.GetValueOrDefault()}): {detail}",
            response.OriginalException);
    }

    private static void EnsureNoBulkErrors(StringResponse response)
    {
        using var doc = JsonDocument.Parse(response.Body);
        if (doc.RootElement.TryGetProperty("errors", out var errors) &&
            errors.ValueKind == JsonValueKind.True)
        {
            var first = doc.RootElement.GetProperty("items").EnumerateArray()
                .Select(i => i.EnumerateObject().First().Value)
                .FirstOrDefault(op => op.TryGetProperty("error", out _));
            var reason = first.ValueKind == JsonValueKind.Object &&
                         first.TryGetProperty("error", out var err)
                ? err.ToString()
                : "one or more bulk items failed";
            throw new InvalidOperationException($"OpenSearch bulk indexing error: {reason}");
        }
    }
}
