namespace PuntersScraper.Shared.Messaging;

/// <summary>
/// Identifies who published a <see cref="MeetingScrapedEvent"/>: the PC (Windows
/// <c>MachineGuid</c> + computer name), the Windows user, and the scraper build. Resolved once per
/// process by the front-end (see <c>PuntersScraper.App.Services.MachineIdentity</c>) and stamped
/// onto every event via <see cref="MeetingScrapedEvent.Create"/>. Any value may be null if it
/// couldn't be determined — identity is informational and must never block a publish.
/// </summary>
public sealed record ProducerIdentity(
    string? MachineGuid,
    string? MachineName,
    string? UserName,
    string? ApplicationVersion);
