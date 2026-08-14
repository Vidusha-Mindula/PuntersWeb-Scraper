namespace PuntersScraper.Shared.Scraping;

/// <summary>
/// Which browser engine <see cref="PuntersScraper.Core.Scraping.IPuntersScraperService"/> drives.
/// Chrome and Edge are both Chromium under the hood (Edge via Playwright's "msedge" channel,
/// pointing at the system-installed Edge rather than a separate download), so the
/// bot-detection workarounds in PuntersScraperService (disabling AutomationControlled, the
/// off-screen-window backgrounding fixes) apply to both. Firefox is a genuinely different
/// rendering engine — those Chromium-specific workarounds don't apply to it, so it may not get
/// past Punters' bot-detection as reliably; try Chrome or Edge first if Firefox gets blocked.
/// </summary>
public enum ScraperBrowserChoice
{
    Chrome = 0,
    Firefox = 1,
    Edge = 2
}
