namespace PuntersScraper.App;

/// <summary>One fully independent scheduled Auto Scraper run: its own time, country scope, day(s),
/// and discipline(s). Nothing here is shared across slots, so e.g. a 06:00 slot can scrape
/// Australia-only Horses for Today while an 18:00 slot scrapes International Greyhounds+Harness
/// for Today+Tomorrow.</summary>
public sealed class AutoScrapeSlot
{
    /// <summary>24h "HH:mm", e.g. "06:00".</summary>
    public string Time { get; set; } = "06:00";

    /// <summary>"All", "Australia", or "International" — see MainViewModel.ToGroupFilter.</summary>
    public string CountryScope { get; set; } = "All";

    public bool IncludeToday { get; set; } = true;
    public bool IncludeTomorrow { get; set; } = true;
    public bool IncludeDayAfterTomorrow { get; set; } = true;

    public bool Horses { get; set; } = true;
    public bool Greyhounds { get; set; } = true;
    public bool Harness { get; set; } = true;
}
