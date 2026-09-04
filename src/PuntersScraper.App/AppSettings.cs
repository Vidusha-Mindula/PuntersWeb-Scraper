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

    /// <summary>Comma-separated 24h "HH:mm" times, e.g. "06:00,18:00" — fires once per listed time
    /// each day, so multiple daily runs are just multiple entries here.</summary>
    public string AutoScrapeTimesOfDay { get; set; } = "06:00,18:00";
    public bool AutoScrapeIncludeToday { get; set; } = true;
    public bool AutoScrapeIncludeTomorrow { get; set; } = true;
    public bool AutoScrapeIncludeDayAfterTomorrow { get; set; } = true;
    public bool AutoScrapeHorses { get; set; } = true;
    public bool AutoScrapeGreyhounds { get; set; } = true;
    public bool AutoScrapeHarness { get; set; } = true;

    public DateTime? AutoScrapeLastRunUtc { get; set; }
    public string AutoScrapeLastRunSummary { get; set; } = "";

    /// <summary>Which browser to scrape with — "Chrome", "Firefox", or "Edge" (see the Browser
    /// tab). Stored as a string rather than the ScraperBrowserChoice enum directly for a
    /// human-readable settings.json. Defaults to Chrome, the only one these bot-detection
    /// workarounds have actually been tested against.</summary>
    public string ScraperBrowser { get; set; } = "Chrome";

    // --- RabbitMQ "meeting.scraped" event publishing (see RabbitMqMeetingEventPublisher). Off by
    // default (opt-in) so existing installs behave exactly as before until real connection details
    // are entered. Flat properties, matching this class's existing style (no nested config
    // sections). Like the S3 credentials above, these are reset to these baked-in defaults on every
    // installer update — re-enter host/credentials after an update if you rely on this. ---

    /// <summary>Master switch: when false, no events are published and the scrape path is
    /// untouched. Turn on deliberately on the machine that should notify downstream systems.</summary>
    public bool RabbitMqEnabled { get; set; } = true;

    public string RabbitMqHostName { get; set; } = "localhost";
    public int RabbitMqPort { get; set; } = 5672;
    public string RabbitMqVirtualHost { get; set; } = "/";

    // guest/guest only works over a localhost connection — it's RabbitMQ's well-known local
    // default, not a real credential, so shipping it as the default is safe and lets local testing
    // work out of the box. Point at real credentials for anything beyond a local broker.
    public string RabbitMqUserName { get; set; } = "guest";
    public string RabbitMqPassword { get; set; } = "guest";

    /// <summary>Shared topic exchange every scraper publishes into. Configurable so ops can point
    /// this at whatever the priority-control project actually expects without a code change.</summary>
    public string RabbitMqExchangeName { get; set; } = "td.scrapers.events";

    /// <summary>Routing-key template; "{source}" and "{disciplineCode}" (lowercased) are filled per
    /// event, e.g. "punters.meeting.scraped.t".</summary>
    public string RabbitMqRoutingKeyTemplate { get; set; } = "{source}.meeting.scraped.{disciplineCode}";

    /// <summary>Lowercase scraper token used in the routing key (distinct from the payload's
    /// "PuntersScraper" source, which names the project).</summary>
    public string RabbitMqSource { get; set; } = "punters";

    /// <summary>Priority (0–10 scale) stamped on every message. This app never sets any other
    /// value — re-prioritization is the priority-control project's job.</summary>
    public int RabbitMqDefaultPriority { get; set; } = 5;

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
