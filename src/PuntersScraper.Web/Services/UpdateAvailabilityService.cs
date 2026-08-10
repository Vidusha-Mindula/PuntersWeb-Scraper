namespace PuntersScraper.Web.Services;

/// <summary>
/// Holds whether a newer Web release is currently available on GitHub. Registered as a
/// singleton; <see cref="Changed"/> lets Razor components (see MainLayout) re-render when the
/// answer changes. Unlike <see cref="DeveloperNoticeService"/> there is no Dismiss — this stays
/// visible until an admin actually applies the update (deploy/update.ps1), same philosophy as
/// the desktop App's update banner.
/// </summary>
public sealed class UpdateAvailabilityService
{
    public UpdateAvailability? Current { get; private set; }

    public event Action? Changed;

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        var next = await UpdateAvailabilityChecker.CheckAsync(cancellationToken);
        if (next?.Version == Current?.Version) return;

        Current = next;
        Changed?.Invoke();
    }
}
