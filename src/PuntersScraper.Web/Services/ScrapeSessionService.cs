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
    private readonly Dictionary<Discipline, ScrapeResult> _lastResults = new();
    private readonly Dictionary<string, RaceDetail> _raceDetails = new();
    private CancellationTokenSource? _cts;

    public bool IsBusy { get; private set; }
    public bool IsStopping { get; private set; }
    public string StatusText { get; private set; } = "Ready.";
    public List<MeetingRow> Meetings { get; } = new();

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

    public async Task ScrapeAsync(
        IReadOnlyList<Discipline> disciplines, DateOnly date, string countryFilter, string courseFilter)
    {
        if (disciplines.Count == 0)
        {
            SetStatus("Select at least one discipline (Horses / Greyhounds / Harness).");
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
            Meetings.Clear();
            _lastResults.Clear();
            _raceDetails.Clear();
            SetStatus("Starting browser...");

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

            // Headless is deliberately not exposed here: this scraper only reliably gets past
            // Punters' bot-detection in a real (non-headless) Chromium window positioned
            // off-screen — see ScraperOptions/PuntersScraperService. On this server that means
            // an actual interactive display surface must exist (a logged-in Windows desktop
            // session, or an Xvfb virtual display on Linux); a truly headless container will
            // fail to launch the browser at all.
            await using IPuntersScraperService service = new PuntersScraperService();
            await service.InitializeAsync(new ScraperOptions(), token);

            foreach (var discipline in disciplines)
            {
                token.ThrowIfCancellationRequested();
                var rows = new List<MeetingRow>();
                try
                {
                    var result = await service.ScrapeMeetingsAsync(discipline, date, progress: progress, cancellationToken: token);

                    result.MeetingsGrouped = result.MeetingsGrouped
                        .Select(g => new MeetingGroup
                        {
                            Group = g.Group,
                            Meetings = g.Meetings.Where(m => MatchesFilters(m, countryFilter, courseFilter)).ToList()
                        })
                        .Where(g => g.Meetings.Count > 0)
                        .ToList();

                    _lastResults[discipline] = result;

                    foreach (var group in result.MeetingsGrouped)
                    {
                        foreach (var meeting in group.Meetings)
                        {
                            var row = new MeetingRow { DisciplineEnum = discipline, Meeting = meeting, Group = group.Group ?? "" };
                            rows.Add(row);
                            Meetings.Add(row);
                        }
                    }

                    if (rows.Count == 0 && (countryFilter.Length > 0 || courseFilter.Length > 0))
                    {
                        progress.Report($"[P-{discipline.Code()}] No meetings matched the country/course filter.");
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    var message = $"[P-{discipline.Code()}] Failed: {ex.Message}";
                    disciplineFailures.Add(message);
                    SetStatus(message);
                    continue;
                }

                foreach (var row in rows)
                {
                    token.ThrowIfCancellationRequested();

                    // Scraped one race at a time (rather than via ScrapeRacesForMeetingAsync,
                    // which only returns once the whole meeting is done) so row.RacesWithDetail —
                    // and so the row's progress bar — advances live as each race finishes, instead
                    // of jumping straight from 0% to 100%.
                    foreach (var raceEvent in row.Meeting.Events)
                    {
                        token.ThrowIfCancellationRequested();
                        try
                        {
                            var detail = await service.ScrapeRaceAsync(discipline, row.Meeting, raceEvent, progress, token);
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

                    if (settings.UploadToS3)
                    {
                        var (uploaded, failed) = await UploadMeetingToS3Async(settings, discipline, row.Group, row.Meeting);
                        totalS3Uploaded += uploaded;
                        totalS3Failed += failed;
                        progress.Report(
                            $"[P-{discipline.Code()}] Uploaded {row.MeetingName} to S3: {uploaded} file(s)." +
                            (failed > 0 ? $" {failed} failed." : ""));
                    }

                    // Deliberately independent of the S3-upload block above rather than coupled
                    // together the way the desktop App's single "export" step does both at once
                    // — keeping them separate avoids double-uploading a file when both toggles
                    // are on, and suits this service's unattended/scheduled use case better.
                    if (settings.AutoExportAfterScrape && !string.IsNullOrWhiteSpace(settings.ExportFolder))
                    {
                        var exported = await ExportMeetingToFolderAsync(settings.ExportFolder, discipline, row.Group, row.Meeting);
                        totalExported += exported;
                        progress.Report($"[P-{discipline.Code()}] Exported {row.MeetingName} to folder: {exported} file(s).");
                    }
                }
            }

            if (_lastResults.Count > 0)
            {
                SetStatus($"Done. {Meetings.Count} meeting(s) loaded from {_lastResults.Count} discipline(s), " +
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
            if (_lastResults.Count > 0 && (settings.UploadToS3 || settings.AutoExportAfterScrape))
            {
                if (settings.UploadToS3)
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
            SetStatus($"Stopped by user. {Meetings.Count} meeting(s) loaded, " +
                      $"{_raceDetails.Count} race(s) with full runner detail before stopping.");
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

    private static bool MatchesFilters(Meeting meeting, string countryFilter, string courseFilter)
    {
        if (countryFilter.Length > 0)
        {
            var iso2 = meeting.Venue?.Country?.Iso2;
            if (!string.Equals(iso2, countryFilter, StringComparison.OrdinalIgnoreCase))
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
            foreach (var (discipline, result) in _lastResults)
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
