using Amazon.S3;
using Amazon.S3.Model;

namespace PuntersScraper.Web.Services;

/// <summary>One object in the configured bucket, as shown on the Bucket page.</summary>
public sealed record S3ObjectInfo(string Key, long Size, DateTime LastModifiedUtc);

/// <summary>Lists, deletes, and uploads objects in the configured S3 bucket, for the "Bucket"
/// admin page. Same connection settings (endpoint/keys/bucket) as <see cref="S3JsonUploader"/>,
/// which only ever writes during a scrape — this is the read/delete/manual-upload counterpart,
/// ported to parity with <c>PuntersScraper.App/Services/S3BucketService.cs</c>.</summary>
public static class S3BucketService
{
    /// <summary>The only folder the Bucket page shows or uploads into — everything this app
    /// itself writes (scrape auto-uploads and manual uploads alike) goes here, so this is the
    /// one place worth looking; other folders that might exist in the bucket are unrelated/
    /// external data.</summary>
    public const string PendingFolder = "pending";

    public static async Task<List<S3ObjectInfo>> ListObjectsAsync(
        WebAppSettings settings, CancellationToken cancellationToken = default)
    {
        using var client = CreateClient(settings);

        var items = new List<S3ObjectInfo>();
        string? continuationToken = null;
        try
        {
            do
            {
                var response = await client.ListObjectsV2Async(new ListObjectsV2Request
                {
                    BucketName = settings.S3BucketName,
                    Prefix = $"{PendingFolder}/",
                    ContinuationToken = continuationToken
                }, cancellationToken);

                // A key ending in "/" (e.g. "pending/" itself) is an S3 "folder marker" - a zero-byte
                // placeholder object some tools create to represent the folder, not a real uploaded
                // file. It has no filename once the folder prefix is stripped, so it can't be shown
                // or deleted meaningfully - skip it entirely rather than list an empty, misleading row.
                items.AddRange(response.S3Objects
                    .Where(o => !o.Key.EndsWith('/'))
                    .Select(o => new S3ObjectInfo(o.Key, o.Size, o.LastModified.ToUniversalTime())));

                continuationToken = response.IsTruncated == true ? response.NextContinuationToken : null;
            } while (continuationToken is not null);
        }
        catch (Exception ex)
        {
            LogFailure("ListObjects", settings, ex);
            throw;
        }

        return items.OrderByDescending(i => i.LastModifiedUtc).ToList();
    }

    /// <summary>Deletes every given key in as few requests as possible (S3's batch-delete API
    /// takes up to 1000 keys per call). Returns the number of keys the request actually reported
    /// as deleted — a key that never existed is silently treated as "already gone" by S3 itself,
    /// so mismatches here reflect genuine per-object errors, surfaced via the out errors list.</summary>
    public static async Task<(int deleted, List<string> errors)> DeleteObjectsAsync(
        WebAppSettings settings, IReadOnlyList<string> keys, CancellationToken cancellationToken = default)
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
            catch (Exception ex)
            {
                LogFailure("DeleteObjects", settings, ex);
                throw;
            }
        }

        return (deleted, errors);
    }

    /// <summary>Uploads a browser-picked file into <see cref="PendingFolder"/> — always,
    /// regardless of whatever the Home page's Folder setting currently is — so manually uploaded
    /// files always land somewhere predictable instead of scattering across whatever folder
    /// happens to be configured. Overwrites an existing object with the same key, same as any
    /// other S3 PutObject. Stream-based (rather than the App's file-path-based overload) since
    /// Blazor hands you an <c>IBrowserFile</c> stream, not a local path.</summary>
    public static async Task UploadFileAsync(
        WebAppSettings settings, Stream fileStream, string fileName, CancellationToken cancellationToken = default)
    {
        using var client = CreateClient(settings);
        var key = $"{PendingFolder}/{fileName}";

        try
        {
            await client.PutObjectAsync(new PutObjectRequest
            {
                BucketName = settings.S3BucketName,
                Key = key,
                InputStream = fileStream,
                ContentType = GetContentType(fileName)
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            LogFailure("UploadFile", settings, ex);
            throw;
        }
    }

    private static string GetContentType(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".json" => "application/json",
        ".html" or ".htm" => "text/html",
        ".txt" => "text/plain",
        ".csv" => "text/csv",
        ".xml" => "application/xml",
        ".zip" => "application/zip",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        _ => "application/octet-stream"
    };

    private static AmazonS3Client CreateClient(WebAppSettings settings)
    {
        var config = new AmazonS3Config
        {
            ServiceURL = settings.S3Endpoint,
            ForcePathStyle = true,
            AuthenticationRegion = "us-east-1",
        };

        return new AmazonS3Client(settings.S3AccessKey, settings.S3SecretKey, config);
    }

    /// <summary>One-line summary for a status bar — <see cref="AmazonS3Exception"/> carries more
    /// than <see cref="Exception.Message"/> alone shows, so surface the fields that actually help
    /// diagnose a denial (ErrorCode/RequestId/AmazonId2/HttpStatus), same as the App's Bucket tab
    /// (<c>BucketViewModel.Describe</c>). Full detail goes to <see cref="LogFailure"/> instead.</summary>
    public static string DescribeS3Exception(Exception ex) => ex is AmazonS3Exception s3
        ? $"{s3.Message} (ErrorCode={s3.ErrorCode}, RequestId={s3.RequestId}, Id2={s3.AmazonId2}, HttpStatus={(int)s3.StatusCode}) - see s3-debug.log"
        : ex.Message;

    private static readonly string LogPath = Path.Combine(AppContext.BaseDirectory, "App_Data", "s3-debug.log");

    /// <summary>Appends full diagnostic detail for a failed S3 call to a log file next to
    /// App_Data/settings.json — the status bar (see <see cref="DescribeS3Exception"/>) only has
    /// room for a one-line summary, but AmazonS3Exception carries more than that: AmazonId2 (the
    /// x-amz-id-2 header S3-compatible backends often need to trace a denial), ErrorType (client
    /// vs. service-side), and ResponseBody (the raw error response, which can contain a more
    /// specific reason than the short Message alone). Never lets a logging failure mask or
    /// replace the real exception - this is called before rethrowing (or silently skips logging
    /// if the log write itself fails).</summary>
    public static void LogFailure(string operation, WebAppSettings settings, Exception ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);

            var lines = new List<string>
            {
                $"[{DateTime.UtcNow:O}] {operation} failed - endpoint={settings.S3Endpoint} bucket={settings.S3BucketName}"
            };

            if (ex is AmazonS3Exception s3)
            {
                lines.Add($"  ErrorCode={s3.ErrorCode} ErrorType={s3.ErrorType} HttpStatus={(int)s3.StatusCode} " +
                          $"RequestId={s3.RequestId} AmazonId2={s3.AmazonId2}");
                if (!string.IsNullOrWhiteSpace(s3.ResponseBody))
                    lines.Add($"  ResponseBody: {s3.ResponseBody}");
            }

            lines.Add($"  {ex}");
            lines.Add("");

            File.AppendAllLines(LogPath, lines);
        }
        catch
        {
            // Logging is best-effort - it must never mask the original failure or throw itself.
        }
    }
}
