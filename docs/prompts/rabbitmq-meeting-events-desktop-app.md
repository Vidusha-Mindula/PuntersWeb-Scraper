# Task for Claude Code: Publish "meeting scraped" events to RabbitMQ — Desktop App (PuntersScraper.App)

Companion to `docs/prompts/rabbitmq-meeting-events.md`, which wires the same event into `PuntersScraper.Web`. This does the equivalent for the WPF desktop app (`PuntersScraper.App`), which has its own independent scrape pipeline and its own S3-upload hook, not shared code with the Web project. Read `src/PuntersScraper.App/ViewModels/MainViewModel.cs` (especially `ScrapeDatesAsync`, `UploadMeetingToS3Async`, `AutoScrapeTickAsync`) and `src/PuntersScraper.App/AppSettings.cs` before starting, and confirm line numbers/structure against the current code — it may have moved since this was written.

## Context

`PuntersScraper.App` scrapes the same way `PuntersScraper.Web` does (same `PuntersScraper.Core` engine) but is a single-user WPF desktop app with no ASP.NET Core, no DI container, and no `ILogger` — services are plain classes wired up manually (see `AppSettings.Load()` and the static `S3JsonUploader` helper). Its scrape flow lives in `MainViewModel.ScrapeDatesAsync`, invoked from both the manual "Scrape" button and `AutoScrapeTickAsync` (a `DispatcherTimer` tick, since this app has no ASP.NET Core hosted services). Per meeting, once its races are done, it already calls `UploadMeetingToS3Async` — that is this task's hook point, exactly mirroring how the Web task hooks `ScrapeSessionService`.

One local quirk that matters here: `AppSettings` persists to `%LocalAppData%\PuntersScraper\settings.json`, and the installer **overwrites this file with baked-in defaults on every version update** (see the doc comment on `AppSettings`) — S3 credentials already live with that trade-off (wiped/reset on update unless re-entered), and RabbitMQ credentials will behave identically. That's an existing characteristic of this app, not something to fix as part of this task.

## Objective

Publish the exact same `meeting.scraped` event, into the exact same shared queue, as the Web task — one event per finished meeting, always at priority 5 — from this app's own scrape flow (manual scrape and the auto-scrape timer both go through `ScrapeDatesAsync`, so hooking that one method covers both).

## Dependency: reuse the shared contract, don't fork it

- If `src/PuntersScraper.Shared/Messaging/MeetingScrapedEvent.cs` and `IMeetingEventPublisher.cs` don't exist yet in this repo (i.e. the companion Web task hasn't been run), create them first, using the schema below. `PuntersScraper.Shared` has no other dependencies, so this is safe to add from either task.
- If they already exist, reuse them exactly as-is. **App and Web must never each define their own copy of this contract** — they publish into the same external queue, and any drift between the two would silently break consumers.

## Message schema (v1) — identical to the Web task, do not diverge

```json
{
  "schemaVersion": 1,
  "eventId": "3f1c9a3e-...-guid",
  "eventType": "meeting.scraped",
  "source": "PuntersScraper",
  "occurredAtUtc": "2026-09-04T05:40:12Z",
  "priority": 5,
  "discipline": "Horses",
  "disciplineCode": "T",
  "meetingId": "12345",
  "meetingName": "Flemington",
  "meetingFileName": "flemington-20260905-TR-2026-09-05-11-42-50-meeting.json",
  "meetingSlug": "flemington-20260905",
  "meetingDateLocal": "2026-09-05",
  "meetingDateUtc": "2026-09-04T14:00:00Z",
  "venueState": "VIC",
  "venueCountry": "AUS",
  "raceCount": 8,
  "correlationId": "guid shared by every meeting from the same scrape run",
  "machineGuid": "0b5c4a2e-1f3d-4e6a-9b8c-7d2e1f0a3b4c",
  "machineName": "RACE-PC-01",
  "userName": "scraper",
  "applicationVersion": "3.18.0"
}
```

