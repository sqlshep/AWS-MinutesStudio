using System.Text.Json.Serialization;

namespace MinutesStudio.Core.Services;

/// <summary>
/// Wire model for a document stored in Azure AI Search. Property names are pinned to the
/// (camelCase) index field names via JsonPropertyName so serialization is unambiguous.
/// </summary>
public sealed class SearchIndexDocument
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("content")] public string Content { get; set; } = string.Empty;
    [JsonPropertyName("sourceFile")] public string SourceFile { get; set; } = string.Empty;
    [JsonPropertyName("title")] public string Title { get; set; } = string.Empty;
    [JsonPropertyName("meetingDate")] public string? MeetingDate { get; set; }
    [JsonPropertyName("chunkIndex")] public int ChunkIndex { get; set; }
    [JsonPropertyName("pageStart")] public int PageStart { get; set; }
    [JsonPropertyName("pageEnd")] public int PageEnd { get; set; }
    [JsonPropertyName("contentVector")] public IReadOnlyList<float> ContentVector { get; set; } = Array.Empty<float>();
}
