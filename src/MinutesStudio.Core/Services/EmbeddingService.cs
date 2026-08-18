using System.Text.Json;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Microsoft.Extensions.Options;
using MinutesStudio.Core.Configuration;

namespace MinutesStudio.Core.Services;

public interface IEmbeddingService
{
    Task<ReadOnlyMemory<float>> EmbedAsync(string text, CancellationToken ct = default);
    Task<IReadOnlyList<ReadOnlyMemory<float>>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default);
}

/// <summary>
/// Generates embeddings via an Amazon Bedrock Titan Text Embeddings model (InvokeModel).
/// Titan embeds one string per request, so batching is done client-side to bound concurrency.
/// </summary>
public sealed class BedrockEmbeddingService : IEmbeddingService
{
    /// <summary>Max embedding requests in flight at once (Titan is single-input per call).</summary>
    private const int MaxConcurrency = 8;

    private readonly IAmazonBedrockRuntime _client;
    private readonly string _modelId;
    private readonly int _dimensions;

    public BedrockEmbeddingService(IAmazonBedrockRuntime client, IOptions<BedrockOptions> options)
    {
        _client = client;
        _modelId = options.Value.EmbeddingModelId;
        _dimensions = options.Value.EmbeddingDimensions;
    }

    public async Task<ReadOnlyMemory<float>> EmbedAsync(string text, CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(new
        {
            inputText = text,
            dimensions = _dimensions,
            normalize = true
        });

        var response = await Retry.OnTransientAsync(
            () => _client.InvokeModelAsync(new InvokeModelRequest
            {
                ModelId = _modelId,
                ContentType = "application/json",
                Accept = "application/json",
                Body = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(body))
            }, ct), ct: ct);

        using var doc = await JsonDocument.ParseAsync(response.Body, cancellationToken: ct);
        var embedding = doc.RootElement.GetProperty("embedding");
        var vector = new float[embedding.GetArrayLength()];
        var i = 0;
        foreach (var value in embedding.EnumerateArray())
            vector[i++] = value.GetSingle();

        return vector;
    }

    public async Task<IReadOnlyList<ReadOnlyMemory<float>>> EmbedBatchAsync(
        IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        var vectors = new ReadOnlyMemory<float>[texts.Count];
        using var throttle = new SemaphoreSlim(MaxConcurrency);

        var tasks = texts.Select(async (text, index) =>
        {
            await throttle.WaitAsync(ct);
            try
            {
                vectors[index] = await EmbedAsync(text, ct);
            }
            finally
            {
                throttle.Release();
            }
        });

        await Task.WhenAll(tasks);
        return vectors;
    }
}
