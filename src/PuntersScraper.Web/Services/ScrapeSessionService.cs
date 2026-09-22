using System.IO.Compression;
using System.Text.Json;
using PuntersScraper.Core.Scraping;
using PuntersScraper.Shared.Json;
using PuntersScraper.Shared.Models;
using PuntersScraper.Shared.Scraping;

namespace PuntersScraper.Web.Services;

/// <summary>A single meeting flattened for display in the results table, plus a back-reference
/// to the underlying Meeting so export/race-detail scraping can work off exactly what's shown.
/// Web equivalent of the desktop apps' MeetingRow.</summary>
public sealed class MeetingRow
{
    public required Discipline DisciplineEnum { get; init; }
    public required Meeting Meeting { get; init; }
    public required string Group { get; init; }
    public required DateOnly Date { get; init; }

    public string Discipline => DisciplineEnum.Code();
    public string MeetingName => Meeting.Name ?? "";
    public string? State => Meeting.State;
    public string? Country => Meeting.Venue?.Country?.Iso3;
    public int RaceCount => Meeting.Events.Count;
    public string? MeetingStage => Meeting.MeetingStage;
    public int RacesWithDetail { get; set; }

    /// <summary>Races attempted so far (success or failure) — drives <see cref="ProgressPercent"/>
    /// so the bar reaches 100% once the meeting's race loop finishes, rather than stalling short
    /// of full whenever a race fails and is skipped.</summary>
    public int RacesProcessed { get; set; }

    public string? FirstRaceLocalTime => Meeting.Events
        .Where(e => e.StartTime is not null)
        .OrderBy(e => e.StartTime)
        .FirstOrDefault()?.StartTime?.ToLocalTime().ToString("t");

    public string? TrackCondition => Meeting.Events
        .Where(e => e.StartTime is not null)
        .OrderBy(e => e.StartTime)
        .FirstOrDefault()?.TrackCondition?.Overall;

    /// <summary>0-100. RacesProcessed/RaceCount, updated one race at a time as ScrapeSessionService
    /// works through this meeting's events — a meeting with no races reads as fully done rather
    /// than 0%.</summary>
    public int ProgressPercent => RaceCount == 0 ? 100 : (int)Math.Round(100.0 * RacesProcessed / RaceCount);

    /// <summary>Set once this meeting's S3 upload has actually run — lets
    /// <see cref="ScrapeSessionService"/>'s race-scrape loop tell "already uploaded" apart from
    /// "not due yet", so resuming after Stop never uploads the same meeting twice.</summary>
    public bool UploadedToS3 { get; set; }

    /// <summary>Same idea as <see cref="UploadedToS3"/>, for the local JSON export step.</summary>
    public bool ExportedLocally { get; set; }
}

