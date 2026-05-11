using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using ClaudeMigrator.Core.Migration;
using ClaudeMigrator.Core.Models;
using ClaudeMigrator.Core.Paths;
using ClaudeMigrator.Core.RemoteTargets;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Threading;

namespace ClaudeMigrator.App.ViewModels;

public partial class MainWindowViewModel : ViewModelBase, IDisposable
{
    private readonly MigrationController _controller;
    private bool _syncingSourceMode;

    public MainWindowViewModel(MigrationController controller)
    {
        _controller = controller;
        DebugLogPath = _controller.LogFilePath;
        LaunchSummary = $"Ready. Debug log: {_controller.LogFilePath}";
        SourceHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        SourceMachineName = Environment.MachineName;
        SourceHost = Environment.MachineName;
        SourceUser = Environment.UserName;
        SourceRepoRoot = Directory.GetCurrentDirectory();
        IsZipSourceMode = true;
        TargetClaude = true;
        TargetCodex = true;

        Steps = new ObservableCollection<StepViewModel>(
            _controller.StepDefinitions.Select(definition => new StepViewModel
            {
                StepId = definition.StepId,
                Title = definition.Title,
                Description = definition.Description,
            }));

        LogLines = new ObservableCollection<string>();
        RemoteMachines = new ObservableCollection<RemoteMachineViewModel>();

        _controller.LogMessage += OnControllerLog;
        _controller.OverallProgressChanged += OnOverallProgressChanged;
        _controller.StepUpdated += OnStepUpdated;
        _controller.ManualActionRequested += OnManualActionRequested;
        _controller.ArtifactRecorded += OnArtifactRecorded;
        _controller.RunStateChanged += OnRunStateChanged;

        RefreshRemoteMachines();
        UpdateLaunchSummary();
    }

    public ObservableCollection<StepViewModel> Steps { get; }
    public ObservableCollection<string> LogLines { get; }
    public ObservableCollection<RemoteMachineViewModel> RemoteMachines { get; }
    public IReadOnlyList<string> ConnectionMethods { get; } = ["ssh", "wsman"];

    [ObservableProperty]
    private string launchSummary = string.Empty;

    [ObservableProperty]
    private string debugLogPath = string.Empty;

    [ObservableProperty]
    private string statusText = "Idle.";

    [ObservableProperty]
    private string overallProgressText = "Overall progress: 0%";

    [ObservableProperty]
    private int overallProgress;

    [ObservableProperty]
    private bool isRunning;

    [ObservableProperty]
    private bool hasManualAction;

    [ObservableProperty]
    private string manualActionLabel = "Continue Current Step";

    [ObservableProperty]
    private string manualActionMessage = "No manual action pending.";

    [ObservableProperty]
    private string sourceHome = string.Empty;

    [ObservableProperty]
    private string sourceMachineName = string.Empty;

    [ObservableProperty]
    private string sourceHost = string.Empty;

    [ObservableProperty]
    private string sourceUser = string.Empty;

    [ObservableProperty]
    private string sourceRepoRoot = string.Empty;

    [ObservableProperty]
    private string connectionMethod = "ssh";

    [ObservableProperty]
    private string selectedExportZip = string.Empty;

    [ObservableProperty]
    private bool isLocalSourceMode;

    [ObservableProperty]
    private bool isZipSourceMode = true;

    [ObservableProperty]
    private bool targetClaude = true;

    [ObservableProperty]
    private bool targetCodex = true;

    [ObservableProperty]
    private RemoteMachineViewModel? selectedRemoteMachine;

    [ObservableProperty]
    private string remoteMachineName = string.Empty;

    [ObservableProperty]
    private string remoteMachineHost = string.Empty;

    [ObservableProperty]
    private string remoteMachineMethod = "ssh";

    [ObservableProperty]
    private string remoteMachineUser = string.Empty;

    [ObservableProperty]
    private string remoteMachineRepoRoot = string.Empty;

    [ObservableProperty]
    private string remoteMachinePort = string.Empty;

    [ObservableProperty]
    private string remoteMachineNotes = string.Empty;

    partial void OnIsLocalSourceModeChanged(bool value)
    {
        if (_syncingSourceMode)
        {
            return;
        }

        if (value)
        {
            _syncingSourceMode = true;
            IsZipSourceMode = false;
            _syncingSourceMode = false;
            _controller.SetSourceMode(SourceMode.LocalSnapshot);
        }
    }

    partial void OnIsZipSourceModeChanged(bool value)
    {
        if (_syncingSourceMode)
        {
            return;
        }

        if (value)
        {
            _syncingSourceMode = true;
            IsLocalSourceMode = false;
            _syncingSourceMode = false;
            _controller.SetSourceMode(SourceMode.Zip);
        }
    }

