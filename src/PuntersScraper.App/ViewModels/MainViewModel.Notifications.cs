using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace PuntersScraper.App.ViewModels;

/// <summary>
/// In-app notifications. <see cref="Notify"/> shows a popup in the top-right of the window for
/// <see cref="ToastDuration"/>. If the user closes it, it goes to the notification list as already
/// read; if they don't, it moves to the list as unread when the time is up. The list is opened from
/// the bell in the header, and clicking a notification there shows its message in a popup again.
/// </summary>
public sealed partial class MainViewModel
{
    private static readonly TimeSpan ToastDuration = TimeSpan.FromSeconds(30);

    /// <summary>Popups so many at once is just noise — past this, the oldest one moves to the list.</summary>
    private const int MaxVisibleToasts = 4;

    /// <summary>Popups currently on screen.</summary>
    public ObservableCollection<AppNotification> ToastNotifications { get; } = new();

    /// <summary>The notification list behind the bell, newest first.</summary>
    public ObservableCollection<AppNotification> Notifications { get; } = new();

    [ObservableProperty]
    private bool isNotificationPanelOpen;

    public int UnreadNotificationCount => Notifications.Count(n => !n.IsRead);
    public string UnreadNotificationBadge => UnreadNotificationCount > 9 ? "9+" : UnreadNotificationCount.ToString();
    public bool HasUnreadNotifications => UnreadNotificationCount > 0;
    public bool HasNotifications => Notifications.Count > 0;

    private void RefreshNotificationState()
    {
        OnPropertyChanged(nameof(UnreadNotificationCount));
        OnPropertyChanged(nameof(UnreadNotificationBadge));
        OnPropertyChanged(nameof(HasUnreadNotifications));
        OnPropertyChanged(nameof(HasNotifications));
    }

    /// <summary>Shows a popup for 30 seconds. Safe to call from any thread.</summary>
    private void Notify(string title, string message)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null) return;

        if (!dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => Notify(title, message));
            return;
        }

        var notification = new AppNotification { Title = title, Message = message };
        var timer = new DispatcherTimer { Interval = ToastDuration };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            notification.Timer = null;
            // Still on screen = the user never closed it: it goes to the list as unread.
            if (ToastNotifications.Remove(notification)) AddToNotificationList(notification, read: false);
        };
        notification.Timer = timer;

        ToastNotifications.Add(notification);
        timer.Start();

        // Keep the screen readable: push the oldest popups into the list early.
        while (ToastNotifications.Count(t => !t.IsDetail) > MaxVisibleToasts)
        {
            var oldest = ToastNotifications.First(t => !t.IsDetail);
            RetireToast(oldest, read: false);
        }
    }

    private void AddToNotificationList(AppNotification notification, bool read)
    {
        notification.IsRead = read;
        Notifications.Insert(0, notification);
        RefreshNotificationState();
    }

    private void RetireToast(AppNotification notification, bool read)
    {
        notification.Timer?.Stop();
        notification.Timer = null;
        if (!ToastNotifications.Remove(notification) || notification.IsDetail) return;
        AddToNotificationList(notification, read);
    }

    /// <summary>The popup's close button. Closing is fine — the message still ends up in the list, as read.</summary>
    [RelayCommand]
    private void DismissToast(AppNotification? notification)
    {
        if (notification is null) return;
        RetireToast(notification, read: true);
    }

    /// <summary>Clicking a notification in the list: marks it read and shows its message in a popup
    /// that stays until closed.</summary>
    [RelayCommand]
    private void OpenNotification(AppNotification? notification)
    {
        if (notification is null) return;

        notification.IsRead = true;
        IsNotificationPanelOpen = false;
        RefreshNotificationState();

        ToastNotifications.Add(new AppNotification
        {
            Title = notification.Title,
            Message = notification.Message,
            Time = notification.Time,
            IsDetail = true,
            IsRead = true,
        });
    }

    [RelayCommand]
    private void ClearNotifications()
    {
        Notifications.Clear();
        RefreshNotificationState();
    }
}
