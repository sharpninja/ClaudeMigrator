using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ClaudeMigrator.Core.Browser;
using ClaudeMigrator.Core.Exporting;
using ClaudeMigrator.Core.Models;
using ClaudeMigrator.Core.Paths;
using ClaudeMigrator.Core.RemoteTargets;
using ClaudeMigrator.Core.Utilities;
using Microsoft.Playwright;

namespace ClaudeMigrator.Core.Migration;

public sealed class MigrationController : IDisposable
{
    private readonly object _logGate = new();
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private readonly Dictionary<string, StepState> _stepStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly StreamWriter _logWriter;
    private readonly string _logFilePath;
    private readonly string _startupSnapshotPath;
    private TaskCompletionSource<bool> _continueSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly IReadOnlyList<StepDefinition> _stepDefinitions;
    private readonly JsonSerializerOptions _jsonOptions = JsonUtils.SnakeCaseIndented;
    private ManualAction? _pendingManualAction;

    public MigrationController(AppPaths paths, Action<string>? logSink = null)
    {
        Paths = paths.Ensure();
        _stepDefinitions = BuildStepDefinitions();
        var sessionTag = $"{PathUtils.TimestampTag()}_{Environment.ProcessId}";
        _logFilePath = System.IO.Path.Combine(Paths.LogsDir, $"claude_migration_{sessionTag}.log");
        _startupSnapshotPath = System.IO.Path.Combine(Paths.LogsDir, $"claude_migration_{sessionTag}_startup.json");
        _logWriter = new StreamWriter(File.Open(_logFilePath, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            AutoFlush = true,
        };
        BrowserManager = new BrowserManager(Paths.RuntimeDir, message => Log(message));
        LocalExporter = new LocalClaudeBundleExporter(Paths.RuntimeDir, message => Log(message));
        Exporter = new UniversalClaudeExporter(Paths.RuntimeDir, message => Log(message));
        RemoteStore = new RemoteTargetStore(Paths.RemoteTargetsPath);
        BuildStartupSnapshot(sessionTag);
        Log("Controller initialized.");
        if (logSink is not null)
        {
            LogMessage += (_, message) => logSink(message);
        }
    }

    public AppPaths Paths { get; }
    public BrowserManager BrowserManager { get; }
    public LocalClaudeBundleExporter LocalExporter { get; }
    public UniversalClaudeExporter Exporter { get; }
    public RemoteTargetStore RemoteStore { get; }
    public string LogFilePath => _logFilePath;
    public string StartupSnapshotPath => _startupSnapshotPath;
    public SourceMode SourceMode { get; private set; } = SourceMode.Zip;
    public IReadOnlyList<TargetApp> TargetApps { get; private set; } = [TargetApp.Claude, TargetApp.Codex];
    public string? SelectedExportZip { get; private set; }
    public LocalBundleResult? LocalBundleResult { get; private set; }
    public PortableExportResult? PortableResult { get; private set; }
    public ManualAction? PendingManualAction => _pendingManualAction;
    public IReadOnlyList<StepDefinition> StepDefinitions => _stepDefinitions;

    public Action<string, string> LogMessage { get; set; } = delegate { };
    public Action<int, string> OverallProgressChanged { get; set; } = delegate { };
    public Action<StepState> StepUpdated { get; set; } = delegate { };
    public Action<ManualAction> ManualActionRequested { get; set; } = delegate { };
    public Action<string, object?> ArtifactRecorded { get; set; } = delegate { };
    public Action<string> RunStateChanged { get; set; } = delegate { };

    public void Dispose()
    {
        try
        {
            Log("Controller shutting down.");
        }
        catch
        {
        }

        try
        {
            BrowserManager.CloseAllAsync().GetAwaiter().GetResult();
        }
        catch
        {
        }

        try
        {
            _logWriter.Dispose();
        }
        catch
        {
        }
    }

    public void SetSourceMode(SourceMode sourceMode)
    {
        SourceMode = sourceMode;
        Log($"Source mode set to {sourceMode}.");
    }

    public void SetTargetApps(IEnumerable<TargetApp> apps)
    {
        var normalized = apps.Distinct().ToList();
        if (normalized.Count == 0)
        {
            normalized.Add(TargetApp.Claude);
        }

        TargetApps = normalized;
        Log("Target apps set to " + string.Join(", ", TargetApps));
    }

    public void SetSelectedExportZip(string? path)
    {
        SelectedExportZip = string.IsNullOrWhiteSpace(path) ? null : System.IO.Path.GetFullPath(path);
        if (SelectedExportZip is not null)
        {
            Log($"Selected export ZIP: {SelectedExportZip}");
        }
    }

    public IReadOnlyList<RemoteMachineSpec> LoadRemoteMachines() => RemoteStore.Load();

    public IReadOnlyList<RemoteMachineSpec> UpsertRemoteMachine(RemoteMachineSpec spec)
    {
        var saved = RemoteStore.Upsert(spec);
        Log($"Saved remote machine {spec.DisplayName} ({spec.Host}).");
        return saved;
    }

    public IReadOnlyList<RemoteMachineSpec> RemoveRemoteMachine(string machineId)
    {
        var saved = RemoteStore.Remove(machineId);
        Log($"Removed remote machine {machineId}.");
        return saved;
    }

    public string BuildRemoteExportCommand(string machineId, IEnumerable<string>? targetApps = null)
    {
        var spec = RemoteStore.Get(machineId) ?? throw new InvalidOperationException($"Remote machine not found: {machineId}");
        return RemoteCommandBuilder.BuildRemoteExportCommand(spec, targetApps: targetApps ?? TargetApps.Select(app => app.ToString().ToLowerInvariant()));
    }

    public void MarkStepQueued(string stepId, string title, string description)
    {
        UpdateStep(new StepState(stepId, title, description, StepStatus.Queued, 0, ""));
    }

    public StepState GetStepState(string stepId)
        => _stepStates.TryGetValue(stepId, out var state)
            ? state
            : new StepState(stepId, stepId, string.Empty);

    public async Task StartFullMigrationAsync(MigrationOptions options, CancellationToken cancellationToken = default)
    {
        await _runGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SetSourceMode(options.SourceMode);
            SetTargetApps(options.TargetApps);
            SelectedExportZip = options.ExportZipPath;
            UpdateOverallProgress(0, "Starting migration");
            RunStateChanged("running");

            if (options.SourceMode == SourceMode.LocalSnapshot)
            {
                await RunLocalSnapshotFlowAsync(options, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await RunExportZipFlowAsync(options, cancellationToken).ConfigureAwait(false);
            }

            UpdateOverallProgress(100, "Migration complete");
            RunStateChanged("complete");
        }
        catch (OperationCanceledException)
        {
            RunStateChanged("cancelled");
            throw;
        }
        catch (Exception ex)
        {
            LogException("Migration failed", ex);
            UpdateOverallProgress(0, "Migration failed");
            RunStateChanged("failed");
            throw;
        }
        finally
        {
            _runGate.Release();
        }
    }

    public async Task BuildSourceBundleAsync(MigrationOptions options, CancellationToken cancellationToken = default)
    {
        await _runGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SetSourceMode(SourceMode.LocalSnapshot);
            SetTargetApps(options.TargetApps);
            UpdateOverallProgress(0, "Building local bundle");
            MarkStepQueued("build_source_bundle", "Build Source Bundle", "Snapshot the local Claude profile.");
            UpdateStep(new StepState("build_source_bundle", "Build Source Bundle", "Snapshot the local Claude profile.", StepStatus.Running, 10, "Reading local profile"));

            var result = LocalExporter.ExportLocalBundle(
                sourceHome: options.SourceHome,
                sourceMachineName: options.SourceMachineName,
                sourceHost: options.SourceHost,
                connectionMethod: options.ConnectionMethod,
                sourceUser: options.SourceUser,
                sourceRepoRoot: options.SourceRepoRoot,
                targetApps: options.TargetAppNames,
                progressCallback: (percent, message) => UpdateOverallProgress(percent, message));

            LocalBundleResult = result;
            ArtifactRecorded("local_bundle_zip", result.ZipPath);
            UpdateStep(new StepState("build_source_bundle", "Build Source Bundle", "Snapshot the local Claude profile.", StepStatus.Done, 100, "Local bundle built"));
            UpdateOverallProgress(100, "Local bundle built");
            RunStateChanged("complete");
        }
        catch (Exception ex)
        {
            LogException("Build source bundle failed", ex);
            UpdateStep(new StepState("build_source_bundle", "Build Source Bundle", "Snapshot the local Claude profile.", StepStatus.Failed, 0, ex.Message));
            RunStateChanged("failed");
            throw;
        }
        finally
        {
            _runGate.Release();
        }
    }

