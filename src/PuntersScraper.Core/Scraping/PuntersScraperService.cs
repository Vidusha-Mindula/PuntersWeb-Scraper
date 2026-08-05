using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using PuntersScraper.Shared.Json;
using PuntersScraper.Shared.Models;
using PuntersScraper.Shared.Scraping;

namespace PuntersScraper.Core.Scraping;

/// <summary>
/// Scrapes Punters.com.au's meeting/race listings for a given discipline and date, and emits
/// the shared <see cref="Meeting"/>/<see cref="RaceDetail"/> DTOs (from
/// PuntersScraper.Shared) that TroyenRaceIngestor expects.
///
/// Punters.com.au is a Nuxt 3 app with an "_apollo:default" cache, reachable via
/// document.getElementById('__nuxt').__vue_app__.config.globalProperties.$nuxt.payload.data.
/// We drive the site's own UI and read whatever it already resolved rather than firing our own
/// request (api.punters.com.au sits behind bot-detection/CORS restrictions that reject an
/// independently injected request).
///
/// A few things worth noting for anyone maintaining this:
///   - Punters' meeting-list query returns per-event racePrizeMoneyValue/racePrizeMoneyUnit —
///     the race's prize money in its OWN native currency (e.g. GBP for a UK meeting), not AUD.
///     There is no separate always-AUD field at this level (confirmed by inspecting the raw
///     cache entry directly). The race's own page (event(...) query, read in
///     <see cref="ExtractRaceDetailAsync"/>) exposes the AUD figure Punters itself computes —
///     event.racePrizeMoney (total) and event.prizeMoney[].value (per-place breakdown), the same
///     numbers shown in that race's own "Prize Money" popover — so <see cref="ScrapeRaceAsync"/>
///     overwrites the passed-in RaceEvent's prize money with those once available, no external
///     FX lookup needed.
///   - Punters' meeting-list query also returns a per-meeting weather object (but no per-event
///     weather), so that single value is copied onto every event when building the meeting
///     export.
///   - Punters exposes a real free-text silk description (selection/competitor.racingColours)
///     for almost every runner, so the SilkSvgDescriber image-based fallback (used defensively
///     here too) should rarely actually trigger.
///   - For meetings/races that haven't finished processing yet, prize money breakdown and
///     historical form/stats for a given runner are simply absent from the API.
/// </summary>
public sealed class PuntersScraperService : IPuntersScraperService
{
    private const string BaseUrl = "https://www.punters.com.au";

    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";

    private const int MaxTabDaysAhead = 4;
    private const int MaxTabDaysBack = 1;

    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IBrowserContext? _context;
    private int _navigationTimeoutMs = 45_000;
    private int _settleDelayMs = 1500;

