using CommunityToolkit.Mvvm.ComponentModel;
using PuntersScraper.Shared.Models;

namespace PuntersScraper.App.ViewModels;

/// <summary>
/// A single meeting flattened for display in the results grid. Keeps a reference to the
/// underlying Discipline/Meeting so export and race-detail scraping can work directly off
/// what's shown in the grid.
/// </summary>
public sealed partial class MeetingRow : ObservableObject
{
    public Discipline DisciplineEnum { get; init; }
    public Meeting Meeting { get; init; } = null!;
    public string Group { get; init; } = "";

    public string Discipline => DisciplineEnum.Code();
    public string MeetingName => Meeting.Name ?? "";
    public string? State => Meeting.State;
    public string? Country => Meeting.Venue?.Country?.Iso3;
    public int RaceCount => Meeting.Events.Count;
    public string? MeetingStage => Meeting.MeetingStage;

    public string? FirstRaceLocalTime => Meeting.Events
        .Where(e => e.StartTime is not null)
        .OrderBy(e => e.StartTime)
        .FirstOrDefault()?.StartTime?.ToLocalTime().ToString("t");

    public string? TrackCondition => Meeting.Events
        .Where(e => e.StartTime is not null)
        .OrderBy(e => e.StartTime)
        .FirstOrDefault()?.TrackCondition?.Overall;

    /// <summary>How many of this meeting's races have full runner detail scraped, updated live as scraping progresses.</summary>
    [ObservableProperty]
    private int racesWithDetail;

    public static MeetingRow From(Discipline discipline, string group, Meeting meeting) => new()
    {
        DisciplineEnum = discipline,
        Meeting = meeting,
        Group = group
    };
}
