# Task for Claude Code: Publish "meeting scraped" events to RabbitMQ

Companion file: `docs/prompts/rabbitmq-meeting-events-desktop-app.md` does the equivalent for the WPF desktop app (`PuntersScraper.App`), reusing the same shared event contract this task creates.

Paste this whole file as your prompt (`claude "$(cat docs/prompts/rabbitmq-meeting-events.md)"`, or just open it and paste the content in). Work inside this repo (`PuntersWeb-Scraper`). Read `README.md` and `src/PuntersScraper.Web/Services/ScrapeSessionService.cs` first — the hook point and existing conventions are described below, but confirm them against the current code before touching anything.

## Context

`PuntersScraper.Web` (Blazor Server, .NET 8) scrapes race meetings from punters.com.au and, per meeting, currently:

1. Uploads the meeting's exported JSON to S3 (`ScrapeSessionService.UploadMeetingToS3Async`), which a downstream service (`TroyenRaceIngestor`) picks up.
2. Optionally exports the same JSON to a local folder.

Both happen inside `ScrapeSessionService.ScrapeAsync`, once per meeting, right after that meeting's races have all been scraped (see the loop with `UploadMeetingToS3Async` / `ExportMeetingToFolderAsync`). Both are used by the manual "Scraper" page and by `AutoScrapeHostedService` (scheduled/unattended runs).

We're adding a second, independent notification path: publish a small **event** to RabbitMQ for every meeting as soon as it's ready, so downstream systems can react immediately instead of only ever discovering new data by polling S3. This queue is **shared infrastructure** — other, separately-deployed scraper projects (e.g. `NedsScraper`) will publish the same kind of event to it, and a separate project is responsible for controlling message **priority** in that queue. This repo's job is only to **publish**, always at **normal priority (5 on a 0–10 scale)** — it does not consume anything and does not implement priority logic itself.

## Objective

After each meeting finishes scraping, publish one `meeting.scraped` event containing at minimum the **meeting name, date, and discipline**, plus enough supporting context for a shared, multi-producer queue to be usable (see schema below). This is additive — do not change or remove the existing S3/export behavior.

## Non-goals (do not implement these)

- No consumer/subscriber code — this repo only publishes.
- No priority-adjustment logic — always publish at the configured default priority (5). A different project owns re-prioritization.
- No replacement of the S3 upload — it stays as-is; the RabbitMQ event is a parallel, lightweight notification, not a payload carrier.
- No new database/queue-of-record for outbox/dedup — keep this simple; note future hardening ideas at the bottom instead of building them now.

## Architecture decisions (already made — implement per these, don't re-derive)

