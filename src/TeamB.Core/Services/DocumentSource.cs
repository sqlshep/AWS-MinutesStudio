namespace TeamB.Core.Services;

/// <summary>A source PDF that can be opened as a stream on demand (from a folder, a blob, etc.).</summary>
public sealed record SourceDocumentRef(string FileName, Func<CancellationToken, Task<Stream>> OpenAsync);

/// <summary>
/// Abstracts where source documents come from so the ingestion pipeline is storage-agnostic.
/// Phase 1 used a local folder; Phase 4 uses Blob Storage — both behind this interface.
/// </summary>
public interface IDocumentSource
{
    /// <summary>Human-friendly description of the source (shown in the UI).</summary>
    string Description { get; }

    /// <summary>Lists the available PDF documents.</summary>
    Task<IReadOnlyList<SourceDocumentRef>> ListAsync(CancellationToken ct = default);
}
