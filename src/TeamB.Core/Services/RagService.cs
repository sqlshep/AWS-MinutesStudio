using System.Text;
using Microsoft.Extensions.Options;
using TeamB.Core.Configuration;
using TeamB.Core.Models;
using TeamB.Core.Prompts;

namespace TeamB.Core.Services;

public interface IRagService
{
    /// <summary>
    /// One-click work product: builds context from the FULL text of the selected document
    /// (or all documents when sourceFile is null) and runs the work-product prompt. Using full
    /// text — rather than a few retrieved snippets — keeps facts like vote tallies intact.
    /// </summary>
    Task<GenerationResult> GenerateWorkProductAsync(WorkProductType workProduct, string? sourceFile, IProgress<string>? progress = null, CancellationToken ct = default);

    /// <summary>
    /// Free-form document chat. When <paramref name="sourceFile"/> is supplied, answers from that
    /// document's full text (most accurate for pointed questions); otherwise hybrid-retrieves across
    /// all documents. Answers are cited.
    /// </summary>
    Task<ChatAnswer> AskAsync(string question, string? sourceFile = null, CancellationToken ct = default);
}

public sealed class RagService : IRagService
{
    private readonly IEmbeddingService _embeddings;
    private readonly ISearchService _search;
    private readonly IGenerationService _generation;
    private readonly RagOptions _options;

    public RagService(
        IEmbeddingService embeddings,
        ISearchService search,
        IGenerationService generation,
        IOptions<RagOptions> options)
    {
        _embeddings = embeddings;
        _search = search;
        _generation = generation;
        _options = options.Value;
    }

    public async Task<GenerationResult> GenerateWorkProductAsync(
        WorkProductType workProduct, string? sourceFile, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var template = PromptLibrary.Get(workProduct);

        // Single meeting: generate directly from its full text.
        if (!string.IsNullOrWhiteSpace(sourceFile))
        {
            var chunks = await _search.GetChunksAsync(sourceFile, ct);
            if (chunks.Count == 0) return NoDocumentsResult(workProduct);

            progress?.Report($"Generating {template.DisplayName} from {chunks[0].Title}\u2026");
            var content = await GenerateFromChunksAsync(template, chunks, ct);
            return new GenerationResult
            {
                Content = content,
                WorkProduct = workProduct,
                Sources = DistinctDocuments(chunks)
            };
        }

        // All meetings: map-reduce so the corpus scales beyond a single prompt.
        var documents = await _search.ListDocumentsAsync(ct);
        if (documents.Count == 0) return NoDocumentsResult(workProduct);

        if (documents.Count == 1)
        {
            var chunks = await _search.GetChunksAsync(documents[0].SourceFile, ct);
            progress?.Report($"Generating {template.DisplayName} from {chunks[0].Title}\u2026");
            var content = await GenerateFromChunksAsync(template, chunks, ct);
            return new GenerationResult
            {
                Content = content,
                WorkProduct = workProduct,
                Sources = DistinctDocuments(chunks)
            };
        }

        // MAP: draft the work product for each meeting from its full text (in parallel).
        progress?.Report($"Analyzing {documents.Count} meetings\u2026");
        var completed = 0;
        var mapTasks = documents.Select(async doc =>
        {
            var chunks = await _search.GetChunksAsync(doc.SourceFile, ct);
            var draft = await GenerateFromChunksAsync(template, chunks, ct);
            var done = Interlocked.Increment(ref completed);
            progress?.Report($"Summarized {done} of {documents.Count} meetings\u2026");
            return (doc.Title, draft);
        });
        var partials = (await Task.WhenAll(mapTasks)).ToList();

        // REDUCE: consolidate the per-meeting drafts into one final work product.
        progress?.Report($"Combining into final {template.DisplayName}\u2026");
        var reducePrompt = PromptLibrary.BuildReduceUserPrompt(template, partials);
        var finalContent = await _generation.CompleteAsync(template.SystemPrompt, reducePrompt, ct);

        return new GenerationResult
        {
            Content = finalContent,
            WorkProduct = workProduct,
            Sources = documents.Select(DocumentToSource).ToList()
        };
    }

    private Task<string> GenerateFromChunksAsync(
        PromptTemplate template, IReadOnlyList<RetrievedChunk> chunks, CancellationToken ct)
    {
        var context = BuildContext(chunks);
        var request = $"Produce a {template.DisplayName} based on the following meeting: {chunks[0].Title}.";
        return _generation.CompleteAsync(template, request, context, ct);
    }

    private static GenerationResult NoDocumentsResult(WorkProductType workProduct) => new()
    {
        Content = string.Empty,
        WorkProduct = workProduct,
        Sources = Array.Empty<RetrievedChunk>(),
        Warning = "No indexed documents found. Ingest the meeting minutes first (Documents page)."
    };

    private static RetrievedChunk DocumentToSource(DocumentInfo doc) => new()
    {
        Id = doc.SourceFile,
        Content = string.Empty,
        SourceFile = doc.SourceFile,
        Title = doc.Title,
        MeetingDate = doc.MeetingDate,
        ChunkIndex = 0,
        Score = 0d
    };

    public async Task<ChatAnswer> AskAsync(string question, string? sourceFile = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(question))
        {
            return new ChatAnswer
            {
                Content = string.Empty,
                Sources = Array.Empty<RetrievedChunk>(),
                Warning = "Please enter a question."
            };
        }

        IReadOnlyList<RetrievedChunk> passages;
        IReadOnlyList<RetrievedChunk> sources;

        if (!string.IsNullOrWhiteSpace(sourceFile))
        {
            // Scoped to one meeting: use its full text so pointed questions get every detail.
            passages = await _search.GetChunksAsync(sourceFile, ct);
            sources = DistinctDocuments(passages);
        }
        else
        {
            var queryVector = await _embeddings.EmbedAsync(question, ct);
            passages = await _search.SearchAsync(question, queryVector, _options.TopK, ct);
            sources = passages;
        }

        if (passages.Count == 0)
        {
            return new ChatAnswer
            {
                Content = string.Empty,
                Sources = Array.Empty<RetrievedChunk>(),
                Warning = "No relevant passages were found. Try rephrasing, or ingest documents first."
            };
        }

        var context = BuildContext(passages);
        var content = await _generation.CompleteAsync(
            PromptLibrary.DocumentChatSystemPrompt,
            PromptLibrary.BuildChatUserPrompt(question, context),
            ct);

        return new ChatAnswer { Content = content, Sources = sources };
    }

    /// <summary>Formats chunks into a labeled, citeable context block.</summary>
    private static string BuildContext(IReadOnlyList<RetrievedChunk> chunks)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < chunks.Count; i++)
        {
            var c = chunks[i];
            sb.AppendLine($"[{i + 1}] {c.Title}  (file: {c.SourceFile})");
            sb.AppendLine(c.Content);
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>Collapses many chunks down to the distinct documents they came from (for source display).</summary>
    private static IReadOnlyList<RetrievedChunk> DistinctDocuments(IReadOnlyList<RetrievedChunk> chunks) =>
        chunks
            .GroupBy(c => c.SourceFile)
            .Select(g => g.First())
            .ToList();
}
