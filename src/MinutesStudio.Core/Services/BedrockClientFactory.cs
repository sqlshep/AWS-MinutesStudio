using Amazon;
using Amazon.BedrockRuntime;
using MinutesStudio.Core.Configuration;

namespace MinutesStudio.Core.Services;

/// <summary>
/// Builds the shared <see cref="AmazonBedrockRuntimeClient"/> used for both chat (Converse) and
/// embeddings (InvokeModel). Credentials come from the AWS default credential chain (environment
/// variables, shared profile, or an IAM role) — the recommended production path.
/// </summary>
public static class BedrockClientFactory
{
    public static AmazonBedrockRuntimeClient Create(BedrockOptions options)
    {
        var region = RegionEndpoint.GetBySystemName(
            string.IsNullOrWhiteSpace(options.Region) ? "us-east-1" : options.Region);
        return new AmazonBedrockRuntimeClient(region);
    }
}
