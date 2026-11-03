using CommunityToolkit.Mvvm.ComponentModel;
using System.Windows.Threading;

namespace PuntersScraper.App.ViewModels;

/// <summary>One message for the user — shown first as a popup, then kept in the notification list
/// (the bell at the top-right of the window).</summary>
public sealed partial class AppNotification : ObservableObject
{
    public string Title { get; init; } = "";
    public string Message { get; init; } = "";
    public DateTime Time { get; init; } = DateTime.Now;

    /// <summary>True for the popup opened by clicking a notification in the list: it stays until
    /// closed and is never added to the list again.</summary>
    public bool IsDetail { get; init; }

    public string TimeText => Time.ToString("HH:mm:ss");

    /// <summary>False while it's an unseen notification in the list (counts toward the bell's badge).</summary>
    [ObservableProperty]
    private bool isRead;

    /// <summary>Counts down the popup's time on screen; null once it has been closed or has expired.</summary>
    internal DispatcherTimer? Timer { get; set; }
}
