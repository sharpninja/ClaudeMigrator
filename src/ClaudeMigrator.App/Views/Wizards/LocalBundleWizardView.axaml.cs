using Avalonia.Controls;
using Avalonia.Platform.Storage;
using ClaudeMigrator.App.ViewModels.Wizards;

namespace ClaudeMigrator.App.Views.Wizards;

public partial class LocalBundleWizardView : UserControl
{
    public LocalBundleWizardView()
    {
        InitializeComponent();
    }

    private LocalBundleWizardViewModel? ViewModel => DataContext as LocalBundleWizardViewModel;

    private async void BrowseSourceHome_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var path = await PickFolderAsync("Source home folder");
        if (!string.IsNullOrWhiteSpace(path) && ViewModel is not null)
        {
            ViewModel.SourceHome = path;
        }
    }

    private async void BrowseDestinationHome_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var path = await PickFolderAsync("Destination home folder");
        if (!string.IsNullOrWhiteSpace(path) && ViewModel is not null)
        {
            ViewModel.DestinationHome = path;
        }
    }

    private async System.Threading.Tasks.Task<string?> PickFolderAsync(string title)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return null;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
        });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }
}
