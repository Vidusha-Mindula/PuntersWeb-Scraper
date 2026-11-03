using System.Collections.ObjectModel;
using System.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PuntersScraper.Core.Scraping;
using PuntersScraper.Shared.Models;
using PuntersScraper.Shared.Scraping;

namespace PuntersScraper.App.ViewModels;

/// <summary>
/// The "Race by Race" tab: list a date's meetings (no per-race scraping yet), tick the exact races
/// wanted under each meeting's expander, then download only those. Shares <see cref="IsBusy"/>,
/// <see cref="StopCommand"/>, the browser choice and <see cref="DownloadFolder"/> with the main
/// scraper, but keeps its own meeting list and race detail so it never mixes with a main run.
/// Downloads are saved to the download folder only — nothing is uploaded to S3 or published to
/// RabbitMQ from here.
/// </summary>
public sealed partial class MainViewModel
{
    public ObservableCollection<RaceMeetingRow> RaceMeetings { get; } = new();

    [ObservableProperty]
    private DateTime raceSearchDate = DateTime.Today;

    [ObservableProperty]
    private bool raceSearchHorses = true;

    [ObservableProperty]
    private bool raceSearchGreyhounds = true;

    [ObservableProperty]
    private bool raceSearchHarness = true;

    /// <summary>Optional: only list meetings whose name contains this text (case-insensitive).</summary>
    [ObservableProperty]
    private string raceSearchCourse = "";

    [ObservableProperty]
    private int selectedRaceCount;

    [ObservableProperty]
    private int selectedRaceMeetingCount;

    public string RaceSelectionSummary =>
        $"{SelectedRaceCount} race(s) selected across {SelectedRaceMeetingCount} meeting(s)";

    public bool HasRaceMeetings => RaceMeetings.Count > 0;

    private void UpdateRaceSelectionCounts()
    {
        SelectedRaceCount = RaceMeetings.Sum(m => m.SelectedCount);
        SelectedRaceMeetingCount = RaceMeetings.Count(m => m.SelectedCount > 0);
        OnPropertyChanged(nameof(RaceSelectionSummary));
        OnPropertyChanged(nameof(HasRaceMeetings));
        StartRaceDownloadCommand.NotifyCanExecuteChanged();
    }

    private bool EnsureBrowserAvailable()
    {
        var browser = SelectedScraperBrowser;
        if (ScraperBrowserAvailability.IsInstalled(browser)) return true;

        StatusText = $"{browser} isn't available on this PC. {ScraperBrowserAvailability.InstallHint(browser)}";
        return false;
    }

