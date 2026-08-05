using CommunityToolkit.Mvvm.ComponentModel;
using PuntersScraper.App.Services;

namespace PuntersScraper.App.ViewModels;

/// <summary>One row in the Bucket window's grid — wraps an S3ObjectInfo with the checkbox
/// selection state the grid needs, which the plain record has no reason to carry itself.</summary>
public sealed partial class S3ObjectRow : ObservableObject
{
    public string Key { get; init; } = "";
    public long Size { get; init; }
    public DateTime LastModifiedUtc { get; init; }

    public string SizeDisplay => FormatSize(Size);
    public string LastModifiedDisplay => LastModifiedUtc.ToString("yyyy-MM-dd HH:mm:ss");

    /// <summary>Everything before the key's last '/', used to group the grid by folder instead of
    /// listing every object in one flat list — S3 has no real folders, just key prefixes, but
    /// this uploader's own keys are already structured that way (e.g. "pending/T-...-meeting.json").</summary>
    public string FolderPath
    {
        get
        {
            var idx = Key.LastIndexOf('/');
            return idx < 0 ? "(root)" : Key[..idx];
        }
    }

    /// <summary>Just the filename — shown in the grid instead of the full key, since the folder
    /// is already the group header.</summary>
    public string FileName
    {
        get
        {
            var idx = Key.LastIndexOf('/');
            return idx < 0 ? Key : Key[(idx + 1)..];
        }
    }

    [ObservableProperty]
    private bool isSelected;

    /// <summary>Fires whenever <see cref="IsSelected"/> changes, so BucketViewModel can keep its
    /// SelectedCount up to date without polling every row on every button render.</summary>
    public event Action? SelectionChanged;

    partial void OnIsSelectedChanged(bool value) => SelectionChanged?.Invoke();

    public static S3ObjectRow From(S3ObjectInfo info) => new()
    {
        Key = info.Key,
        Size = info.Size,
        LastModifiedUtc = info.LastModifiedUtc
    };

    private static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB" };
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return $"{size:0.#} {units[unit]}";
    }
}
