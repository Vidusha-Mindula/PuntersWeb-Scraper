using Amazon.S3;
using Amazon.S3.Model;

namespace PuntersScraper.App.Services;

/// <summary>One object in the configured bucket, as shown in the Bucket window.</summary>
public sealed record S3ObjectInfo(string Key, long Size, DateTime LastModifiedUtc);

/// <summary>Lists and deletes objects in the configured S3 bucket, for the "Manage Bucket..."
/// window. Same connection settings (endpoint/keys/bucket) as <see cref="S3JsonUploader"/>, which
/// only ever writes — this is the read/delete counterpart.</summary>
public static class S3BucketService
{
    public static async Task<List<S3ObjectInfo>> ListObjectsAsync(
        AppSettings settings, CancellationToken cancellationToken = default)
    {
        using var client = CreateClient(settings);

        var items = new List<S3ObjectInfo>();
        string? continuationToken = null;
        do
        {
            var response = await client.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = settings.S3BucketName,
                ContinuationToken = continuationToken
            }, cancellationToken);

            items.AddRange(response.S3Objects.Select(o =>
                new S3ObjectInfo(o.Key, o.Size, o.LastModified.ToUniversalTime())));

            continuationToken = response.IsTruncated == true ? response.NextContinuationToken : null;
        } while (continuationToken is not null);

        return items.OrderByDescending(i => i.LastModifiedUtc).ToList();
    }

    /// <summary>Deletes every given key in as few requests as possible (S3's batch-delete API
    /// takes up to 1000 keys per call). Returns the number of keys the request actually reported
    /// as deleted — a key that never existed is silently treated as "already gone" by S3 itself,
    /// so mismatches here reflect genuine per-object errors, surfaced via the out errors list.</summary>
    public static async Task<(int deleted, List<string> errors)> DeleteObjectsAsync(
        AppSettings settings, IReadOnlyList<string> keys, CancellationToken cancellationToken = default)
    {
        if (keys.Count == 0) return (0, new List<string>());

        using var client = CreateClient(settings);
        var deleted = 0;
        var errors = new List<string>();

        foreach (var batch in keys.Chunk(1000))
        {
            var request = new DeleteObjectsRequest
            {
                BucketName = settings.S3BucketName,
                Objects = batch.Select(k => new KeyVersion { Key = k }).ToList()
            };

            try
            {
                var response = await client.DeleteObjectsAsync(request, cancellationToken);
                deleted += response.DeletedObjects.Count;
                errors.AddRange(response.DeleteErrors.Select(e => $"{e.Key}: {e.Message}"));
            }
            catch (DeleteObjectsException ex)
            {
                deleted += ex.Response.DeletedObjects.Count;
                errors.AddRange(ex.Response.DeleteErrors.Select(e => $"{e.Key}: {e.Message}"));
            }
        }

        return (deleted, errors);
    }

    private static AmazonS3Client CreateClient(AppSettings settings)
    {
        var config = new AmazonS3Config
        {
            ServiceURL = settings.S3Endpoint,
            ForcePathStyle = true,
            AuthenticationRegion = "us-east-1",
        };

        return new AmazonS3Client(settings.S3AccessKey, settings.S3SecretKey, config);
    }
}
