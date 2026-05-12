using System.IO.Compression;
using System.Text;
using System.Text.Json;
using ClaudeMigrator.Core.Browser;
using ClaudeMigrator.Core.Exporting;
using ClaudeMigrator.Tests.TestSupport;

namespace ClaudeMigrator.Tests;

public sealed class BrowserManagerIntegrationTests
{
    [Fact]
    public async Task EdgeGeneratesPortableArchiveFromSourceFixture()
    {
        using var workspace = new TestWorkspace();
        var browserPage = SampleData.CreateSampleBrowserPage(workspace.Root);
        var pageUrl = new Uri(browserPage).AbsoluteUri;
        var logs = new List<string>();
        var manager = new BrowserManager(Path.Combine(workspace.Root, "runtime"), logs.Add);
        var browserName = OperatingSystem.IsWindows() ? "Edge" : "Chromium";
        var channel = OperatingSystem.IsWindows() ? "msedge" : null;

        var edgeSpec = new BrowserAccountSpec(
            Key: "edge_source",
            Email: "source@example.com",
            BrowserName: browserName,
            Channel: channel,
            StorageStatePath: Path.Combine(manager.SessionsDir, "edge_source.storage.json"),
            StartUrl: pageUrl);

        var edge = await manager.OpenSessionAsync(edgeSpec, headless: true);

        try
        {
            await edge.Page.Locator("#message").FillAsync("source@example.com");
            Assert.Equal("source@example.com", await edge.Page.Locator("#message").InputValueAsync());

            var clicked = await manager.ClickCandidatesAsync(edge.Page, new[] { "Export data" });
            Assert.True(clicked);
            Assert.Equal("export-clicked", await edge.Page.Locator("#status").TextContentAsync());

            var exportZip = SampleData.CreateRichSampleExportZip(workspace.Root);
            var exporter = new UniversalClaudeExporter(Path.Combine(workspace.Root, "runtime"));
            var portable = exporter.ExportPortableZip(exportZip);

            Assert.True(File.Exists(portable.ZipPath));
            Assert.True(File.Exists(portable.MemoryPath));
            Assert.True(File.Exists(portable.ManifestPath));
            Assert.Equal(2, portable.Counts["conversations"]);
            Assert.Equal(2, portable.Counts["projects"]);
            Assert.Equal(2, portable.Counts["memory_items"]);
            Assert.Equal(12, portable.Counts["source_files"]);
            Assert.Equal(12, portable.Counts["extracted_artifacts"]);

            using var sourceArchive = ZipFile.OpenRead(exportZip);
            var sourceMembers = sourceArchive.Entries
                .Where(entry => !string.IsNullOrEmpty(entry.Name))
                .Select(entry => entry.FullName)
                .ToArray();

            using var manifestDocument = JsonDocument.Parse(File.ReadAllText(portable.ManifestPath, Encoding.UTF8));
            var manifest = manifestDocument.RootElement;
            var counts = manifest.GetProperty("counts");
            Assert.Equal(2, counts.GetProperty("conversations").GetInt32());
            Assert.Equal(2, counts.GetProperty("projects").GetInt32());
            Assert.Equal(2, counts.GetProperty("memory_items").GetInt32());
            Assert.Equal(12, counts.GetProperty("source_files").GetInt32());
            Assert.Equal(12, counts.GetProperty("extracted_artifacts").GetInt32());

            AssertSameMembers(new[] { "Test Chat", "Migration Retrospective" }, ReadObjectFieldArray(manifest.GetProperty("conversation_blueprints"), "title"));
            AssertSameMembers(new[] { "Demo Project", "Ops Project" }, ReadObjectFieldArray(manifest.GetProperty("project_blueprints"), "name"));

            using var memoryDocument = JsonDocument.Parse(File.ReadAllText(portable.MemoryPath, Encoding.UTF8));
            AssertSameMembers(new[] { "Project Memory", "Release Notes" }, ReadObjectFieldArray(memoryDocument.RootElement.GetProperty("items"), "title"));

            var expectedSourceMembers = new[]
            {
                "conversations/test-chat.json",
                "conversations/migration-retrospective.json",
                "projects/demo-project.json",
                "projects/ops-project.json",
                "memory/project-memory.json",
                "memory/release-notes.json",
                "broken.json",
                "source/code/sample.py",
                "source/code/worker.ts",
                "source/assets/readme.txt",
                "source/scripts/cleanup.ps1",
                "source/docs/notes.md",
            };

            AssertSameMembers(expectedSourceMembers, sourceMembers);
            AssertSameMembers(expectedSourceMembers, ReadStringArray(manifest.GetProperty("source_members")));
            AssertSameMembers(expectedSourceMembers, ReadStringArray(manifest.GetProperty("code_files")));

            var seedPrompts = manifest.GetProperty("seed_prompts");
            Assert.Equal(2, seedPrompts.GetArrayLength());
            AssertSameMembers(new[] { "Demo Project", "Ops Project" }, ReadObjectFieldArray(seedPrompts, "project_name"));
            Assert.All(seedPrompts.EnumerateArray(), item => Assert.False(string.IsNullOrWhiteSpace(item.GetProperty("prompt").GetString())));

            AssertPortableArchiveMatchesBundle(portable.BundleRoot, portable.ZipPath);

            var saved = await manager.SaveAllSessionStatesAsync();
            Assert.Contains(saved, path => path.EndsWith("edge_source.storage.json", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(logs, line => line.Contains("Launching Edge", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            await manager.CloseAllAsync();
        }
    }

    [Fact]
    public async Task BrowserManagerClicksNthMatchingButtonAndHiddenFileInput()
    {
        using var workspace = new TestWorkspace();
        var browserPage = SampleData.CreateSampleBrowserPage(workspace.Root);
        var pageUrl = new Uri(browserPage).AbsoluteUri;
        var logs = new List<string>();
        var manager = new BrowserManager(Path.Combine(workspace.Root, "runtime"), logs.Add);
        var browserName = OperatingSystem.IsWindows() ? "Edge" : "Chromium";
        var channel = OperatingSystem.IsWindows() ? "msedge" : null;

        var edgeSpec = new BrowserAccountSpec(
            Key: "edge_source",
            Email: "source@example.com",
            BrowserName: browserName,
            Channel: channel,
            StorageStatePath: Path.Combine(manager.SessionsDir, "edge_source.storage.json"),
            StartUrl: pageUrl);

        var edge = await manager.OpenSessionAsync(edgeSpec, headless: true);

        try
        {
            var clicked = await manager.ClickNthCandidateAsync(edge.Page, "Manage", 1);
            Assert.True(clicked);
            Assert.Equal("manage-2", await edge.Page.Locator("#status").TextContentAsync());

            var exportZip = SampleData.CreateSampleExportZip(workspace.Root);
            var exporter = new UniversalClaudeExporter(Path.Combine(workspace.Root, "runtime"));
            var portable = exporter.ExportPortableZip(exportZip);

            var uploaded = await manager.SetFirstFileInputAsync(edge.Page, portable.MemoryPath);
            Assert.True(uploaded);
            Assert.Equal("files:1", await edge.Page.Locator("#status").TextContentAsync());
        }
        finally
        {
            await manager.CloseAllAsync();
        }
    }

    [Fact]
    public async Task FirefoxPushesPortableArchiveIntoTargetFixture()
    {
        using var workspace = new TestWorkspace();
        var browserPage = SampleData.CreateSampleBrowserPage(workspace.Root);
        var pageUrl = new Uri(browserPage).AbsoluteUri;
        var logs = new List<string>();
        var manager = new BrowserManager(Path.Combine(workspace.Root, "runtime"), logs.Add);

        var firefoxSpec = new BrowserAccountSpec(
            Key: "firefox_target",
            Email: "target@example.com",
            BrowserName: "Firefox",
            Channel: null,
            StorageStatePath: Path.Combine(manager.SessionsDir, "firefox_target.storage.json"),
            StartUrl: pageUrl);

        var exportZip = SampleData.CreateSampleExportZip(workspace.Root);
        var exporter = new UniversalClaudeExporter(Path.Combine(workspace.Root, "runtime"));
        var portable = exporter.ExportPortableZip(exportZip);

        var firefox = await manager.OpenSessionAsync(firefoxSpec, headless: true);

        try
        {
            var uploaded = await manager.SetFirstFileInputAsync(firefox.Page, portable.MemoryPath);
            Assert.True(uploaded);
            Assert.Equal("files:1", await firefox.Page.Locator("#status").TextContentAsync());

            await firefox.Page.Locator("#notes").FillAsync(
                $"Imported {portable.Counts["projects"]} project blueprint(s) and {portable.Counts["memory_items"]} memory item(s).");

            var clicked = await manager.ClickCandidatesAsync(firefox.Page, new[] { "Send" });
            Assert.True(clicked);
            Assert.Equal("send-clicked", await firefox.Page.Locator("#status").TextContentAsync());

            var saved = await manager.SaveAllSessionStatesAsync();
            Assert.Contains(saved, path => path.EndsWith("firefox_target.storage.json", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(logs, line => line.Contains("Launching Firefox", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            await manager.CloseAllAsync();
        }
    }

    private static void AssertPortableArchiveMatchesBundle(string bundleRoot, string zipPath)
    {
        var expectedFiles = Directory.EnumerateFiles(bundleRoot, "*", SearchOption.AllDirectories)
            .Select(path => NormalizePath(Path.GetRelativePath(bundleRoot, path)))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        using var archive = ZipFile.OpenRead(zipPath);
        var prefix = NormalizePath(Path.GetFileName(bundleRoot)) + "/";
        var actualFiles = archive.Entries
            .Where(entry => !string.IsNullOrEmpty(entry.Name))
            .Select(entry => entry.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? entry.FullName[prefix.Length..]
                : entry.FullName)
            .Select(NormalizePath)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.Equal(expectedFiles, actualFiles);
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement array)
    {
        return array.EnumerateArray()
            .Select(item => item.GetString() ?? string.Empty)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();
    }

    private static IReadOnlyList<string> ReadObjectFieldArray(JsonElement array, string propertyName)
    {
        return array.EnumerateArray()
            .Select(item => item.TryGetProperty(propertyName, out var property) ? property.GetString() ?? string.Empty : string.Empty)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();
    }

    private static void AssertSameMembers(IEnumerable<string> expected, IEnumerable<string> actual)
    {
        var normalizedExpected = expected.Select(NormalizePath).OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
        var normalizedActual = actual.Select(NormalizePath).OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
        Assert.Equal(normalizedExpected, normalizedActual);
    }

    private static string NormalizePath(string value)
    {
        return value.Replace('\\', '/');
    }
}