    partial void OnSelectedRemoteMachineChanged(RemoteMachineViewModel? value)
    {
        if (value is null)
        {
            return;
        }

        RemoteMachineName = value.DisplayName;
        RemoteMachineHost = value.Host;
        RemoteMachineMethod = value.ConnectionMethod;
        RemoteMachineUser = value.Username;
        RemoteMachineRepoRoot = value.RepoRoot;
        RemoteMachinePort = value.Port?.ToString() ?? string.Empty;
        RemoteMachineNotes = value.Notes;
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task StartMigrationAsync()
    {
        if (IsRunning)
        {
            return;
        }

        IsRunning = true;
        try
        {
            await _controller.StartFullMigrationAsync(BuildOptions()).ConfigureAwait(false);
            StatusText = "Migration complete.";
        }
        catch (Exception ex)
        {
            StatusText = $"Migration failed: {ex.Message}";
        }
        finally
        {
            IsRunning = false;
        }
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task BuildSourceBundleAsync()
    {
        if (IsRunning)
        {
            return;
        }

        IsRunning = true;
        try
        {
            await _controller.BuildSourceBundleAsync(BuildOptions()).ConfigureAwait(false);
            StatusText = "Local bundle built.";
        }
        catch (Exception ex)
        {
            StatusText = $"Build failed: {ex.Message}";
        }
        finally
        {
            IsRunning = false;
        }
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task RestoreLocalBundleAsync()
    {
        if (IsRunning)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(SelectedExportZip))
        {
            StatusText = "Select an export ZIP first.";
            return;
        }

        IsRunning = true;
        try
        {
            await _controller.RestoreLocalBundleAsync(SelectedExportZip, BuildOptions().TargetApps).ConfigureAwait(false);
            StatusText = "Local bundle restored.";
        }
        catch (Exception ex)
        {
            StatusText = $"Restore failed: {ex.Message}";
        }
        finally
        {
            IsRunning = false;
        }
    }

    [RelayCommand]
    private void ContinueCurrentStep() => _controller.ContinueCurrentStep();

    [RelayCommand]
    private async Task CloseBrowsersAsync()
    {
        await _controller.CloseBrowsersAsync().ConfigureAwait(false);
    }

    [RelayCommand]
    private void RefreshRemoteMachines() => LoadRemoteMachines();

    [RelayCommand]
    private void SaveRemoteMachine()
    {
        var spec = BuildRemoteMachineSpec();
        var existingIndex = RemoteMachines.ToList().FindIndex(item => string.Equals(item.MachineId, spec.MachineId, StringComparison.OrdinalIgnoreCase));
        var viewModel = RemoteMachineViewModel.FromSpec(spec);
        if (existingIndex >= 0)
        {
            RemoteMachines[existingIndex] = viewModel;
        }
        else
        {
            RemoteMachines.Add(viewModel);
        }

        SelectedRemoteMachine = viewModel;
        _controller.UpsertRemoteMachine(spec);
        UpdateLaunchSummary();
    }

    [RelayCommand]
    private void RemoveRemoteMachine()
    {
        if (SelectedRemoteMachine is null)
        {
            return;
        }

        RemoteMachines.Remove(SelectedRemoteMachine);
        _controller.RemoveRemoteMachine(SelectedRemoteMachine.MachineId);
        SelectedRemoteMachine = null;
        ClearRemoteMachineForm();
        UpdateLaunchSummary();
    }

    [RelayCommand]
    private async Task CopyRemoteExport()
    {
        if (SelectedRemoteMachine is null)
        {
            StatusText = "Select a remote machine first.";
            return;
        }

        var command = _controller.BuildRemoteExportCommand(SelectedRemoteMachine.MachineId, BuildOptions().TargetAppNames);
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow?.Clipboard is not null)
        {
            await desktop.MainWindow.Clipboard.SetTextAsync(command).ConfigureAwait(false);
            StatusText = "Remote export command copied to clipboard.";
            return;
        }

        StatusText = "Remote export command generated. Clipboard unavailable on this system.";
    }

    [RelayCommand]
    private void OpenLogsFolder()
    {
        try
        {
            var folder = Path.GetDirectoryName(DebugLogPath);
            if (string.IsNullOrWhiteSpace(folder))
            {
                return;
            }

            var processStartInfo = new ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "explorer.exe" : "xdg-open",
                Arguments = folder,
                UseShellExecute = true,
            };
            Process.Start(processStartInfo);
        }
        catch (Exception ex)
        {
            StatusText = $"Could not open logs folder: {ex.Message}";
        }
    }

    public void SetSelectedExportZipPath(string? path)
    {
        SelectedExportZip = string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetFullPath(path);
        if (!string.IsNullOrWhiteSpace(SelectedExportZip))
        {
            _controller.SetSelectedExportZip(SelectedExportZip);
            StatusText = $"Selected export ZIP: {SelectedExportZip}";
        }
    }

