using System.Windows;
using PuntersScraper.App.ViewModels;
using MessageBox = System.Windows.MessageBox;

namespace PuntersScraper.App;

public partial class BucketWindow : Window
{
    private readonly BucketViewModel _viewModel = new();

    public BucketWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        Loaded += async (_, _) => await _viewModel.RefreshCommand.ExecuteAsync(null);
    }

    private async void DeleteOne_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not S3ObjectRow row) return;

        var confirm = MessageBox.Show(
            $"Delete '{row.Key}' from the bucket? This cannot be undone.",
            "Delete file", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        await _viewModel.DeleteAsync(new[] { row.Key });
    }

    private async void DeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        var keys = _viewModel.Objects.Where(o => o.IsSelected).Select(o => o.Key).ToList();
        if (keys.Count == 0) return;

        var confirm = MessageBox.Show(
            $"Delete {keys.Count} file(s) from the bucket? This cannot be undone.",
            "Delete files", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        await _viewModel.DeleteAsync(keys);
    }
}