    public async Task RestoreLocalBundleAsync(string bundlePath, IEnumerable<TargetApp>? targetApps = null, CancellationToken cancellationToken = default)
    {
        await _runGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var selectedTargets = (targetApps ?? TargetApps).Select(app => app.ToString().ToLowerInvariant());
            var result = LocalExporter.RestoreLocalBundle(
                bundlePath,
                destinationHome: Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                targetApps: selectedTargets,
                progressCallback: (percent, message) => UpdateOverallProgress(percent, message));
            ArtifactRecorded("local_restore_result", result);
            UpdateOverallProgress(100, "Local bundle restored");
            RunStateChanged("complete");
        }
        catch (Exception ex)
        {
            LogException("Restore local bundle failed", ex);
            RunStateChanged("failed");
            throw;
        }
        finally
        {
            _runGate.Release();
        }
    }

    public void ContinueCurrentStep()
    {
        if (_pendingManualAction is null)
        {
            return;
        }

        _pendingManualAction = null;
        _continueSource.TrySetResult(true);
    }

    public async Task WaitForContinueAsync()
    {
        await _continueSource.Task.ConfigureAwait(false);
    }

    public async Task CloseBrowsersAsync()
    {
        await BrowserManager.CloseAllAsync().ConfigureAwait(false);
        Log("Browsers closed.");
    }

    public void Log(string message, string level = "info")
    {
        WriteLogEntry(message, level);
        LogMessage(level, message);
    }

    public void LogException(string message, Exception exception, string level = "error")
    {
        var summary = $"{message}: {exception.Message}";
        var traceback = exception.ToString();
        WriteLogLines([summary], level);
        WriteLogLines(traceback.Split(Environment.NewLine), level);
        LogMessage(level, summary);
    }

    public void RecordArtifact(string key, object? value)
    {
        ArtifactRecorded(key, value);
        Log($"Artifact recorded: {key} -> {FormatArtifact(value)}");
    }

    private void BuildStartupSnapshot(string sessionTag)
    {
        var snapshot = new Dictionary<string, object?>
        {
            ["session_tag"] = sessionTag,
            ["timestamp"] = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            ["process_id"] = Environment.ProcessId,
            ["thread"] = Environment.CurrentManagedThreadId,
            ["dotnet"] = Environment.Version.ToString(),
            ["executable"] = Environment.ProcessPath ?? string.Empty,
            ["platform"] = Environment.OSVersion.ToString(),
            ["machine"] = Environment.MachineName,
            ["user"] = Environment.UserName,
            ["cwd"] = Environment.CurrentDirectory,
            ["paths"] = new Dictionary<string, object?>
            {
                ["root_dir"] = Paths.RootDir,
                ["runtime_dir"] = Paths.RuntimeDir,
                ["log_file"] = _logFilePath,
                ["startup_snapshot_path"] = _startupSnapshotPath,
            },
            ["source_mode"] = SourceMode.ToString().ToLowerInvariant(),
            ["target_apps"] = TargetApps.Select(app => app.ToString().ToLowerInvariant()).ToArray(),
            ["browser_accounts"] = BrowserManager.DefaultAccountSpecs().Select(spec => new Dictionary<string, object?>
            {
                ["key"] = spec.Key,
                ["email"] = spec.Email,
                ["browser_name"] = spec.BrowserName,
                ["channel"] = spec.Channel ?? string.Empty,
                ["storage_state_path"] = spec.StorageStatePath,
            }).ToArray(),
        };

        File.WriteAllText(_startupSnapshotPath, JsonSerializer.Serialize(snapshot, _jsonOptions), Encoding.UTF8);
    }

    private async Task RunLocalSnapshotFlowAsync(MigrationOptions options, CancellationToken cancellationToken)
    {
        MarkStepQueued("build_source_bundle", "Build Source Bundle", "Snapshot the local Claude profile.");
        MarkStepQueued("restore_local_bundle", "Restore Local Bundle", "Restore the snapshot to the selected targets.");

        UpdateStep(new StepState("build_source_bundle", "Build Source Bundle", "Snapshot the local Claude profile.", StepStatus.Running, 10, "Reading local profile"));
        var buildResult = LocalExporter.ExportLocalBundle(
            sourceHome: options.SourceHome,
            sourceMachineName: options.SourceMachineName,
            sourceHost: options.SourceHost,
            connectionMethod: options.ConnectionMethod,
            sourceUser: options.SourceUser,
            sourceRepoRoot: options.SourceRepoRoot,
            targetApps: options.TargetAppNames,
            progressCallback: (percent, message) => UpdateOverallProgress(percent, message));

        LocalBundleResult = buildResult;
        RecordArtifact("local_bundle_zip", buildResult.ZipPath);
        UpdateStep(new StepState("build_source_bundle", "Build Source Bundle", "Snapshot the local Claude profile.", StepStatus.Done, 100, "Local bundle built"));

        UpdateStep(new StepState("restore_local_bundle", "Restore Local Bundle", "Restore the snapshot to the selected targets.", StepStatus.Running, 10, "Restoring bundle"));
        var restoreResult = LocalExporter.RestoreLocalBundle(
            buildResult.ZipPath,
            destinationHome: options.SourceHome,
            targetApps: options.TargetAppNames,
            progressCallback: (percent, message) => UpdateOverallProgress(percent, message));
        RecordArtifact("local_restore_result", restoreResult);
        UpdateStep(new StepState("restore_local_bundle", "Restore Local Bundle", "Restore the snapshot to the selected targets.", StepStatus.Done, 100, "Bundle restored"));
        UpdateOverallProgress(100, "Local snapshot migration complete");
        await Task.CompletedTask;
    }

    private async Task RunExportZipFlowAsync(MigrationOptions options, CancellationToken cancellationToken)
    {
        var exportPath = ResolveExportZip(options.ExportZipPath);
        SetSelectedExportZip(exportPath);

        MarkStepQueued("setup_browsers", "Setup Browsers & Sessions", "Open Edge and Firefox and wait for login.");
        UpdateStep(new StepState("setup_browsers", "Setup Browsers & Sessions", "Open Edge and Firefox and wait for login.", StepStatus.Running, 5, "Launching browsers"));
        await SetupBrowsersAsync(cancellationToken).ConfigureAwait(false);

        MarkStepQueued("trigger_export", "Trigger Official Export", "Open Claude settings and start export.");
        UpdateStep(new StepState("trigger_export", "Trigger Official Export", "Open Claude settings and start export.", StepStatus.Running, 5, "Opening Claude settings"));
        await TriggerOfficialExportAsync(cancellationToken).ConfigureAwait(false);

        MarkStepQueued("prepare_download", "Download & Prepare Export", "Find the exported ZIP and stage it.");
        UpdateStep(new StepState("prepare_download", "Download & Prepare Export", "Find the exported ZIP and stage it.", StepStatus.Running, 10, "Locating export ZIP"));
        var portableResult = Exporter.ExportPortableZip(exportPath, progressCallback: (percent, message) => UpdateOverallProgress(percent, message));
        PortableResult = portableResult;
        RecordArtifact("portable_export_zip", portableResult.ZipPath);
        UpdateStep(new StepState("prepare_download", "Download & Prepare Export", "Find the exported ZIP and stage it.", StepStatus.Done, 100, "Portable export ready"));

        if (TargetApps.Contains(TargetApp.Claude))
        {
            await RunClaudeRestoreFlowAsync(cancellationToken).ConfigureAwait(false);
        }

        if (TargetApps.Contains(TargetApp.Codex))
        {
            RecordArtifact("codex_target", "Local artifacts will be restored to ~/.codex during the local bundle path.");
        }
    }

    private async Task SetupBrowsersAsync(CancellationToken cancellationToken)
    {
        var edgeSpec = BrowserManager.DefaultAccountSpecs().First(spec => string.Equals(spec.Key, "edge_original", StringComparison.OrdinalIgnoreCase));
        var firefoxSpec = BrowserManager.DefaultAccountSpecs().First(spec => string.Equals(spec.Key, "firefox_new", StringComparison.OrdinalIgnoreCase));
        await BrowserManager.OpenSessionAsync(edgeSpec, headless: false, cancellationToken).ConfigureAwait(false);
        Log($"Edge opened for {edgeSpec.Email}.");
        await BrowserManager.OpenSessionAsync(firefoxSpec, headless: false, cancellationToken).ConfigureAwait(false);
        Log($"Firefox opened for {firefoxSpec.Email}.");

        RequestManualAction(new ManualAction(
            StepId: "setup_browsers",
            Label: "Save Sessions & Continue",
            Message: "Sign in to Claude in both open browsers, then click Save Sessions & Continue. The app will store storage_state files for later automation.",
            Kind: "save_sessions"));
        UpdateStep(new StepState("setup_browsers", "Setup Browsers & Sessions", "Open Edge and Firefox and wait for login.", StepStatus.Waiting, 45, "Sign in to both accounts, then save the session states."));
        await WaitForContinueAsync().ConfigureAwait(false);
        await BrowserManager.SaveAllSessionStatesAsync(cancellationToken).ConfigureAwait(false);
        UpdateStep(new StepState("setup_browsers", "Setup Browsers & Sessions", "Open Edge and Firefox and wait for login.", StepStatus.Done, 100, "Browser sessions captured"));
    }

    private async Task TriggerOfficialExportAsync(CancellationToken cancellationToken)
    {
        var session = BrowserManager.GetSession("edge_original") ?? throw new InvalidOperationException("Edge session not found.");
        try
        {
            await session.Page.GotoAsync("https://claude.ai/settings", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded }).ConfigureAwait(false);
            try
            {
                await session.Page.WaitForLoadStateAsync(LoadState.NetworkIdle, new PageWaitForLoadStateOptions { Timeout = 10000 }).ConfigureAwait(false);
            }
            catch
            {
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Could not open Claude settings: {ex.Message}", ex);
        }

        var clicked = await BrowserManager.ClickCandidatesAsync(
            session.Page,
            new[]
            {
                "Privacy",
                "Data",
                "Data controls",
                "Export data",
                "Download data",
                "Export",
                "Request export",
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (clicked)
        {
            Log("Clicked the most likely export control.");
        }
        else
        {
            Log("Could not find a definitive export control. The page is still open for manual completion.", "warning");
        }

        try
        {
            await session.Page.WaitForTimeoutAsync(3000).ConfigureAwait(false);
        }
        catch
        {
        }

        UpdateStep(new StepState("trigger_export", "Trigger Official Export", "Open Claude settings and start export.", StepStatus.Done, 100, "Export trigger step finished"));
    }

    private async Task RunClaudeRestoreFlowAsync(CancellationToken cancellationToken)
    {
        if (PortableResult is null)
        {
            return;
        }

        MarkStepQueued("import_memory", "Import Memory to New Account", "Use Firefox to import the prepared memory bundle into the new Claude account.");
        MarkStepQueued("recreate_projects", "Recreate Projects in New Account", "Use the generated blueprints to recreate Claude projects in the new account.");
        MarkStepQueued("inject_seeds", "Inject Conversation Seeds", "Create continuation chats using the seed prompts generated from the export.");
        MarkStepQueued("configure_edge", "Configure Edge for New Account", "Open the new account in Edge and guide the user through the default-browser step.");

        UpdateStep(new StepState("import_memory", "Import Memory to New Account", "Use Firefox to import the prepared memory bundle into the new Claude account.", StepStatus.Running, 10, "Opening Claude settings in Firefox"));
        await ImportMemoryAsync(cancellationToken).ConfigureAwait(false);

        UpdateStep(new StepState("recreate_projects", "Recreate Projects in New Account", "Use the generated blueprints to recreate Claude projects in the new account.", StepStatus.Running, 10, "Opening Claude projects page"));
        await RecreateProjectsAsync(cancellationToken).ConfigureAwait(false);

        UpdateStep(new StepState("inject_seeds", "Inject Conversation Seeds", "Create continuation chats using the seed prompts generated from the export.", StepStatus.Running, 10, "Opening chat composer"));
        await InjectSeedPromptsAsync(cancellationToken).ConfigureAwait(false);

        UpdateStep(new StepState("configure_edge", "Configure Edge for New Account", "Open the new account in Edge and guide the user through the default-browser step.", StepStatus.Running, 15, "Opening Edge settings"));
        await ConfigureEdgeDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ImportMemoryAsync(CancellationToken cancellationToken)
    {
        if (PortableResult is null)
        {
            throw new InvalidOperationException("Portable export is not ready.");
        }

        var session = BrowserManager.GetSession("firefox_new") ?? throw new InvalidOperationException("Firefox session not found.");
        var page = session.Page;
        try
        {
            await page.GotoAsync("https://claude.ai/settings", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Could not open Claude settings in Firefox: {ex.Message}", ex);
        }

        await BrowserManager.ClickCandidatesAsync(page, new[] { "Memory", "Preferences", "Personalization", "Data" }, cancellationToken: cancellationToken).ConfigureAwait(false);
        var uploaded = await BrowserManager.SetFirstFileInputAsync(page, PortableResult.MemoryPath, cancellationToken).ConfigureAwait(false);
        if (uploaded)
        {
            Log($"Loaded memory JSON into a file input: {PortableResult.MemoryPath}");
        }
        else
        {
            Log("No direct memory import control was found. Manual import may be required.", "warning");
        }

        UpdateStep(new StepState("import_memory", "Import Memory to New Account", "Use Firefox to import the prepared memory bundle into the new Claude account.", StepStatus.Done, 100, "Memory import step finished"));
    }

    private async Task RecreateProjectsAsync(CancellationToken cancellationToken)
    {
        if (PortableResult is null)
        {
            return;
        }

        var session = BrowserManager.GetSession("firefox_new") ?? throw new InvalidOperationException("Firefox session not found.");
        var page = session.Page;
        try
        {
            await page.GotoAsync("https://claude.ai/projects", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded }).ConfigureAwait(false);
        }
        catch
        {
            try
            {
                await page.GotoAsync("https://claude.ai/", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Could not open the projects page: {ex.Message}", ex);
            }
        }

        var projectBlueprints = PortableResult.Manifest.TryGetValue("project_blueprints", out var projectBlueprintsObject) && projectBlueprintsObject is IEnumerable<object?> projectBlueprintsEnumerable
            ? projectBlueprintsEnumerable.OfType<Dictionary<string, object?>>().ToList()
            : [];

        if (projectBlueprints.Count == 0)
        {
            Log("No project blueprints were generated.", "warning");
            UpdateStep(new StepState("recreate_projects", "Recreate Projects in New Account", "Use the generated blueprints to recreate Claude projects in the new account.", StepStatus.Done, 100, "No project blueprints"));
            return;
        }

        var total = Math.Max(1, projectBlueprints.Count);
        for (var index = 0; index < projectBlueprints.Count; index++)
        {
            var blueprint = projectBlueprints[index];
            var projectName = ReadValue(blueprint, "name") ?? ReadValue(blueprint, "slug") ?? $"Project {index + 1}";
            Log($"Recreating project blueprint: {projectName}");
            try
            {
                await BrowserManager.ClickCandidatesAsync(page, new[] { "Create project", "New project", "Create", "Add project" }, cancellationToken: cancellationToken).ConfigureAwait(false);
                await BrowserManager.FillFirstTextControlAsync(page, projectName, cancellationToken).ConfigureAwait(false);
                Log($"Blueprint staged for {projectName}.");
            }
            catch (Exception ex)
            {
                Log($"Best-effort project recreation failed for {projectName}: {ex.Message}", "warning");
            }

            UpdateStep(new StepState("recreate_projects", "Recreate Projects in New Account", "Use the generated blueprints to recreate Claude projects in the new account.", StepStatus.Running, 10 + (int)(((index + 1) / (double)total) * 80), $"Processed {projectName}"));
            await Task.Yield();
        }

        UpdateStep(new StepState("recreate_projects", "Recreate Projects in New Account", "Use the generated blueprints to recreate Claude projects in the new account.", StepStatus.Done, 100, "Project recreation step finished"));
    }

    private async Task InjectSeedPromptsAsync(CancellationToken cancellationToken)
    {
        if (PortableResult is null)
        {
            return;
        }

        var session = BrowserManager.GetSession("firefox_new") ?? throw new InvalidOperationException("Firefox session not found.");
        var page = session.Page;
        try
        {
            await page.GotoAsync("https://claude.ai/", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded }).ConfigureAwait(false);
        }
        catch
        {
        }

        var seedPrompts = PortableResult.Manifest.TryGetValue("seed_prompts", out var seedPromptsObject) && seedPromptsObject is IEnumerable<object?> seedPromptsEnumerable
            ? seedPromptsEnumerable.OfType<Dictionary<string, object?>>().ToList()
            : [];

        if (seedPrompts.Count == 0)
        {
            Log("No seed prompts were generated.", "warning");
            UpdateStep(new StepState("inject_seeds", "Inject Conversation Seeds", "Create continuation chats using the seed prompts generated from the export.", StepStatus.Done, 100, "No seed prompts"));
            return;
        }

        var total = Math.Max(1, seedPrompts.Count);
        for (var index = 0; index < seedPrompts.Count; index++)
        {
            var item = seedPrompts[index];
            var projectName = ReadValue(item, "project_name") ?? $"Project {index + 1}";
            var prompt = ReadValue(item, "prompt").Trim();
            if (string.IsNullOrWhiteSpace(prompt))
            {
                continue;
            }

            Log($"Injecting seed prompt for {projectName}");
            try
            {
                await BrowserManager.ClickCandidatesAsync(page, new[] { "New chat", "Start new chat", "New conversation", "Chat" }, cancellationToken: cancellationToken).ConfigureAwait(false);
                var typed = await BrowserManager.FillFirstTextControlAsync(page, prompt[..Math.Min(7000, prompt.Length)], cancellationToken).ConfigureAwait(false);
                if (!typed)
                {
                    Log($"No obvious composer was found for {projectName}; keeping the prompt in the notes file.", "warning");
                }
                else
                {
                    await BrowserManager.ClickCandidatesAsync(page, new[] { "Send", "Start", "Submit" }, cancellationToken: cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Log($"Best-effort seed injection failed for {projectName}: {ex.Message}", "warning");
            }

            UpdateStep(new StepState("inject_seeds", "Inject Conversation Seeds", "Create continuation chats using the seed prompts generated from the export.", StepStatus.Running, 10 + (int)(((index + 1) / (double)total) * 80), $"Processed {projectName}"));
            await Task.Yield();
        }

        UpdateStep(new StepState("inject_seeds", "Inject Conversation Seeds", "Create continuation chats using the seed prompts generated from the export.", StepStatus.Done, 100, "Seed injection step finished"));
    }

    private async Task ConfigureEdgeDefaultAsync(CancellationToken cancellationToken)
    {
        var session = BrowserManager.GetSession("edge_original") ?? throw new InvalidOperationException("Edge session not found.");
        var page = session.Page;

        try
        {
            await page.GotoAsync("edge://settings/defaultBrowser", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded }).ConfigureAwait(false);
        }
        catch
        {
            try
            {
                await page.GotoAsync("edge://settings", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded }).ConfigureAwait(false);
            }
            catch
            {
            }
        }

        Log("Use the opened Edge settings page to make Microsoft Edge the default browser for Windows, then continue.", "warning");
        RequestManualAction(new ManualAction(
            StepId: "configure_edge",
            Label: "Continue After Setting Default Browser",
            Message: "Set Microsoft Edge as the default browser for the new account, then click Continue.",
            Kind: "continue"));
        UpdateStep(new StepState("configure_edge", "Configure Edge for New Account", "Open the new account in Edge and guide the user through the default-browser step.", StepStatus.Waiting, 85, "Set Edge as the default browser, then continue."));
        await WaitForContinueAsync().ConfigureAwait(false);
        UpdateStep(new StepState("configure_edge", "Configure Edge for New Account", "Open the new account in Edge and guide the user through the default-browser step.", StepStatus.Done, 100, "Default browser guidance complete"));
    }

    private void RequestManualAction(ManualAction action)
    {
        _pendingManualAction = action;
        _continueSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ManualActionRequested(action);
    }

    private void UpdateStep(StepState state)
    {
        _stepStates[state.StepId] = state;
        StepUpdated(state);
    }

    private void UpdateOverallProgress(int percent, string message)
    {
        OverallProgressChanged(percent, message);
    }

    private void WriteLogEntry(string message, string level)
    {
        WriteLogLines([message], level);
    }

    private void WriteLogLines(IEnumerable<string> lines, string level)
    {
        var stamp = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss");
        var threadName = Environment.CurrentManagedThreadId.ToString();
        var prefix = $"[{stamp}] [{level.ToUpperInvariant()}] [T{threadName}] ";
        lock (_logGate)
        {
            foreach (var line in lines)
            {
                _logWriter.WriteLine(prefix + line.TrimEnd());
            }
        }
    }

    private static IReadOnlyList<StepDefinition> BuildStepDefinitions()
    {
        return new[]
        {
            new StepDefinition("build_source_bundle", "Build Source Bundle", "Snapshot the local Claude profile."),
            new StepDefinition("restore_local_bundle", "Restore Local Bundle", "Restore the snapshot to the selected targets."),
            new StepDefinition("setup_browsers", "Setup Browsers & Sessions", "Open Edge and Firefox and wait for login."),
            new StepDefinition("trigger_export", "Trigger Official Export", "Open Claude settings and start export."),
            new StepDefinition("prepare_download", "Download & Prepare Export", "Find the exported ZIP and stage it."),
            new StepDefinition("import_memory", "Import Memory to New Account", "Use Firefox to import the prepared memory bundle into the new Claude account."),
            new StepDefinition("recreate_projects", "Recreate Projects in New Account", "Use the generated blueprints to recreate Claude projects in the new account."),
            new StepDefinition("inject_seeds", "Inject Conversation Seeds", "Create continuation chats using the seed prompts generated from the export."),
            new StepDefinition("configure_edge", "Configure Edge for New Account", "Open the new account in Edge and guide the user through the default-browser step."),
        };
    }

    private string ResolveExportZip(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            var path = System.IO.Path.GetFullPath(explicitPath);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"Export ZIP does not exist: {path}");
            }

            return path;
        }

        if (!string.IsNullOrWhiteSpace(SelectedExportZip) && File.Exists(SelectedExportZip))
        {
            return SelectedExportZip;
        }

        var latest = Paths.FindLatestExportZip();
        if (string.IsNullOrWhiteSpace(latest))
        {
            throw new FileNotFoundException("No export ZIP found. Select one before starting the migration.");
        }

        return latest;
    }

    private static string FormatArtifact(object? value)
        => value switch
        {
            null => string.Empty,
            string text => text,
            _ => value.ToString() ?? string.Empty,
        };

    private static string? ReadValue(Dictionary<string, object?> root, string key)
    {
        if (!root.TryGetValue(key, out var value) || value is null)
        {
            return string.Empty;
        }

        return value switch
        {
            string text => text,
            JsonElement element => element.ValueKind == JsonValueKind.String ? element.GetString() ?? string.Empty : element.ToString(),
            _ => value.ToString() ?? string.Empty,
        };
    }
}
