using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ClaudeMigrator.App.ViewModels.Wizards;
using ClaudeMigrator.Core.Models;
using ClaudeMigrator.Tests.TestSupport;

namespace ClaudeMigrator.Tests;

public sealed class LocalBundleWizardViewModelTests
{
    [Fact]
    public void PathsStepInvalidWhenSourceMissing()
    {
        var wizard = BuildWizard();
        wizard.Next();

        wizard.SourceHome = @"C:\does\not\exist";
        wizard.DestinationHome = @"C:\Users\test";

        Assert.False(wizard.CanGoNext);
    }

    [Fact]
    public void PathsStepValidWhenSourceExists()
    {
        using var workspace = new TestWorkspace();
        var wizard = BuildWizard();
        wizard.Next();

        wizard.SourceHome = workspace.Root;
        wizard.DestinationHome = workspace.Root;

        Assert.True(wizard.CanGoNext);
    }

    [Fact]
    public void AccountsStepRequiresAtLeastOneTargetApp()
    {
        using var workspace = new TestWorkspace();
        var wizard = BuildWizard();
        wizard.SourceHome = workspace.Root;
        wizard.DestinationHome = workspace.Root;
        wizard.Next();
        wizard.Next();

        wizard.TargetClaude = false;
        wizard.TargetCodex = false;
        Assert.False(wizard.CanGoNext);

        wizard.TargetCodex = true;
        Assert.True(wizard.CanGoNext);
    }

    [Fact]
    public async Task FinishBuildsAndRestoresByDefault()
    {
        using var workspace = new TestWorkspace();
        var builds = 0;
        var restores = 0;
        MigrationOptions? capturedOptions = null;

        var wizard = new LocalBundleWizardViewModel(
            build: (options, log, _) =>
            {
                builds++;
                capturedOptions = options;
                log("built");
                return Task.FromResult(new LocalBundleResult(
                    SourceHome: options.SourceHome,
                    DestinationHome: options.DestinationHome,
                    ProfileRoot: options.SourceHome,
                    AccountFile: null,
                    BundleRoot: workspace.Root,
                    ZipPath: Path.Combine(workspace.Root, "bundle.zip"),
                    ManifestPath: Path.Combine(workspace.Root, "manifest.json"),
                    SourceEnvironmentPath: Path.Combine(workspace.Root, "env.json"),
                    SourceAccountPath: null,
                    RestorePlanPath: Path.Combine(workspace.Root, "restore.json"),
                    Targets: new List<string> { "claude" },
                    Manifest: new Dictionary<string, object?>(),
                    Counts: new Dictionary<string, int>()));
            },
            restore: (_, _, _, log, _) =>
            {
                restores++;
                log("restored");
                return Task.CompletedTask;
            })
        {
            SourceHome = workspace.Root,
            DestinationHome = workspace.Root,
            SourceAccountLabel = "old@example.com",
            TargetAccountLabel = "new@example.com",
        };

        wizard.Next();
        wizard.Next();
        wizard.Next();
        wizard.Next();

        await wizard.FinishCommand.ExecuteAsync(null);

        Assert.Equal(1, builds);
        Assert.Equal(1, restores);
        Assert.NotNull(capturedOptions);
        Assert.Equal(SourceMode.LocalSnapshot, capturedOptions!.SourceMode);
        Assert.Equal("old@example.com", capturedOptions.SourceAccount);
        Assert.True(wizard.HasCompleted);
        Assert.False(wizard.HasFailed);
        Assert.Contains("bundle.zip", wizard.BundleZipPath);
    }

    [Fact]
    public async Task FinishSkipsRestoreWhenDisabled()
    {
        using var workspace = new TestWorkspace();
        var restores = 0;

        var wizard = new LocalBundleWizardViewModel(
            build: (options, _, _) => Task.FromResult(new LocalBundleResult(
                SourceHome: options.SourceHome,
                DestinationHome: options.DestinationHome,
                ProfileRoot: options.SourceHome,
                AccountFile: null,
                BundleRoot: workspace.Root,
                ZipPath: Path.Combine(workspace.Root, "bundle.zip"),
                ManifestPath: Path.Combine(workspace.Root, "manifest.json"),
                SourceEnvironmentPath: Path.Combine(workspace.Root, "env.json"),
                SourceAccountPath: null,
                RestorePlanPath: Path.Combine(workspace.Root, "restore.json"),
                Targets: new List<string> { "claude" },
                Manifest: new Dictionary<string, object?>(),
                Counts: new Dictionary<string, int>())),
            restore: (_, _, _, _, _) =>
            {
                restores++;
                return Task.CompletedTask;
            })
        {
            SourceHome = workspace.Root,
            DestinationHome = workspace.Root,
            RestoreAfterBuild = false,
        };

        wizard.Next();
        wizard.Next();
        wizard.Next();
        wizard.Next();

        await wizard.FinishCommand.ExecuteAsync(null);

        Assert.Equal(0, restores);
        Assert.Contains("Restore skipped", wizard.ResultMessage);
    }

    private static LocalBundleWizardViewModel BuildWizard()
        => new(
            build: (options, _, _) => Task.FromResult(new LocalBundleResult(
                SourceHome: options.SourceHome,
                DestinationHome: options.DestinationHome,
                ProfileRoot: options.SourceHome,
                AccountFile: null,
                BundleRoot: string.Empty,
                ZipPath: string.Empty,
                ManifestPath: string.Empty,
                SourceEnvironmentPath: string.Empty,
                SourceAccountPath: null,
                RestorePlanPath: string.Empty,
                Targets: Array.Empty<string>(),
                Manifest: new Dictionary<string, object?>(),
                Counts: new Dictionary<string, int>())),
            restore: (_, _, _, _, _) => Task.CompletedTask);
}
