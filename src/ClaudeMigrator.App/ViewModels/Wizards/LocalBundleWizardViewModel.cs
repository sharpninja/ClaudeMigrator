using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ClaudeMigrator.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ClaudeMigrator.App.ViewModels.Wizards;

public sealed partial class LocalBundleWizardViewModel : WizardViewModelBase
{
    public delegate Task<LocalBundleResult> BuildDelegate(
        MigrationOptions options,
        Action<string> log,
        CancellationToken cancellationToken);

    public delegate Task RestoreDelegate(
        string zipPath,
        IReadOnlyList<TargetApp> targetApps,
        string destinationHome,
        Action<string> log,
        CancellationToken cancellationToken);

    private readonly BuildDelegate _build;
    private readonly RestoreDelegate _restore;

    public LocalBundleWizardViewModel(BuildDelegate build, RestoreDelegate restore)
        : base(
            title: "Local Profile Bundle",
            subtitle: "Snapshot ~/.claude into a portable bundle, then restore it under a destination home folder.",
            steps: CreateSteps())
    {
        _build = build ?? throw new ArgumentNullException(nameof(build));
        _restore = restore ?? throw new ArgumentNullException(nameof(restore));

        SourceHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        DestinationHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    private static IReadOnlyList<WizardStepViewModel> CreateSteps() =>
        new[]
        {
            new WizardStepViewModel(
                "intro",
                "Overview",
                "Capture the Claude profile under ~/.claude into a portable bundle, then unpack it under a destination home folder. Useful for moving between machines or accounts on the same machine."),
            new WizardStepViewModel(
                "paths",
                "Source and destination",
                "Pick the source home folder (the one that contains the existing ~/.claude) and the destination home folder (where the bundle will be restored)."),
            new WizardStepViewModel(
                "accounts",
                "Account labels and target apps",
                "Annotate the bundle with source and target account labels. Choose whether to restore for Claude, Codex, or both."),
            new WizardStepViewModel(
                "confirm",
                "Confirm",
                "Review the plan. The bundle is always built. The restore step runs only when 'Restore after build' is enabled."),
            new WizardStepViewModel(
                "run",
                "Run",
                "Execute build and optional restore. Output appears below."),
        };

    [ObservableProperty]
    private string sourceHome = string.Empty;

    [ObservableProperty]
    private string destinationHome = string.Empty;

    [ObservableProperty]
    private string sourceAccountLabel = string.Empty;

    [ObservableProperty]
    private string targetAccountLabel = string.Empty;

    [ObservableProperty]
    private bool targetClaude = true;

    [ObservableProperty]
    private bool targetCodex = true;

    [ObservableProperty]
    private bool restoreAfterBuild = true;

    [ObservableProperty]
    private string bundleZipPath = string.Empty;

    [ObservableProperty]
    private string restoreSummary = string.Empty;

    partial void OnSourceHomeChanged(string value) => RaiseValidationChanged();

    partial void OnDestinationHomeChanged(string value) => RaiseValidationChanged();

    partial void OnTargetClaudeChanged(bool value) => RaiseValidationChanged();

    partial void OnTargetCodexChanged(bool value) => RaiseValidationChanged();

    protected override bool IsStepValid(int index)
        => index switch
        {
            1 => !string.IsNullOrWhiteSpace(SourceHome) && Directory.Exists(SourceHome)
                 && !string.IsNullOrWhiteSpace(DestinationHome),
            2 => TargetClaude || TargetCodex,
            _ => true,
        };

    protected override async Task<WizardResult> ExecuteAsync(IProgress<string> log, CancellationToken cancellationToken)
    {
        var targetApps = new List<TargetApp>();
        if (TargetClaude) targetApps.Add(TargetApp.Claude);
        if (TargetCodex) targetApps.Add(TargetApp.Codex);
        if (targetApps.Count == 0) targetApps.Add(TargetApp.Claude);

        var options = new MigrationOptions
        {
            SourceMode = SourceMode.LocalSnapshot,
            SourceHome = SourceHome,
            DestinationHome = DestinationHome,
            SourceAccount = SourceAccountLabel,
            TargetAccount = TargetAccountLabel,
            TargetApps = targetApps,
        };

        log.Report($"Building local bundle from {options.SourceHome}");
        var bundle = await _build(options, line => log.Report(line), cancellationToken).ConfigureAwait(false);
        BundleZipPath = bundle.ZipPath;
        log.Report($"Bundle written: {bundle.ZipPath}");

        if (!RestoreAfterBuild)
        {
            return new WizardResult(true, $"Bundle built: {bundle.ZipPath}. Restore skipped.");
        }

        log.Report($"Restoring bundle to {DestinationHome}");
        await _restore(bundle.ZipPath, targetApps, DestinationHome, line => log.Report(line), cancellationToken).ConfigureAwait(false);
        RestoreSummary = $"Restored to {DestinationHome}";
        return new WizardResult(true, $"Bundle built and restored to {DestinationHome}.");
    }
}