/// <summary>
/// Holds the one shared scrape session for this whole deployment — there is only ever one
/// scrape running at a time across every connected user (a second Scrape click while one is
/// already running is rejected with a clear message, same idea as the desktop apps' IsBusy
/// gating), and every connected browser sees the same live results/progress. Registered as a
/// singleton; <see cref="Changed"/> lets Razor components re-render when state changes from a
/// background scrape task.
/// </summary>
public sealed class ScrapeSessionService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<(DateOnly Date, Discipline Discipline), ScrapeResult> _lastResults = new();
    private readonly Dictionary<string, RaceDetail> _raceDetails = new();
    private CancellationTokenSource? _cts;

    /// <summary>The exact request behind the run currently sitting in <see cref="Meetings"/> —
    /// remembered so <see cref="ContinueAsync"/> can re-enter <see cref="ScrapeAsync"/> with the
    /// same parameters after a Stop.</summary>
    private ScrapeRequest? _lastScrapeRequest;

    /// <summary>True only right after a run ends via Stop (not a clean finish or a hard failure).</summary>
    private bool _canResume;

    private sealed record ScrapeRequest(
        IReadOnlyList<Discipline> Disciplines, IReadOnlyList<DateOnly> Dates, string CountryFilter, string CourseFilter, bool ForceUploadToS3);

    public bool IsBusy { get; private set; }
    public bool IsStopping { get; private set; }
    public string StatusText { get; private set; } = "Ready.";
    public List<MeetingRow> Meetings { get; } = new();

    public bool CanResume => !IsBusy && _canResume && _lastScrapeRequest is not null;

    /// <summary>Moves a meeting higher in <see cref="Meetings"/> — since <see cref="ScrapeAsync"/>'s
    /// race-scrape phase always picks whichever unfinished meeting currently sits highest in this
    /// same list, reordering it here (before or even while a scrape is running) directly changes
    /// what gets scraped next.</summary>
    public void MoveMeetingUp(MeetingRow row)
    {
        var index = Meetings.IndexOf(row);
        if (index <= 0) return;
        (Meetings[index - 1], Meetings[index]) = (Meetings[index], Meetings[index - 1]);
        NotifyChanged();
    }

    public void MoveMeetingDown(MeetingRow row)
    {
        var index = Meetings.IndexOf(row);
        if (index < 0 || index >= Meetings.Count - 1) return;
        (Meetings[index + 1], Meetings[index]) = (Meetings[index], Meetings[index + 1]);
        NotifyChanged();
    }

    /// <summary>Resumes the scrape stopped via <see cref="RequestStop"/> — re-enters
    /// <see cref="ScrapeAsync"/> with the same request, which skips every date/discipline combo
    /// already fetched and every race already recorded, so only what didn't finish gets
    /// (re)done.</summary>
    public async Task ContinueAsync()
    {
        if (_lastScrapeRequest is not { } request) return;
        await ScrapeAsync(
            request.Disciplines, request.Dates, request.CountryFilter, request.CourseFilter,
            request.ForceUploadToS3, isResume: true);
    }

    /// <summary>Fired whenever <see cref="StatusText"/>, <see cref="IsBusy"/>, or
    /// <see cref="Meetings"/> changes, so subscribed components know to re-render.</summary>
    public event Action? Changed;

    public bool CanExport => !IsBusy && _lastResults.Count > 0;

    /// <summary>Cancels the running scrape. Takes effect at the next checkpoint the scraper
    /// checks — typically within a few seconds, once the in-flight page navigation/settle finishes
    /// — rather than instantly, since Playwright's own calls don't observe the token directly.</summary>
    public void RequestStop()
    {
        if (!IsBusy || _cts is null) return;
        IsStopping = true;
        SetStatus("Stopping — finishing the current request...");
        _cts.Cancel();
    }

    /// <summary>Clears the results grid/status (from either a finished, stopped, or failed run) —
    /// shared by both the Scraper and Auto Scraper pages since they show the same
    /// <see cref="Meetings"/> list. No-ops while a scrape is running so it can't be used to wipe
    /// an in-progress run's rows out from under it — callers should also disable the button then.</summary>
    public void ClearResults()
    {
        if (IsBusy) return;
        Meetings.Clear();
        _lastResults.Clear();
        _raceDetails.Clear();
        _lastScrapeRequest = null;
        _canResume = false;
        SetStatus("Ready.");
    }

    /// <param name="forceUploadToS3">Auto-scrape always passes true here — its whole point is
    /// unattended delivery into the bucket for TroyenRaceIngestor, so it uploads regardless of
    /// whether the manual Scraper page's "Also upload to S3" checkbox happens to be on.</param>
    /// <param name="isResume">True when called from <see cref="ContinueAsync"/> after a Stop —
    /// skips the usual "clear everything and start fresh" step, so meetings/races already
    /// captured (and this run's original request) survive into this call instead of being
    /// wiped.</param>
    public async Task ScrapeAsync(
        IReadOnlyList<Discipline> disciplines, IReadOnlyList<DateOnly> dates, string countryFilter, string courseFilter,
        bool forceUploadToS3 = false, bool isResume = false)
    {
        if (disciplines.Count == 0)
        {
            SetStatus("Select at least one discipline (Horses / Greyhounds / Harness).");
            return;
        }

        if (dates.Count == 0)
        {
            SetStatus("Select at least one date.");
            return;
        }

        var browser = WebAppSettings.Load().ScraperBrowser;
        if (!ScraperBrowserAvailability.IsInstalled(browser))
        {
            SetStatus($"{browser} isn't available on this server. {ScraperBrowserAvailability.InstallHint(browser)}");
            return;
        }

        if (!await _gate.WaitAsync(0))
        {
            SetStatus("A scrape is already running (started by another user) — try again shortly.");
            return;
        }

        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        try
        {
            IsBusy = true;
            IsStopping = false;
            if (!isResume)
            {
                Meetings.Clear();
                _lastResults.Clear();
                _raceDetails.Clear();
                _lastScrapeRequest = new ScrapeRequest(disciplines, dates, countryFilter, courseFilter, forceUploadToS3);
            }
            _canResume = false;
            SetStatus(isResume ? "Resuming..." : "Starting browser...");

            IProgress<string> progress = new Progress<string>(SetStatus);
            var disciplineFailures = new List<string>();

            // Loaded once up front so each meeting can upload to S3 (and/or export to a local
            // folder) as soon as its races finish scraping, instead of only ever doing so in one
            // go at the very end (see the calls in the race-detail loop below) — a long
            // multi-meeting scrape that gets interrupted partway through would otherwise lose
            // every meeting it had already finished, which matters more here than on the desktop
            // apps since this service is meant for unattended/scheduled runs.
            var settings = WebAppSettings.Load();
            var totalS3Uploaded = 0;
            var totalS3Failed = 0;
            var totalExported = 0;

            // A meeting counts as fully done once every one of its races has recorded detail AND
            // (whichever of these are actually enabled) its S3 upload/local export has run — see
            // the matching comment on the desktop App's MainViewModel.ScrapeDatesAsync.
            bool RowNeedsUploadOrExport(MeetingRow r) =>
                ((settings.UploadToS3 || forceUploadToS3) && !r.UploadedToS3) ||
                (settings.AutoExportAfterScrape && !string.IsNullOrWhiteSpace(settings.ExportFolder) && !r.ExportedLocally);
            bool RowIsFullyDone(MeetingRow r) =>
                r.Meeting.Events.All(e => e.Id is not null && _raceDetails.ContainsKey(e.Id)) && !RowNeedsUploadOrExport(r);

            // Headless is deliberately not exposed here: this scraper only reliably gets past
            // Punters' bot-detection in a real (non-headless) Chromium window positioned
            // off-screen — see ScraperOptions/PuntersScraperService. On this server that means
            // an actual interactive display surface must exist (a logged-in Windows desktop
            // session, or an Xvfb virtual display on Linux); a truly headless container will
            // fail to launch the browser at all.
            await using IPuntersScraperService service = new PuntersScraperService();
            await service.InitializeAsync(new ScraperOptions { Browser = settings.ScraperBrowser }, token);

            // Phase 1 — list every requested date/discipline combo's meetings up front (just
            // meeting-list requests, no per-race scraping yet), so the whole list is visible —
            // and reorderable via MoveMeetingUp/MoveMeetingDown — before Phase 2 below commits to
            // any particular scrape order. A combo already fetched on an earlier attempt
            // (isResume, tracked via _lastResults) is skipped rather than re-fetched.
            foreach (var date in dates)
            foreach (var discipline in disciplines)
            {
                token.ThrowIfCancellationRequested();
                if (isResume && _lastResults.ContainsKey((date, discipline))) continue;

                try
                {
                    var result = await VpnRotator.RunWithRotationOnBlockAsync(
                        () => service.ScrapeMeetingsAsync(discipline, date, progress: progress, cancellationToken: token),
                        progress, token);

                    result.MeetingsGrouped = result.MeetingsGrouped
                        .Select(g => new MeetingGroup
                        {
                            Group = g.Group,
                            Meetings = g.Meetings.Where(m => MatchesFilters(m, countryFilter, courseFilter)).ToList()
                        })
                        .Where(g => g.Meetings.Count > 0)
                        .ToList();

                    _lastResults[(date, discipline)] = result;

                    var addedAny = false;
                    foreach (var group in result.MeetingsGrouped)
                    {
                        foreach (var meeting in group.Meetings)
                        {
                            Meetings.Add(new MeetingRow { DisciplineEnum = discipline, Meeting = meeting, Group = group.Group ?? "", Date = date });
                            addedAny = true;
                        }
                    }

                    if (!addedAny && (countryFilter.Length > 0 || courseFilter.Length > 0))
                    {
                        progress.Report($"[P-{discipline.Code()}] {date:yyyy-MM-dd}: No meetings matched the country/course filter.");
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    var message = $"[P-{discipline.Code()}] {date:yyyy-MM-dd}: Failed: {ex.Message}";
                    disciplineFailures.Add(message);
                    SetStatus(message);
                }
            }

            // Phase 2 — race every meeting's full detail. Always re-picks whichever unfinished
            // meeting currently sits highest in Meetings (rather than snapshotting an order up
            // front), so MoveMeetingUp/MoveMeetingDown have a real, live effect on what gets
            // scraped next — including while this phase is already running.
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var row = Meetings.FirstOrDefault(r => !RowIsFullyDone(r));
                if (row is null) break;

                var discipline = row.DisciplineEnum;

                // Scraped one race at a time (rather than via ScrapeRacesForMeetingAsync, which
                // only returns once the whole meeting is done) so row.RacesWithDetail — and so the
                // row's progress bar — advances live as each race finishes, instead of jumping
                // straight from 0% to 100%. Only races this meeting doesn't already have detail
                // for — on a fresh run that's all of them, on a resume it's whatever didn't
                // finish (or was never reached) before the previous Stop.
                foreach (var raceEvent in row.Meeting.Events)
                {
                    if (raceEvent.Id is not null && _raceDetails.ContainsKey(raceEvent.Id)) continue;

                    token.ThrowIfCancellationRequested();
                    try
                    {
                        var detail = await VpnRotator.RunWithRotationOnBlockAsync(
                            () => service.ScrapeRaceAsync(discipline, row.Meeting, raceEvent, progress, token),
                            progress, token);
                        if (detail.RaceId is not null) _raceDetails[detail.RaceId] = detail;
                        row.RacesWithDetail++;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        progress.Report(
                            $"[P-{discipline.Code()}] Race {raceEvent.EventNumber} ({row.MeetingName}) failed, skipping: {ex.Message}");
                    }

                    row.RacesProcessed++;
                    NotifyChanged();
                }

                if ((settings.UploadToS3 || forceUploadToS3) && !row.UploadedToS3)
                {
                    var (uploaded, failed) = await UploadMeetingToS3Async(settings, discipline, row.Group, row.Meeting);
                    totalS3Uploaded += uploaded;
                    totalS3Failed += failed;
                    row.UploadedToS3 = true;
                    progress.Report(
                        $"[P-{discipline.Code()}] Uploaded {row.MeetingName} to S3: {uploaded} file(s)." +
                        (failed > 0 ? $" {failed} failed." : ""));
                }

                // Deliberately independent of the S3-upload block above rather than coupled
                // together the way the desktop App's single "export" step does both at once
                // — keeping them separate avoids double-uploading a file when both toggles
                // are on, and suits this service's unattended/scheduled use case better.
                if (settings.AutoExportAfterScrape && !string.IsNullOrWhiteSpace(settings.ExportFolder) && !row.ExportedLocally)
                {
                    var exported = await ExportMeetingToFolderAsync(settings.ExportFolder, discipline, row.Group, row.Meeting);
                    totalExported += exported;
                    row.ExportedLocally = true;
                    progress.Report($"[P-{discipline.Code()}] Exported {row.MeetingName} to folder: {exported} file(s).");
                }
            }

            if (_lastResults.Count > 0)
            {
                SetStatus($"Done. {Meetings.Count} meeting(s) loaded from {dates.Count} date(s) / {disciplines.Count} discipline(s), " +
                          $"{_raceDetails.Count} race(s) with full runner detail.");
            }
            else if (disciplineFailures.Count > 0)
            {
                // Keep the actual error visible instead of overwriting it with a generic
                // "see status messages above" — there's nowhere else to see it, StatusText is
                // the only place any of this shows up.
                SetStatus("Finished with errors: " + string.Join(" | ", disciplineFailures));
            }
            else
            {
                SetStatus("Finished, but no meetings matched for the selected date/discipline(s)/filters.");
            }

            // Each meeting was already uploaded to S3 / exported to a folder as soon as its
            // races finished scraping (see the calls in the race-detail loop above) — this just
            // reports the running totals from those per-meeting actions.
            if (_lastResults.Count > 0 && (settings.UploadToS3 || forceUploadToS3 || settings.AutoExportAfterScrape))
            {
                if (settings.UploadToS3 || forceUploadToS3)
                {
                    StatusText += totalS3Failed > 0
                        ? $" Uploaded {totalS3Uploaded} file(s) to S3 ({totalS3Failed} failed — see above)."
                        : $" Uploaded {totalS3Uploaded} file(s) to S3.";
                }
                if (settings.AutoExportAfterScrape)
                {
                    StatusText += $" Exported {totalExported} file(s) to folder.";
                }
                NotifyChanged();
            }
        }
        catch (OperationCanceledException)
        {
            _canResume = true;
            SetStatus($"Stopped by user. {Meetings.Count} meeting(s) loaded, " +
                      $"{_raceDetails.Count} race(s) with full runner detail before stopping. " +
                      "Click Continue to pick up where it left off.");
        }
        catch (Exception ex)
        {
            SetStatus($"Scrape failed: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
            IsStopping = false;
            _cts?.Dispose();
            _cts = null;
            NotifyChanged();
            _gate.Release();
        }
    }

    /// <summary>Splits a comma/whitespace-separated ISO2 list (e.g. "AU, NZ") into its codes.
    /// Blank input yields an empty set, meaning "no filter — all countries".</summary>
    private static string[] ParseCountryCodes(string countryFilter) =>
        countryFilter.Split(new[] { ',', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);

    private static bool MatchesFilters(Meeting meeting, string countryFilter, string courseFilter)
    {
        var countryCodes = ParseCountryCodes(countryFilter);
        if (countryCodes.Length > 0)
        {
            var iso2 = meeting.Venue?.Country?.Iso2;
            if (iso2 is null || !countryCodes.Any(code => string.Equals(iso2, code, StringComparison.OrdinalIgnoreCase)))
                return false;
        }

        if (courseFilter.Length > 0)
        {
            if (meeting.Name is null || meeting.Name.IndexOf(courseFilter, StringComparison.OrdinalIgnoreCase) < 0)
                return false;
        }

        return true;
    }

    /// <summary>Builds the same "one folder per meeting" layout as the desktop apps' export,
    /// zips it in memory, and — if <paramref name="settings"/> says to — uploads every file to
    /// S3 the same way (flat, no per-meeting nesting there) while it's at it.</summary>
    public async Task<(byte[] ZipBytes, int FileCount, int S3UploadedCount, int S3FailedCount)> BuildExportAsync(WebAppSettings settings)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "punters-web-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        var fileCount = 0;
        var s3UploadedCount = 0;
        var s3FailedCount = 0;

        try
        {
            foreach (var ((_, discipline), result) in _lastResults)
            {
                foreach (var group in result.MeetingsGrouped)
                {
                    foreach (var meeting in group.Meetings)
                    {
                        var meetingFolderName = Slugify(meeting.Slug ?? meeting.Name ?? meeting.Id ?? "meeting");
                        var meetingFolder = Path.Combine(tempDir, meetingFolderName);
                        Directory.CreateDirectory(meetingFolder);

                        var meetingPayload = new
                        {
                            data = new
                            {
                                meetingsGrouped = new[]
                                {
                                    new { group = group.Group, meetings = new[] { BuildMeetingExport(meeting) } }
                                }
                            }
                        };

                        var (uploaded, failed) = await WriteAndMaybeUploadAsync(
                            settings, meetingFolder, meetingFolderName, MeetingFileName(discipline), meetingPayload);
                        s3UploadedCount += uploaded; s3FailedCount += failed; fileCount++;

                        foreach (var raceEvent in meeting.Events)
                        {
                            if (raceEvent.Id is null || !_raceDetails.TryGetValue(raceEvent.Id, out var detail))
                                continue;

                            (uploaded, failed) = await WriteAndMaybeUploadAsync(
                                settings, meetingFolder, meetingFolderName, DataDumpFileName(detail.RaceNumber), detail);
                            s3UploadedCount += uploaded; s3FailedCount += failed; fileCount++;
                        }
                    }
                }
            }

            var zipPath = tempDir + ".zip";
            if (File.Exists(zipPath)) File.Delete(zipPath);
            ZipFile.CreateFromDirectory(tempDir, zipPath);
            var bytes = await File.ReadAllBytesAsync(zipPath);
            File.Delete(zipPath);

            return (bytes, fileCount, s3UploadedCount, s3FailedCount);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    /// <summary>
    /// Uploads a single meeting straight to S3 (no local temp folder/zip involved — that
    /// machinery in <see cref="BuildExportAsync"/> only exists for the on-demand "Download ZIP"
    /// button) — called directly from <see cref="ScrapeAsync"/> as soon as each meeting's races
    /// finish, rather than waiting for the whole scrape to complete before uploading anything.
    /// </summary>
    private async Task<(int uploaded, int failed)> UploadMeetingToS3Async(
        WebAppSettings settings, Discipline discipline, string group, Meeting meeting)
    {
        var meetingFolderName = Slugify(meeting.Slug ?? meeting.Name ?? meeting.Id ?? "meeting");
        var uploaded = 0;
        var failed = 0;

        var meetingPayload = new
        {
            data = new
            {
                meetingsGrouped = new[]
                {
                    new { group, meetings = new[] { BuildMeetingExport(meeting) } }
                }
            }
        };

        var (u, f) = await UploadJsonToS3Async(settings, meetingFolderName, MeetingFileName(discipline), meetingPayload);
        uploaded += u; failed += f;

        foreach (var raceEvent in meeting.Events)
        {
            if (raceEvent.Id is null || !_raceDetails.TryGetValue(raceEvent.Id, out var detail))
                continue;

            (u, f) = await UploadJsonToS3Async(settings, meetingFolderName, DataDumpFileName(detail.RaceNumber), detail);
            uploaded += u; failed += f;
        }

        return (uploaded, failed);
    }

    private async Task<(int uploaded, int failed)> UploadJsonToS3Async(
        WebAppSettings settings, string meetingFolderName, string fileName, object payload)
    {
        var json = JsonSerializer.Serialize(payload, ScraperJsonOptions.Write);
        try
        {
            await S3JsonUploader.UploadAsync(settings, $"{meetingFolderName}-{fileName}", json);
            return (1, 0);
        }
        catch (Exception ex)
        {
            S3BucketService.LogFailure("UploadJson", settings, ex);
            SetStatus($"S3 upload failed for {fileName}: {S3BucketService.DescribeS3Exception(ex)}");
            return (0, 1);
        }
    }

    private async Task<(int uploaded, int failed)> WriteAndMaybeUploadAsync(
        WebAppSettings settings, string localFolder, string meetingFolderName, string fileName, object payload)
    {
        var json = JsonSerializer.Serialize(payload, ScraperJsonOptions.Write);
        await File.WriteAllTextAsync(Path.Combine(localFolder, fileName), json);

        if (!settings.UploadToS3) return (0, 0);

        try
        {
            await S3JsonUploader.UploadAsync(settings, $"{meetingFolderName}-{fileName}", json);
            return (1, 0);
        }
        catch (Exception ex)
        {
            S3BucketService.LogFailure("UploadJson", settings, ex);
            SetStatus($"S3 upload failed for {fileName}: {S3BucketService.DescribeS3Exception(ex)}");
            return (0, 1);
        }
    }

    /// <summary>Writes a single meeting straight to a folder on this server's disk (no S3
    /// involved — that's <see cref="UploadMeetingToS3Async"/>) — called directly from
    /// <see cref="ScrapeAsync"/> as soon as each meeting's races finish, same per-meeting timing
    /// as the S3 upload, just independent of it.</summary>
    private async Task<int> ExportMeetingToFolderAsync(
        string baseFolder, Discipline discipline, string group, Meeting meeting)
    {
        var meetingFolderName = Slugify(meeting.Slug ?? meeting.Name ?? meeting.Id ?? "meeting");
        var meetingFolder = Path.Combine(baseFolder, meetingFolderName);
        Directory.CreateDirectory(meetingFolder);
        var fileCount = 0;

        var meetingPayload = new
        {
            data = new
            {
                meetingsGrouped = new[]
                {
                    new { group, meetings = new[] { BuildMeetingExport(meeting) } }
                }
            }
        };

        await File.WriteAllTextAsync(
            Path.Combine(meetingFolder, MeetingFileName(discipline)),
            JsonSerializer.Serialize(meetingPayload, ScraperJsonOptions.Write));
        fileCount++;

        foreach (var raceEvent in meeting.Events)
        {
            if (raceEvent.Id is null || !_raceDetails.TryGetValue(raceEvent.Id, out var detail))
                continue;

            await File.WriteAllTextAsync(
                Path.Combine(meetingFolder, DataDumpFileName(detail.RaceNumber)),
                JsonSerializer.Serialize(detail, ScraperJsonOptions.Write));
            fileCount++;
        }

        return fileCount;
    }

    private static string MeetingFileName(Discipline discipline) =>
        $"{discipline.FilePrefix()}-{DateTime.Now:yyyy-MM-dd-HH-mm-ss}-meeting.json";

    private static string DataDumpFileName(int raceNumber) =>
        $"R{raceNumber}-{DateTime.Now:yyyyMMddHHmmss}-DataDump.json";

    private object BuildMeetingExport(Meeting meeting) => new
    {
        id = meeting.Id,
        name = meeting.Name,
        meetingDateUtc = meeting.MeetingDateUtc,
        meetingDateLocal = meeting.MeetingDateLocal,
        meetingType = meeting.MeetingType,
        meetingCategory = meeting.MeetingCategory,
        meetingStage = meeting.MeetingStage,
        isFuture = meeting.IsFuture ?? IsMeetingInFuture(meeting),
        tabStatus = meeting.TabStatus,
        state = meeting.State,
        slug = meeting.Slug,
        trackComments = meeting.TrackComments,
        penetrometer = meeting.Penetrometer,
        railPosition = meeting.RailPosition,
        isAbandoned = meeting.IsAbandoned ?? false,
        showSpeedMaps = meeting.ShowSpeedMaps ?? true,
        showSectionals = meeting.ShowSectionals ?? true,
        showOdds = meeting.ShowOdds ?? true,
        venue = meeting.Venue,
        events = meeting.Events.Select(e => new
        {
            id = e.Id,
            meetingId = e.MeetingId ?? meeting.Id,
            slug = e.Slug,
            name = e.Name,
            startTime = e.StartTime,
            eventNumber = e.EventNumber,
            eventClass = e.EventClass,
            status = e.Status,
            distance = e.Distance,
            starters = e.Starters,
            isResulted = e.IsResulted,
            isAbandoned = e.IsAbandoned,
            racePrizeMoney = e.RacePrizeMoney,
            trackCondition = e.TrackCondition,
            weather = e.Weather,
            entryConditions = e.EntryConditions,
            prizeMoney = e.PrizeMoney
        })
    };

    private static bool IsMeetingInFuture(Meeting meeting) =>
        !DateOnly.TryParse(meeting.MeetingDateLocal, out var d) || d >= DateOnly.FromDateTime(DateTime.Today);

    private static string Slugify(string value)
    {
        var chars = value.ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray();
        var slug = new string(chars);
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        return slug.Trim('-');
    }

    private void SetStatus(string message)
    {
        StatusText = message;
        NotifyChanged();
    }

    private void NotifyChanged() => Changed?.Invoke();
}
