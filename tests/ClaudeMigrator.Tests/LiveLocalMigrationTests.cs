using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using ClaudeMigrator.Core.Migration;
using ClaudeMigrator.Core.Models;
using ClaudeMigrator.Core.Paths;
using ClaudeMigrator.Tests.TestSupport;

namespace ClaudeMigrator.Tests;

public sealed class LiveLocalMigrationTests
{
    [LiveClaudeLocalFact]
    public async Task LiveLocalSnapshotBundlesTheRealClaudeProfileAsync()
    {
        using var workspace = new TestWorkspace("live-local-snapshot");
        var appPaths = new AppPaths(workspace.Root).Ensure();
        var sourceHome = LiveLocalTestEnvironment.SourceHome;
        var destinationHome = Path.Combine(workspace.Root, "restore_home");
        Directory.CreateDirectory(destinationHome);

        var logs = new List<(string Level, string Message)>();
        var states = new List<string>();
        var artifacts = new List<(string Key, object? Value)>();

        using var controller = new MigrationController(appPaths);
        controller.LogMessage = (level, message) => logs.Add((level, message));
        controller.RunStateChanged = state => states.Add(state);
        controller.ArtifactRecorded = (key, value) => artifacts.Add((key, value));

        await controller.StartFullMigrationAsync(new MigrationOptions
        {
            SourceMode = SourceMode.LocalSnapshot,
            SourceHome = sourceHome,
            DestinationHome = destinationHome,
            SourceMachineName = Environment.MachineName,
            SourceHost = Environment.MachineName,
            ConnectionMethod = "local",
            SourceUser = Environment.UserName,
            SourceAccount = Environment.UserName,
            TargetAccount = "restored-local",
            TargetApps = new[] { TargetApp.Claude, TargetApp.Codex },
        });

        Assert.NotNull(controller.LocalBundleResult);
        var result = controller.LocalBundleResult!;
        Assert.True(File.Exists(result.ZipPath));
        Assert.True(File.Exists(result.ManifestPath));
        Assert.True(File.Exists(result.SourceEnvironmentPath));
        Assert.True(File.Exists(result.RestorePlanPath));
        Assert.Equal(sourceHome, result.SourceHome);
        Assert.Equal(destinationHome, result.DestinationHome);
        Assert.Equal(sourceHome, result.Manifest["source_home"]?.ToString());
        Assert.Equal(destinationHome, result.Manifest["destination_home"]?.ToString());
        Assert.Contains(states, state => string.Equals(state, "running", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(states, state => string.Equals(state, "complete", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(artifacts, item => item.Key == "local_bundle_zip");
        Assert.Contains(artifacts, item => item.Key == "local_restore_result");

        using var manifestDocument = JsonDocument.Parse(File.ReadAllText(result.ManifestPath, System.Text.Encoding.UTF8));
        var manifest = manifestDocument.RootElement;
        Assert.Equal("claude_local_bundle", manifest.GetProperty("format").GetString());
        Assert.Equal(sourceHome, manifest.GetProperty("source_home").GetString());
        Assert.Equal(destinationHome, manifest.GetProperty("destination_home").GetString());
        Assert.Equal("local", manifest.GetProperty("source_environment").GetProperty("connection_method").GetString());
        Assert.NotEmpty(manifest.GetProperty("source_account").GetProperty("keys").EnumerateArray());
        Assert.NotEmpty(manifest.GetProperty("source_account").GetProperty("project_paths").EnumerateArray());

        var profileFiles = Directory.EnumerateFiles(Path.Combine(result.BundleRoot, "source", "home", ".claude"), "*", SearchOption.AllDirectories).ToList();
        Assert.NotEmpty(profileFiles);
        Assert.True((result.Counts["profile_files"]) > 0);
        Assert.True((result.Counts["account_files"]) > 0);
        Assert.True((result.Counts["targets"]) == 2);
        AssertBundleZipMatchesBundleRoot(result.BundleRoot, result.ZipPath);
        AssertTreeEquals(Path.Combine(result.BundleRoot, "source", "home", ".claude"), Path.Combine(destinationHome, ".claude"));
        AssertTreeEquals(Path.Combine(result.BundleRoot, "source", "home", ".claude"), Path.Combine(destinationHome, ".codex"));
        AssertRootFilesEquals(Path.Combine(result.BundleRoot, "source", "home"), destinationHome, ".claude.json*");

        Assert.Contains(logs, item => item.Message.Contains("Local bundle destination_home", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(logs, item => item.Message.Contains("Restored local bundle", StringComparison.OrdinalIgnoreCase));
    }

    private static void AssertBundleZipMatchesBundleRoot(string bundleRoot, string zipPath)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var parent = Path.GetDirectoryName(bundleRoot) ?? throw new InvalidOperationException($"Bundle root has no parent: {bundleRoot}");

        var expectedFiles = Directory
            .EnumerateFiles(bundleRoot, "*", SearchOption.AllDirectories)
            .Select(file => NormalizeZipPath(Path.GetRelativePath(parent, file)))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var expectedEmptyDirectories = Directory
            .EnumerateDirectories(bundleRoot, "*", SearchOption.AllDirectories)
            .Where(directory => !Directory.EnumerateFileSystemEntries(directory).Any())
            .Select(directory => NormalizeZipPath(Path.GetRelativePath(parent, directory)) + "/")
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var actualFiles = archive.Entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Name))
            .Select(entry => NormalizeZipPath(entry.FullName))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var actualEmptyDirectories = archive.Entries
            .Where(entry => string.IsNullOrWhiteSpace(entry.Name))
            .Select(entry => NormalizeZipPath(entry.FullName))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.Equal(expectedFiles, actualFiles);
        Assert.Equal(expectedEmptyDirectories, actualEmptyDirectories);

        foreach (var relative in expectedFiles)
        {
            var filePath = Path.Combine(parent, relative.Replace('/', Path.DirectorySeparatorChar));
            var entry = archive.GetEntry(relative);
            Assert.NotNull(entry);
            AssertFileContentsEqual(filePath, entry!);
        }
    }

    private static void AssertTreeEquals(string expectedRoot, string actualRoot)
    {
        var expectedFiles = Directory
            .EnumerateFiles(expectedRoot, "*", SearchOption.AllDirectories)
            .Select(file => NormalizeRelativePath(Path.GetRelativePath(expectedRoot, file)))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var actualFiles = Directory
            .EnumerateFiles(actualRoot, "*", SearchOption.AllDirectories)
            .Select(file => NormalizeRelativePath(Path.GetRelativePath(actualRoot, file)))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.Equal(expectedFiles, actualFiles);

        var expectedDirectories = Directory
            .EnumerateDirectories(expectedRoot, "*", SearchOption.AllDirectories)
            .Select(directory => NormalizeRelativePath(Path.GetRelativePath(expectedRoot, directory)))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var actualDirectories = Directory
            .EnumerateDirectories(actualRoot, "*", SearchOption.AllDirectories)
            .Select(directory => NormalizeRelativePath(Path.GetRelativePath(actualRoot, directory)))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.Equal(expectedDirectories, actualDirectories);

        foreach (var relative in expectedFiles)
        {
            var expectedFile = Path.Combine(expectedRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            var actualFile = Path.Combine(actualRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            AssertFileContentsEqual(expectedFile, actualFile);
        }
    }

    private static void AssertRootFilesEquals(string expectedRoot, string actualRoot, string searchPattern)
    {
        var expectedFiles = Directory
            .EnumerateFiles(expectedRoot, searchPattern, SearchOption.TopDirectoryOnly)
            .Select(file => Path.GetFileName(file) ?? string.Empty)
            .Where(fileName => !string.IsNullOrWhiteSpace(fileName))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var actualFiles = Directory
            .EnumerateFiles(actualRoot, searchPattern, SearchOption.TopDirectoryOnly)
            .Select(file => Path.GetFileName(file) ?? string.Empty)
            .Where(fileName => !string.IsNullOrWhiteSpace(fileName))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.Equal(expectedFiles, actualFiles);

        foreach (var fileName in expectedFiles)
        {
            var expectedFile = Path.Combine(expectedRoot, fileName);
            var actualFile = Path.Combine(actualRoot, fileName);
            AssertFileContentsEqual(expectedFile, actualFile);
        }
    }

    private static void AssertFileContentsEqual(string expectedPath, string actualPath)
    {
        var expectedInfo = new FileInfo(expectedPath);
        var actualInfo = new FileInfo(actualPath);
        Assert.Equal(expectedInfo.Length, actualInfo.Length);

        using var expectedStream = File.OpenRead(expectedPath);
        using var actualStream = File.OpenRead(actualPath);
        Assert.Equal(GetSha256(expectedStream), GetSha256(actualStream));
    }

    private static void AssertFileContentsEqual(string expectedPath, ZipArchiveEntry actualEntry)
    {
        var expectedInfo = new FileInfo(expectedPath);
        Assert.Equal(expectedInfo.Length, actualEntry.Length);

        using var expectedStream = File.OpenRead(expectedPath);
        using var actualStream = actualEntry.Open();
        Assert.Equal(GetSha256(expectedStream), GetSha256(actualStream));
    }

    private static string GetSha256(Stream stream) => Convert.ToHexString(SHA256.HashData(stream));

    private static string NormalizeZipPath(string value)
        => value.Replace('\\', '/').TrimStart('/');

    private static string NormalizeRelativePath(string value)
        => value.Replace('\\', '/').TrimStart('/');
}
