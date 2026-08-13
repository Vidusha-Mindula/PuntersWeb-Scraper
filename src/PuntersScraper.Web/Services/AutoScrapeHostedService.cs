using Microsoft.Extensions.Hosting;
using PuntersScraper.Shared.Models;

namespace PuntersScraper.Web.Services;

/// <summary>
/// Fires an unattended scrape (yesterday/today/tomorrow by default, all disciplines, customizable
/// via <see cref="WebAppSettings"/>'s AutoScrape* fields) at each configured time of day, for as
/// long as this deployment's process is running. Modeled on <see cref="PeriodicChecksHostedService"/>,
/// but polled every 30s (rather than hourly) since it needs to catch specific HH:mm slots rather
/// than just "at least once an hour".
/// </summary>
public sealed class AutoScrapeHostedService(
    ScrapeSessionService session, ILogger<AutoScrapeHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    /// <summary>"{yyyy-MM-dd}T{HH:mm}" of the last slot that actually fired — guards against firing
    /// twice for the same configured time if a 30s poll happens to land on it more than once.</summary>
    private string? _lastFiredSlotKey;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Auto-scrape tick failed");
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task TickAsync(CancellationToken token)
    {
        var settings = WebAppSettings.Load();
        if (!settings.AutoScrapeEnabled) return;

        var now = DateTime.Now;
        var currentSlot = now.ToString("HH:mm");
        var configuredTimes = settings.AutoScrapeTimesOfDay
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (!configuredTimes.Contains(currentSlot)) return;

        var slotKey = $"{now:yyyy-MM-dd}T{currentSlot}";
        if (slotKey == _lastFiredSlotKey) return;
        _lastFiredSlotKey = slotKey;

        if (session.IsBusy)
        {
            logger.LogWarning("Auto-scrape skipped at {Slot} — a scrape was already running", slotKey);
            return;
        }

        var disciplines = new List<Discipline>();
        if (settings.AutoScrapeHorses) disciplines.Add(Discipline.Horses);
        if (settings.AutoScrapeGreyhounds) disciplines.Add(Discipline.Greyhounds);
        if (settings.AutoScrapeHarness) disciplines.Add(Discipline.Harness);
        if (disciplines.Count == 0)
        {
            logger.LogWarning("Auto-scrape at {Slot} skipped — no disciplines selected", slotKey);
            return;
        }

        var today = DateOnly.FromDateTime(now);
        var dates = new List<DateOnly>();
        if (settings.AutoScrapeIncludeYesterday) dates.Add(today.AddDays(-1));
        if (settings.AutoScrapeIncludeToday) dates.Add(today);
        if (settings.AutoScrapeIncludeTomorrow) dates.Add(today.AddDays(1));
        if (dates.Count == 0)
        {
            logger.LogWarning("Auto-scrape at {Slot} skipped — no dates selected", slotKey);
            return;
        }

        logger.LogInformation("Auto-scrape starting at {Slot} for {Dates}", slotKey, string.Join(", ", dates));

        await session.ScrapeAsync(disciplines, dates, countryFilter: "", courseFilter: "", forceUploadToS3: true);

        // Re-load rather than reuse the earlier `settings` instance — an admin could have edited
        // and saved other fields (e.g. from the Bucket page) while this multi-date/discipline
        // scrape was running, and this must only overwrite the two AutoScrape*LastRun fields.
        var latest = WebAppSettings.Load();
        latest.AutoScrapeLastRunUtc = DateTime.UtcNow;
        latest.AutoScrapeLastRunSummary = session.StatusText;
        latest.Save();

        logger.LogInformation("Auto-scrape finished at {Slot}: {Summary}", slotKey, latest.AutoScrapeLastRunSummary);
    }
}
