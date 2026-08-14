using System.Text.Json;
using PuntersScraper.Shared.Scraping;

namespace PuntersScraper.Web.Services;

/// <summary>Persisted S3 upload settings, shared across every user of this deployment (there is
/// no per-user setting — this is a single shared tool, not a multi-tenant one). Stored under
/// App_Data next to the app rather than in appsettings.json, so it survives a redeploy and can
/// be changed from the UI without editing files on the server.
///
/// Login credentials are deliberately NOT here — they come from configuration
/// (appsettings/environment variables) only, so they're never edited or displayed through the
/// web UI. See <see cref="AdminCredentialsOptions"/>.</summary>
public sealed class WebAppSettings
{
    public bool UploadToS3 { get; set; }
    public string S3Endpoint { get; set; } = "https://s3.troyendata.com";

    // Deliberately no default access/secret key here (source is public) — set these from the
    // Export panel on the Home page on first run; they're then persisted to App_Data/settings.json
    // (gitignored, server-local) rather than ever living in source control.
    public string S3AccessKey { get; set; } = "";
    public string S3SecretKey { get; set; } = "";
    public string S3BucketName { get; set; } = "troyen-gen-prod";
    public string S3Folder { get; set; } = "pending";

    /// <summary>A path on this server's own filesystem to auto-write each meeting's JSON to as
    /// soon as it's scraped — the Web equivalent of the App's "Download folder", except there's
    /// no native folder-picker a hosted page can show for an arbitrary server path, so this is a
    /// typed path rather than a browsed one.</summary>
    public string ExportFolder { get; set; } = "";
    public bool AutoExportAfterScrape { get; set; }

    /// <summary>Id of the last developer notice (see DeveloperNoticeChecker) an admin explicitly
    /// dismissed. Shared across every user of this deployment, same as the rest of this class —
    /// unlike the App, where it's per-machine.</summary>
    public string LastSeenNoticeId { get; set; } = "";

    // --- Auto Scrape (see AutoScrapeHostedService) — off by default (opt-in). This deployment is
    // a single shared server, so this matters less than for the desktop App (which can be
    // installed on many PCs), but keeping the default consistent across both. ---
    public bool AutoScrapeEnabled { get; set; }

    /// <summary>Comma-separated 24h "HH:mm" times, e.g. "06:00,18:00" — AutoScrapeHostedService
    /// fires once per listed time each day, so multiple daily runs are just multiple entries here.</summary>
    public string AutoScrapeTimesOfDay { get; set; } = "06:00,18:00";
    public bool AutoScrapeIncludeToday { get; set; } = true;
    public bool AutoScrapeIncludeTomorrow { get; set; } = true;
    public bool AutoScrapeIncludeDayAfterTomorrow { get; set; } = true;
    public bool AutoScrapeHorses { get; set; } = true;
    public bool AutoScrapeGreyhounds { get; set; } = true;
    public bool AutoScrapeHarness { get; set; } = true;

    public DateTime? AutoScrapeLastRunUtc { get; set; }
    public string AutoScrapeLastRunSummary { get; set; } = "";

    /// <summary>Which browser to scrape with (see the Browser page). Defaults to Chrome, the only
    /// one these bot-detection workarounds have actually been tested against.</summary>
    public ScraperBrowserChoice ScraperBrowser { get; set; } = ScraperBrowserChoice.Chrome;

    private static string FilePath => Path.Combine(AppContext.BaseDirectory, "App_Data", "settings.json");

    private static WebAppSettings? _cached;

    /// <summary>Returns the one shared settings instance for this server process, loading it from
    /// disk only the first time. Home.razor/AutoScrape.razor/Bucket.razor each used to call this
    /// independently and hold their own private copy for the component's lifetime — since this is
    /// deployment-wide config (see class doc comment), that meant whichever page's Save() ran last
    /// could silently wipe out a field changed on a different page (e.g. typing S3 keys on the
    /// Bucket page, then toggling something on the Auto Scraper page, reverted the keys back to
    /// blank). Caching one shared instance means every page reads and writes the exact same
    /// object, so no save can ever clobber a field it doesn't know about.</summary>
    public static WebAppSettings Load()
    {
        if (_cached is not null) return _cached;

        try
        {
            _cached = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<WebAppSettings>(File.ReadAllText(FilePath)) ?? new WebAppSettings()
                : new WebAppSettings();
        }
        catch
        {
            _cached = new WebAppSettings();
        }

        return _cached;
    }

    public void Save()
    {
        var path = FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this));
    }
}
