using System.Text.Json;
using ClaudeMigrator.Core.Migration;
using ClaudeMigrator.Core.Models;
using ClaudeMigrator.Core.Paths;
using ClaudeMigrator.Tests.TestSupport;

namespace ClaudeMigrator.Tests;

public sealed class MigrationControllerTests
{
    [Fact]
    public async Task LocalSnapshotMigrationWritesLogsAndBundles()
    {
        using var workspace = new TestWorkspace();
        var home = SampleData.CreateSampleLocalHome(workspace.Root);
        var destinationHome = Path.Combine(workspace.Root, "restore_home");
        Directory.CreateDirectory(destinationHome);
        var paths = new AppPaths(workspace.Root).Ensure();
        var logs = new List<(string Level, string Message)>();
        var progress = new List<(int Percent, string Message)>();
        var states = new List<string>();
        var artifacts = new List<(string Key, object? Value)>();

        using var controller = new MigrationController(paths);
        controller.LogMessage = (level, message) => logs.Add((level, message));
        controller.OverallProgressChanged = (percent, message) => progress.Add((percent, message));
        controller.RunStateChanged = state => states.Add(state);
        controller.ArtifactRecorded = (key, value) => artifacts.Add((key, value));

        await controller.StartFullMigrationAsync(new MigrationOptions
        {
            SourceMode = SourceMode.LocalSnapshot,
            SourceHome = home,
            SourceMachineName = "LAB-03",
            SourceHost = "lab-03.example.com",
            ConnectionMethod = "ssh",
            SourceUser = "kingd",
            SourceAccount = "source@example.com",
            TargetAccount = "target@example.com",
            SourceRepoRoot = @"F:\GitHub\ClaudeMigrator",
            DestinationHome = destinationHome,
            TargetApps = new[] { TargetApp.Claude, TargetApp.Codex },
        });

        Assert.True(File.Exists(controller.LogFilePath));
        Assert.True(File.Exists(controller.StartupSnapshotPath));
        Assert.NotNull(controller.LocalBundleResult);
        Assert.True(File.Exists(controller.LocalBundleResult!.ZipPath));
        Assert.Equal("source@example.com", controller.LocalBundleResult.Manifest["source_account_name"]?.ToString());
        Assert.Equal("target@example.com", controller.LocalBundleResult.Manifest["target_account_name"]?.ToString());
        Assert.Equal(destinationHome, controller.LocalBundleResult.Manifest["destination_home"]?.ToString());
        Assert.True(File.Exists(Path.Combine(destinationHome, ".claude", "CLAUDE.md")));
        Assert.True(File.Exists(Path.Combine(destinationHome, ".codex", "CLAUDE.md")));
        Assert.Contains(states, state => string.Equals(state, "running", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(states, state => string.Equals(state, "complete", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(progress, item => item.Percent >= 100);
        Assert.Contains(artifacts, item => item.Key == "local_bundle_zip");
        Assert.Contains(artifacts, item => item.Key == "local_restore_result");
        Assert.Contains(logs, item => item.Message.Contains("Local bundle", StringComparison.OrdinalIgnoreCase));

        var snapshot = JsonDocument.Parse(File.ReadAllText(controller.StartupSnapshotPath, System.Text.Encoding.UTF8));
        Assert.Equal(paths.RuntimeDir, snapshot.RootElement.GetProperty("paths").GetProperty("runtime_dir").GetString());
        Assert.Equal(controller.LogFilePath, snapshot.RootElement.GetProperty("paths").GetProperty("log_file").GetString());
        Assert.Equal("zip", snapshot.RootElement.GetProperty("source_mode").GetString());
    }
}
