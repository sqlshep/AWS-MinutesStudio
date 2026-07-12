using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TeamB.Core.Configuration;
using TeamB.Core.Models;

namespace TeamB.Core.Services;

/// <summary>Summary of an ingestion run for display in the UI.</summary>
public sealed record IngestionReport(int FilesProcessed, int ChunksIndexed, IReadOnlyList<string> FileNames);

public interface IIngestionService
{
    /// <summary>Ingests every PDF from the configured document source into the search index.</summary>
    Task<IngestionReport> IngestAsync(bool reset, CancellationToken ct = default);
}

/// <summary>
/// Orchestrates the ingestion pipeline: read PDF (from the document source) -> extract text ->
/// chunk -> embed -> upload to Azure AI Search. The source is abstracted (blob, folder, …).
/// </summary>
public sealed partial class IngestionService : IIngestionService
{
    private readonly IDocumentSource _source;
    private readonly IPdfTextExtractor _extractor;
    private readonly ITextChunker _chunker;
    private readonly IEmbeddingService _embeddings;
    private readonly ISearchService _search;
    private readonly RagOptions _rag;
    private readonly ILogger<IngestionService> _logger;

    public IngestionService(
        IDocumentSource source,
        IPdfTextExtractor extractor,
        ITextChunker chunker,
        IEmbeddingService embeddings,
        ISearchService search,
        IOptions<RagOptions> rag,
        ILogger<IngestionService> logger)
    {
        _source = source;
        _extractor = extractor;
        _chunker = chunker;
        _embeddings = embeddings;
        _search = search;
        _rag = rag.Value;
        _logger = logger;
    }

    public async Task<IngestionReport> IngestAsync(bool reset, CancellationToken ct = default)
    {
        if (reset)
            await _search.ResetIndexAsync(ct);
        else
            await _search.EnsureIndexAsync(ct);

        var documents = await _source.ListAsync(ct);
        var totalChunks = 0;
        var names = new List<string>();

        foreach (var doc in documents)
        {
            var count = await IngestDocumentAsync(doc, ct);
            totalChunks += count;
            names.Add(doc.FileName);
        }

        _logger.LogInformation(
            "Ingestion complete from {Source}: {Files} files, {Chunks} chunks.",
            _source.Description, documents.Count, totalChunks);
        return new IngestionReport(documents.Count, totalChunks, names);
    }

    private async Task<int> IngestDocumentAsync(SourceDocumentRef doc, CancellationToken ct)
    {
        byte[] bytes;
        await using (var stream = await doc.OpenAsync(ct))
        using (var buffer = new MemoryStream())
        {
            await stream.CopyToAsync(buffer, ct);
            bytes = buffer.ToArray();
        }

        var pages = _extractor.ExtractPages(bytes);
        var textChunks = _chunker.Chunk(pages, _rag.ChunkSizeChars, _rag.ChunkOverlapChars);
        if (textChunks.Count == 0)
        {
            _logger.LogWarning("No extractable text in {File}.", doc.FileName);
            return 0;
        }

        var meta = DocumentMetadata.FromFileName(doc.FileName);
        var idBase = SanitizeId(Path.GetFileNameWithoutExtension(doc.FileName));

        var vectors = await _embeddings.EmbedBatchAsync(textChunks.Select(c => c.Content).ToList(), ct);

        var documentChunks = new List<DocumentChunk>(textChunks.Count);
        for (var i = 0; i < textChunks.Count; i++)
        {
            var tc = textChunks[i];
            documentChunks.Add(new DocumentChunk
            {
                Id = $"{idBase}_{tc.Index}",
                Content = tc.Content,
                SourceFile = doc.FileName,
                Title = meta.Title,
                MeetingDate = meta.MeetingDate,
                ChunkIndex = tc.Index,
                PageStart = tc.PageStart,
                PageEnd = tc.PageEnd,
                ContentVector = vectors[i]
            });
        }

        await _search.UploadAsync(documentChunks, ct);
        _logger.LogInformation("Ingested {File}: {Chunks} chunks.", doc.FileName, documentChunks.Count);
        return documentChunks.Count;
    }

    /// <summary>Azure AI Search keys may only contain letters, digits, dash, underscore, or equals.</summary>
    private static string SanitizeId(string value)
    {
        var cleaned = InvalidKeyChars().Replace(value, "_");
        return cleaned.Length == 0 ? "doc" : cleaned;
    }

    [GeneratedRegex("[^A-Za-z0-9_\\-=]")]
    private static partial Regex InvalidKeyChars();
}
