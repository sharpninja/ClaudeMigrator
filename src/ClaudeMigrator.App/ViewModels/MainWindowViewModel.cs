using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using ClaudeMigrator.App.ViewModels.Wizards;
using ClaudeMigrator.Core.Local;
using ClaudeMigrator.Core.Migration;
using ClaudeMigrator.Core.Models;
using ClaudeMigrator.Core.RemoteTargets;
using ClaudeMigrator.Core.Web;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClaudeMigrator.App.ViewModels;

public sealed partial class MainWindowViewModel : ViewModelBase, IDisposable
{
    private readonly MigrationController _controller;
    private readonly Action<string> _openFolder;
    private readonly ClaudeOauthAccountReader _oauthReader;
    private readonly Func<RemoteMachineSpec, IEnumerable<string>, string> _buildRemoteCommand;
    private readonly Func<string, CancellationToken, Task<bool>> _copyToClipboard;
    private readonly Func<HomeViewModel> _homeFactory;

    public MainWindowViewModel(
        MigrationController controller,
        Action<string>? openFolder = null,
        ClaudeOauthAccountReader? oauthReader = null,
        Func<RemoteMachineSpec, IEnumerable<string>, string>? buildRemoteCommand = null,
        Func<string, CancellationToken, Task<bool>>? copyToClipboard = null)
    {
        _controller = controller;
        _openFolder = openFolder ?? OpenFolderInSystemShell;
        _oauthReader = oauthReader ?? new ClaudeOauthAccountReader();
        _buildRemoteCommand = buildRemoteCommand ?? ((spec, apps) => RemoteCommandBuilder.BuildRemoteExportCommand(spec, targetApps: apps));
        _copyToClipboard = copyToClipboard ?? DefaultCopyToClipboard;
        DebugLogPath = _controller.LogFilePath;

        _homeFactory = () =>
        {
            var home = new HomeViewModel(_ => OpenLogsFolder(), _ => OpenSessionsFolder(), DebugLogPath);
            home.WorkflowSelected += OnWorkflowSelected;
            return home;
        };

        NavigateHome();
    }

    [ObservableProperty]
    private ViewModelBase? currentView;

    [ObservableProperty]
    private string debugLogPath = string.Empty;

    [ObservableProperty]
    private string statusText = string.Empty;

    public bool IsOnHome => CurrentView is HomeViewModel;

    public bool IsOnWizard => CurrentView is WizardViewModelBase;

    partial void OnCurrentViewChanged(ViewModelBase? value)
    {
        OnPropertyChanged(nameof(IsOnHome));
        OnPropertyChanged(nameof(IsOnWizard));
    }

    [RelayCommand]
    public void NavigateHome()
    {
        DetachCurrentView();
        var home = _homeFactory();
        CurrentView = home;
        StatusText = $"Ready. Debug log: {DebugLogPath}";
    }

    public void NavigateToWizard(WizardViewModelBase wizard)
    {
        ArgumentNullException.ThrowIfNull(wizard);
        DetachCurrentView();
        wizard.Cancelled += OnWizardCancelled;
        wizard.Completed += OnWizardCompleted;
        CurrentView = wizard;
        StatusText = wizard.Title;
    }

    public WizardViewModelBase BuildWizard(string workflowId)
        => workflowId switch
        {
            HomeViewModel.WebRecreationWorkflowId => BuildWebRecreationWizard(),
            HomeViewModel.CoworkSessionsWorkflowId => BuildCoworkSessionsWizard(),
            HomeViewModel.LocalBundleWorkflowId => BuildLocalBundleWizard(),
            HomeViewModel.RemoteBundleWorkflowId => BuildRemoteBundleWizard(),
            _ => throw new ArgumentException($"Unknown workflow: {workflowId}", nameof(workflowId)),
        };

    private void OnWorkflowSelected(object? sender, string workflowId)
    {
        var wizard = BuildWizard(workflowId);
        NavigateToWizard(wizard);
    }

