using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using ClaudeMigrator.Core.Models;
using ClaudeMigrator.Core.RemoteTargets;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ClaudeMigrator.App.ViewModels.Wizards;

public sealed partial class RemoteBundleWizardViewModel : WizardViewModelBase
{
    public delegate string BuildCommandDelegate(RemoteMachineSpec spec, IEnumerable<string> targetApps);

    public delegate Task<bool> CopyToClipboardDelegate(string text, CancellationToken cancellationToken);

    private readonly BuildCommandDelegate _buildCommand;
    private readonly CopyToClipboardDelegate _copyToClipboard;

    public RemoteBundleWizardViewModel(
        IEnumerable<RemoteMachineSpec> existingMachines,
        BuildCommandDelegate buildCommand,
        CopyToClipboardDelegate copyToClipboard)
        : base(
            title: "Remote Bundle Command",
            subtitle: "Generate the command line to build a local bundle on a remote machine via ssh or wsman.",
            steps: CreateSteps())
    {
        _buildCommand = buildCommand ?? throw new ArgumentNullException(nameof(buildCommand));
        _copyToClipboard = copyToClipboard ?? throw new ArgumentNullException(nameof(copyToClipboard));
        Machines = new ObservableCollection<RemoteMachineSpec>(existingMachines ?? Array.Empty<RemoteMachineSpec>());
        if (Machines.Count > 0)
        {
            SelectedMachine = Machines[0];
        }
    }

    private static IReadOnlyList<WizardStepViewModel> CreateSteps() =>
        new[]
        {
            new WizardStepViewModel(
                "intro",
                "Overview",
                "Build a command that you can paste on the remote machine to snapshot ~/.claude and produce a bundle. Use SSH or WSMan as the transport."),
            new WizardStepViewModel(
                "machine",
                "Pick machine",
                "Choose a configured remote machine. Add and save machines from the Home view if the list is empty."),
            new WizardStepViewModel(
                "options",
                "Target apps",
                "Select which apps to write bundle restore plans for (Claude, Codex, or both)."),
            new WizardStepViewModel(
                "command",
                "Command",
                "Review the generated command and copy it to the clipboard."),
        };

    public ObservableCollection<RemoteMachineSpec> Machines { get; }

    [ObservableProperty]
    private RemoteMachineSpec? selectedMachine;

    [ObservableProperty]
    private bool targetClaude = true;

    [ObservableProperty]
    private bool targetCodex = true;

    [ObservableProperty]
    private string generatedCommand = string.Empty;

    [ObservableProperty]
    private bool copiedToClipboard;

    partial void OnSelectedMachineChanged(RemoteMachineSpec? value) => RaiseValidationChanged();

    partial void OnTargetClaudeChanged(bool value) => RaiseValidationChanged();

    partial void OnTargetCodexChanged(bool value) => RaiseValidationChanged();

    protected override bool IsStepValid(int index)
        => index switch
        {
            1 => SelectedMachine is not null && !string.IsNullOrWhiteSpace(SelectedMachine.MachineId) && !string.IsNullOrWhiteSpace(SelectedMachine.Host),
            2 => TargetClaude || TargetCodex,
            _ => true,
        };

    protected override async Task<WizardResult> ExecuteAsync(IProgress<string> log, CancellationToken cancellationToken)
    {
        if (SelectedMachine is null)
        {
            return new WizardResult(false, "Pick a remote machine first.");
        }

        var apps = new List<string>();
        if (TargetClaude) apps.Add(TargetApp.Claude.ToString().ToLowerInvariant());
        if (TargetCodex) apps.Add(TargetApp.Codex.ToString().ToLowerInvariant());
        if (apps.Count == 0) apps.Add(TargetApp.Claude.ToString().ToLowerInvariant());

        GeneratedCommand = _buildCommand(SelectedMachine, apps);
        log.Report("Built remote export command.");

        var copied = false;
        try
        {
            copied = await _copyToClipboard(GeneratedCommand, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log.Report($"Clipboard copy failed: {ex.Message}");
        }

        CopiedToClipboard = copied;
        var message = copied
            ? "Command generated and copied to clipboard."
            : "Command generated. Copy it manually from the box above.";
        return new WizardResult(true, message);
    }
}
