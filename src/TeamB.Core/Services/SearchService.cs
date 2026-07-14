using Azure;
using Azure.Identity;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TeamB.Core.Configuration;
using TeamB.Core.Models;

namespace TeamB.Core.Services;

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
/// Azure AI Search vector store. Creates a hybrid (keyword + vector) index, uploads chunks,
/// and runs hybrid queries — combining BM25 keyword matching with HNSW vector similarity,
/// which is more robust than either alone for short analyst questions.
/// </summary>
public sealed class AzureSearchService : ISearchService
{
    private const string VectorProfile = "vec-profile";
    private const string HnswConfig = "hnsw-config";

    private readonly SearchIndexClient _indexClient;
    private readonly AzureSearchOptions _options;
    private readonly int _dimensions;
    private readonly ILogger<AzureSearchService> _logger;

    /// <summary>Configured IndexName is treated as a prefix; each concrete index is "{prefix}-{yyyyMMdd-HHmmss}".</summary>
    private readonly string _prefix;
    private readonly SemaphoreSlim _activeLock = new(1, 1);
    private string? _activeIndex;

    public AzureSearchService(
        IOptions<AzureSearchOptions> searchOptions,
        IOptions<AzureOpenAIOptions> openAiOptions,
        ILogger<AzureSearchService> logger)
    {
        _options = searchOptions.Value;
        _dimensions = openAiOptions.Value.EmbeddingDimensions;
        _logger = logger;
        _prefix = _options.IndexName;

        var endpoint = new Uri(_options.Endpoint);
        _indexClient = string.IsNullOrWhiteSpace(_options.ApiKey)
            ? new SearchIndexClient(endpoint, new DefaultAzureCredential())
            : new SearchIndexClient(endpoint, new AzureKeyCredential(_options.ApiKey));
    }

