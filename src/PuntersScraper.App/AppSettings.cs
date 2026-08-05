using System.IO;
using System.Text.Json;

namespace PuntersScraper.App;

/// <summary>Small persisted user-preference blob, stored outside the install folder so it
/// survives reinstalls/updates. Kept in its own "PuntersScraper" folder.</summary>
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
    public string S3BucketName { get; set; } = "punter-web-scraper";
    public string S3Folder { get; set; } = "pending";

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PuntersScraper", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings()
                : new AppSettings();
        }
        catch
        {
            // Corrupt or unreadable settings file — fall back to defaults rather than crash the app.
            return new AppSettings();
        }
    }

    public void Save()
    {
        var path = FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this));
    }
}
