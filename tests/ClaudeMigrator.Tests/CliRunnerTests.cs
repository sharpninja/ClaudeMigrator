using ClaudeMigrator.App.Cli;
using ClaudeMigrator.Tests.TestSupport;
using System.IO.Compression;
using System.Text.Json;

namespace ClaudeMigrator.Tests;

public sealed class CliRunnerTests
{
    [Fact]
    public void BuildSourceBundleCliCreatesLocalBundleArtifacts()
    {
        using var workspace = new TestWorkspace();
        var home = SampleData.CreateSampleLocalHome(workspace.Root);
        var destinationHome = Path.Combine(workspace.Root, "restore_home");
        Directory.CreateDirectory(destinationHome);
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
                "--source-account",
                "source@example.com",
                "--target-account",
                "target@example.com",
                "--destination-home",
                destinationHome,
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

            var exportedZip = Directory.EnumerateFiles(Path.Combine(runtimeRoot, "local_bundles"), "*.zip", SearchOption.TopDirectoryOnly).Single();
            using var archive = ZipFile.OpenRead(exportedZip);
            var manifestEntry = archive.Entries.Single(entry => entry.FullName.EndsWith("manifest.json", StringComparison.OrdinalIgnoreCase));
            using var manifestStream = manifestEntry.Open();
            using var manifestDocument = JsonDocument.Parse(manifestStream);
            Assert.Equal("source@example.com", manifestDocument.RootElement.GetProperty("source_account_name").GetString());
            Assert.Equal("target@example.com", manifestDocument.RootElement.GetProperty("target_account_name").GetString());
            Assert.Equal(destinationHome, manifestDocument.RootElement.GetProperty("destination_home").GetString());
        }
        finally
        {
            Directory.SetCurrentDirectory(originalCurrentDirectory);
        }
    }
}
