using System.IO;
using System.Text.Json;

namespace PuntersScraper.App;

/// <summary>Small persisted user-preference blob, stored outside the install folder in its own
/// "PuntersScraper" folder. NOT preserved across installs/updates on purpose — the installer
/// (see installer/PuntersScraper.iss's WriteDefaultSettings) overwrites this file with baked-in
/// defaults on every version, to stop per-machine config drift from silently surviving updates.</summary>
public sealed class AppSettings
{
    public string DownloadFolder { get; set; } = "";
    public bool AutoExportAfterScrape { get; set; }

    public bool UploadToS3 { get; set; }
    public string S3Endpoint { get; set; } = "https://s3.troyendata.com";

    // Deliberately no default access/secret key here (source is public) — set these via the
    // app's own UI on first run, or by hand-editing settings.json at the path below; either way
    // they're saved locally and never checked into source control.
    public string S3AccessKey { get; set; } = "";
    public string S3SecretKey { get; set; } = "";
    public string S3BucketName { get; set; } = "troyen-gen-prod";
    public string S3Folder { get; set; } = "pending";

    /// <summary>Id of the last developer notice (see DeveloperNoticeChecker) the user explicitly
    /// dismissed. A notice with a different Id is treated as new and shown again, even if an
    /// earlier one was already read.</summary>
    public string LastSeenNoticeId { get; set; } = "";

    // --- Auto Scrape (see MainViewModel's DispatcherTimer) — only fires while this app is open.
    // Off by default (opt-in) — if this app is installed on more than one PC, having it on by
    // default everywhere means every PC scrapes/uploads at the same scheduled times, causing
    // duplicate work. Turn it on deliberately on only one machine via the "Enabled" checkbox. ---
    public bool AutoScrapeEnabled { get; set; }

    /// <summary>One entry per scheduled time — each with independent Australia and International
    /// day(s)/discipline(s) selections (either can be disabled), so e.g. a single 13:50 slot can
    /// scrape Australia-only Harness for Today while ALSO scraping International Harness for
    /// Today, in the same run. Fires once per slot whose time matches, each day (see
    /// MainViewModel.AutoScrapeTickAsync).</summary>
    public List<AutoScrapeSlot> AutoScrapeSlots { get; set; } = new()
    {
        new AutoScrapeSlot { Time = "06:00" },
        new AutoScrapeSlot { Time = "18:00" },
    };

    public DateTime? AutoScrapeLastRunUtc { get; set; }
    public string AutoScrapeLastRunSummary { get; set; } = "";

    /// <summary>Which browser to scrape with — "Chrome", "Firefox", or "Edge" (see the Browser
    /// tab). Stored as a string rather than the ScraperBrowserChoice enum directly for a
    /// human-readable settings.json. Defaults to Chrome, the only one these bot-detection
    /// workarounds have actually been tested against.</summary>
    public string ScraperBrowser { get; set; } = "Chrome";

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PuntersScraper", "settings.json");

    private static AppSettings? _cached;

    /// <summary>Returns the one shared settings instance for this process, loading it from disk
    /// only the first time. MainViewModel and BucketViewModel each used to call this independently
    /// and hold their own private copy — since nothing else writes this file while the app is
    /// running (single-user desktop app), that just meant whichever one called Save() last
    /// silently wiped out the other's in-memory changes (e.g. typing S3 keys on the Bucket tab,
    /// then toggling anything on the Scraper tab, reverted the keys back to blank). Caching one
    /// shared instance means every viewmodel reads and writes the exact same object, so no save
    /// can ever clobber a field it doesn't know about.</summary>
    public static AppSettings Load()
    {
        if (_cached is not null) return _cached;

        try
        {
            _cached = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings()
                : new AppSettings();
        }
        catch
        {
            // Corrupt or unreadable settings file — fall back to defaults rather than crash the app.
            _cached = new AppSettings();
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
