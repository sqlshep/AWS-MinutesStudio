using Azure.AI.OpenAI;
using Microsoft.Extensions.Options;
using TeamB.Core.Configuration;
using TeamB.Core.Models;
using TeamB.Core.Services;
using TeamB.Web.Components;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// --- Configuration binding (AzureOpenAI, AzureSearch, AzureBlob, Rag) ---
builder.Services.Configure<AzureOpenAIOptions>(builder.Configuration.GetSection(AzureOpenAIOptions.SectionName));
builder.Services.Configure<AzureSearchOptions>(builder.Configuration.GetSection(AzureSearchOptions.SectionName));
builder.Services.Configure<AzureBlobOptions>(builder.Configuration.GetSection(AzureBlobOptions.SectionName));
builder.Services.Configure<RagOptions>(builder.Configuration.GetSection(RagOptions.SectionName));

// --- Azure OpenAI client (shared by chat + embeddings) ---
builder.Services.AddSingleton(sp =>
{
    var options = sp.GetRequiredService<IOptions<AzureOpenAIOptions>>().Value;
    return AzureOpenAIClientFactory.Create(options);
});

// --- Core services ---
builder.Services.AddSingleton<IPdfTextExtractor, PdfTextExtractor>();
builder.Services.AddSingleton<ITextChunker, TextChunker>();
builder.Services.AddSingleton<IEmbeddingService, AzureOpenAIEmbeddingService>();
builder.Services.AddSingleton<ISearchService, AzureSearchService>();
builder.Services.AddSingleton<IGenerationService, AzureOpenAIGenerationService>();
builder.Services.AddSingleton<IBillLinker, BillLinker>();
builder.Services.AddSingleton<ICitationPreviewer, CitationPreviewer>();

// Blob-backed document source (Phase 4). Registered as both the concrete uploader and the
// storage-agnostic source consumed by the ingestion pipeline.
builder.Services.AddSingleton<BlobDocumentSource>();
builder.Services.AddSingleton<IBlobDocumentSource>(sp => sp.GetRequiredService<BlobDocumentSource>());
builder.Services.AddSingleton<IDocumentSource>(sp => sp.GetRequiredService<BlobDocumentSource>());

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

app.MapGet("/api/workproduct", async (string type, string? sourceFile, string? tone, string? length, IRagService rag, CancellationToken ct) =>
{
    if (!Enum.TryParse<WorkProductType>(type, ignoreCase: true, out var workProduct))
        return Results.BadRequest($"Unknown work product '{type}'. Valid: {string.Join(", ", Enum.GetNames<WorkProductType>())}.");

    var options = new GenerationOptions(
        Enum.TryParse<WorkProductLength>(length, ignoreCase: true, out var l) ? l : WorkProductLength.Standard,
        Enum.TryParse<WorkProductTone>(tone, ignoreCase: true, out var t) ? t : WorkProductTone.Default);

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