    private void OnWizardCancelled(object? sender, EventArgs e) => NavigateHome();

    private void OnWizardCompleted(object? sender, WizardResult result)
    {
        StatusText = result.Message;
    }

    private CoworkSessionsWizardViewModel BuildCoworkSessionsWizard()
    {
        var accounts = _oauthReader.ReadAll();
        return new CoworkSessionsWizardViewModel(
            accounts,
            (options, log) => new LocalAgentSessionsMigrator(log).Migrate(options));
    }

    private WebRecreationWizardViewModel BuildWebRecreationWizard()
    {
        return new WebRecreationWizardViewModel(
            recreate: async (options, log, cancellationToken) =>
            {
                var recreator = new ClaudeWebRecreator(log);
                return await recreator.RecreateAsync(options, cancellationToken).ConfigureAwait(false);
            },
            defaultOutputFolder: Path.Combine(_controller.Paths.RuntimeDir, "web_recreation"));
    }

    private LocalBundleWizardViewModel BuildLocalBundleWizard()
    {
        return new LocalBundleWizardViewModel(
            build: async (options, log, cancellationToken) =>
            {
                void Handler(string level, string message) => log($"[{level.ToLowerInvariant()}] {message}");
                _controller.LogMessage += Handler;
                try
                {
                    await _controller.BuildSourceBundleAsync(options).ConfigureAwait(false);
                    return _controller.LocalBundleResult
                        ?? throw new InvalidOperationException("Local bundle build produced no result.");
                }
                finally
                {
                    _controller.LogMessage -= Handler;
                }
            },
            restore: async (zipPath, targetApps, destinationHome, log, cancellationToken) =>
            {
                void Handler(string level, string message) => log($"[{level.ToLowerInvariant()}] {message}");
                _controller.LogMessage += Handler;
                try
                {
                    await _controller.RestoreLocalBundleAsync(zipPath, targetApps, destinationHome).ConfigureAwait(false);
                }
                finally
                {
                    _controller.LogMessage -= Handler;
                }
            });
    }

    private RemoteBundleWizardViewModel BuildRemoteBundleWizard()
    {
        var machines = _controller.LoadRemoteMachines();
        return new RemoteBundleWizardViewModel(
            machines,
            (spec, apps) => _buildRemoteCommand(spec, apps),
            (text, cancellationToken) => _copyToClipboard(text, cancellationToken));
    }

    private void OpenLogsFolder() => OpenFolder(_controller.Paths.LogsDir, "logs");

    private void OpenSessionsFolder() => OpenFolder(_controller.Paths.SessionsDir, "sessions");

    private void OpenFolder(string folder, string label)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            {
                StatusText = $"Could not open {label} folder: folder does not exist.";
                return;
            }

            _openFolder(folder);
        }
        catch (Exception ex)
        {
            StatusText = $"Could not open {label} folder: {ex.Message}";
        }
    }

    private void DetachCurrentView()
    {
        switch (CurrentView)
        {
            case HomeViewModel home:
                home.WorkflowSelected -= OnWorkflowSelected;
                break;
            case WizardViewModelBase wizard:
                wizard.Cancelled -= OnWizardCancelled;
                wizard.Completed -= OnWizardCompleted;
                break;
        }
    }

    private static void OpenFolderInSystemShell(string folder)
    {
        if (OperatingSystem.IsWindows())
        {
            Process.Start(new ProcessStartInfo(folder)
            {
                UseShellExecute = true,
            });
            return;
        }

        var opener = OperatingSystem.IsMacOS() ? "open" : "xdg-open";
        Process.Start(new ProcessStartInfo(opener, folder)
        {
            UseShellExecute = true,
        });
    }

    private static async Task<bool> DefaultCopyToClipboard(string text, CancellationToken cancellationToken)
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text).ConfigureAwait(false);
            return true;
        }

        return false;
    }

    public void Dispose()
    {
        DetachCurrentView();
        _controller.Dispose();
    }
}
