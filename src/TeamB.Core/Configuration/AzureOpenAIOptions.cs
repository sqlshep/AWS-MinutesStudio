namespace TeamB.Core.Configuration;

/// <summary>
/// Connection settings for the Azure OpenAI (Azure AI Foundry) resource.
/// Bind from configuration section "AzureOpenAI". Secrets (ApiKey) should come
/// from user-secrets locally or Key Vault / managed identity in Azure.
/// </summary>
public sealed class AzureOpenAIOptions
{
    public const string SectionName = "AzureOpenAI";

    /// <summary>Foundry/Azure OpenAI endpoint, e.g. https://my-foundry.openai.azure.com/ </summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>
    /// API key. If left empty, the app falls back to DefaultAzureCredential
    /// (managed identity / az login) which is the preferred path in Azure.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>Deployment name for the chat model (e.g. "gpt-5-mini").</summary>
    public string ChatDeployment { get; set; } = "gpt-5-mini";

    /// <summary>Deployment name for the embeddings model (e.g. "text-embedding-3-large").</summary>
    public string EmbeddingDeployment { get; set; } = "text-embedding-3-large";

    /// <summary>Vector dimensions produced by the embedding model. 3072 for text-embedding-3-large.</summary>
    public int EmbeddingDimensions { get; set; } = 3072;
}
