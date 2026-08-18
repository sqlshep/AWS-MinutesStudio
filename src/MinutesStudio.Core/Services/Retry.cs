using System.Net;
using Amazon.Runtime;

namespace MinutesStudio.Core.Services;

/// <summary>
/// Small retry helper with exponential backoff. Covers the usual transient statuses (408/429/5xx),
/// network faults, and AWS throttling. The AWS SDK also retries internally, so this is a second,
/// operation-level safety net for longer-lived flows (batch embedding, map-reduce generation).
/// </summary>
public static class Retry
{
    public static async Task<T> OnTransientAsync<T>(
        Func<Task<T>> action,
        int maxAttempts = 5,
        int baseDelayMs = 250,
        CancellationToken ct = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await action();
            }
            catch (Exception ex) when (attempt < maxAttempts && IsTransient(ex))
            {
                var delayMs = baseDelayMs * (int)Math.Pow(2, attempt - 1);
                await Task.Delay(delayMs, ct);
            }
        }
    }

    private static bool IsTransient(Exception ex) => ex switch
    {
        AmazonServiceException ase =>
            ase.Retryable is not null
            || IsTransientStatus((int)ase.StatusCode)
            || ase.ErrorCode is "ThrottlingException" or "TooManyRequestsException"
                or "ServiceUnavailableException" or "ModelNotReadyException"
                or "InternalServerException" or "RequestTimeout",
        HttpRequestException => true,
        TaskCanceledException => true,
        TimeoutException => true,
        _ => false
    };

    private static bool IsTransientStatus(int status) =>
        status is 408 or 429 || status >= 500;

    private static bool IsTransientStatus(HttpStatusCode status) => IsTransientStatus((int)status);
}
