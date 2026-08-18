namespace MinutesStudio.Core.Configuration;

/// <summary>
/// Connection settings for the Amazon S3 bucket that holds the source meeting-minute PDFs.
/// Bind from section "S3". Auth uses the AWS default credential chain.
/// </summary>
public sealed class S3Options
{
    public const string SectionName = "S3";

    /// <summary>Bucket that holds the source PDFs.</summary>
    public string BucketName { get; set; } = "meeting-minutes";

    /// <summary>AWS region of the bucket, e.g. "us-east-1".</summary>
    public string Region { get; set; } = "us-east-1";

    /// <summary>Optional key prefix ("folder") within the bucket. Empty means the bucket root.</summary>
    public string Prefix { get; set; } = string.Empty;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(BucketName);
}