    /// <summary>Lists every meeting for the chosen date/discipline(s)/course name — meeting list
    /// only, so it's quick. The races under each meeting come from that same listing.</summary>
    [RelayCommand(CanExecute = nameof(CanScrape))]
    private async Task SearchRaceMeetingsAsync()
    {
        var disciplines = new List<Discipline>();
        if (RaceSearchHorses) disciplines.Add(Discipline.Horses);
        if (RaceSearchGreyhounds) disciplines.Add(Discipline.Greyhounds);
        if (RaceSearchHarness) disciplines.Add(Discipline.Harness);

        if (disciplines.Count == 0)
        {
            StatusText = "Select at least one discipline (Horses / Greyhounds / Harness).";
            return;
        }

        if (!EnsureBrowserAvailable()) return;

        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        IsBusy = true;
        IsStopping = false;
        RaceMeetings.Clear();
        UpdateRaceSelectionCounts();
        StatusText = "Starting browser...";

        IProgress<string> progress = new Progress<string>(msg => StatusText = msg);
        var date = DateOnly.FromDateTime(RaceSearchDate);
        var course = RaceSearchCourse.Trim();
        var failures = new List<string>();

        try
        {
            await using IPuntersScraperService service = new PuntersScraperService();
            await service.InitializeAsync(new ScraperOptions { Headless = Headless, Browser = SelectedScraperBrowser }, token);

            var seen = new HashSet<(Discipline Discipline, string Id)>();

            foreach (var discipline in disciplines)
            {
                token.ThrowIfCancellationRequested();

                try
                {
                    var result = await VpnRotator.RunWithRotationOnBlockAsync(
                        () => service.ScrapeMeetingsAsync(discipline, date, progress: progress, cancellationToken: token),
                        progress, token);

                    foreach (var group in result.MeetingsGrouped)
                    foreach (var meeting in group.Meetings.Where(m => MatchesFilters(m, "", course)))
                    {
                        // The same meeting can show up under more than one group — list it once.
                        if (meeting.Id is not null && !seen.Add((discipline, meeting.Id))) continue;

                        var row = RaceMeetingRow.From(discipline, group.Group ?? "", meeting, date);
                        row.SelectionChanged += UpdateRaceSelectionCounts;
                        RaceMeetings.Add(row);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failures.Add($"[P-{discipline.Code()}] {date:yyyy-MM-dd}: Failed: {ex.Message}");
                }
            }

            UpdateRaceSelectionCounts();

            if (RaceMeetings.Count > 0)
            {
                var raceTotal = RaceMeetings.Sum(m => m.RaceCount);
                StatusText = $"Found {RaceMeetings.Count} meeting(s) with {raceTotal} race(s) on {date:yyyy-MM-dd}. " +
                             "Expand a meeting, tick the races you want, then click Start.";
            }
            else if (failures.Count > 0)
            {
                StatusText = "Finished with errors: " + string.Join(" | ", failures);
            }
            else
            {
                StatusText = "No meetings matched the selected date/discipline(s)/course name.";
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = "Search stopped.";
        }
        catch (Exception ex)
        {
            StatusText = $"Search failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            IsStopping = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>Downloads only the ticked races: scrapes each one's full detail, then saves the
    /// meeting folder (the meeting file plus a DataDump file for each downloaded race) under
    /// <see cref="DownloadFolder"/>\{date}\{meeting}.</summary>
    [RelayCommand(CanExecute = nameof(CanStartRaceDownload))]
    private async Task StartRaceDownloadAsync()
    {
        var work = RaceMeetings
            .Select(m => (Meeting: m, Races: m.Races.Where(r => r.IsSelected).ToList()))
            .Where(x => x.Races.Count > 0)
            .ToList();

        if (work.Count == 0)
        {
            StatusText = "Tick at least one race to download.";
            return;
        }

        if (string.IsNullOrWhiteSpace(DownloadFolder))
        {
            StatusText = "Set a download folder first (Browse...).";
            return;
        }

        if (!EnsureBrowserAvailable()) return;

        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        IsBusy = true;
        IsStopping = false;
        foreach (var (_, races) in work)
            foreach (var race in races) race.Status = "Queued";

        StatusText = "Starting browser...";
        IProgress<string> progress = new Progress<string>(msg => StatusText = msg);

        var downloaded = 0;
        var failed = 0;
        var meetingFolders = 0;

        // Same cap as the main scraper — several race tabs at once within a meeting.
        const int RaceConcurrency = 3;
        using var limiter = new SemaphoreSlim(RaceConcurrency);

        try
        {
            await using IPuntersScraperService service = new PuntersScraperService();
            await service.InitializeAsync(new ScraperOptions { Headless = Headless, Browser = SelectedScraperBrowser }, token);

            foreach (var (meetingRow, races) in work)
            {
                token.ThrowIfCancellationRequested();

                var discipline = meetingRow.DisciplineEnum;
                // This meeting's races for this click only — so what's exported is exactly what
                // was ticked, never races fetched by an earlier click.
                var runDetails = new Dictionary<string, RaceDetail>();

                var raceTasks = races.Select(async race =>
                {
                    await limiter.WaitAsync(token);
                    try
                    {
                        token.ThrowIfCancellationRequested();
                        race.Status = "Downloading...";
                        try
                        {
                            var detail = await VpnRotator.RunWithRotationOnBlockAsync(
                                () => service.ScrapeRaceAsync(discipline, meetingRow.Meeting, race.Event, progress, token),
                                progress, token);

                            // Export looks races up by the event id, so key by that.
                            var key = race.Event.Id ?? detail.RaceId
                                ?? throw new InvalidOperationException("Race has no id to save it under.");
                            runDetails[key] = detail;
                            race.Status = "Done";
                            downloaded++;
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            race.Status = $"Failed: {ex.Message}";
                            failed++;
                            progress.Report(
                                $"[P-{discipline.Code()}] Race {race.RaceNumber} ({meetingRow.MeetingName}) failed: {ex.Message}");
                        }
                    }
                    finally
                    {
                        limiter.Release();
                    }
                }).ToList();

                var cancelled = false;
                try
                {
                    await Task.WhenAll(raceTasks);
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                }

                // Save whatever finished for this meeting, even if Stop was clicked partway.
                if (runDetails.Count > 0)
                {
                    var export = await ExportMeetingAsync(
                        discipline, meetingRow.Group, meetingRow.Meeting, DownloadFolder, uploadToS3: false, runDetails);
                    meetingFolders += export.MeetingFolderCount;
                    progress.Report(
                        $"[P-{discipline.Code()}] Saved {runDetails.Count} race(s) of {meetingRow.MeetingName} ({export.FileCount} file(s)).");
                }

                if (cancelled) token.ThrowIfCancellationRequested();
            }

            StatusText = $"Done. Downloaded {downloaded} race(s) from {meetingFolders} meeting(s) to {DownloadFolder}" +
                         (failed > 0 ? $". {failed} race(s) failed — see the status next to each race." : ".");
            SystemSounds.Asterisk.Play();
        }
        catch (OperationCanceledException)
        {
            foreach (var (_, races) in work)
                foreach (var race in races.Where(r => r.Status is "Queued" or "Downloading..."))
                    race.Status = "Stopped";

            StatusText = $"Stopped by user. Downloaded {downloaded} race(s) before stopping.";
        }
        catch (Exception ex)
        {
            StatusText = $"Download failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            IsStopping = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private bool CanStartRaceDownload() => !IsBusy && SelectedRaceCount > 0;

    [RelayCommand]
    private void SelectAllRaces()
    {
        foreach (var meeting in RaceMeetings) meeting.AllSelected = true;
    }

    [RelayCommand]
    private void ClearRaceSelection()
    {
        foreach (var meeting in RaceMeetings) meeting.AllSelected = false;
    }

    [RelayCommand]
    private void ExpandAllRaceMeetings()
    {
        foreach (var meeting in RaceMeetings) meeting.IsExpanded = true;
    }

    [RelayCommand]
    private void CollapseAllRaceMeetings()
    {
        foreach (var meeting in RaceMeetings) meeting.IsExpanded = false;
    }
}
