using CommunityToolkit.Mvvm.ComponentModel;

namespace PuntersScraper.App.ViewModels;

/// <summary>One editable row in the Auto Scraper tab's slot list — the UI-bound mirror of
/// <see cref="AutoScrapeSlot"/>, same "row wraps its own state and reports changes" shape as
/// <see cref="CountryOption"/>/<see cref="S3ObjectRow"/>.</summary>
public sealed partial class AutoScrapeSlotRow : ObservableObject
{
    [ObservableProperty]
    private string time = "06:00";

    [ObservableProperty]
    private string countryScope = "All";

    [ObservableProperty]
    private bool includeToday = true;

    [ObservableProperty]
    private bool includeTomorrow = true;

    [ObservableProperty]
    private bool includeDayAfterTomorrow = true;

    [ObservableProperty]
    private bool horses = true;

    [ObservableProperty]
    private bool greyhounds = true;

    [ObservableProperty]
    private bool harness = true;

    /// <summary>Bound as the country-scope ComboBox's ItemsSource — an instance property (not
    /// static) purely so it's reachable from the DataTemplate without a RelativeSource lookup.</summary>
    public string[] CountryScopeOptions { get; } = { "All", "Australia", "International" };

    /// <summary>Fires whenever any field changes, so MainViewModel can persist the whole slot list
    /// without polling every row.</summary>
    public event Action? Changed;

    partial void OnTimeChanged(string value) => Changed?.Invoke();
    partial void OnCountryScopeChanged(string value) => Changed?.Invoke();
    partial void OnIncludeTodayChanged(bool value) => Changed?.Invoke();
    partial void OnIncludeTomorrowChanged(bool value) => Changed?.Invoke();
    partial void OnIncludeDayAfterTomorrowChanged(bool value) => Changed?.Invoke();
    partial void OnHorsesChanged(bool value) => Changed?.Invoke();
    partial void OnGreyhoundsChanged(bool value) => Changed?.Invoke();
    partial void OnHarnessChanged(bool value) => Changed?.Invoke();

    public static AutoScrapeSlotRow From(AutoScrapeSlot slot) => new()
    {
        Time = slot.Time,
        CountryScope = slot.CountryScope,
        IncludeToday = slot.IncludeToday,
        IncludeTomorrow = slot.IncludeTomorrow,
        IncludeDayAfterTomorrow = slot.IncludeDayAfterTomorrow,
        Horses = slot.Horses,
        Greyhounds = slot.Greyhounds,
        Harness = slot.Harness,
    };

    public AutoScrapeSlot ToSlot() => new()
    {
        Time = Time,
        CountryScope = CountryScope,
        IncludeToday = IncludeToday,
        IncludeTomorrow = IncludeTomorrow,
        IncludeDayAfterTomorrow = IncludeDayAfterTomorrow,
        Horses = Horses,
        Greyhounds = Greyhounds,
        Harness = Harness,
    };
}
