namespace MinutesStudio.Core.Configuration;

/// <summary>
/// Connection settings for the Amazon OpenSearch vector store. Works with both a managed
/// OpenSearch Service domain (service code "es") and an OpenSearch Serverless collection
/// (service code "aoss"). Bind from section "OpenSearch". Auth is SigV4 via the AWS default
/// credential chain — no keys are stored here.
/// </summary>
public sealed class OpenSearchOptions
{
    public const string SectionName = "OpenSearch";

    /// <summary>
    /// Data-plane endpoint. For a managed domain, e.g. https://search-xxx.us-east-1.es.amazonaws.com;
    /// for Serverless, e.g. https://abc123.us-east-1.aoss.amazonaws.com
    /// </summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>AWS region of the domain/collection, e.g. "us-east-1".</summary>
    public string Region { get; set; } = "us-east-1";

    /// <summary>Index name prefix; each concrete index is "{prefix}-{yyyyMMdd-HHmmss}".</summary>
    public string IndexName { get; set; } = "minutesstudio-minutes";

    /// <summary>
    /// SigV4 service code: "es" (managed domain) or "aoss" (Serverless). When left empty it is
    /// auto-detected from the endpoint host (".aoss." → aoss, otherwise "es").
    /// </summary>
    public string? ServiceCode { get; set; }
}
