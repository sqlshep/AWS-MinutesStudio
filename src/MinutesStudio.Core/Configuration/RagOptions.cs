namespace MinutesStudio.Core.Configuration;

/// <summary>
/// Tunable retrieval-augmented-generation parameters. Bind from section "Rag".
/// </summary>
public sealed class RagOptions
{
    public const string SectionName = "Rag";

    /// <summary>Target chunk size in characters. ~3500 chars ≈ 800-900 tokens.</summary>
    public int ChunkSizeChars { get; set; } = 3500;

    /// <summary>Character overlap between adjacent chunks to preserve context across boundaries.</summary>
    public int ChunkOverlapChars { get; set; } = 400;

    /// <summary>Number of chunks to retrieve for each query.</summary>
    public int TopK { get; set; } = 6;

    /// <summary>Folder containing source PDFs for local ingestion (Phase 1). Blob storage replaces this later.</summary>
    public string SamplesPath { get; set; } = "samples";
}
