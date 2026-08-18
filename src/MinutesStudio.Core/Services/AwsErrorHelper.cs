using System.Net;
using Amazon.Runtime;

namespace MinutesStudio.Core.Services;

/// <summary>
/// Turns raw AWS SDK exceptions into short, actionable messages for the UI and logs.
/// Distinguishes auth/access problems, missing resources, throttling, and connectivity.
/// </summary>
public static class AwsErrorHelper
{
    public static string Describe(Exception ex)
    {
        if (ex is AmazonServiceException ase)
        {
            var status = (int)ase.StatusCode;
            return status switch
            {
                401 or 403 =>
                    "Access denied (401/403). The AWS credentials or IAM policy lack permission for this action "
                    + "(e.g. bedrock:InvokeModel, aoss:APIAccessAll, or s3:GetObject). Check the role/policy.",
                404 =>
                    "Resource not found (404). The model id, bucket, or index name may be wrong — verify the "
                    + "configuration and that the resource exists in this region.",
                429 =>
                    "Throttled (429). Bedrock/OpenSearch is rate-limiting requests — retry shortly or raise the quota.",
                >= 500 =>
                    $"AWS returned a server error ({status}). This is usually transient — retry shortly.",
                _ when ase.ErrorCode == "AccessDeniedException" =>
                    "Access denied. Ensure model access is enabled in Bedrock and the identity has the required IAM permissions.",
                _ => ase.Message
            };
        }

        return IsConnectivity(ex)
            ? "Could not reach the AWS service. Check the region, endpoint, and network connectivity."
            : ex.Message;
    }

    private static bool IsConnectivity(Exception ex) =>
        ex is HttpRequestException
        || ex.InnerException is HttpRequestException
        || ex is TaskCanceledException or TimeoutException
        || (ex as AmazonServiceException)?.StatusCode == HttpStatusCode.RequestTimeout;
}
