using PuntersScraper.Shared.Models;

namespace PuntersScraper.Shared.Messaging;

/// <summary>
/// v1 "meeting.scraped" event — the shared contract published by BOTH the desktop app
/// (<c>PuntersScraper.App</c>) and the web app (<c>PuntersScraper.Web</c>) into the same external
/// <c>td.scrapers.events</c> exchange, one event per finished meeting.
///
/// This type lives in <c>PuntersScraper.Shared</c> on purpose: App and Web must publish the
/// byte-identical schema, so neither project may define its own copy. Any drift between the two
/// would silently break downstream consumers. Do not change field names or shape without bumping
/// <see cref="SchemaVersion"/>.
///
/// Consumers should dedupe on the (<see cref="MeetingId"/>, <see cref="DisciplineCode"/>,
/// <see cref="MeetingDateLocal"/>) triple rather than on <see cref="EventId"/> — a meeting can be
/// legitimately rescraped (manual re-run, auto-scrape overlap) and will republish with a fresh
/// <see cref="EventId"/> each time.
/// </summary>
public sealed record MeetingScrapedEvent
{
    public int SchemaVersion { get; init; } = 1;
    public string EventId { get; init; } = Guid.NewGuid().ToString();
    public string EventType { get; init; } = "meeting.scraped";

    /// <summary>Identifies the scraper *project*, not the front-end — always "PuntersScraper" for
    /// both the desktop app and the web app. (The routing key's own lowercase source token, e.g.
    /// "punters", is a separate, configurable value — see the publisher.)</summary>
    public string Source { get; init; } = "PuntersScraper";

    public DateTimeOffset OccurredAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public int Priority { get; init; } = 5;

    /// <summary>Discipline name, e.g. "Horses" / "Greyhounds" / "Harness".</summary>
    public string? Discipline { get; init; }

    /// <summary>Single-letter discipline code from <see cref="DisciplineExtensions.Code"/> (T/G/H).</summary>
    public string? DisciplineCode { get; init; }

    public string? MeetingId { get; init; }
    public string? MeetingName { get; init; }

    /// <summary>File name of the meeting's exported JSON for this scrape (e.g.
    /// "TR-2026-09-09-14-30-05-meeting.json") — the same base name written locally and uploaded to
    /// S3 for this meeting, so a consumer can correlate the event with the exported file. Not the
    /// full S3 key: the bucket object is prefixed with the meeting slug (e.g.
    /// "birdsville-TR-...-meeting.json") under the configured S3 folder.</summary>
    public string? MeetingFileName { get; init; }

    public string? MeetingDateLocal { get; init; }
    public string? MeetingDateUtc { get; init; }
    public string? VenueState { get; init; }
    public string? VenueCountry { get; init; }
    public int RaceCount { get; init; }

    /// <summary>Shared by every meeting event from the same scrape run.</summary>
    public string? CorrelationId { get; init; }

    /// <summary>Builds an event from a scraped <paramref name="meeting"/> and its
    /// <paramref name="discipline"/>. <paramref name="correlationId"/> is scoped to one scrape run
    /// (every meeting in that run shares it); <paramref name="priority"/> is the configured default
    /// (5) — this contract never re-prioritizes after the fact.</summary>
    public static MeetingScrapedEvent Create(
        Discipline discipline, Meeting meeting, Guid correlationId, int priority = 5,
        string? meetingFileName = null) => new()
    {
        Priority = priority,
        Discipline = discipline.ToString(),
        DisciplineCode = discipline.Code(),
        MeetingId = meeting.Id,
        MeetingName = meeting.Name,
        MeetingFileName = meetingFileName,
        MeetingDateLocal = meeting.MeetingDateLocal,
        MeetingDateUtc = meeting.MeetingDateUtc,
        // Meeting.State carries the venue state (e.g. "VIC") in the scraped payload; fall back to
        // the nested Venue.State if the top-level one is blank.
        VenueState = string.IsNullOrWhiteSpace(meeting.State) ? meeting.Venue?.State : meeting.State,
        VenueCountry = meeting.Venue?.Country?.Iso3,
        RaceCount = meeting.Events.Count,
        CorrelationId = correlationId.ToString(),
    };
}
