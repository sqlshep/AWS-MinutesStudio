using Amazon.BedrockRuntime;
using Microsoft.Extensions.Options;
using MinutesStudio.Core.Configuration;
using MinutesStudio.Core.Models;
using MinutesStudio.Core.Services;
using MinutesStudio.Web.Components;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// --- Configuration binding (Bedrock, OpenSearch, S3, Rag) ---
builder.Services.Configure<BedrockOptions>(builder.Configuration.GetSection(BedrockOptions.SectionName));
builder.Services.Configure<OpenSearchOptions>(builder.Configuration.GetSection(OpenSearchOptions.SectionName));
builder.Services.Configure<S3Options>(builder.Configuration.GetSection(S3Options.SectionName));
builder.Services.Configure<RagOptions>(builder.Configuration.GetSection(RagOptions.SectionName));

// --- Amazon Bedrock runtime client (shared by chat + embeddings) ---
builder.Services.AddSingleton<IAmazonBedrockRuntime>(sp =>
{
    var options = sp.GetRequiredService<IOptions<BedrockOptions>>().Value;
    return BedrockClientFactory.Create(options);
});

// --- Core services ---
builder.Services.AddSingleton<IPdfTextExtractor, PdfTextExtractor>();
builder.Services.AddSingleton<ITextChunker, TextChunker>();
builder.Services.AddSingleton<IEmbeddingService, BedrockEmbeddingService>();
builder.Services.AddSingleton<ISearchService, OpenSearchService>();
builder.Services.AddSingleton<IGenerationService, BedrockGenerationService>();
builder.Services.AddSingleton<IBillLinker, BillLinker>();
builder.Services.AddSingleton<ICitationPreviewer, CitationPreviewer>();

// S3-backed document source. Registered as both the concrete uploader and the
// storage-agnostic source consumed by the ingestion pipeline.
builder.Services.AddSingleton<S3DocumentSource>();
builder.Services.AddSingleton<IBlobDocumentSource>(sp => sp.GetRequiredService<S3DocumentSource>());
builder.Services.AddSingleton<IDocumentSource>(sp => sp.GetRequiredService<S3DocumentSource>());

builder.Services.AddScoped<IIngestionService, IngestionService>();
builder.Services.AddScoped<IRagService, RagService>();
builder.Services.AddScoped<IConnectionChecker, ConnectionChecker>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// --- Lightweight JSON API (automation, scripts, and Phase 4 ingestion triggers) ---
app.MapPost("/api/ingest", async (bool? reset, IIngestionService ingestion, CancellationToken ct) =>
    Results.Ok(await ingestion.IngestAsync(reset ?? true, ct)));

app.MapPost("/api/blob/upload-samples", async (IBlobDocumentSource blob, IOptions<RagOptions> rag, CancellationToken ct) =>
    Results.Ok(await blob.UploadFromFolderAsync(rag.Value.SamplesPath, ct)));

app.MapGet("/api/blob/preview", async (string name, IBlobDocumentSource blob, CancellationToken ct) =>
{
    try
    {
        var stream = await blob.DownloadAsync(name, ct);
        // No download name => inline Content-Disposition, so the browser renders the PDF in-tab.
        return Results.File(stream, "application/pdf", enableRangeProcessing: true);
    }
    catch (Exception)
    {
        return Results.NotFound($"Blob '{name}' not found.");
    }
});

app.MapGet("/api/workproduct", async (string type, string? sourceFile, string? tone, string? length, string? references, IRagService rag, CancellationToken ct) =>
{
    if (!Enum.TryParse<WorkProductType>(type, ignoreCase: true, out var workProduct))
        return Results.BadRequest($"Unknown work product '{type}'. Valid: {string.Join(", ", Enum.GetNames<WorkProductType>())}.");

    var options = new GenerationOptions(
        Enum.TryParse<WorkProductLength>(length, ignoreCase: true, out var l) ? l : WorkProductLength.Standard,
        Enum.TryParse<WorkProductTone>(tone, ignoreCase: true, out var t) ? t : WorkProductTone.Default,
        Enum.TryParse<ReferenceMode>(references, ignoreCase: true, out var r) ? r : ReferenceMode.Included);

    return Results.Ok(await rag.GenerateWorkProductAsync(workProduct, sourceFile, options, progress: null, ct));
});

app.MapGet("/api/ask", async (string q, string? sourceFile, IRagService rag, CancellationToken ct) =>
    Results.Ok(await rag.AskAsync(q, sourceFile, ct)));

app.MapGet("/api/health", async (IConnectionChecker checker, CancellationToken ct) =>
{
    var report = await checker.CheckAsync(ct);
    return report.AllOk ? Results.Ok(report) : Results.Json(report, statusCode: 503);
});

app.Run();
