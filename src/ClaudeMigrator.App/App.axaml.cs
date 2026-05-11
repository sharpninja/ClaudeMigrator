using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ClaudeMigrator.App.ViewModels;
using ClaudeMigrator.Core.Migration;
using ClaudeMigrator.Core.Paths;
using ClaudeMigrator.App.Views;

namespace ClaudeMigrator.App;

public partial class App : Application
{
    private MigrationController? _controller;
    private MainWindowViewModel? _viewModel;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var paths = new AppPaths(AppContext.BaseDirectory).Ensure();
            _controller = new MigrationController(paths);
            _viewModel = new MainWindowViewModel(_controller);
            desktop.Exit += OnDesktopExit;
            desktop.MainWindow = new MainWindow
            {
                DataContext = _viewModel,
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void OnDesktopExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        _viewModel?.Dispose();
        _viewModel = null;
        _controller = null;
    }
}