1. **Client library:** the official `RabbitMQ.Client` package, latest stable **7.x** (async-first `IConnection`/`IChannel` API — not the old synchronous `IModel` API). Check NuGet for the actual current version before pinning it in the `.csproj`; don't guess a version number. No MassTransit/NServiceBus here — this service only ever publishes, and the queue is shared across independently-versioned repos, so a plain, explicitly-versioned JSON contract is the right level of coupling (a shared bus library would force every producer repo onto the same major version).
2. **Topology ownership is split, and that matters:**
   - This repo declares (idempotently, `durable: true`) only the **topic exchange**. It never declares, binds, or asserts arguments on the **queue** — the queue (including the `x-max-priority` argument that priority requires) is owned by the consuming/priority-control project. Don't add `x-max-priority` anywhere in this codebase.
   - Publish with `mandatory: true` and handle `BasicReturn` (log a warning, not an error, when nothing is bound yet — that's an expected state before the consumer side exists or during its rollout, not a bug in this service).
3. **Config:** an Options-pattern class bound from a `RabbitMq` configuration section, overridable via environment variables (`RabbitMq__HostName`, etc.) — mirror the fail-fast style of `AdminCredentialsOptions` in `Program.cs`, but add an `Enabled` flag defaulting to `false` so existing deployments keep working unmodified until someone turns this on with real connection details. When `Enabled` is `false`, resolve a no-op publisher so the rest of the code never needs to branch on whether messaging is on.
4. **Reliability, and how failures must behave:** one long-lived, auto-recovering `IConnection` (own it in a singleton/hosted-service, `AutomaticRecoveryEnabled` + `TopologyRecoveryEnabled` on), a fresh short-lived `IChannel` per publish (channels aren't thread-safe to share; they're cheap to open/close at this volume — a handful of meetings per scrape run). Use publisher confirms. Wrap the whole publish (including retries) so **a publish failure must never fail, cancel, or delay the scrape** — same non-fatal treatment the existing S3 upload failures already get (tracked as a count, surfaced in the status text, scrape continues). Add a couple of retries with backoff (Polly) for transient connection issues only; after that, log and move on.
5. **Priority:** always set `BasicProperties.Priority` to a configurable `DefaultPriority` (default `5`) on the 0–10 scale the team has agreed on. This service never sets any other value and never changes a message's priority after publishing — that's entirely the other project's responsibility.
6. **Idempotency key for consumers:** don't rely on a random event id for dedup. Include the meeting's own stable `meetingId` + `disciplineCode` + `meetingDateLocal` in the payload and call out in a code comment that consumers should dedupe on that triple, since a meeting can legitimately be rescraped (manual re-run, auto-scrape overlap) and will republish.
7. **Naming (defaults — flag these as needing sign-off, see "Open questions" below):**
   - Exchange: `td.scrapers.events` (topic, durable).
   - Routing key: `{source}.meeting.scraped.{disciplineCode}`, e.g. `punters.meeting.scraped.t`, lowercase throughout, `source` = `punters` for this repo.
   - Documented (not declared here) suggested consumer queue name: `td.race-ingestion.meeting-scraped`.
   - Make all of these configurable — don't hardcode strings anywhere except as config defaults — so ops can point this at whatever the priority-control project actually expects without a code change.

## Message schema (v1, JSON, camelCase)

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

Map `discipline`/`disciplineCode` from the existing `Discipline` enum and its `Code()` extension in `PuntersScraper.Shared/Models/Discipline.cs` — don't invent a second code scheme. Pull the rest from the `Meeting`/`MeetingRow` already available at the hook point.

RabbitMQ message properties (`BasicProperties`): `ContentType = "application/json"`, `DeliveryMode = Persistent`, `Priority = <same value as the payload>`, `MessageId = eventId`, `Type = eventType`, `AppId = source`, `Timestamp = now`.

## Implementation checklist

1. `src/PuntersScraper.Shared/Messaging/MeetingScrapedEvent.cs` — the record above, plus a static factory that builds one from a `Discipline`, `Meeting`, and a run-scoped correlation id.
2. `src/PuntersScraper.Shared/Messaging/IMeetingEventPublisher.cs` — `Task PublishMeetingScrapedAsync(MeetingScrapedEvent evt, CancellationToken ct = default)`.
3. `src/PuntersScraper.Web/Messaging/RabbitMqOptions.cs` — bound from `"RabbitMq"`: `Enabled`, `HostName`, `Port`, `VirtualHost`, `UserName`, `Password`, `ExchangeName` (default `td.scrapers.events`), `RoutingKeyTemplate` (default `{source}.meeting.scraped.{disciplineCode}`), `Source` (default `punters`), `DefaultPriority` (default `5`).
4. `src/PuntersScraper.Web/Messaging/RabbitMqConnectionManager.cs` — singleton (or `IHostedService`) owning the one recoverable `IConnection`; exposes `Task<IChannel> CreateChannelAsync()`.
5. `src/PuntersScraper.Web/Messaging/RabbitMqMeetingEventPublisher.cs` — implements `IMeetingEventPublisher`: asserts the exchange, serializes with `System.Text.Json` (camelCase), publishes with confirms + `mandatory: true` + a `BasicReturn` handler, retries transient failures via Polly, logs structured success/failure via `ILogger<RabbitMqMeetingEventPublisher>`, and **never throws out of `PublishMeetingScrapedAsync`** — swallow after retries are exhausted, log at `Warning`/`Error`, return.
6. `src/PuntersScraper.Web/Messaging/NullMeetingEventPublisher.cs` — no-op, registered when `RabbitMq:Enabled` is `false`.
7. `Program.cs` — bind `RabbitMqOptions`, register the connection manager and the correct publisher implementation based on `Enabled` (same fail-fast-on-missing-config idea as the existing `AdminCredentialsOptions` block, but only when `Enabled` is `true` — don't require RabbitMQ config on deployments that haven't opted in).
8. `ScrapeSessionService.ScrapeAsync` — inject `IMeetingEventPublisher`. Immediately after the existing `UploadMeetingToS3Async` call for each meeting (same place, same per-meeting granularity), build the event and publish it. Wrap in try/catch exactly like the surrounding code already treats S3 failures: report via `progress.Report(...)`, track `totalEventsPublished`/`totalEventsFailed`, and fold those counts into the final `StatusText` summary alongside the existing S3/export counts. A publish failure must not stop the loop or fail the scrape.
9. `appsettings.json` — add a `RabbitMq` section with `Enabled: false` and the documented defaults; put real `guest`/`guest`-style local-dev values only in `appsettings.Development.json`, never committed credentials for anything beyond local dev.
10. `docker-compose.yml` — add a `rabbitmq` service (latest stable `rabbitmq:<version>-management` image — check what's current rather than assuming) for local dev/testing, with a healthcheck and the management UI port (15672) exposed, so the queue can be inspected while testing.
11. `src/PuntersScraper.Web/PuntersScraper.Web.csproj` — add `RabbitMQ.Client` and a Polly package (`Polly.Core` or `Microsoft.Extensions.Http.Resilience`, whichever fits a non-HTTP retry more cleanly) at their current latest stable versions.
12. `README.md` — short new section documenting the event contract, the config keys, and the exchange/queue ownership split, so the next person (or the other scraper repos) can implement a compatible producer/consumer without reading the code.

## Testing / verification

- Unit test `MeetingScrapedEvent`'s factory mapping (discipline code, dates, etc.) against a hand-built `Meeting`.
- Manual end-to-end: `docker compose up rabbitmq`, set `RabbitMq:Enabled=true` in `appsettings.Development.json` pointing at it, run a scrape from the UI, and in the management UI (`http://localhost:15672`) bind a temporary queue to `td.scrapers.events` with routing key `#` to confirm messages arrive with the right body, `Priority`, and content type.
- Confirm the scrape still completes normally with `RabbitMq:Enabled=false` (default/untouched deployments) and, separately, with `Enabled=true` but the broker unreachable — the scrape must finish and report the failure count, not crash or hang.

## Definition of done

- All items in the implementation checklist are done and build cleanly (`dotnet build`).
- A scrape run with RabbitMQ enabled and a temporary bound queue shows one correctly-shaped, priority-5 message per meeting.
- A scrape run with RabbitMQ enabled but the broker down completes successfully and reports the publish failures in the status text.
- A scrape run with RabbitMQ disabled behaves exactly as it does today (no behavior change, no new required config).
- README updated.

## Open questions — surface these back to me, don't silently guess

- Whether `td.scrapers.events` / the routing-key template above actually match what the priority-control project (and any already-existing scraper producers) expect — I'm proposing sensible defaults, but they need sign-off before this runs against the shared/production broker.
- Where connection credentials should come from in shared environments (Docker secrets, GitHub Actions/Rundeck-injected env vars, etc.) — local dev can use `guest`/`guest`, production cannot.
- Whether a future shared `Td.Scrapers.Contracts` NuGet package (carrying `MeetingScrapedEvent` + the naming constants) is worth extracting now — my recommendation is **not yet**: wait until a second scraper repo actually implements this (rule of three), so the contract isn't frozen prematurely; document it as a follow-up instead.
