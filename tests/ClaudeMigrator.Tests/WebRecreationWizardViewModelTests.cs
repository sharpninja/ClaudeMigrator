using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ClaudeMigrator.App.ViewModels.Wizards;
using ClaudeMigrator.Core.Web;
using ClaudeMigrator.Tests.TestSupport;

namespace ClaudeMigrator.Tests;

public sealed class WebRecreationWizardViewModelTests
{
    [Fact]
    public void ExportStepInvalidWithMissingFile()
    {
        var wizard = BuildWizard();
        wizard.Next();

        wizard.ExportZipPath = @"C:\does\not\exist.zip";

        Assert.False(wizard.CanGoNext);
    }

    [Fact]
    public void ExportStepValidWithRealFile()
    {
        using var workspace = new TestWorkspace();
        var path = Path.Combine(workspace.Root, "export.zip");
        File.WriteAllText(path, "zip");
        var wizard = BuildWizard();
        wizard.Next();

        wizard.ExportZipPath = path;

        Assert.True(wizard.CanGoNext);
    }

    [Fact]
    public void EdgeStepValidationRequiresHttpUrl()
    {
        var wizard = BuildWizard();
        wizard.Next();
        wizard.ExportZipPath = CreateZip();
        wizard.Next();

        wizard.EdgeDebugUrl = "not a url";
        Assert.False(wizard.CanGoNext);

        wizard.EdgeDebugUrl = "ftp://example.com";
        Assert.False(wizard.CanGoNext);

        wizard.EdgeDebugUrl = "http://127.0.0.1:9222";
        Assert.True(wizard.CanGoNext);
    }

    [Fact]
    public async Task FinishInvokesRecreateWithBoundOptions()
    {
        using var workspace = new TestWorkspace();
        var zip = Path.Combine(workspace.Root, "export.zip");
        File.WriteAllText(zip, "zip");
        ClaudeWebRecreationOptions? captured = null;

        var wizard = new WebRecreationWizardViewModel(
            (options, log, _) =>
            {
                captured = options;
                log("hello");
                return Task.FromResult(new ClaudeWebRecreationResult(
                    ManifestPath: options.OutputManifestPath,
                    TargetOrganizationUuid: "target",
                    TargetOrganizationName: "target-org",
                    SourceConversationCount: 5,
                    SourceConversationMessageCount: 20,
                    SourceProjectCount: 2,
                    CreatedConversationCount: 5,
                    ExistingConversationCount: 0,
                    CreatedProjectCount: 2,
                    ExistingProjectCount: 0,
                    CreatedDocCount: 7,
                    ExistingDocCount: 0,
                    FailedOperationCount: 0));
            },
            defaultOutputFolder: workspace.Root)
        {
            ExportZipPath = zip,
            EdgeDebugUrl = "http://127.0.0.1:9222",
            OutputManifestPath = Path.Combine(workspace.Root, "manifest.json"),
            DryRun = false,
        };

        for (var i = 0; i < 5; i++)
        {
            wizard.Next();
        }

        await wizard.FinishCommand.ExecuteAsync(null);

        Assert.NotNull(captured);
        Assert.Equal(Path.GetFullPath(zip), captured!.ExportZipPath);
        Assert.Equal("http://127.0.0.1:9222", captured.EdgeDebugUrl);
        Assert.Equal(2, wizard.CreatedProjectCount);
        Assert.Equal(7, wizard.CreatedDocCount);
        Assert.False(wizard.HasFailed);
    }

    [Fact]
    public async Task FinishReportsFailuresWhenAnyOperationFailed()
    {
        using var workspace = new TestWorkspace();
        var zip = Path.Combine(workspace.Root, "export.zip");
        File.WriteAllText(zip, "zip");

        var wizard = new WebRecreationWizardViewModel(
            (options, _, _) => Task.FromResult(new ClaudeWebRecreationResult(
                ManifestPath: options.OutputManifestPath,
                TargetOrganizationUuid: "t",
                TargetOrganizationName: "t-org",
                SourceConversationCount: 1,
                SourceConversationMessageCount: 1,
                SourceProjectCount: 1,
                CreatedConversationCount: 0,
                ExistingConversationCount: 0,
                CreatedProjectCount: 0,
                ExistingProjectCount: 0,
                CreatedDocCount: 0,
                ExistingDocCount: 0,
                FailedOperationCount: 3)),
            defaultOutputFolder: workspace.Root)
        {
            ExportZipPath = zip,
            EdgeDebugUrl = "http://127.0.0.1:9222",
            OutputManifestPath = Path.Combine(workspace.Root, "manifest.json"),
        };

        for (var i = 0; i < 5; i++)
        {
            wizard.Next();
        }

        await wizard.FinishCommand.ExecuteAsync(null);

        Assert.True(wizard.HasFailed);
        Assert.Equal(3, wizard.FailedOperationCount);
    }

    private static WebRecreationWizardViewModel BuildWizard()
        => new(
            (_, _, _) => Task.FromResult(new ClaudeWebRecreationResult("m", "t", "t-org", 0, 0, 0, 0, 0, 0, 0, 0, 0, 0)),
            defaultOutputFolder: Path.GetTempPath());

    private static string CreateZip()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, "stub");
        return path;
    }
}
