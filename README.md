# Punters Meetings Scraper

Scrapes the meetings/races list — and full runner/jockey/form detail per race — from
[punters.com.au](https://www.punters.com.au) for Horses (T), Greyhounds (G) and Harness (H),
for a chosen date. Ships as a reusable class library (`PuntersScraper.Core`) built on shared
DTOs/utilities (`PuntersScraper.Shared`), plus a WPF desktop UI (`PuntersScraper.App`) and a
Blazor web UI (`PuntersScraper.Web`) for scheduled/headless runs.

## How it works

Punters.com.au is a Nuxt 3 app with an `_apollo:default` cache, reachable via
`document.getElementById('__nuxt').__vue_app__.config.globalProperties.$nuxt.payload.data`.
Its API sits behind bot-detection/CORS restrictions that reject an independently injected
request, so the scraper doesn't construct its own GraphQL request at all — it drives the
site's real UI and reads back whatever it already resolved:

1. Launches a real Chromium browser via [Playwright](https://playwright.dev/dotnet/) (headed
   by default — see note below) and navigates to the discipline's form-guide page.
2. **For today's date**, the meetings list is already sitting in the page's embedded Nuxt
   cache — no extra request needed, just read it back out.
3. **For the next few days**, the page exposes date tabs ("Tomorrow", then named weekdays)
   that trigger the page's own client-side query when clicked; the scraper clicks the
   matching tab and captures the fresh payload that shows up in Nuxt's own data cache.
4. **Per-race detail** (runners, jockeys, trainers, past form, stats) is read from each race's
   own page, using the same embedded-cache approach.

**Prize money currency:** Punters' meeting-list query returns each race's prize money in its
own **native currency** (`racePrizeMoneyValue` + `racePrizeMoneyUnit`, e.g. GBP for a UK
meeting) — there's no separate always-AUD field at that level. Punters itself computes a true
AUD figure per race (the same numbers shown in that race's own "Prize Money" popover), exposed
only on the race's own page — so once a race's detail has been scraped (`ScrapeRaceAsync`/
`ScrapeRacesForMeetingAsync`), its `RacePrizeMoney`/`RacePrizeMoneyUnit`/`PrizeMoney` are
overwritten in place with that AUD total and per-place breakdown, matching what the downstream
ingester expects (always AUD). No external FX lookup is involved — until a race's detail is
scraped, its prize money stays in native currency.

**Why headed by default:** headless Chromium was observed getting hard-blocked (HTTP 403) on
the very first page load by bot-detection; a visible (headed) browser passed every time in
testing. `ScraperOptions.Headless` defaults to `false` accordingly — flip it to `true` if you
don't want to see the browser window, but expect it to be less reliable. When headed and
`HideWindow` is set (the default), the window is positioned off-screen so it never actually
appears or steals focus.

**Scope/limitation:** Punters' own UI only exposes a handful of named date tabs (Yesterday,
Today + a few days ahead) before folding everything else into date-picker navigation this
scraper doesn't automate. Requesting a date outside that window throws a clear
`PuntersScrapeException` rather than silently returning nothing. Each call scrapes a single
date for a single discipline.

## Prerequisites

- .NET 8 SDK
- Windows (the desktop UI project is WPF)

## First-time setup

```bash
dotnet restore
```

Playwright needs its own browser binaries downloaded once (separate from NuGet). Build the
app first so `Microsoft.Playwright.dll` and the generated `playwright.ps1` land in its output
folder, then run the install script from there:

```powershell
dotnet build src/PuntersScraper.App
pwsh src/PuntersScraper.App/bin/Debug/net8.0-windows/playwright.ps1 install chromium
```

(If you don't have `pwsh`, install PowerShell 7, or run the generated `playwright.ps1` under
Windows PowerShell instead.)

If disk space is tight on your system drive, both NuGet and Playwright's browser download
respect the `NUGET_PACKAGES` and `PLAYWRIGHT_BROWSERS_PATH` environment variables if you want
to redirect them elsewhere first.

## Running the desktop app

```bash
dotnet run --project src/PuntersScraper.App
```

Pick a date, tick the disciplines you want (optionally filter by country/course), click
**Scrape**, then **Export JSON...** to save one meeting-wise folder per meeting — each
containing a `{TR|GR|HR}-{date}-{time}-meeting.json` file plus a `R{n}-{timestamp}-DataDump.json`
per race with full runner detail. Auto-export and direct S3 upload can be configured in the
UI and are remembered across restarts.

## Using the library directly

```csharp
using PuntersScraper.Core.Scraping;
using PuntersScraper.Shared.Models;
using PuntersScraper.Shared.Scraping;

await using IPuntersScraperService scraper = new PuntersScraperService();
await scraper.InitializeAsync(new ScraperOptions { Headless = false });

var result = await scraper.ScrapeMeetingsAsync(
    Discipline.Horses,
    DateOnly.FromDateTime(DateTime.Today));

foreach (var group in result.MeetingsGrouped)
{
    Console.WriteLine(group.Group);
    foreach (var meeting in group.Meetings)
        Console.WriteLine($"  {meeting.Name} ({meeting.State}) - {meeting.Events.Count} races");
}
```

## Project layout

```
src/
  PuntersScraper.Shared/    Shared models/utilities (no UI, no scraper-specific logic)
    Models/                 Discipline, Meeting, RaceEvent, RaceDetail, ScrapeResult, ...
    Scraping/               ScraperOptions, SilkSvgDescriber
    Json/                   Shared System.Text.Json options
  PuntersScraper.Core/      Scraping engine (Playwright-driven)
    Scraping/               IPuntersScraperService / PuntersScraperService
  PuntersScraper.App/       WPF desktop UI (MVVM via CommunityToolkit.Mvvm)
  PuntersScraper.Web/       Blazor web UI, for scheduled/headless scrape sessions
samples/                    Hand-captured example output (meetings + per-race runner data)
```
