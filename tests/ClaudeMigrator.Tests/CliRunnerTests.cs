using ClaudeMigrator.App.Cli;
using ClaudeMigrator.Tests.TestSupport;

namespace ClaudeMigrator.Tests;

public sealed class CliRunnerTests
{
    [Fact]
    public void BuildSourceBundleCliCreatesLocalBundleArtifacts()
    {
        using var workspace = new TestWorkspace();
        var home = SampleData.CreateSampleLocalHome(workspace.Root);
        var originalCurrentDirectory = Directory.GetCurrentDirectory();

        try
        {
            Directory.SetCurrentDirectory(workspace.Root);

            var handled = CliRunner.TryRun(new[]
            {
                "--build-source-bundle",
                "--source-home",
                home,
                "--source-machine-name",
                "LAB-CLI",
                "--source-host",
                "lab-cli.example.com",
                "--connection-method",
                "ssh",
                "--source-user",
                "kingd",
                "--source-repo-root",
                @"F:\GitHub\ClaudeMigrator",
                "--target-apps",
                "claude,codex",
            });

            Assert.True(handled);
            Assert.Equal(0, Environment.ExitCode);

            var runtimeRoot = Path.Combine(workspace.Root, "migration_data");
            var logRoot = Path.Combine(runtimeRoot, "logs");
            var bundleRoot = Path.Combine(runtimeRoot, "processing");
            Assert.True(Directory.Exists(logRoot));
            Assert.True(Directory.Exists(bundleRoot));
            Assert.NotEmpty(Directory.EnumerateFiles(Path.Combine(runtimeRoot, "local_bundles"), "*.zip", SearchOption.TopDirectoryOnly));
            Assert.NotEmpty(Directory.EnumerateFiles(logRoot, "*.log", SearchOption.TopDirectoryOnly));
            Assert.NotEmpty(Directory.EnumerateFiles(logRoot, "*_startup.json", SearchOption.TopDirectoryOnly));
        }
        finally
        {
            Directory.SetCurrentDirectory(originalCurrentDirectory);
        }
    }
}
