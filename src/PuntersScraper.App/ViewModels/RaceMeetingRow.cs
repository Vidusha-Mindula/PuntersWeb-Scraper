using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using PuntersScraper.Shared.Models;

namespace PuntersScraper.App.ViewModels;

/// <summary>One meeting on the Race by Race tab: its header (with a select-all checkbox) and an
/// expandable list of its races, each individually selectable.</summary>
public sealed partial class RaceMeetingRow : ObservableObject
{
    public Discipline DisciplineEnum { get; init; }
    public Meeting Meeting { get; init; } = null!;
    public string Group { get; init; } = "";
    public DateOnly Date { get; init; }

    public string Discipline => DisciplineEnum.Code();
    public string MeetingName => Meeting.Name ?? "";
    public string? State => Meeting.State;
    public string? Country => Meeting.Venue?.Country?.Iso3;
    public int RaceCount => Races.Count;

    public ObservableCollection<RaceSelectionRow> Races { get; } = new();

    [ObservableProperty]
    private bool isExpanded;

    public int SelectedCount => Races.Count(r => r.IsSelected);

    public string SelectedSummary => $"{SelectedCount} of {RaceCount} selected";

    /// <summary>Header checkbox (three-state): checked = every race selected, indeterminate = some.
    /// Setting true selects every race; anything else (a click on a checked or mixed box) clears
    /// them all.</summary>
    public bool? AllSelected
    {
        get
        {
            var selected = SelectedCount;
            if (selected == 0) return false;
            return selected == Races.Count ? true : null;
        }
        set
        {
            var select = value == true;
            foreach (var race in Races) race.IsSelected = select;
        }
    }

    /// <summary>Fires whenever any race's selection changes.</summary>
    public event Action? SelectionChanged;

    public static RaceMeetingRow From(Discipline discipline, string group, Meeting meeting, DateOnly date)
    {
        var row = new RaceMeetingRow
        {
            DisciplineEnum = discipline,
            Meeting = meeting,
            Group = group,
            Date = date
        };

        foreach (var e in meeting.Events.OrderBy(e => e.EventNumber))
        {
            var race = new RaceSelectionRow { Event = e };
            race.SelectionChanged += row.OnRaceSelectionChanged;
            row.Races.Add(race);
        }

        return row;
    }

    private void OnRaceSelectionChanged()
    {
        OnPropertyChanged(nameof(AllSelected));
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectedSummary));
        SelectionChanged?.Invoke();
    }
}
