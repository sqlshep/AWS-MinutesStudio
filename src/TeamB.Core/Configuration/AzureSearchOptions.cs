namespace TeamB.Core.Configuration;

/// <summary>
/// Connection settings for the Azure AI Search resource used as the vector store.
/// Bind from configuration section "AzureSearch".
/// </summary>
public sealed class AzureSearchOptions
{
    public const string SectionName = "AzureSearch";

    /// <summary>Search endpoint, e.g. https://my-search.search.windows.net </summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>
    /// Admin API key (needed to create the index and upload documents). If empty,
    /// DefaultAzureCredential is used (requires the "Search Index Data Contributor"
    /// and "Search Service Contributor" roles on the identity).
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>Name of the index that stores meeting-minute chunks.</summary>
    public string IndexName { get; set; } = "teamb-minutes";
}
