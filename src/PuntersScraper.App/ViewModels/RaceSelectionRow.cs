using CommunityToolkit.Mvvm.ComponentModel;
using PuntersScraper.Shared.Models;

namespace PuntersScraper.App.ViewModels;

/// <summary>One race inside a <see cref="RaceMeetingRow"/> on the Race by Race tab — a checkbox plus
/// a live status for the download.</summary>
public sealed partial class RaceSelectionRow : ObservableObject
{
    public RaceEvent Event { get; init; } = null!;

    public int RaceNumber => Event.EventNumber;
    public string RaceName => Event.Name ?? "";
    public int? Distance => Event.Distance;
    public string? Class => Event.EventClass;

    public string StartTimeLocal => Event.StartTime?.ToLocalTime().ToString("t") ?? "";

    [ObservableProperty]
    private bool isSelected;

    /// <summary>"" before a download, then "Downloading…", "Done" or "Failed: …".</summary>
    [ObservableProperty]
    private string status = "";

    /// <summary>Fires whenever <see cref="IsSelected"/> changes, so the parent meeting and the view
    /// model can recompute their counts without polling every race.</summary>
    public event Action? SelectionChanged;

    partial void OnIsSelectedChanged(bool value) => SelectionChanged?.Invoke();
}
