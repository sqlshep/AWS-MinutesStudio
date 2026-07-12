using Azure.AI.OpenAI;
using Microsoft.Extensions.Options;
using OpenAI.Embeddings;
using TeamB.Core.Configuration;

namespace TeamB.Core.Services;

public interface IEmbeddingService
{
    Task<ReadOnlyMemory<float>> EmbedAsync(string text, CancellationToken ct = default);
    Task<IReadOnlyList<ReadOnlyMemory<float>>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default);
}

/// <summary>Generates embeddings via an Azure OpenAI (Foundry) deployment, batching to reduce round-trips.</summary>
public sealed class AzureOpenAIEmbeddingService : IEmbeddingService
{
    private const int BatchSize = 16;
    private readonly EmbeddingClient _client;

    public AzureOpenAIEmbeddingService(AzureOpenAIClient client, IOptions<AzureOpenAIOptions> options)
    {
        _client = client.GetEmbeddingClient(options.Value.EmbeddingDeployment);
    }

    public async Task<ReadOnlyMemory<float>> EmbedAsync(string text, CancellationToken ct = default)
    {
        var result = await Retry.OnTransientAsync(
            () => _client.GenerateEmbeddingAsync(text, cancellationToken: ct), ct: ct);
        return result.Value.ToFloats();
    }

    public async Task<IReadOnlyList<ReadOnlyMemory<float>>> EmbedBatchAsync(
        IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        var vectors = new List<ReadOnlyMemory<float>>(texts.Count);
        for (var i = 0; i < texts.Count; i += BatchSize)
        {
            var batch = texts.Skip(i).Take(BatchSize).ToList();
            var response = await Retry.OnTransientAsync(
                () => _client.GenerateEmbeddingsAsync(batch, cancellationToken: ct), ct: ct);
            vectors.AddRange(response.Value.Select(e => e.ToFloats()));
        }

        return vectors;
    }
}
