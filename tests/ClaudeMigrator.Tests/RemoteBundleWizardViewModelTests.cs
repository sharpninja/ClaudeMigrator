using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClaudeMigrator.App.ViewModels.Wizards;
using ClaudeMigrator.Core.RemoteTargets;

namespace ClaudeMigrator.Tests;

public sealed class RemoteBundleWizardViewModelTests
{
    [Fact]
    public void MachineStepInvalidWithoutSelection()
    {
        var wizard = new RemoteBundleWizardViewModel(
            Array.Empty<RemoteMachineSpec>(),
            (_, _) => "command",
            (_, _) => Task.FromResult(true));
        wizard.Next();

        Assert.Null(wizard.SelectedMachine);
        Assert.False(wizard.CanGoNext);
    }

    [Fact]
    public void MachineStepValidAfterSelection()
    {
        var spec = SampleMachine();
        var wizard = new RemoteBundleWizardViewModel(
            new[] { spec },
            (_, _) => "command",
            (_, _) => Task.FromResult(true));
        wizard.Next();

        Assert.Same(spec, wizard.SelectedMachine);
        Assert.True(wizard.CanGoNext);
    }

    [Fact]
    public void OptionsStepRequiresAtLeastOneTargetApp()
    {
        var wizard = new RemoteBundleWizardViewModel(
            new[] { SampleMachine() },
            (_, _) => "command",
            (_, _) => Task.FromResult(true));
        wizard.Next();
        wizard.Next();

        wizard.TargetClaude = false;
        wizard.TargetCodex = false;
        Assert.False(wizard.CanGoNext);

        wizard.TargetClaude = true;
        Assert.True(wizard.CanGoNext);
    }

    [Fact]
    public async Task FinishBuildsCommandAndCopiesToClipboard()
    {
        var spec = SampleMachine();
        IEnumerable<string>? capturedApps = null;
        RemoteMachineSpec? capturedSpec = null;
        var clipboardCalls = 0;
        string? lastClipboardText = null;

        var wizard = new RemoteBundleWizardViewModel(
            new[] { spec },
            (machine, apps) =>
            {
                capturedSpec = machine;
                capturedApps = apps;
                return "dotnet run -- --build-source-bundle";
            },
            (text, _) =>
            {
                clipboardCalls++;
                lastClipboardText = text;
                return Task.FromResult(true);
            });

        wizard.Next();
        wizard.Next();
        wizard.Next();

        await wizard.FinishCommand.ExecuteAsync(null);

        Assert.NotNull(capturedSpec);
        Assert.Equal(spec.MachineId, capturedSpec!.MachineId);
        Assert.NotNull(capturedApps);
        Assert.Equal(new[] { "claude", "codex" }, capturedApps!.ToArray());
        Assert.Equal(1, clipboardCalls);
        Assert.Equal("dotnet run -- --build-source-bundle", lastClipboardText);
        Assert.True(wizard.CopiedToClipboard);
        Assert.False(wizard.HasFailed);
    }

    [Fact]
    public async Task FinishReportsClipboardUnavailable()
    {
        var wizard = new RemoteBundleWizardViewModel(
            new[] { SampleMachine() },
            (_, _) => "command",
            (_, _) => Task.FromResult(false));

        wizard.Next();
        wizard.Next();
        wizard.Next();

        await wizard.FinishCommand.ExecuteAsync(null);

        Assert.True(wizard.HasCompleted);
        Assert.False(wizard.CopiedToClipboard);
        Assert.Contains("manually", wizard.ResultMessage);
    }

    private static RemoteMachineSpec SampleMachine()
        => new RemoteMachineSpec
        {
            MachineId = "lab-box",
            DisplayName = "Lab Box",
            Host = "lab.example.com",
            ConnectionMethod = "ssh",
            RepoRoot = "/home/dev/ClaudeMigrator",
            Username = "dev",
        }.Normalized();
}