**Producer identity fields** (added to v1 additively — optional, `null` if the producer couldn't determine them; every other field is unchanged, so `schemaVersion` stays `1`). They identify which PC, Windows user and scraper build published the event, and are populated automatically by the desktop app (`PuntersScraper.App.Services.MachineIdentity`) — no configuration or machine-registration service involved:

| Field | Source |
|---|---|
| `machineGuid` | Windows `MachineGuid` from `HKLM\SOFTWARE\Microsoft\Cryptography` (64-bit registry view). Stable per Windows install — group/identify machines by this, not by name. |
| `machineName` | Windows computer name (`Environment.MachineName`). |
| `userName` | Windows user name running the app (`Environment.UserName`, no domain). |
| `applicationVersion` | The scraper build, e.g. `3.18.0` (`dev` for an unversioned local build). |

`source` stays `"PuntersScraper"` for both App and Web — it identifies the *scraper project*, not the front-end. Don't add a field to distinguish desktop vs. web unless you decide it's genuinely needed (see "Open questions").

## Architecture decisions specific to this project

1. **No DI container, no hosted service.** Implement `src/PuntersScraper.App/Services/RabbitMqMeetingEventPublisher.cs`, a plain class owning one recoverable `IConnection` (`AutomaticRecoveryEnabled` on), created lazily on first publish. Instantiate one instance as a field on `MainViewModel`, next to `_settings`, and pass it into `ScrapeDatesAsync`'s call site — don't build a service locator or container for this one class.
2. **Disposal.** Implement `IAsyncDisposable` on the publisher and dispose it from `App.xaml.cs` (add an `OnExit` override if one doesn't already exist). Bound the dispose/close with a short timeout so app shutdown never hangs waiting on an unreachable broker.
3. **No `ILogger` here.** Report outcomes the same way S3 failures already are — through `StatusText`/the `progress` callback the UI reads, not a log file or console.
4. **Same non-fatal rule as the Web task:** a publish failure, or the broker being unreachable, must never fail, cancel, or delay a scrape. Same topology split (only assert the exchange, never the queue or `x-max-priority` — that's the priority-control project's queue to own). Always publish at the configured default priority (5); this app never changes a message's priority after the fact.
5. **Config:** extend `AppSettings.cs` with a `RabbitMq`-prefixed settings block — `RabbitMqEnabled` (default `false`), `RabbitMqHostName`, `RabbitMqPort`, `RabbitMqVirtualHost`, `RabbitMqUserName`, `RabbitMqPassword`, `RabbitMqExchangeName` (default `td.scrapers.events`), `RabbitMqRoutingKeyTemplate` (default `{source}.meeting.scraped.{disciplineCode}`), `RabbitMqSource` (default `punters`), `RabbitMqDefaultPriority` (default `5`) — flat properties on the class, matching this file's existing style (it doesn't use nested config sections), persisted via the existing `Load()`/`Save()`.
6. **Hook point** — in `ScrapeDatesAsync`, immediately after the existing `UploadMeetingToS3Async(...)` call for each meeting: build the event from that `Meeting`/`Discipline` and publish it. Track `totalEventsPublishedCount`/`totalEventsFailedCount` the same way `totalS3UploadedCount`/`totalS3FailedCount` already are, and fold them into the final `StatusText` summary the same way the S3 counts are today.
7. **Don't double-publish.** `ExportMeetingAsync`/`WriteAndMaybeUploadAsync` (used by the manual "Export JSON..." button, a separate code path from the live per-meeting scrape loop) must **not** also publish — only the live per-meeting path inside `ScrapeDatesAsync` does. A meeting exported after the fact from already-scraped results shouldn't re-fire the event.
8. **UI toggle (optional, keep it minimal):** if you add a visible checkbox, put "Also publish to RabbitMQ" next to the existing "Also upload to S3" one on the Scraper tab, bound to a new observable property mirroring `UploadToS3`'s existing wiring. Not required for this to be safe to ship — `RabbitMqEnabled` in settings is sufficient; treat the checkbox as polish, not a blocker.

## csproj

- Add `RabbitMQ.Client` (latest stable 7.x — check NuGet for the current version, don't assume) to `PuntersScraper.App.csproj`. It already references `PuntersScraper.Shared`, so the shared contract types are available with no new project reference.
- Polly is optional here — this app's publish volume is low and it has no other resilience-library dependency today. A hand-rolled 2-attempt retry with a short delay is fine. If the Web task already pulled in Polly, you don't need to match that choice here; keep this project's dependency footprint as small as it already is unless there's a real reason not to.

## Testing / verification

- Use the same local RabbitMQ container the Web task adds to the repo's `docker-compose.yml` (`docker compose up rabbitmq`) to test against.
- Manual: set the `RabbitMq*` fields in `%LocalAppData%\PuntersScraper\settings.json` to point at it with `RabbitMqEnabled: true`, run a scrape from the UI, bind a temporary queue to `td.scrapers.events` with routing key `#` in the RabbitMQ management UI (`http://localhost:15672`), and confirm messages arrive with the right body and `Priority`.
- Confirm the app exits cleanly (no hang, no crash) while a RabbitMQ connection is open, and that a scrape still completes normally with the broker unreachable — it should behave exactly as it does today except for the extra status-text counts.

## Definition of done

- Shared `MeetingScrapedEvent`/`IMeetingEventPublisher` exist in `PuntersScraper.Shared` and are reused, not redefined, here.
- A scrape run (manual button and, if you can trigger it, the auto-scrape timer) with RabbitMQ enabled and a temporary bound queue shows one correctly-shaped, priority-5 message per meeting.
- A scrape run with RabbitMQ enabled but the broker down completes successfully and reports the publish failures in `StatusText`.
- A scrape run with RabbitMQ disabled (the default) behaves exactly as it does today.
- The app exits cleanly whether or not a RabbitMQ connection is open.

## Open questions — surface these back to me, don't silently guess

- Same exchange/routing-key naming and production-credential-source questions as the Web task apply here too — don't re-decide them independently; use whatever was confirmed there, since both projects publish into the same broker.
- Whether it's worth tagging events with which front-end produced them (an optional `producer: "web" | "desktop"` field) for observability, given both App and Web can independently scrape the same meeting on the same machine/network — flagging this as a nice-to-have, not adding it unilaterally, since it changes the shared schema both projects depend on. *(Resolved: events now carry the producer identity fields `machineGuid`, `machineName`, `userName` and `applicationVersion` — see the schema above — which identify the publishing PC, user and build.)*