    public async Task InitializeAsync(ScraperOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new ScraperOptions();

        var args = new List<string> { "--disable-blink-features=AutomationControlled" };
        if (!options.Headless && options.HideWindow)
        {
            args.Add("--window-position=-32000,-32000");
        }

        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = options.Headless,
            Args = args
        });

        _context = await _browser.NewContextAsync(new BrowserNewContextOptions
        {
            UserAgent = UserAgent,
            ViewportSize = new ViewportSize { Width = 1400, Height = 900 },
            Locale = "en-AU",
            TimezoneId = "Australia/Sydney"
        });

        await _context.AddInitScriptAsync(
            "Object.defineProperty(navigator, 'webdriver', { get: () => undefined });");

        _navigationTimeoutMs = options.NavigationTimeoutMs;
        _settleDelayMs = options.SettleDelayMs;
    }

    private static string FormGuidePath(Discipline discipline) => discipline switch
    {
        Discipline.Horses => "horses",
        Discipline.Greyhounds => "greyhounds",
        Discipline.Harness => "harness",
        _ => throw new ArgumentOutOfRangeException(nameof(discipline), discipline, null)
    };

    public async Task<ScrapeResult> ScrapeMeetingsAsync(
        Discipline discipline,
        DateOnly startDate,
        DateOnly? endDate = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (_context is null)
            throw new InvalidOperationException($"Call {nameof(InitializeAsync)} before scraping.");

        if (endDate is { } explicitEnd && explicitEnd != startDate)
        {
            throw new PuntersScrapeException(
                "Only single-day scraping is currently supported: Punters' own UI presents one " +
                "day at a time (via date tabs), so there is no single request that covers a range. " +
                "Call ScrapeMeetingsAsync once per day instead.");
        }

        var page = await _context.NewPageAsync();
        try
        {
            var formGuideUrl = $"{BaseUrl}/form-guide/{FormGuidePath(discipline)}/";
            progress?.Report($"[P-{discipline.Code()}] Opening {formGuideUrl} ...");

            await page.GotoAsync(formGuideUrl, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = _navigationTimeoutMs
            });

            cancellationToken.ThrowIfCancellationRequested();
            await page.WaitForTimeoutAsync(_settleDelayMs);
            await WaitForNuxtAppAsync(page);

            // "Today" here must be Sydney's calendar day (the browser context's TimezoneId, set
            // in InitializeAsync), not the host machine's local date — Punters' date tabs
            // ("Tomorrow", weekday names, ...) are labeled relative to Sydney's clock, and a
            // scraper running in any other timezone can otherwise be a day off whenever the two
            // calendars don't currently agree (e.g. host machine already into tomorrow while
            // Sydney is still "today", or vice versa), landing on the wrong tab label entirely.
            var todayStr = await page.EvaluateAsync<string>("""
                () => {
                    const d = new Date();
                    const p = n => String(n).padStart(2, '0');
                    return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}`;
                }
                """);
            var today = DateOnly.Parse(todayStr);
            var dayOffset = startDate.DayNumber - today.DayNumber;

            string bodyJson;
            if (dayOffset == 0)
            {
                bodyJson = await ReadEmbeddedMeetingsAsync(page);
            }
            else
            {
                if (dayOffset < -MaxTabDaysBack || dayOffset > MaxTabDaysAhead)
                {
                    throw new PuntersScrapeException(
                        $"{startDate:yyyy-MM-dd} is {(dayOffset < 0 ? $"{-dayOffset} day(s) in the past" : $"{dayOffset} days ahead")}, " +
                        $"which is outside Punters' named date tabs (Yesterday, Today + {MaxTabDaysAhead} days ahead).");
                }

                var tabLabel = dayOffset switch
                {
                    -1 => "Yesterday",
                    1 => "Tomorrow",
                    _ => startDate.ToDateTime(TimeOnly.MinValue).DayOfWeek.ToString()
                };
                progress?.Report($"[P-{discipline.Code()}] Clicking the '{tabLabel}' date tab ...");
                var tab = page.GetByRole(AriaRole.Tab, new PageGetByRoleOptions { Name = tabLabel, Exact = true });
                if (await tab.CountAsync() == 0)
                {
                    throw new PuntersScrapeException(
                        $"Could not find a '{tabLabel}' date tab on {formGuideUrl}. Punters may have changed its " +
                        "date-tab labels/layout since this was written.");
                }

                // Snapshot BEFORE clicking: clicking a date tab does not update the Apollo cache
                // ReadEmbeddedMeetingsAsync reads (confirmed by diffing it before/after) — the
                // fresh data instead lands under a brand-new, unpredictable content-hash key in
                // Nuxt's own payload, findable only by noticing it wasn't there before the click.
                var keysBeforeClick = await GetNuxtPayloadDataKeysAsync(page);

                // Punters' own lazy-loaded promo banner/modal frequently ends up sitting
                // visually on top of the tab bar (it starts as a zero-height placeholder and
                // expands once its ad content loads), which both blocks a normal click
                // (endless "intercepts pointer events" retries against it, since the tab itself
                // also carries "disable-pointer-events" — Punters' own click handling clearly
                // works around that via something other than a plain simulated click) and
                // silently hijacks a Force click into "clicking" the banner instead of the tab
                // (Force still clicks at screen coordinates, and the banner is what's actually
                // there). Removing it outright sidesteps both failure modes at once.
                await page.EvaluateAsync("() => { const b = document.querySelector('.np-web-widget-campaign-modal'); if (b) b.remove(); }");

                await tab.First.ClickAsync(new LocatorClickOptions { Timeout = 10_000 });

                bodyJson = await ReadFreshTabMeetingsAsync(page, keysBeforeClick);
            }

            using var doc = JsonDocument.Parse(bodyJson);
            if (doc.RootElement.TryGetProperty("error", out var errorProp))
            {
                throw new PuntersScrapeException(
                    $"Could not read meetings from {formGuideUrl}: {errorProp.GetString()}");
            }

            var response = JsonSerializer.Deserialize<GroupedMeetingsPayload>(bodyJson, ScraperJsonOptions.Deserialize)
                ?? throw new PuntersScrapeException("Could not parse Punters meetings response (empty result).");

            var groups = response.Data?.MeetingsGrouped ?? new List<MeetingGroup>();
            progress?.Report(
                $"[P-{discipline.Code()}] Received {groups.Sum(g => g.Meetings.Count)} meeting(s) " +
                $"across {groups.Count} group(s).");

            return new ScrapeResult
            {
                Discipline = discipline,
                StartDate = startDate,
                EndDate = startDate,
                ScrapedAtUtc = DateTimeOffset.UtcNow,
                MeetingsGrouped = groups
            };
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    public async Task<RaceDetail> ScrapeRaceAsync(
        Discipline discipline,
        Meeting meeting,
        RaceEvent raceEvent,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (_context is null)
            throw new InvalidOperationException($"Call {nameof(InitializeAsync)} before scraping.");

        if (string.IsNullOrEmpty(meeting.Slug) || string.IsNullOrEmpty(raceEvent.Slug))
        {
            throw new PuntersScrapeException(
                "Meeting/race slug is missing — pass in the Meeting/RaceEvent objects returned by " +
                $"{nameof(ScrapeMeetingsAsync)}, not hand-built ones.");
        }

        var raceUrl = $"{BaseUrl}/form-guide/{FormGuidePath(discipline)}/{meeting.Slug}/{raceEvent.Slug}/";

        var page = await _context.NewPageAsync();
        try
        {
            progress?.Report(
                $"[P-{discipline.Code()}] Opening race {raceEvent.EventNumber} ({meeting.Name}): {raceUrl} ...");

            await page.GotoAsync(raceUrl, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = _navigationTimeoutMs
            });

            cancellationToken.ThrowIfCancellationRequested();
            await page.WaitForTimeoutAsync(_settleDelayMs);
            await WaitForNuxtAppAsync(page);

            var json = await ExtractRaceDetailAsync(page);

            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("error", out var errorProp))
            {
                throw new PuntersScrapeException(
                    $"Could not extract race detail from {raceUrl}: {errorProp.GetString()}");
            }

            var detail = JsonSerializer.Deserialize<RaceDetail>(json, ScraperJsonOptions.Deserialize)
                ?? throw new PuntersScrapeException("Could not parse race detail (empty result).");

            // Overwrite the meeting-list's native-currency prize money with the true AUD figure
            // Punters itself computes (this race's own "Prize Money" popover) — see the comment
            // on racePrizeMoneyAud/prizeMoneyBreakdownAud in ExtractRaceDetailAsync. raceEvent is
            // the same object living in meeting.Events, so this is visible to the caller too.
            if (doc.RootElement.TryGetProperty("racePrizeMoneyAud", out var audProp)
                && audProp.ValueKind == JsonValueKind.Number)
            {
                raceEvent.RacePrizeMoney = audProp.GetDouble();
                raceEvent.RacePrizeMoneyUnit = "AUD";
            }

            if (doc.RootElement.TryGetProperty("prizeMoneyBreakdownAud", out var breakdownProp)
                && breakdownProp.ValueKind == JsonValueKind.Array)
            {
                raceEvent.PrizeMoney = breakdownProp.EnumerateArray()
                    .Select(p => new PrizeMoneyEntry
                    {
                        Position = p.TryGetProperty("position", out var pos) && pos.ValueKind == JsonValueKind.String
                            ? pos.GetString() : null,
                        Value = p.TryGetProperty("value", out var val) && val.ValueKind == JsonValueKind.Number
                            ? val.GetDouble() : (double?)null
                    })
                    .ToList();
            }

            // Parallel to detail.Runners — each runner's raw selection id, only used below to
            // match it up with its getFullFormsBySelectionIds response (see ExtractRaceDetailAsync
            // and ScrapeFullFormsAsync).
            var runnerSelectionIds = doc.RootElement.TryGetProperty("runners", out var runnersProp)
                && runnersProp.ValueKind == JsonValueKind.Array
                    ? runnersProp.EnumerateArray()
                        .Select(r => r.TryGetProperty("selectionId", out var sid) && sid.ValueKind == JsonValueKind.String
                            ? sid.GetString() : null)
                        .ToList()
                    : new List<string?>();

            var fullForms = await ScrapeFullFormsAsync(page, runnerSelectionIds.Count(id => id != null), progress, discipline);

            for (var i = 0; i < detail.Runners.Count; i++)
            {
                var runner = detail.Runners[i];
                runner.Discipline = discipline.Code();

                // Punters carries a real free-text silk description (racingColours) for almost
                // every runner, so this image-based fallback should rarely actually fire — kept
                // for the rare cases where it's missing.
                if (string.IsNullOrEmpty(runner.SilkColourText) && !string.IsNullOrEmpty(runner.SilkImageUrl))
                {
                    runner.SilkColourText = await SilkSvgDescriber.DescribeAsync(runner.SilkImageUrl);
                }

                // Overwrite the single lastRun-derived entry with up to 5 real, distinct past
                // runs, if we managed to capture this runner's full-form response.
                var selectionId = i < runnerSelectionIds.Count ? runnerSelectionIds[i] : null;
                if (selectionId != null && fullForms.TryGetValue(selectionId, out var forms) && forms.Count > 0)
                {
                    var pastRuns = new List<PastRun>();
                    var seenRaceKeys = new HashSet<string>();
                    foreach (var form in forms)
                    {
                        var mapped = MapPastRunFromForm(form);

                        // Punters' full-form feed occasionally lists the same underlying race
                        // twice — once with full detail, once as a sparser duplicate under a
                        // slightly different course/date spelling (e.g. "Epsom Downs"/2026-07-02
                        // vs "Epsom"/2026-07-01 for the same race). Course/date aren't a safe
                        // dedup key, and even the margin can round differently between the two
                        // (18.5L vs 18.75L) — but the runner's own finish position plus the
                        // winner/2nd/3rd/SP have matched exactly on every duplicate pair seen.
                        var key = string.Join('|', mapped.FinishPosition, mapped.WinnerName,
                            mapped.SecondName, mapped.ThirdName, mapped.StartingPrice);
                        if (!seenRaceKeys.Add(key)) continue;

                        pastRuns.Add(mapped);
                        if (pastRuns.Count == 5) break;
                    }

                    if (pastRuns.Count > 0)
                    {
                        runner.PastRuns = pastRuns;
                        runner.LastRun = pastRuns[0].Date;
                    }
                }
            }

            progress?.Report(
                $"[P-{discipline.Code()}] Race {raceEvent.EventNumber} ({meeting.Name}): {detail.Runners.Count} runner(s).");

            return detail;
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    /// <summary>
    /// Each runner's last 5+ starts (beyond the single lastRun ExtractRaceDetailAsync already
    /// has) come from a getFullFormsBySelectionIds query, fired one-per-runner by Punters' own
    /// page code — but only once that runner's row scrolls into view (confirmed empirically: with
    /// no scrolling at all, none of these requests fire, no matter how long you wait). This clicks
    /// the page's own "Show All Form" button, then sweeps down the page in small wheel steps with
    /// a real pause between each — confirmed empirically to be what actually triggers every row's
    /// lazy fetch reliably; scrollIntoViewIfNeeded() (which jumps straight there) and large wheel
    /// steps (which can hop clean over a row between polls) both missed rows in testing. Reads
    /// back the real responses Playwright observes rather than replaying the request ourselves
    /// (api.punters.com.au rejects requests it doesn't recognize as coming from the real page —
    /// see the class doc comment; confirmed by testing an equivalent in-page fetch() call
    /// directly, which was rejected outright).
    ///
    /// Best-effort like <see cref="SilkSvgDescriber"/>: if the button isn't there, or some
    /// runners' requests never arrive before the overall deadline, those runners simply keep the
    /// single lastRun entry ExtractRaceDetailAsync already gave them.
    /// </summary>
    private static async Task<Dictionary<string, List<JsonElement>>> ScrapeFullFormsAsync(
        IPage page, int expectedRunnerCount, IProgress<string>? progress, Discipline discipline)
    {
        var formsBySelectionId = new Dictionary<string, List<JsonElement>>();
        if (expectedRunnerCount == 0) return formsBySelectionId;

        async void OnResponse(object? _, IResponse response)
        {
            if (!response.Url.Contains("getFullFormsBySelectionIds")) return;
            try
            {
                var body = await response.TextAsync();
                using var responseDoc = JsonDocument.Parse(body);
                if (!responseDoc.RootElement.TryGetProperty("data", out var dataEl)) return;
                if (!dataEl.TryGetProperty("competitorForms", out var cfEl) || cfEl.ValueKind != JsonValueKind.Array) return;

                foreach (var cf in cfEl.EnumerateArray())
                {
                    if (!cf.TryGetProperty("selectionId", out var idEl) || idEl.ValueKind != JsonValueKind.String) continue;
                    if (!cf.TryGetProperty("forms", out var formsEl) || formsEl.ValueKind != JsonValueKind.Array) continue;

                    formsBySelectionId[idEl.GetString()!] = formsEl.EnumerateArray().Select(f => f.Clone()).ToList();
                }
            }
            catch
            {
                // Best-effort: a malformed/partial response just leaves that runner without full form.
            }
        }

        page.Response += OnResponse;
        try
        {
            await page.EvaluateAsync("() => { const b = document.querySelector('.np-web-widget-campaign-modal'); if (b) b.remove(); }");
            var showAllButton = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Show All Form", Exact = true });
            if (await showAllButton.CountAsync() == 0)
            {
                progress?.Report($"[P-{discipline.Code()}] No 'Show All Form' button found; keeping each runner's single last run.");
                return formsBySelectionId;
            }

            await showAllButton.First.ClickAsync(new LocatorClickOptions { Timeout = 10_000 });
            await page.WaitForTimeoutAsync(500);

            // Small wheel steps with a real pause between each reliably trigger every runner
            // row's lazy fetch as it passes through the viewport; scrollIntoViewIfNeeded() (which
            // jumps straight there) and large wheel steps (which can hop clean over a row between
            // polls) both missed rows in testing — this needs an actual gradual scroll, not a
            // teleport. Sweeps top-to-bottom repeatedly (bounded by the overall deadline below)
            // in case a row's request didn't fire on the first pass.
            var overallDeadline = DateTime.UtcNow.AddSeconds(60);
            while (formsBySelectionId.Count < expectedRunnerCount && DateTime.UtcNow < overallDeadline)
            {
                await page.EvaluateAsync("() => window.scrollTo(0, 0)");
                await page.WaitForTimeoutAsync(300);

                var atBottom = false;
                while (!atBottom && formsBySelectionId.Count < expectedRunnerCount && DateTime.UtcNow < overallDeadline)
                {
                    await page.Mouse.WheelAsync(0, 600);
                    await page.WaitForTimeoutAsync(350);
                    atBottom = await page.EvaluateAsync<bool>(
                        "() => (window.innerHeight + window.scrollY) >= document.body.scrollHeight - 10");
                }
            }

            // Let any request that's already in flight land before we move on.
            await page.WaitForTimeoutAsync(1000);

            if (formsBySelectionId.Count < expectedRunnerCount)
            {
                progress?.Report(
                    $"[P-{discipline.Code()}] Only got full form for {formsBySelectionId.Count}/{expectedRunnerCount} " +
                    "runner(s); the rest keep their single last run.");
            }
        }
        finally
        {
            page.Response -= OnResponse;
        }

        return formsBySelectionId;
    }

    /// <summary>
    /// Maps one entry from getFullFormsBySelectionIds' forms[] (see
    /// <see cref="ScrapeFullFormsAsync"/>) into the same <see cref="PastRun"/> shape used for the
    /// single-run lastRun fallback in ExtractRaceDetailAsync. This endpoint's shape differs from
    /// selection.lastRun — barrier and starting price are embedded in a free-text summaryMarkup
    /// string rather than dedicated numeric fields, so they're pulled out with a regex; jockey
    /// name for the runner's own run isn't exposed at all here either, matching the existing
    /// lastRun mapping (which also leaves it null).
    /// </summary>
    private static PastRun MapPastRunFromForm(JsonElement f)
    {
        string? GetStr(string prop) => f.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        int? GetInt(string prop) => f.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
        double? GetDouble(string prop) => f.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
        bool GetBool(string prop) => f.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.True;

        string? summaryMarkup = null;
        string? winnerName = null, secondName = null, thirdName = null;
        if (f.TryGetProperty("formLine", out var formLine) && formLine.ValueKind == JsonValueKind.Object)
        {
            summaryMarkup = formLine.TryGetProperty("summaryMarkup", out var sm) && sm.ValueKind == JsonValueKind.String
                ? sm.GetString() : null;

            if (formLine.TryGetProperty("places", out var placesEl) && placesEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in placesEl.EnumerateArray())
                {
                    var pos = p.TryGetProperty("finishPosition", out var pv) && pv.ValueKind == JsonValueKind.Number ? pv.GetInt32() : (int?)null;
                    var name = p.TryGetProperty("competitorName", out var nv) && nv.ValueKind == JsonValueKind.String ? nv.GetString() : null;
                    if (pos == 1) winnerName = name;
                    else if (pos == 2) secondName = name;
                    else if (pos == 3) thirdName = name;
                }
            }
        }

        string? barrier = null;
        string? startingPrice = null;
        if (summaryMarkup != null)
        {
            var barrierMatch = Regex.Match(summaryMarkup, @"Barrier:\s*(\d+)");
            if (barrierMatch.Success) barrier = barrierMatch.Groups[1].Value;

            var spMatch = Regex.Match(summaryMarkup, @"SP\s*\$([\d.]+)");
            if (spMatch.Success) startingPrice = "$" + spMatch.Groups[1].Value;
        }

        var trackCondition = GetStr("trackCondition");
        var trackConditionRating = GetStr("trackConditionRating");
        var margin = GetDouble("margin");
        var eventDistance = GetInt("eventDistance");
        var finishPosition = GetInt("finishPosition");

        return new PastRun
        {
            Type = GetBool("isTrial") ? "TRIAL" : "RACE",
            FinishPosition = finishPosition,
            Starters = GetInt("eventStarters"),
            RaceNumber = GetInt("eventNumber"),
            Course = GetStr("meetingName"),
            Date = GetStr("meetingDate"),
            Distance = eventDistance != null ? $"{eventDistance}m" : null,
            RaceName = GetStr("eventNameForm") ?? GetStr("eventNameNews"),
            StartingPrice = startingPrice,
            JockeyName = null,
            WinnerName = winnerName,
            SecondName = secondName,
            ThirdName = thirdName,
            LengthsBehind = margin != null ? $"{margin}L" : null,
            TrackCondition = trackCondition != null && trackConditionRating != null
                ? $"{trackCondition} {trackConditionRating}" : trackCondition,
            EventClass = null,
            FinishTime = GetStr("finishTime"),
            BarrierPosition = barrier,
            RunDetail = GetStr("videoComment") ?? GetStr("videoNote"),
            RaceResult = new RaceResultRef { HorseName = null, Position = finishPosition }
        };
    }

    public async Task<List<RaceDetail>> ScrapeRacesForMeetingAsync(
        Discipline discipline,
        Meeting meeting,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var results = new List<RaceDetail>();
        foreach (var raceEvent in meeting.Events)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                results.Add(await ScrapeRaceAsync(discipline, meeting, raceEvent, progress, cancellationToken));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                progress?.Report(
                    $"[P-{discipline.Code()}] Race {raceEvent.EventNumber} ({meeting.Name}) failed, skipping: {ex.Message}");
            }
        }

        return results;
    }

    /// <summary>DTO purely for deserializing the shape <see cref="ReadEmbeddedMeetingsAsync"/> builds
    /// on the JS side — never sent anywhere, just a typed stepping stone to <see cref="ScrapeResult"/>.</summary>
    private sealed class GroupedMeetingsPayload
    {
        public GroupedMeetingsData? Data { get; set; }
    }

    private sealed class GroupedMeetingsData
    {
        public List<MeetingGroup>? MeetingsGrouped { get; set; }
    }

    /// <summary>
    /// Bot-detection interstitials ("Checking your browser... this should only take a moment")
    /// are far more likely to greet a datacenter/cloud server IP than a residential one, and —
    /// unlike an outright block — normally auto-resolve and redirect to the real page within a
    /// few seconds once their own JS challenge completes. The fixed settle delay elsewhere is
    /// tuned for the normal case and isn't long enough for that; this polls for the real Nuxt
    /// app to actually mount before giving up, so a slow-but-solvable challenge doesn't get
    /// mistaken for a hard failure. If it never mounts (a genuinely blocked/rate-limited IP,
    /// or Punters changed something structurally), this simply times out and the existing
    /// error path reports whatever page actually ended up loaded.
    /// </summary>
    private static async Task WaitForNuxtAppAsync(IPage page, int timeoutMs = 20_000)
    {
        try
        {
            await page.WaitForFunctionAsync(
                "() => { const el = document.getElementById('__nuxt'); return !!(el && el.__vue_app__); }",
                new PageWaitForFunctionOptions { Timeout = timeoutMs, PollingInterval = 500 });
        }
        catch (TimeoutException)
        {
            // Swallowed deliberately: the caller's own extraction script still runs afterwards
            // and reports a proper diagnostic (page title/URL/body snippet) if the app never
            // mounted, rather than failing here with a less useful bare timeout message.
        }
    }

    /// <summary>
    /// Resolves Punters' Nuxt 3 "_apollo:default" cache's meetings(...) root query into our own
    /// Meeting/RaceEvent shape (grouped Australia/International, matching the two-tier grouping
    /// TroyenRaceIngestor's MeetingFileDto expects — Punters' own query returns a flat list, so
    /// the grouping is inferred here from venue.country.iso2).
    /// </summary>
    private static async Task<string> ReadEmbeddedMeetingsAsync(IPage page)
    {
        const string script = """
            () => {
                function resolveNuxt3(val, cache, seen) {
                    if (val === null || val === undefined) return val;
                    if (Array.isArray(val)) return val.map(v => resolveNuxt3(v, cache, seen));
                    if (typeof val === 'object') {
                        if (val.__ref) {
                            if (seen.has(val.__ref)) return null;
                            const next = cache[val.__ref];
                            return next ? resolveNuxt3(next, cache, new Set(seen).add(val.__ref)) : null;
                        }
                        const out = {};
                        for (const k of Object.keys(val)) out[k] = resolveNuxt3(val[k], cache, seen);
                        return out;
                    }
                    return val;
                }

                // If bot-detection intercepts this navigation (a CAPTCHA/"just a moment"/block
                // page instead of the real site — far more likely from a datacenter/cloud
                // server IP than a residential one), the page simply won't have this Nuxt
                // scaffold at all. Reporting the title/URL/a body snippet here means that shows
                // up directly in the error instead of a bare "cache not found" with no clue why.
                function diagnostics() {
                    return `title="${document.title}" url="${location.href}" bodySnippet="${(document.body.innerText || '').replace(/\s+/g, ' ').trim().slice(0, 200)}"`;
                }

                const el = document.getElementById('__nuxt');
                const app = el && el.__vue_app__;
                const nuxt = app && app.config.globalProperties.$nuxt;
                const cache = nuxt && nuxt.payload && nuxt.payload.data && nuxt.payload.data['_apollo:default'];
                if (!cache) return JSON.stringify({ error: 'No embedded Nuxt3 apollo cache found on this page. ' + diagnostics() });

                const rootQuery = cache['ROOT_QUERY'];
                const mKey = rootQuery && Object.keys(rootQuery).find(k => k.startsWith('meetings('));
                if (!mKey) return JSON.stringify({ error: 'No meetings(...) entry found in the embedded cache. ' + diagnostics() });

                const resolved = resolveNuxt3(rootQuery[mKey], cache, new Set()) || [];

                function buildEvent(meeting, e) {
                    return {
                        id: e.id,
                        meetingId: meeting.id,
                        slug: e.slug,
                        name: e.name,
                        startTime: e.startTime,
                        eventNumber: e.eventNumber,
                        eventClass: e.eventClass || null,
                        status: e.status || null,
                        distance: e.distance != null ? e.distance : null,
                        starters: null,
                        isResulted: !!e.isResulted,
                        isAbandoned: !!e.isAbandoned,
                        // racePrizeMoneyValue + racePrizeMoneyUnit is the race's prize money in
                        // its OWN native currency (e.g. 14756/"GBP" for a UK meeting) — there is
                        // no separate always-AUD field at this meeting-list level, confirmed by
                        // inspecting the raw cache entry directly. ScrapeRaceAsync overwrites this
                        // with the true AUD figure (event.racePrizeMoney) once that race's own
                        // page has been scraped — until then this stays in native currency.
                        racePrizeMoney: e.racePrizeMoneyValue != null ? e.racePrizeMoneyValue : null,
                        racePrizeMoneyUnit: e.racePrizeMoneyUnit || null,
                        trackCondition: e.trackCondition || null,
                        // Punters has no per-event weather query — its meeting-list query only
                        // returns ONE weather object per meeting, so it's copied onto every
                        // event here (a meeting is one place/day, so this is a fair proxy).
                        weather: meeting.weather || null,
                        entryConditions: [],
                        prizeMoney: []
                    };
                }

                function buildMeeting(m) {
                    return {
                        id: m.id,
                        name: m.name,
                        slug: m.slug,
                        meetingDateUtc: m.meetingDateUtc,
                        meetingDateLocal: m.meetingDateLocal,
                        meetingType: m.meetingType || null,
                        meetingCategory: m.meetingCategory || null,
                        meetingStage: m.meetingStage || null,
                        tabStatus: m.tabStatus,
                        state: (m.venue && m.venue.state) || null,
                        trackComments: null,
                        isAbandoned: null,
                        venue: m.venue ? {
                            id: m.venue.id,
                            name: m.venue.name,
                            slug: m.venue.slug,
                            state: m.venue.state,
                            address: m.venue.address,
                            trackMapUrl: m.venue.trackMapUrl || null,
                            straight: m.venue.straight != null ? m.venue.straight : null,
                            straightUnit: m.venue.straightUnit || null,
                            circumference: m.venue.circumference != null ? m.venue.circumference : null,
                            circumferenceUnit: m.venue.circumferenceUnit || null,
                            weatherLastUpdated: m.venue.weatherLastUpdated || null,
                            isClockWise: m.venue.isClockWise != null ? m.venue.isClockWise : null,
                            country: m.venue.country ? {
                                id: m.venue.country.id,
                                name: m.venue.country.name,
                                iso2: m.venue.country.iso2,
                                iso3: m.venue.country.iso3
                            } : null
                        } : null,
                        events: (m['events({})'] || m.events || []).map(e => buildEvent(m, e))
                    };
                }

                const groupsMap = new Map();
                for (const m of resolved) {
                    const iso2 = m.venue && m.venue.country && m.venue.country.iso2;
                    const group = iso2 === 'AU' ? 'Australia' : 'International';
                    if (!groupsMap.has(group)) groupsMap.set(group, []);
                    groupsMap.get(group).push(buildMeeting(m));
                }

                const meetingsGrouped = Array.from(groupsMap.entries()).map(([group, meetings]) => ({ group, meetings }));
                return JSON.stringify({ data: { meetingsGrouped } });
            }
            """;

        return await page.EvaluateAsync<string>(script);
    }

    private static Task<string[]> GetNuxtPayloadDataKeysAsync(IPage page) =>
        page.EvaluateAsync<string[]>("""
            () => {
                const app = document.getElementById('__nuxt') && document.getElementById('__nuxt').__vue_app__;
                const nuxt = app && app.config.globalProperties.$nuxt;
                return Object.keys((nuxt && nuxt.payload && nuxt.payload.data) || {});
            }
            """);

    /// <summary>
    /// Clicking a date tab (Tomorrow/a weekday) does NOT update the Apollo cache that
    /// <see cref="ReadEmbeddedMeetingsAsync"/> reads — confirmed by diffing the cache before and
    /// after a click, it's untouched. Instead it lands in Nuxt's own useAsyncData payload under
    /// an opaque content-hash key (unrelated to the query variables, so it can't be predicted or
    /// searched for by name) holding an already-resolved (no __ref indirection) { meetings: [...] }
    /// array. The only reliable way to find it is to snapshot the payload's keys before the
    /// click and poll for whichever NEW key shows up afterwards with a .meetings array — which is
    /// exactly what this does.
    /// </summary>
    private static async Task<string> ReadFreshTabMeetingsAsync(IPage page, string[] keysBeforeClick, int timeoutMs = 15_000)
    {
        var keysBeforeClickJs = string.Join(",", keysBeforeClick.Select(k => JsonSerializer.Serialize(k)));

        string script = $$"""
            () => {
                function diagnostics() {
                    return `title="${document.title}" url="${location.href}" bodySnippet="${(document.body.innerText || '').replace(/\s+/g, ' ').trim().slice(0, 200)}"`;
                }

                const before = new Set([{{keysBeforeClickJs}}]);
                const app = document.getElementById('__nuxt') && document.getElementById('__nuxt').__vue_app__;
                const nuxt = app && app.config.globalProperties.$nuxt;
                const data = (nuxt && nuxt.payload && nuxt.payload.data) || {};
                const freshKey = Object.keys(data).find(k => !before.has(k) && data[k] && Array.isArray(data[k].meetings));
                if (!freshKey) return JSON.stringify({ error: 'No fresh meetings payload appeared after clicking the date tab. ' + diagnostics() });

                const resolved = data[freshKey].meetings || [];

                function buildEvent(meeting, e) {
                    return {
                        id: e.id,
                        meetingId: meeting.id,
                        slug: e.slug,
                        name: e.name,
                        startTime: e.startTime,
                        eventNumber: e.eventNumber,
                        eventClass: e.eventClass || null,
                        status: e.status || null,
                        distance: e.distance != null ? e.distance : null,
                        starters: null,
                        isResulted: !!e.isResulted,
                        isAbandoned: !!e.isAbandoned,
                        // racePrizeMoneyValue + racePrizeMoneyUnit is the race's prize money in
                        // its OWN native currency (e.g. 14756/"GBP" for a UK meeting) — see the
                        // matching comment in buildEvent() in ReadEmbeddedMeetingsAsync above for
                        // why, and ScrapeRaceAsync for where this gets overwritten with the true
                        // AUD figure once that race's own page has been scraped.
                        racePrizeMoney: e.racePrizeMoneyValue != null ? e.racePrizeMoneyValue : null,
                        racePrizeMoneyUnit: e.racePrizeMoneyUnit || null,
                        trackCondition: e.trackCondition || null,
                        weather: meeting.weather || null,
                        entryConditions: [],
                        prizeMoney: []
                    };
                }

                function buildMeeting(m) {
                    return {
                        id: m.id,
                        name: m.name,
                        slug: m.slug,
                        meetingDateUtc: m.meetingDateUtc,
                        meetingDateLocal: m.meetingDateLocal,
                        meetingType: m.meetingType || null,
                        meetingCategory: m.meetingCategory || null,
                        meetingStage: m.meetingStage || null,
                        tabStatus: m.tabStatus,
                        state: (m.venue && m.venue.state) || null,
                        trackComments: null,
                        isAbandoned: null,
                        venue: m.venue ? {
                            id: m.venue.id,
                            name: m.venue.name,
                            slug: m.venue.slug,
                            state: m.venue.state,
                            address: m.venue.address,
                            trackMapUrl: m.venue.trackMapUrl || null,
                            straight: m.venue.straight != null ? m.venue.straight : null,
                            straightUnit: m.venue.straightUnit || null,
                            circumference: m.venue.circumference != null ? m.venue.circumference : null,
                            circumferenceUnit: m.venue.circumferenceUnit || null,
                            weatherLastUpdated: m.venue.weatherLastUpdated || null,
                            isClockWise: m.venue.isClockWise != null ? m.venue.isClockWise : null,
                            country: m.venue.country ? {
                                id: m.venue.country.id,
                                name: m.venue.country.name,
                                iso2: m.venue.country.iso2,
                                iso3: m.venue.country.iso3
                            } : null
                        } : null,
                        events: (m['events({})'] || m.events || []).map(e => buildEvent(m, e))
                    };
                }

                const groupsMap = new Map();
                for (const m of resolved) {
                    const iso2 = m.venue && m.venue.country && m.venue.country.iso2;
                    const group = iso2 === 'AU' ? 'Australia' : 'International';
                    if (!groupsMap.has(group)) groupsMap.set(group, []);
                    groupsMap.get(group).push(buildMeeting(m));
                }

                const meetingsGrouped = Array.from(groupsMap.entries()).map(([group, meetings]) => ({ group, meetings }));
                return JSON.stringify({ data: { meetingsGrouped } });
            }
            """;

        try
        {
            await page.WaitForFunctionAsync(
                $$"""
                () => {
                    const before = new Set([{{keysBeforeClickJs}}]);
                    const app = document.getElementById('__nuxt') && document.getElementById('__nuxt').__vue_app__;
                    const nuxt = app && app.config.globalProperties.$nuxt;
                    const data = (nuxt && nuxt.payload && nuxt.payload.data) || {};
                    return Object.keys(data).some(k => !before.has(k) && data[k] && Array.isArray(data[k].meetings));
                }
                """,
                new PageWaitForFunctionOptions { Timeout = timeoutMs, PollingInterval = 500 });
        }
        catch (TimeoutException)
        {
            // Swallowed deliberately, same reasoning as WaitForNuxtAppAsync: the script below
            // still runs and reports a proper diagnostic instead of a bare timeout.
        }

        return await page.EvaluateAsync<string>(script);
    }

    /// <summary>
    /// Reads a race's runners/jockeys/trainers/form/stats out of Punters' Nuxt 3 apollo cache
    /// and normalizes it into our RaceDetail JSON shape, entirely inside the page's JS context.
    /// Only the single most-recent past run is available here (via selection.lastRun) — each
    /// runner's last 5+ starts come from a separate lazy-loaded query, filled in afterwards by
    /// <see cref="ScrapeFullFormsAsync"/> (see ScrapeRaceAsync), which needs each runner's raw
    /// selectionId (carried through in the output below) to line results back up. Stats ship as
    /// raw totalRuns/totalPlaces[]-style counters rather than pre-formatted ratio strings.
    /// </summary>
    private static async Task<string> ExtractRaceDetailAsync(IPage page)
    {
        const string script = """
            () => {
                function resolveNuxt3(val, cache, seen) {
                    if (val === null || val === undefined) return val;
                    if (Array.isArray(val)) return val.map(v => resolveNuxt3(v, cache, seen));
                    if (typeof val === 'object') {
                        if (val.__ref) {
                            if (seen.has(val.__ref)) return null;
                            const next = cache[val.__ref];
                            return next ? resolveNuxt3(next, cache, new Set(seen).add(val.__ref)) : null;
                        }
                        const out = {};
                        for (const k of Object.keys(val)) out[k] = resolveNuxt3(val[k], cache, seen);
                        return out;
                    }
                    return val;
                }

                function diagnostics() {
                    return `title="${document.title}" url="${location.href}" bodySnippet="${(document.body.innerText || '').replace(/\s+/g, ' ').trim().slice(0, 200)}"`;
                }

                const el = document.getElementById('__nuxt');
                const app = el && el.__vue_app__;
                const nuxt = app && app.config.globalProperties.$nuxt;
                const cache = nuxt && nuxt.payload && nuxt.payload.data && nuxt.payload.data['_apollo:default'];
                if (!cache) return JSON.stringify({ error: 'No embedded Nuxt3 apollo cache found on this page. ' + diagnostics() });

                const rootQuery = cache['ROOT_QUERY'];
                if (!rootQuery) return JSON.stringify({ error: 'No ROOT_QUERY in the embedded cache. ' + diagnostics() });

                const eventKey = Object.keys(rootQuery).find(k => k.startsWith('event('));
                const meetingKey = Object.keys(rootQuery).find(k => k.startsWith('meeting('));
                if (!eventKey) return JSON.stringify({ error: 'No event(...) root query found on this page. ' + diagnostics() });

                const event_ = resolveNuxt3(rootQuery[eventKey], cache, new Set());
                const meeting = meetingKey ? resolveNuxt3(rootQuery[meetingKey], cache, new Set()) : null;

                const selKey = Object.keys(event_).find(k => k.startsWith('selections('));
                const selections = selKey ? (event_[selKey] || []) : [];

                function slugify(s) {
                    return (s || '').toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/(^-|-$)/g, '');
                }

                // 1 furlong = 201.168m, 1 furlong = 220 yards, matching this scraper's own
                // samples/*-DataDump.json ground truth.
                function metersToDistanceInfo(m) {
                    if (m == null) return { distanceM: null, distanceF: null, distanceMi: null };
                    const furlongs = m / 201.168;
                    let wholeFurlongs = Math.floor(furlongs);
                    let yards = Math.round((furlongs - wholeFurlongs) * 220);
                    if (yards >= 220) { yards -= 220; wholeFurlongs += 1; }
                    return {
                        distanceM: m + 'm',
                        distanceF: furlongs.toFixed(1),
                        distanceMi: wholeFurlongs + 'f ' + yards + 'y'
                    };
                }

                function kgToWeightInfo(kg) {
                    if (kg == null) return { weightKg: null, weightImp: null, weightLbs: null };
                    const lbsTotal = Math.round(Number(kg) * 2.20462);
                    const stone = Math.floor(lbsTotal / 14);
                    const remainder = lbsTotal % 14;
                    return {
                        weightKg: String(kg),
                        weightImp: stone + '-' + remainder,
                        weightLbs: String(lbsTotal)
                    };
                }

                function ratio(runs, places) {
                    if (runs === null || runs === undefined) return null;
                    const p = places || [0, 0, 0];
                    return runs + ':' + p.join('-');
                }

                function pct(v) { return v != null ? (v + '%') : null; }
                function money(v) { return v != null ? ('$' + v) : null; }
                function str(v) { return v != null ? String(v) : null; }

                function mapPastRunFromLastRun(lr) {
                    if (!lr) return null;
                    return {
                        type: 'RACE',
                        finishPosition: lr.finishPosition ?? null,
                        starters: lr.eventStarters ?? null,
                        raceNumber: null,
                        course: lr.meetingName ?? null,
                        date: lr.meetingDate ?? null,
                        distance: lr.eventDistance != null ? (lr.eventDistance + 'm') : null,
                        raceName: lr.eventNameForm ?? null,
                        startingPrice: lr.startingWinPriceDecimal != null ? ('$' + lr.startingWinPriceDecimal) : null,
                        jockeyName: null,
                        winnerName: null,
                        secondName: null,
                        thirdName: null,
                        lengthsBehind: lr.margin != null ? (lr.margin + 'L') : null,
                        trackCondition: (lr.trackCondition != null && lr.trackConditionRating != null)
                            ? (lr.trackCondition + ' ' + lr.trackConditionRating) : (lr.trackCondition ?? null),
                        eventClass: null,
                        finishTime: lr.finishTime ?? null,
                        barrierPosition: lr.barrierRow ?? null,
                        runDetail: lr.stewardsReport ?? null,
                        raceResult: { horseName: null, position: lr.finishPosition ?? null }
                    };
                }

                function mapStats(st) {
                    if (!st) return { performanceStatistics: null, rawStats: null };

                    const winRangeStr = Array.isArray(st.winRange) && st.winRange.length
                        ? st.winRange.join('-') + 'm' : null;
                    const placePer = pct(st.placePercentage);
                    const jockeyWinPer = (st.runsByJockey != null && st.runsByJockey > 0 && st.placesByJockey)
                        ? pct(Math.round((st.placesByJockey[0] / st.runsByJockey) * 100)) : null;
                    const synthRuns = st.synthRun ?? st.runsBySynth ?? null;
                    const synthPlaces = st.synthPlaces ?? st.placesBySynth ?? null;

                    return {
                        performanceStatistics: {
                            career: ratio(st.totalRuns, st.totalPlaces),
                            winPer: pct(st.winPercentage),
                            placePer: placePer,
                            showPer: placePer,
                            last10Starts: st.lastTenFigure ?? null,
                            last12Months: ratio(st.lastYearRuns, st.lastYearPlaces),
                            season: ratio(st.currentSeasonRuns, st.currentSeasonPlaces),
                            track: ratio(st.runsByTrack, st.placesByTrack),
                            distance: ratio(st.runsByDistance, st.placesByDistance),
                            trackDistanceCombo: ratio(st.runsByDistTrack, st.placesByDistTrack),
                            wetConditions: ratio(st.wetRuns, st.wetPlaces),
                            prizeMoney: money(st.totalPrizeMoney),
                            avgPrizeMoney: money(st.averagePrizeMoney),
                            winRange: winRangeStr,
                            rating: str(st.rating),
                            jockeyWinPer: jockeyWinPer,
                            jockeyHorse: ratio(st.runsByTrainerJockey, st.placesByTrainerJockey),
                            firstUp: ratio(st.firstUpRuns, st.firstUpPlaces),
                            secondUp: ratio(st.secondUpStarts, st.secondUpPlaces),
                            firm: ratio(st.firmRuns, st.firmPlaces),
                            good: ratio(st.goodRuns, st.goodPlaces),
                            soft: ratio(st.softRuns, st.softPlaces),
                            heavy: ratio(st.heavyRuns, st.heavyPlaces),
                            synthetic: ratio(synthRuns, synthPlaces),
                            clockwise: ratio(st.clockwiseRuns, st.clockwisePlaces),
                            antiClockwise: ratio(st.aClockwiseRuns, st.aClockwisePlaces)
                        },
                        rawStats: {
                            rating: str(st.rating),
                            totalRuns: st.totalRuns ?? null,
                            totalPlaces: st.totalPlaces ?? null,
                            winPercentage: pct(st.winPercentage),
                            placePercentage: pct(st.placePercentage),
                            totalPrizeMoney: money(st.totalPrizeMoney),
                            averagePrizeMoney: money(st.averagePrizeMoney),
                            winRange: winRangeStr,
                            runsByJockey: st.runsByJockey ?? 0,
                            placesByJockey: st.placesByJockey ?? [0, 0, 0],
                            firstUpRuns: st.firstUpRuns ?? null,
                            firstUpPlaces: st.firstUpPlaces ?? null,
                            secondUpStarts: st.secondUpStarts ?? null,
                            thirdUpStarts: st.thirdUpStarts ?? null,
                            lastYearRuns: st.lastYearRuns ?? null,
                            runsByDistance: st.runsByDistance ?? null,
                            runsByTrack: st.runsByTrack ?? null,
                            runsByDistTrack: st.runsByDistTrack ?? null,
                            firmRuns: st.firmRuns ?? null,
                            goodRuns: st.goodRuns ?? null,
                            softRuns: st.softRuns ?? null,
                            runsByTurf: st.runsByTurf ?? null,
                            wetRuns: st.wetRuns ?? null,
                            heavyRuns: st.heavyRuns ?? null,
                            synthRun: synthRuns,
                            clockwiseRuns: st.clockwiseRuns ?? null,
                            currentSeasonRuns: st.currentSeasonRuns ?? null,
                            aClockwiseRuns: st.aClockwiseRuns ?? null,
                            secondUpPlaces: st.secondUpPlaces ?? null,
                            thirdUpPlaces: st.thirdUpPlaces ?? null,
                            lastYearPlaces: st.lastYearPlaces ?? null,
                            placesByDistance: st.placesByDistance ?? null,
                            placesByTrack: st.placesByTrack ?? null,
                            placesByDistTrack: st.placesByDistTrack ?? null,
                            firmPlaces: st.firmPlaces ?? null,
                            goodPlaces: st.goodPlaces ?? null,
                            softPlaces: st.softPlaces ?? null,
                            placesByTurf: st.placesByTurf ?? null,
                            wetPlaces: st.wetPlaces ?? null,
                            heavyPlaces: st.heavyPlaces ?? null,
                            synthPlaces: synthPlaces,
                            currentSeasonPlaces: st.currentSeasonPlaces ?? null,
                            clockwisePlaces: st.clockwisePlaces ?? null,
                            aClockwisePlaces: st.aClockwisePlaces ?? null
                        }
                    };
                }

                const runners = selections.map(sel => {
                    const c = sel.competitor || {};
                    const mapped = mapStats(sel.stats);
                    const pastRuns = sel.lastRun ? [mapPastRunFromLastRun(sel.lastRun)] : [];
                    const lastRunComment = sel.stats && sel.stats.lastRun ? String(sel.stats.lastRun).trim() : null;

                    // Bonus enrichment Punters carries (speed-map prediction, "Punters Edge"
                    // rating, quick-form indicators, price flucs) — stashed under pointers (an
                    // untyped object in the shared Runner DTO)
                    // rather than dropped, since it doesn't fit any existing typed field.
                    const pointers = (sel.quickForm || sel.predictorRatings || sel.prediction || sel.puntersEdge || sel.flucs) ? {
                        quickForm: sel.quickForm || null,
                        predictorRatings: sel.predictorRatings || null,
                        prediction: sel.prediction || null,
                        puntersEdge: sel.puntersEdge || null,
                        flucs: sel.flucs || null
                    } : null;

                    return {
                        runnerId: 'runner-' + (c.slug || slugify(c.name)),
                        // Raw selection id, only used C#-side to correlate this runner with its
                        // getFullFormsBySelectionIds response (see ScrapeFullFormsAsync) — not a
                        // field on the shared Runner DTO, so it's simply ignored on deserialize.
                        selectionId: sel.id != null ? String(sel.id) : null,
                        tabNumber: sel.competitorNumber != null ? String(sel.competitorNumber) : null,
                        runnerName: c.name || null,
                        age: c.age != null ? String(c.age) : null,
                        sex: c.sexShort || null,
                        colour: c.colour || null,
                        sire: c.sire || null,
                        dam: c.dam || null,
                        // Racing-industry country abbreviation shown after a horse's name (e.g.
                        // "(GB)", "(NZ)") to disambiguate horses of the same name. Lives on
                        // competitor.horseCountry (a sibling of competitor.country, which is just
                        // a plain string like "AUS") — that Country object only carries iso2/iso3
                        // here (no nested "horseCountry" subfield; that only exists on the
                        // meeting/venue's own Country, a separately-cached GraphQL selection).
                        horseCountry: (c.horseCountry && (c.horseCountry.iso3 || c.horseCountry.iso2)) || null,
                        draw: sel.barrierNumber || null,
                        barrierPosition: sel.barrierNumber || null,
                        comment: sel.comments || null,
                        silkColourText: sel.racingColours || c.racingColours || null,
                        silkImageUrl: sel.silkImageUrl || (c.imageUrl ? (c.imageUrl.startsWith('//') ? 'https:' + c.imageUrl : c.imageUrl) : null),
                        gearChanges: sel.gearChanges || null,
                        jockey: sel.jockey ? { id: sel.jockey.id, name: sel.jockey.name, slug: sel.jockey.slug } : null,
                        trainer: sel.trainer ? { id: sel.trainer.id, name: sel.trainer.name, slug: sel.trainer.slug } : null,
                        carryingWeight: kgToWeightInfo(sel.weight),
                        lastRun: pastRuns.length ? pastRuns[0].date : null,
                        lastRunComment: lastRunComment,
                        pastRuns: pastRuns,
                        performanceStatistics: mapped.performanceStatistics,
                        rawStats: mapped.rawStats,
                        pointers: pointers,
                        currentOdds: sel.startingPrice != null ? ('$' + sel.startingPrice) : null,
                        isScratched: sel.status === 'SCR' || sel.status === 'Scratched' || sel.status === 'SCRATCHED' || sel.status === 'WDN'
                    };
                });

                const out = {
                    meetingId: (meeting && meeting.id) || event_.meetingId || null,
                    meetingName: meeting && meeting.name ? meeting.name.toUpperCase() : null,
                    country: (meeting && meeting.venue && meeting.venue.country) ? meeting.venue.country.iso3 : null,
                    date: meeting ? meeting.meetingDateLocal : null,
                    raceId: event_.id,
                    slug: event_.slug,
                    raceNumber: event_.eventNumber,
                    raceDistance: metersToDistanceInfo(event_.distance),
                    runners: runners,
                    // event.racePrizeMoney is the AUD total Punters itself computes (the same
                    // figure shown in this race's own "Prize Money" popover) — distinct from
                    // event.racePrizeMoneyValue, which is the native-currency total used at the
                    // meeting-list level. event.prizeMoney[].value is the matching per-place AUD
                    // breakdown (position 1..N). Both are only populated once Punters has
                    // finished processing the race (see class doc comment).
                    racePrizeMoneyAud: event_.racePrizeMoney != null ? event_.racePrizeMoney : null,
                    prizeMoneyBreakdownAud: (event_.prizeMoney || []).map(p => ({
                        position: p.position != null ? String(p.position) : null,
                        value: p.value != null ? p.value : null
                    }))
                };

                return JSON.stringify(out);
            }
            """;

        return await page.EvaluateAsync<string>(script);
    }

    public async ValueTask DisposeAsync()
    {
        if (_context is not null) await _context.CloseAsync();
        if (_browser is not null) await _browser.CloseAsync();
        _playwright?.Dispose();
    }
}
