using Azure;
using Azure.AI.OpenAI;
using Azure.Identity;
using MinutesStudio.Core.Configuration;

namespace MinutesStudio.Core.Services;

/// <summary>
/// Builds the shared <see cref="AzureOpenAIClient"/> used for both chat and embeddings.
/// Prefers an API key when supplied (simple local dev); otherwise falls back to
/// DefaultAzureCredential (managed identity) which is the recommended production path.
/// </summary>
public static class AzureOpenAIClientFactory
{
    public static AzureOpenAIClient Create(AzureOpenAIOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Endpoint))
            throw new InvalidOperationException(
                "AzureOpenAI:Endpoint is not configured. Set it via user-secrets or app settings.");

        var endpoint = new Uri(options.Endpoint);

        // Pin the data-plane API version. This Foundry resource only serves the text-embedding-3-large
        // deployment on 2024-10-21+, and the SDK's default can fall back to an older version that 404s.
        var clientOptions = new AzureOpenAIClientOptions(AzureOpenAIClientOptions.ServiceVersion.V2024_10_21);

        return string.IsNullOrWhiteSpace(options.ApiKey)
            ? new AzureOpenAIClient(endpoint, new DefaultAzureCredential(), clientOptions)
            : new AzureOpenAIClient(endpoint, new AzureKeyCredential(options.ApiKey), clientOptions);
    }
}
