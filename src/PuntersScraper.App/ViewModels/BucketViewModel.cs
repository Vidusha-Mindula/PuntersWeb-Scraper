using System.Collections.ObjectModel;
using System.IO;
using Amazon.S3;
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

    /// <summary>Amazon's SDK exception carries the actual S3 error code and request id, which
    /// tell apart otherwise-identical-looking failures (e.g. a true bucket-policy AccessDenied vs.
    /// RequestTimeTooSkewed from a wrong system clock vs. SignatureDoesNotMatch) - plain
    /// ex.Message alone often doesn't distinguish these, which matters since "same credentials
    /// work on other machines" points at something machine/network-specific rather than config.</summary>
    private static string Describe(Exception ex) => ex is AmazonS3Exception s3
        ? $"{s3.Message} (ErrorCode={s3.ErrorCode}, RequestId={s3.RequestId}, HttpStatus={(int)s3.StatusCode})"
        : ex.Message;

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var row in Objects) row.IsSelected = true;
    }

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var row in Objects) row.IsSelected = false;
    }

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
            StatusText = $"Failed to list bucket: {Describe(ex)}";
        }
        finally
        {
            IsBusy = false;
            NotifySelectionChanged();
        }
    }

    public async Task UploadAsync(IReadOnlyList<string> localFilePaths)
    {
        if (localFilePaths.Count == 0) return;

        IsBusy = true;
        StatusText = $"Uploading {localFilePaths.Count} file(s)...";
        try
        {
            var settings = AppSettings.Load();
            var uploaded = 0;
            var errors = new List<string>();

            foreach (var path in localFilePaths)
            {
                try
                {
                    await S3BucketService.UploadFileAsync(settings, path);
                    uploaded++;
                }
                catch (Exception ex)
                {
                    errors.Add($"{Path.GetFileName(path)}: {Describe(ex)}");
                }
            }

            StatusText = errors.Count > 0
                ? $"Uploaded {uploaded} file(s). {errors.Count} failed: {string.Join(" | ", errors)}"
                : $"Uploaded {uploaded} file(s).";

            await RefreshAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"Upload failed: {Describe(ex)}";
        }
        finally
        {
            IsBusy = false;
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
            StatusText = $"Delete failed: {Describe(ex)}";
        }
        finally
        {
            IsBusy = false;
            NotifySelectionChanged();
        }
    }
}
