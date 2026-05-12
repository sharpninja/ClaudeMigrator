using Avalonia.Controls;
using Avalonia.Platform.Storage;
using ClaudeMigrator.App.ViewModels;

namespace ClaudeMigrator.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    private async void BrowseExportZip_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var storage = StorageProvider;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select Claude export ZIP",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("ZIP archives")
                {
                    Patterns = new[] { "*.zip" },
                },
            },
        });

        if (files.Count > 0)
        {
            ViewModel?.SetSelectedExportZipPath(files[0].TryGetLocalPath());
        }
    }

    private async void BrowseSourceHome_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var storage = StorageProvider;
        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select Claude source home folder",
            AllowMultiple = false,
        });

        if (folders.Count > 0)
        {
            ViewModel?.SetSourceHomePath(folders[0].TryGetLocalPath());
        }
    }

    private async void BrowseDestinationHome_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var storage = StorageProvider;
        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select Claude destination home folder",
            AllowMultiple = false,
        });

        if (folders.Count > 0)
        {
            ViewModel?.SetDestinationHomePath(folders[0].TryGetLocalPath());
        }
    }

    private async void BrowseSourceRepoRoot_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var storage = StorageProvider;
        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select source repo root",
            AllowMultiple = false,
        });

        if (folders.Count > 0)
        {
            ViewModel?.SetSourceRepoRootPath(folders[0].TryGetLocalPath());
        }
    }
}
