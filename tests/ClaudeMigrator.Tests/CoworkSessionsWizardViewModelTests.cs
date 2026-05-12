using System;
using System.Collections.Generic;
using System.IO;
using ClaudeMigrator.App.ViewModels.Wizards;
using ClaudeMigrator.Core.Local;
using ClaudeMigrator.Tests.TestSupport;

namespace ClaudeMigrator.Tests;

public sealed class CoworkSessionsWizardViewModelTests
{
    private static readonly ClaudeOauthAccount Source = new(
        AccountUuid: "8e003dee-a2c8-4173-a458-d6a77819ebbb",
        EmailAddress: "ninja@thesharp.ninja",
        DisplayName: "Sharp Ninja",
        OrganizationUuid: "ff532a36-c1b0-428f-9164-e7c383dfd3da",
        OrganizationName: "ninja@thesharp.ninja's Organization",
        SourceFile: "backup.json",
        SourceTimestampUtc: DateTimeOffset.UtcNow);

    private static readonly ClaudeOauthAccount Target = new(
        AccountUuid: "118701b6-cb3e-4953-bf39-9546781751b8",
        EmailAddress: "plbyrd@gmail.com",
        DisplayName: "Payton",
        OrganizationUuid: "dc52499b-5e9e-4149-a90d-f6fe5c165c7b",
        OrganizationName: "plbyrd@gmail.com's Organization",
        SourceFile: ".claude.json",
        SourceTimestampUtc: DateTimeOffset.UtcNow);

    [Fact]
    public void StepsAreInExpectedOrder()
    {
        var wizard = BuildWizard();

        Assert.Equal(4, wizard.Steps.Count);
        Assert.Equal("intro", wizard.Steps[0].StepId);
        Assert.Equal("accounts", wizard.Steps[1].StepId);
        Assert.Equal("confirm", wizard.Steps[2].StepId);
        Assert.Equal("run", wizard.Steps[3].StepId);
    }

    [Fact]
    public void AccountsStepInvalidWhenSourceAndTargetMatch()
    {
        var wizard = BuildWizard();
        wizard.Next();

        wizard.SourceAccount = Target;
        wizard.TargetAccount = Target;

        Assert.False(wizard.CanGoNext);
    }

    [Fact]
    public void AccountsStepValidWithDistinctAccounts()
    {
        var wizard = BuildWizard();
        wizard.Next();

        wizard.SourceAccount = Source;
        wizard.TargetAccount = Target;

        Assert.True(wizard.CanGoNext);
    }

    [Fact]
    public void SessionPathsReflectSelectedAccounts()
    {
        using var workspace = new TestWorkspace();
        var wizard = new CoworkSessionsWizardViewModel(
            new[] { Source, Target },
            (_, _) => throw new NotSupportedException(),
            claudeAppDataRoot: workspace.Root)
        {
            SourceAccount = Source,
            TargetAccount = Target,
        };

        Assert.Equal(Path.Combine(workspace.Root, "local-agent-mode-sessions", Source.AccountUuid, Source.OrganizationUuid), wizard.SourceAgentModeSessionsPath);
        Assert.Equal(Path.Combine(workspace.Root, "claude-code-sessions", Target.AccountUuid, Target.OrganizationUuid), wizard.TargetClaudeCodeSessionsPath);
    }

    [Fact]
    public async System.Threading.Tasks.Task FinishInvokesMigrateForBothStores()
    {
        using var workspace = new TestWorkspace();
        var calls = new List<LocalAgentSessionsMigrationOptions>();
        LocalAgentSessionsMigrationResult Migrate(LocalAgentSessionsMigrationOptions options, Action<string> log)
        {
            calls.Add(options);
            log("ran");
            return new LocalAgentSessionsMigrationResult(
                SourceDirectory: "source",
                TargetDirectory: "target",
                CopiedFileCount: 10,
                SkippedFileCount: 2,
                FailedFileCount: 0,
                TotalBytesCopied: 1024,
                FailedRelativePaths: Array.Empty<string>());
        }

        var wizard = new CoworkSessionsWizardViewModel(new[] { Source, Target }, Migrate, claudeAppDataRoot: workspace.Root)
        {
            SourceAccount = Source,
            TargetAccount = Target,
        };
        wizard.Next();
        wizard.Next();
        wizard.Next();

        await wizard.FinishCommand.ExecuteAsync(null);

        Assert.Equal(2, calls.Count);
        Assert.Contains(calls, options => options.SessionsRoot!.EndsWith("local-agent-mode-sessions", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(calls, options => options.SessionsRoot!.EndsWith("claude-code-sessions", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(20, wizard.TotalCopiedCount);
        Assert.Equal(4, wizard.TotalSkippedCount);
        Assert.True(wizard.HasCompleted);
        Assert.False(wizard.HasFailed);
    }

    [Fact]
    public async System.Threading.Tasks.Task FinishTreatsMissingSourceFolderAsSkippable()
    {
        using var workspace = new TestWorkspace();
        var calls = 0;
        LocalAgentSessionsMigrationResult Migrate(LocalAgentSessionsMigrationOptions options, Action<string> log)
        {
            calls++;
            throw new DirectoryNotFoundException($"missing: {options.SessionsRoot}");
        }

        var wizard = new CoworkSessionsWizardViewModel(new[] { Source, Target }, Migrate, claudeAppDataRoot: workspace.Root)
        {
            SourceAccount = Source,
            TargetAccount = Target,
        };
        wizard.Next();
        wizard.Next();
        wizard.Next();

        await wizard.FinishCommand.ExecuteAsync(null);

        Assert.Equal(2, calls);
        Assert.True(wizard.HasCompleted);
        Assert.True(wizard.HasFailed);
        Assert.Contains("No source folders", wizard.ResultMessage);
    }

    [Fact]
    public async System.Threading.Tasks.Task FinishPassesDryRunFlag()
    {
        using var workspace = new TestWorkspace();
        LocalAgentSessionsMigrationOptions? lastOptions = null;
        LocalAgentSessionsMigrationResult Migrate(LocalAgentSessionsMigrationOptions options, Action<string> log)
        {
            lastOptions = options;
            return new LocalAgentSessionsMigrationResult("src", "dst", 5, 0, 0, 100, Array.Empty<string>());
        }

        var wizard = new CoworkSessionsWizardViewModel(new[] { Source, Target }, Migrate, claudeAppDataRoot: workspace.Root)
        {
            SourceAccount = Source,
            TargetAccount = Target,
            DryRun = true,
        };
        wizard.Next();
        wizard.Next();
        wizard.Next();

        await wizard.FinishCommand.ExecuteAsync(null);

        Assert.NotNull(lastOptions);
        Assert.True(lastOptions!.DryRun);
        Assert.Contains("Dry run complete", wizard.ResultMessage);
    }

    private static CoworkSessionsWizardViewModel BuildWizard()
        => new(new[] { Source, Target }, (_, _) => throw new NotSupportedException(), claudeAppDataRoot: Path.GetTempPath());
}
