using PuntersScraper.Shared.Models;
using PuntersScraper.Shared.Scraping;

namespace PuntersScraper.Core.Scraping;

/// <summary>
/// Scrapes meetings/races and feeds the shared TroyenRaceIngestor DTOs
/// (<see cref="Meeting"/>/<see cref="RaceDetail"/> etc., from PuntersScraper.Shared).
/// </summary>
public interface IPuntersScraperService : IAsyncDisposable
{
    Task InitializeAsync(ScraperOptions? options = null, CancellationToken cancellationToken = default);

    Task<ScrapeResult> ScrapeMeetingsAsync(
        Discipline discipline,
        DateOnly startDate,
        DateOnly? endDate = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);

    Task<RaceDetail> ScrapeRaceAsync(
        Discipline discipline,
        Meeting meeting,
        RaceEvent raceEvent,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);

    Task<List<RaceDetail>> ScrapeRacesForMeetingAsync(
        Discipline discipline,
        Meeting meeting,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);
}
