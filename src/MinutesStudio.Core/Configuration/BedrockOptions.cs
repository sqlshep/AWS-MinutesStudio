namespace MinutesStudio.Core.Configuration;

/// <summary>
/// Connection settings for Amazon Bedrock (chat + embeddings). Bind from section "Bedrock".
/// Credentials are never configured here — the AWS default credential chain (environment
/// variables, shared profile, or an IAM role) supplies them, which is the preferred path.
/// </summary>
public sealed class BedrockOptions
{
    public const string SectionName = "Bedrock";

    /// <summary>AWS region the Bedrock models are invoked in, e.g. "us-east-1".</summary>
    public string Region { get; set; } = "us-east-1";

    /// <summary>
    /// Model (or inference-profile) id for chat, e.g. "us.amazon.nova-pro-v1:0".
    /// Newer models often require the cross-region inference-profile form ("us.*").
    /// </summary>
    public string ChatModelId { get; set; } = "us.amazon.nova-pro-v1:0";

    /// <summary>Embedding model id, e.g. "amazon.titan-embed-text-v2:0".</summary>
    public string EmbeddingModelId { get; set; } = "amazon.titan-embed-text-v2:0";

    /// <summary>Vector dimensions requested from the embedding model. Titan v2 supports 256, 512, or 1024.</summary>
    public int EmbeddingDimensions { get; set; } = 1024;

    /// <summary>Upper bound on tokens the chat model may generate (Nova caps at 5000).</summary>
    public int MaxOutputTokens { get; set; } = 4096;
}
