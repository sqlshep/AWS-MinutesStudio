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
    Task ResetIndexAsync(CancellationToken ct = default);
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
    private readonly SearchClient _searchClient;
    private readonly AzureSearchOptions _options;
    private readonly int _dimensions;
    private readonly ILogger<AzureSearchService> _logger;

    public AzureSearchService(
        IOptions<AzureSearchOptions> searchOptions,
        IOptions<AzureOpenAIOptions> openAiOptions,
        ILogger<AzureSearchService> logger)
    {
        _options = searchOptions.Value;
        _dimensions = openAiOptions.Value.EmbeddingDimensions;
        _logger = logger;

        var endpoint = new Uri(_options.Endpoint);
        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            var credential = new AzureKeyCredential(_options.ApiKey);
            _indexClient = new SearchIndexClient(endpoint, credential);
            _searchClient = new SearchClient(endpoint, _options.IndexName, credential);
        }
        else
        {
            var credential = new DefaultAzureCredential();
            _indexClient = new SearchIndexClient(endpoint, credential);
            _searchClient = new SearchClient(endpoint, _options.IndexName, credential);
        }
    }

    public async Task EnsureIndexAsync(CancellationToken ct = default)
    {
        var exists = false;
        await foreach (var name in _indexClient.GetIndexNamesAsync(ct))
        {
            if (string.Equals(name, _options.IndexName, StringComparison.OrdinalIgnoreCase))
            {
                exists = true;
                break;
            }
        }

        if (exists)
        {
            _logger.LogInformation("Search index '{Index}' already exists.", _options.IndexName);
            return;
        }

        var index = BuildIndex();
        await _indexClient.CreateOrUpdateIndexAsync(index, cancellationToken: ct);
        _logger.LogInformation("Created search index '{Index}'.", _options.IndexName);
    }

    public async Task ResetIndexAsync(CancellationToken ct = default)
    {
        try
        {
            await _indexClient.DeleteIndexAsync(_options.IndexName, ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // Nothing to delete.
        }

        await _indexClient.CreateOrUpdateIndexAsync(BuildIndex(), cancellationToken: ct);
        _logger.LogInformation("Reset search index '{Index}'.", _options.IndexName);
    }

    public async Task UploadAsync(IReadOnlyList<DocumentChunk> chunks, CancellationToken ct = default)
    {
        if (chunks.Count == 0) return;

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
            await _searchClient.MergeOrUploadDocumentsAsync(batch, cancellationToken: ct);
        }

        _logger.LogInformation("Uploaded {Count} chunks to '{Index}'.", documents.Count, _options.IndexName);
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

        // Passing queryText alongside the vector query makes this a hybrid search.
        var response = await _searchClient.SearchAsync<SearchIndexDocument>(queryText, options, ct);

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

        var response = await _searchClient.SearchAsync<SearchIndexDocument>("*", options, ct);

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
        try
        {
            var response = await _searchClient.GetDocumentCountAsync(ct);
            return response.Value;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return 0;
        }
    }

    private SearchIndex BuildIndex() => new(_options.IndexName)
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
