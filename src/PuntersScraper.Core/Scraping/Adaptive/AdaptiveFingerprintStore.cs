using System.IO;
using System.Text.Json;

namespace PuntersScraper.Core.Scraping.Adaptive;

/// <summary>
/// Where <see cref="AdaptiveLocator"/> persists element "fingerprints" between runs, keyed by
/// (site domain, identifier) -- e.g. ("www.punters.com.au", "date-tab:Tomorrow"). A fingerprint
/// can only ever be relocated FROM a prior successful run, so this needs to survive process
/// restarts (see docs/adaptive-scraping.md's "cold start" limitation).
///
/// Mirrors PuntersScraper.App's AppSettings on-disk convention -- one small JSON file under
/// %LOCALAPPDATA%\PuntersScraper -- rather than pulling in a database dependency, since this is
/// an infrequently-written key/value blob, not a dataset.
/// </summary>
public sealed class AdaptiveFingerprintStore
{
    public static readonly AdaptiveFingerprintStore Shared = new();

    private readonly string _filePath;
    private readonly object _lock = new();
    private Dictionary<string, string>? _cache;

    /// <param name="filePath">Overrides the default on-disk location; used by tests so they don't
    /// read/write the real per-machine store.</param>
    public AdaptiveFingerprintStore(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PuntersScraper", "adaptive_fingerprints.json");
    }

    public string? Retrieve(string domain, string identifier)
    {
        lock (_lock)
        {
            Load();
            return _cache!.TryGetValue(Key(domain, identifier), out var value) ? value : null;
        }
    }

    public void Save(string domain, string identifier, string fingerprintJson)
    {
        lock (_lock)
        {
            Load();
            _cache![Key(domain, identifier)] = fingerprintJson;
            Persist();
        }
    }

    private static string Key(string domain, string identifier) => $"{domain}|{identifier}";

    private void Load()
    {
        if (_cache is not null) return;

        try
        {
            _cache = File.Exists(_filePath)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_filePath)) ?? new()
                : new();
        }
        catch
        {
            // Corrupt or unreadable store -- fall back to an empty one rather than crash the
            // scrape; worst case, this just means adaptive relocation has nothing to fall back
            // on until the next successful primary-locator hit re-seeds it.
            _cache = new();
        }
    }

    private void Persist()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        File.WriteAllText(_filePath, JsonSerializer.Serialize(_cache));
    }
}