    /// <summary>Resolves (and caches) the newest index matching the prefix. Timestamped names sort chronologically.</summary>
    public async Task<string?> GetActiveIndexNameAsync(CancellationToken ct = default)
    {
        if (_activeIndex is not null) return _activeIndex;

        await _activeLock.WaitAsync(ct);
        try
        {
            if (_activeIndex is not null) return _activeIndex;

            var matches = new List<string>();
            await foreach (var name in _indexClient.GetIndexNamesAsync(ct))
            {
                if (name.StartsWith(_prefix + "-", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, _prefix, StringComparison.OrdinalIgnoreCase))
                {
                    matches.Add(name);
                }
            }

            _activeIndex = matches
                .OrderByDescending(n => n, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            return _activeIndex;
        }
        finally
        {
            _activeLock.Release();
        }
    }

    private async Task<SearchClient> GetActiveSearchClientAsync(CancellationToken ct)
    {
        var name = await GetActiveIndexNameAsync(ct)
            ?? throw new InvalidOperationException("No search index exists yet. Ingest documents first.");
        return _indexClient.GetSearchClient(name);
    }

    private async Task<string> CreateNewIndexAsync(CancellationToken ct)
    {
        var name = $"{_prefix}-{DateTime.Now:yyyyMMdd-HHmmss}";
        await _indexClient.CreateOrUpdateIndexAsync(BuildIndex(name), cancellationToken: ct);

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
        // Snapshot the existing prefix-matching indexes so we can clean them up after switching.
        var old = new List<string>();
        await foreach (var name in _indexClient.GetIndexNamesAsync(ct))
        {
            if (name.StartsWith(_prefix + "-", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, _prefix, StringComparison.OrdinalIgnoreCase))
            {
                old.Add(name);
            }
        }

        // Create a brand-new, ready-to-use index (no delete-then-recreate race on the same name).
        var created = await CreateNewIndexAsync(ct);

        // Best-effort cleanup of the now-orphaned older indexes.
        foreach (var name in old.Where(n => !string.Equals(n, created, StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                await _indexClient.DeleteIndexAsync(name, ct);
                _logger.LogInformation("Deleted old search index '{Index}'.", name);
            }
            catch (RequestFailedException ex)
            {
                _logger.LogWarning(ex, "Could not delete old index '{Index}'.", name);
            }
        }
    }

    public async Task UploadAsync(IReadOnlyList<DocumentChunk> chunks, CancellationToken ct = default)
    {
        if (chunks.Count == 0) return;

        var client = await GetActiveSearchClientAsync(ct);
        var documents = chunks.Select(c => new SearchIndexDocument
        {
            Id = c.Id,
            Content = c.Content,
            SourceFile = c.SourceFile,
            Title = c.Title,
            MeetingDate = c.MeetingDate,
            ChunkIndex = c.ChunkIndex,
            PageStart = c.PageStart,
            PageEnd = c.PageEnd,
            ContentVector = c.ContentVector.ToArray()
        }).ToList();

        // Batch to stay well under service payload limits.
        const int batchSize = 50;
        for (var i = 0; i < documents.Count; i += batchSize)
        {
            var batch = documents.Skip(i).Take(batchSize).ToList();
            await client.MergeOrUploadDocumentsAsync(batch, cancellationToken: ct);
        }

        _logger.LogInformation("Uploaded {Count} chunks to '{Index}'.", documents.Count, _activeIndex);
    }

    public async Task<IReadOnlyList<RetrievedChunk>> SearchAsync(
        string queryText, ReadOnlyMemory<float> queryVector, int topK, CancellationToken ct = default)
    {
        var options = new SearchOptions
        {
            Size = topK,
            VectorSearch = new VectorSearchOptions
            {
                Queries =
                {
                    new VectorizedQuery(queryVector)
                    {
                        KNearestNeighborsCount = topK,
                        Fields = { "contentVector" }
                    }
                }
            }
        };
        options.Select.Add("id");
        options.Select.Add("content");
        options.Select.Add("sourceFile");
        options.Select.Add("title");
        options.Select.Add("meetingDate");
        options.Select.Add("chunkIndex");

        var active = await GetActiveIndexNameAsync(ct);
        if (active is null) return Array.Empty<RetrievedChunk>();
        var client = _indexClient.GetSearchClient(active);

        // Passing queryText alongside the vector query makes this a hybrid search.
        var response = await client.SearchAsync<SearchIndexDocument>(queryText, options, ct);

        var results = new List<RetrievedChunk>();
        await foreach (var item in response.Value.GetResultsAsync())
        {
            var doc = item.Document;
            results.Add(new RetrievedChunk
            {
                Id = doc.Id,
                Content = doc.Content,
                SourceFile = doc.SourceFile,
                Title = doc.Title,
                MeetingDate = doc.MeetingDate,
                ChunkIndex = doc.ChunkIndex,
                Score = item.Score ?? 0d
            });
        }

        return results;
    }

    public async Task<IReadOnlyList<RetrievedChunk>> GetChunksAsync(string? sourceFile, CancellationToken ct = default)
    {
        var options = new SearchOptions { Size = 1000 };
        options.Select.Add("id");
        options.Select.Add("content");
        options.Select.Add("sourceFile");
        options.Select.Add("title");
        options.Select.Add("meetingDate");
        options.Select.Add("chunkIndex");
        if (!string.IsNullOrWhiteSpace(sourceFile))
            options.Filter = $"sourceFile eq '{sourceFile.Replace("'", "''")}'";

        var active = await GetActiveIndexNameAsync(ct);
        if (active is null) return Array.Empty<RetrievedChunk>();
        var client = _indexClient.GetSearchClient(active);

        var response = await client.SearchAsync<SearchIndexDocument>("*", options, ct);

        var results = new List<RetrievedChunk>();
        await foreach (var item in response.Value.GetResultsAsync())
        {
            var doc = item.Document;
            results.Add(new RetrievedChunk
            {
                Id = doc.Id,
                Content = doc.Content,
                SourceFile = doc.SourceFile,
                Title = doc.Title,
                MeetingDate = doc.MeetingDate,
                ChunkIndex = doc.ChunkIndex,
                Score = item.Score ?? 0d
            });
        }

        return results
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

        try
        {
            var response = await _indexClient.GetSearchClient(active).GetDocumentCountAsync(ct);
            return response.Value;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return 0;
        }
    }

    private SearchIndex BuildIndex(string indexName) => new(indexName)
    {
        Fields =
        {
            new SimpleField("id", SearchFieldDataType.String) { IsKey = true, IsFilterable = true },
            new SearchableField("content"),
            new SimpleField("sourceFile", SearchFieldDataType.String) { IsFilterable = true, IsFacetable = true },
            new SearchableField("title") { IsFilterable = true },
            new SimpleField("meetingDate", SearchFieldDataType.String) { IsFilterable = true, IsSortable = true },
            new SimpleField("chunkIndex", SearchFieldDataType.Int32) { IsFilterable = true },
            new SimpleField("pageStart", SearchFieldDataType.Int32),
            new SimpleField("pageEnd", SearchFieldDataType.Int32),
            new SearchField("contentVector", SearchFieldDataType.Collection(SearchFieldDataType.Single))
            {
                IsSearchable = true,
                VectorSearchDimensions = _dimensions,
                VectorSearchProfileName = VectorProfile
            }
        },
        VectorSearch = new VectorSearch
        {
            Profiles = { new VectorSearchProfile(VectorProfile, HnswConfig) },
            Algorithms = { new HnswAlgorithmConfiguration(HnswConfig) }
        }
    };
}
