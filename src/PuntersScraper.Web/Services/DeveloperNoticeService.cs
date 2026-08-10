namespace PuntersScraper.Web.Services;

/// <summary>
/// Holds the current developer notice (if any) for this whole deployment — a shared-settings
/// equivalent of the desktop App's per-machine dismissal tracking. Registered as a singleton;
/// <see cref="Changed"/> lets Razor components (see MainLayout) re-render when a new notice
/// arrives or one is dismissed.
/// </summary>
public sealed class DeveloperNoticeService
{
    public DeveloperNotice? Current { get; private set; }

    public event Action? Changed;

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        var notice = await DeveloperNoticeChecker.CheckAsync(cancellationToken);
        var settings = WebAppSettings.Load();
        var next = notice is not null && notice.Id != settings.LastSeenNoticeId ? notice : null;

        if (next?.Id == Current?.Id) return;
        Current = next;
        Changed?.Invoke();
    }

    /// <summary>Marks the current notice as seen so it won't show again until a new one (a
    /// different Id) is published.</summary>
    public void Dismiss()
    {
        if (Current is null) return;

        var settings = WebAppSettings.Load();
        settings.LastSeenNoticeId = Current.Id;
        settings.Save();

        Current = null;
        Changed?.Invoke();
    }
}
