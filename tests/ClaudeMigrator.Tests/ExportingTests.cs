using System.IO.Compression;
using System.Text.Json;
using ClaudeMigrator.Core.Exporting;
using ClaudeMigrator.Tests.TestSupport;

namespace ClaudeMigrator.Tests;

public sealed class ExportingTests
{
    [Fact]
    public void UniversalExporterInspectsStructuredArchives()
    {
        using var workspace = new TestWorkspace();
        var archive = SampleData.CreateSampleExportZip(workspace.Root);
        var exporter = new UniversalClaudeExporter(Path.Combine(workspace.Root, "runtime"));

        var parsed = exporter.InspectArchive(archive);

        Assert.Single(parsed.Conversations);
        Assert.Single(parsed.Projects);
        Assert.Single(parsed.MemoryItems);
        Assert.Equal(7, parsed.CodeFiles.Count);
        Assert.False(string.IsNullOrWhiteSpace(parsed.Conversations[0].Summary));
        Assert.False(string.IsNullOrWhiteSpace(parsed.Conversations[0].SeedPrompt));
        Assert.False(string.IsNullOrWhiteSpace(parsed.Projects[0].SeedPrompt));
    }

    [Fact]
    public void UniversalExporterWritesPortableBundleAndManifest()
    {
        using var workspace = new TestWorkspace();
        var archive = SampleData.CreateSampleExportZip(workspace.Root);
        var exporter = new UniversalClaudeExporter(Path.Combine(workspace.Root, "runtime"));
        var outputZip = Path.Combine(workspace.Root, "portable_exports", "custom_output.zip");

        var result = exporter.ExportPortableZip(archive, outputZip: outputZip);

        Assert.True(File.Exists(result.ZipPath));
        Assert.Equal(outputZip, result.ZipPath);
        Assert.True(File.Exists(result.ManifestPath));
        Assert.True(File.Exists(result.MemoryPath));
        Assert.Equal(1, result.Counts["conversations"]);
        Assert.Equal(1, result.Counts["projects"]);
        Assert.Equal(1, result.Counts["memory_items"]);
        Assert.Equal(7, result.Counts["source_files"]);
        Assert.Equal(7, result.Counts["extracted_artifacts"]);
        Assert.True(result.Manifest.ContainsKey("import_guides"));

        using var archiveReader = ZipFile.OpenRead(result.ZipPath);
        var names = archiveReader.Entries.Select(entry => entry.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var bundleName = Path.GetFileName(result.BundleRoot);
        Assert.Contains($"{bundleName}/manifest.json", names);
        Assert.Contains($"{bundleName}/memory/memory.json", names);
        Assert.Contains(names, entry => entry.EndsWith("conversations/test-chat/chat.md", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(names, entry => entry.EndsWith("projects/demo-project/instructions.md", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(names, entry => entry.EndsWith("artifacts/extracted_code/source/source/code/sample-py", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void UniversalExporterFallsBackForUnstructuredArchives()
    {
        using var workspace = new TestWorkspace();
        var archive = SampleData.CreateSampleExportZip(workspace.Root, structured: false);
        var exporter = new UniversalClaudeExporter(Path.Combine(workspace.Root, "runtime"));

        var result = exporter.ExportPortableZip(archive);

        Assert.True(result.Counts["conversations"] >= 1);
        Assert.True(result.Counts["projects"] >= 1);
        Assert.Equal(0, result.Counts["memory_items"]);
        var projectBlueprints = Assert.IsAssignableFrom<Array>(result.Manifest["project_blueprints"]);
        var conversationBlueprints = Assert.IsAssignableFrom<Array>(result.Manifest["conversation_blueprints"]);
        Assert.NotEmpty(projectBlueprints);
        Assert.NotEmpty(conversationBlueprints);
    }

    [Fact]
    public void LocalBundleExporterCopiesProfileAndRestoresTargets()
    {
        using var workspace = new TestWorkspace();
        var home = SampleData.CreateSampleLocalHome(workspace.Root);
        var destinationHome = Path.Combine(workspace.Root, "restore_home");
        Directory.CreateDirectory(destinationHome);
        var exporter = new LocalClaudeBundleExporter(Path.Combine(workspace.Root, "runtime"));

        var result = exporter.ExportLocalBundle(
            sourceHome: home,
            destinationHome: destinationHome,
            sourceMachineName: "LAB-01",
            sourceHost: "lab-01.example.com",
            connectionMethod: "wsman",
            sourceUser: "kingd",
            sourceAccount: "source@example.com",
            targetAccount: "target@example.com",
            sourceRepoRoot: @"F:\GitHub\ClaudeMigrator",
            targetApps: new[] { "claude", "codex" });

        Assert.True(File.Exists(result.ZipPath));
        Assert.True(File.Exists(result.ManifestPath));
        Assert.True(File.Exists(result.SourceEnvironmentPath));
        Assert.True(File.Exists(result.RestorePlanPath));
        Assert.True(File.Exists(Path.Combine(result.BundleRoot, "source", "home", ".claude", "CLAUDE.md")));
        Assert.True(File.Exists(Path.Combine(result.BundleRoot, "source", "home", ".claude.json")));

        using var manifestDocument = JsonDocument.Parse(File.ReadAllText(result.ManifestPath, System.Text.Encoding.UTF8));
        var manifest = manifestDocument.RootElement;
        Assert.Equal("claude_local_bundle", manifest.GetProperty("format").GetString());
        Assert.Equal("LAB-01", manifest.GetProperty("source_environment").GetProperty("source_machine_name").GetString());
        Assert.Equal("wsman", manifest.GetProperty("source_environment").GetProperty("connection_method").GetString());
        Assert.Equal("source@example.com", manifest.GetProperty("source_environment").GetProperty("source_account_name").GetString());
        Assert.Equal("target@example.com", manifest.GetProperty("source_environment").GetProperty("target_account_name").GetString());
        Assert.Equal("ninja@thesharp.ninja", manifest.GetProperty("source_account").GetProperty("email_address").GetString());
        Assert.Equal("source/home/.claude", manifest.GetProperty("paths").GetProperty("source_profile_root").GetString());
        Assert.Equal("source/home/.claude.json", manifest.GetProperty("paths").GetProperty("source_account_file").GetString());
        Assert.Equal(destinationHome, manifest.GetProperty("destination_home").GetString());
        Assert.Equal(2, manifest.GetProperty("restore_targets").GetArrayLength());
        Assert.Equal("source@example.com", manifest.GetProperty("source_account_name").GetString());
        Assert.Equal("target@example.com", manifest.GetProperty("target_account_name").GetString());
        Assert.Equal(destinationHome, manifest.GetProperty("restore_targets").EnumerateArray().First().GetProperty("target_home").GetString());

        using var archiveReader = ZipFile.OpenRead(result.ZipPath);
        var names = archiveReader.Entries.Select(entry => entry.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains(names, entry => entry.EndsWith("/manifest.json", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(names, entry => entry.EndsWith("/source/home/.claude/CLAUDE.md", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(names, entry => entry.EndsWith("/source/home/.claude.json", StringComparison.OrdinalIgnoreCase));
        var restoreReadme = File.ReadAllText(Path.Combine(result.BundleRoot, "restore", "README.md"), System.Text.Encoding.UTF8);
        Assert.Contains("source@example.com", restoreReadme);
        Assert.Contains("target@example.com", restoreReadme);
        Assert.Contains(destinationHome, restoreReadme);

        var restoreResult = exporter.RestoreLocalBundle(result.ZipPath, destinationHome: destinationHome, targetApps: new[] { "claude", "codex" });

        var restoredTargets = Assert.IsType<List<Dictionary<string, object?>>>(restoreResult["restored_targets"]);
        Assert.Equal(2, restoredTargets.Count);
        Assert.True(File.Exists(Path.Combine(destinationHome, ".claude", "CLAUDE.md")));
        Assert.True(File.Exists(Path.Combine(destinationHome, ".claude", "projects", "alpha", "notes.txt")));
        Assert.True(File.Exists(Path.Combine(destinationHome, ".claude.json")));
        Assert.True(File.Exists(Path.Combine(destinationHome, ".codex", "CLAUDE.md")));
    }

    [Fact]
    public void LocalBundleExporterPreservesDestinationMetadata()
    {
        using var workspace = new TestWorkspace();
        var home = SampleData.CreateSampleLocalHome(workspace.Root);
        var exporter = new LocalClaudeBundleExporter(Path.Combine(workspace.Root, "runtime"));
        var destinationHome = Path.Combine(workspace.Root, "restore_home");
        Directory.CreateDirectory(destinationHome);

        var result = exporter.ExportLocalBundle(
            sourceHome: home,
            destinationHome: destinationHome,
            sourceMachineName: "LAB-02",
            sourceHost: "lab-02.example.com",
            connectionMethod: "ssh",
            sourceUser: "kingd",
            sourceAccount: "source@example.com",
            targetAccount: "target@example.com",
            sourceRepoRoot: @"F:\GitHub\ClaudeMigrator",
            targetApps: new[] { "claude" });

        var restoreResult = exporter.RestoreLocalBundle(result.ZipPath, destinationHome: destinationHome, targetApps: new[] { "claude" });
        Assert.Equal(destinationHome, result.Manifest["destination_home"]?.ToString());
        Assert.Equal("LAB-02", restoreResult["source_machine"]?.ToString());
        Assert.Equal("source@example.com", restoreResult["source_account_name"]?.ToString());
        Assert.Equal("target@example.com", restoreResult["target_account_name"]?.ToString());
        Assert.True(File.Exists(Path.Combine(destinationHome, ".claude", "CLAUDE.md")));
    }
}
