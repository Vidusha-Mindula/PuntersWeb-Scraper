using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PuntersScraper.App.Services;

namespace PuntersScraper.App.ViewModels;

/// <summary>Backs the Bucket window: lists objects in the configured S3 bucket and deletes
/// selected ones. Reads <see cref="AppSettings"/> fresh on every refresh, so it always reflects
/// whatever bucket/keys are currently configured on the main window.</summary>
public sealed partial class BucketViewModel : ObservableObject
{
    public ObservableCollection<S3ObjectRow> Objects { get; } = new();

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private string statusText = "";

    [ObservableProperty]
    private string bucketName = "";

    public int SelectedCount => Objects.Count(o => o.IsSelected);
    public string DeleteSelectedLabel => $"Delete selected ({SelectedCount})";
    public bool CanDeleteSelected => SelectedCount > 0 && !IsBusy;

    public void NotifySelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(DeleteSelectedLabel));
        OnPropertyChanged(nameof(CanDeleteSelected));
    }

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanDeleteSelected));

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        StatusText = "Loading...";
        try
        {
            var settings = AppSettings.Load();
            BucketName = settings.S3BucketName;

            var items = await S3BucketService.ListObjectsAsync(settings);
            Objects.Clear();
            foreach (var item in items)
            {
                var row = S3ObjectRow.From(item);
                row.SelectionChanged += NotifySelectionChanged;
                Objects.Add(row);
            }
            StatusText = $"{Objects.Count} file(s).";
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to list bucket: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            NotifySelectionChanged();
        }
    }

    public async Task DeleteAsync(IReadOnlyList<string> keys)
    {
        if (keys.Count == 0) return;

        IsBusy = true;
        StatusText = $"Deleting {keys.Count} file(s)...";
        try
        {
            var settings = AppSettings.Load();
            var (deleted, errors) = await S3BucketService.DeleteObjectsAsync(settings, keys);

            foreach (var row in Objects.Where(o => keys.Contains(o.Key) && !errors.Any(e => e.StartsWith(o.Key + ":"))).ToList())
            {
                Objects.Remove(row);
            }

            StatusText = errors.Count > 0
                ? $"Deleted {deleted} file(s). {errors.Count} failed: {string.Join(" | ", errors)}"
                : $"Deleted {deleted} file(s).";
        }
        catch (Exception ex)
        {
            StatusText = $"Delete failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            NotifySelectionChanged();
        }
    }
}
