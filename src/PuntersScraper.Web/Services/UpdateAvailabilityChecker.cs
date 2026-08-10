using System.Net.Http.Headers;
using System.Text.Json;

namespace PuntersScraper.Web.Services;

/// <summary>A newer Web build found on GitHub Releases.</summary>
public sealed record UpdateAvailability(Version Version, string ReleaseUrl);

/// <summary>
/// Checks GitHub Releases for a Web build newer than the one currently running — the same idea as
/// the desktop App's UpdateChecker, but there's no equivalent of "download the installer and
/// relaunch": a Blazor Server process running under a Scheduled Task has no way to replace itself.
/// This only detects and reports; applying the update is still <c>deploy/update.ps1</c>, run by an
/// admin on the server (see deploy/README.md).
///
/// Uses the releases LIST endpoint rather than "/releases/latest", because Web releases are
/// deliberately tagged "web-vX.Y.Z" and marked prerelease (so they don't hijack the desktop App's
/// own "/releases/latest" update check) — the list endpoint is the only way to see them.
/// </summary>
public static class UpdateAvailabilityChecker
{
    private const string RepoOwner = "Vidusha-Mindula";
    private const string RepoName = "PuntersWeb-Scraper";
    private const string TagPrefix = "web-v";

    private static Version? CurrentVersion =>
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;

    /// <summary>Returns the newest available Web release, or null if this is already the latest
    /// version or the check failed for any reason (offline, rate-limited, no releases yet) — a
    /// failed background check should never bother an admin with an error.</summary>
    public static async Task<UpdateAvailability?> CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PuntersScraper.Web", "1.0"));
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            var url = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases?per_page=10";
            using var response = await http.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode) return null;

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

            var current = CurrentVersion;
            Version? bestVersion = null;
            string? bestUrl = null;

            foreach (var release in doc.RootElement.EnumerateArray())
            {
                var tag = release.TryGetProperty("tag_name", out var tagProp) ? tagProp.GetString() : null;
                if (tag is null || !tag.StartsWith(TagPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                if (!Version.TryParse(tag[TagPrefix.Length..], out var version)) continue;
                if (current is not null && version <= current) continue;
                if (bestVersion is not null && version <= bestVersion) continue;

                bestVersion = version;
                bestUrl = release.TryGetProperty("html_url", out var htmlProp) ? htmlProp.GetString() : null;
            }

            return bestVersion is null || bestUrl is null ? null : new UpdateAvailability(bestVersion, bestUrl);
        }
        catch
        {
            return null;
        }
    }
}
