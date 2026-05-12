using Avalonia.Controls;
using Avalonia.Platform.Storage;
using ClaudeMigrator.App.ViewModels.Wizards;

namespace ClaudeMigrator.App.Views.Wizards;

public partial class WebRecreationWizardView : UserControl
{
    public WebRecreationWizardView()
    {
        InitializeComponent();
    }

    private WebRecreationWizardViewModel? ViewModel => DataContext as WebRecreationWizardViewModel;

    private async void BrowseExportZip_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null || ViewModel is null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select Claude export ZIP",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("ZIP archives") { Patterns = new[] { "*.zip" } },
            },
        });

        if (files.Count > 0)
        {
            var path = files[0].TryGetLocalPath();
            if (!string.IsNullOrWhiteSpace(path))
            {
                ViewModel.ExportZipPath = path;
            }
        }
    }

    private async void BrowseManifest_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null || ViewModel is null) return;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save manifest as",
            DefaultExtension = "json",
            SuggestedFileName = "claude_web_recreation_manifest.json",
        });

        var path = file?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path))
        {
            ViewModel.OutputManifestPath = path;
        }
    }
}