    public void SetSourceHomePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        SourceHome = Path.GetFullPath(path);
    }

    public void SetSourceRepoRootPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        SourceRepoRoot = Path.GetFullPath(path);
    }

    private MigrationOptions BuildOptions()
    {
        var targetApps = new List<TargetApp>();
        if (TargetClaude)
        {
            targetApps.Add(TargetApp.Claude);
        }

        if (TargetCodex)
        {
            targetApps.Add(TargetApp.Codex);
        }

        if (targetApps.Count == 0)
        {
            targetApps.Add(TargetApp.Claude);
        }

        return new MigrationOptions
        {
            SourceMode = IsLocalSourceMode ? SourceMode.LocalSnapshot : SourceMode.Zip,
            SourceHome = SourceHome,
            SourceMachineName = SourceMachineName,
            SourceHost = SourceHost,
            ConnectionMethod = ConnectionMethod,
            SourceUser = SourceUser,
            SourceRepoRoot = SourceRepoRoot,
            ExportZipPath = SelectedExportZip,
            TargetApps = targetApps,
        };
    }

    private RemoteMachineSpec BuildRemoteMachineSpec()
    {
        int? port = null;
        if (int.TryParse(RemoteMachinePort, out var parsedPort))
        {
            port = parsedPort;
        }

        return new RemoteMachineSpec
        {
            MachineId = SelectedRemoteMachine?.MachineId ?? string.Empty,
            DisplayName = RemoteMachineName,
            Host = RemoteMachineHost,
            ConnectionMethod = RemoteMachineMethod,
            RepoRoot = RemoteMachineRepoRoot,
            Username = RemoteMachineUser,
            Port = port,
            Notes = RemoteMachineNotes,
            CreatedAt = SelectedRemoteMachine?.CreatedAt ?? string.Empty,
            UpdatedAt = SelectedRemoteMachine?.UpdatedAt ?? string.Empty,
        }.Normalized();
    }

    private void LoadRemoteMachines()
    {
        RemoteMachines.Clear();
        foreach (var machine in _controller.LoadRemoteMachines())
        {
            RemoteMachines.Add(RemoteMachineViewModel.FromSpec(machine));
        }

        if (RemoteMachines.Count > 0)
        {
            SelectedRemoteMachine = RemoteMachines[0];
        }

        UpdateLaunchSummary();
    }

    private void ClearRemoteMachineForm()
    {
        RemoteMachineName = string.Empty;
        RemoteMachineHost = string.Empty;
        RemoteMachineMethod = "ssh";
        RemoteMachineUser = string.Empty;
        RemoteMachineRepoRoot = string.Empty;
        RemoteMachinePort = string.Empty;
        RemoteMachineNotes = string.Empty;
    }

    private void UpdateLaunchSummary()
    {
        LaunchSummary = $"{RemoteMachines.Count} remote machine(s) configured. Debug log: {DebugLogPath}";
    }

    private void OnControllerLog(string level, string message)
    {
        var line = $"[{DateTimeOffset.Now:HH:mm:ss}] [{level.ToUpperInvariant()}] {message}";
        Dispatcher.UIThread.Post(() =>
        {
            LogLines.Add(line);
            if (LogLines.Count > 800)
            {
                LogLines.RemoveAt(0);
            }
        });
    }

    private void OnOverallProgressChanged(int percent, string message)
    {
        Dispatcher.UIThread.Post(() =>
        {
            OverallProgress = percent;
            OverallProgressText = $"Overall progress: {percent}%";
            StatusText = message;
        });
    }

    private void OnStepUpdated(StepState state)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var step = Steps.FirstOrDefault(item => string.Equals(item.StepId, state.StepId, StringComparison.OrdinalIgnoreCase));
            if (step is not null)
            {
                step.Update(state);
            }
        });
    }

    private void OnManualActionRequested(ManualAction action)
    {
        Dispatcher.UIThread.Post(() =>
        {
            HasManualAction = true;
            ManualActionLabel = action.Label;
            ManualActionMessage = action.Message;
            StatusText = action.Message;
        });
    }

    private void OnArtifactRecorded(string key, object? value)
    {
        Dispatcher.UIThread.Post(() => LogLines.Add($"Artifact: {key} -> {value}"));
    }

    private void OnRunStateChanged(string state)
    {
        Dispatcher.UIThread.Post(() =>
        {
            IsRunning = string.Equals(state, "running", StringComparison.OrdinalIgnoreCase);
            if (string.Equals(state, "complete", StringComparison.OrdinalIgnoreCase))
            {
                HasManualAction = false;
                ManualActionMessage = "No manual action pending.";
                ManualActionLabel = "Continue Current Step";
            }
        });
    }

    public void Dispose()
    {
        _controller.LogMessage -= OnControllerLog;
        _controller.OverallProgressChanged -= OnOverallProgressChanged;
        _controller.StepUpdated -= OnStepUpdated;
        _controller.ManualActionRequested -= OnManualActionRequested;
        _controller.ArtifactRecorded -= OnArtifactRecorded;
        _controller.RunStateChanged -= OnRunStateChanged;
        _controller.Dispose();
    }
}
