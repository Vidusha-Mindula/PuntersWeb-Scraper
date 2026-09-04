namespace PuntersScraper.Shared.Messaging;

/// <summary>
/// Publishes <see cref="MeetingScrapedEvent"/>s into the shared <c>td.scrapers.events</c> exchange.
/// Shared between App and Web — the concrete implementations differ (the WPF app has no DI or
/// ILogger; the web app is DI-hosted), but the contract is the same.
///
/// Implementations MUST be non-fatal: a publish failure, or an unreachable broker, must never
/// throw out of this method — it is a parallel notification path and must never fail, cancel, or
/// delay a scrape.
/// </summary>
public interface IMeetingEventPublisher
{
    Task PublishMeetingScrapedAsync(MeetingScrapedEvent evt, CancellationToken ct = default);
}
