using System.Security.Cryptography;
using System.Text;
using ClaudeMigrator.Core.Local;
using ClaudeMigrator.Tests.TestSupport;

namespace ClaudeMigrator.Tests;

public sealed class LocalAgentSessionsMigratorTests
{
    private const string SourceAccount = "8e003dee-a2c8-4173-a458-d6a77819ebbb";
    private const string SourceOrg = "ff532a36-c1b0-428f-9164-e7c383dfd3da";
    private const string TargetAccount = "118701b6-cb3e-4953-bf39-9546781751b8";
    private const string TargetOrg = "dc52499b-5e9e-4149-a90d-f6fe5c165c7b";

    [Fact]
    public void CopiesAllSessionFilesFromSourceToTarget()
    {
        using var workspace = new TestWorkspace();
        var sessionsRoot = Path.Combine(workspace.Root, "local-agent-mode-sessions");
        var sourceDir = SeedSourceTree(sessionsRoot, includeRpm: false);

        var result = new LocalAgentSessionsMigrator().Migrate(new LocalAgentSessionsMigrationOptions(
            SourceAccountUuid: SourceAccount,
            SourceOrgUuid: SourceOrg,
            TargetAccountUuid: TargetAccount,
            TargetOrgUuid: TargetOrg,
            SessionsRoot: sessionsRoot));

        var targetDir = Path.Combine(sessionsRoot, TargetAccount, TargetOrg);
        Assert.Equal(sourceDir, result.SourceDirectory);
        Assert.Equal(targetDir, result.TargetDirectory);
        Assert.Equal(4, result.CopiedFileCount);
        Assert.Equal(0, result.SkippedFileCount);
        Assert.Equal(0, result.FailedFileCount);

        AssertTreeEquals(sourceDir, targetDir);
    }

    [Fact]
    public void SkipsRpmSubfolder()
    {
        using var workspace = new TestWorkspace();
        var sessionsRoot = Path.Combine(workspace.Root, "local-agent-mode-sessions");
        SeedSourceTree(sessionsRoot, includeRpm: true);

        var result = new LocalAgentSessionsMigrator().Migrate(new LocalAgentSessionsMigrationOptions(
            SourceAccountUuid: SourceAccount,
            SourceOrgUuid: SourceOrg,
            TargetAccountUuid: TargetAccount,
            TargetOrgUuid: TargetOrg,
            SessionsRoot: sessionsRoot));

        Assert.Equal(4, result.CopiedFileCount);
        var targetDir = Path.Combine(sessionsRoot, TargetAccount, TargetOrg);
        Assert.False(Directory.Exists(Path.Combine(targetDir, "rpm")), "rpm/ must not be copied");
        Assert.True(File.Exists(Path.Combine(targetDir, "scheduled-tasks.json")));
    }

    [Fact]
    public void PreservesPreExistingTargetRpmContent()
    {
        using var workspace = new TestWorkspace();
        var sessionsRoot = Path.Combine(workspace.Root, "local-agent-mode-sessions");
        SeedSourceTree(sessionsRoot, includeRpm: false);

        var targetRpm = Path.Combine(sessionsRoot, TargetAccount, TargetOrg, "rpm");
        Directory.CreateDirectory(targetRpm);
        var targetPluginFile = Path.Combine(targetRpm, "plugin_existing.json");
        File.WriteAllText(targetPluginFile, "fresh-plugin-state");

        new LocalAgentSessionsMigrator().Migrate(new LocalAgentSessionsMigrationOptions(
            SourceAccountUuid: SourceAccount,
            SourceOrgUuid: SourceOrg,
            TargetAccountUuid: TargetAccount,
            TargetOrgUuid: TargetOrg,
            SessionsRoot: sessionsRoot));

        Assert.Equal("fresh-plugin-state", File.ReadAllText(targetPluginFile));
    }

    [Fact]
    public void IdempotentSecondRunSkipsExistingMatchingFiles()
    {
        using var workspace = new TestWorkspace();
        var sessionsRoot = Path.Combine(workspace.Root, "local-agent-mode-sessions");
        SeedSourceTree(sessionsRoot, includeRpm: false);
        var migrator = new LocalAgentSessionsMigrator();
        var options = new LocalAgentSessionsMigrationOptions(
            SourceAccountUuid: SourceAccount,
            SourceOrgUuid: SourceOrg,
            TargetAccountUuid: TargetAccount,
            TargetOrgUuid: TargetOrg,
            SessionsRoot: sessionsRoot);

        var first = migrator.Migrate(options);
        var second = migrator.Migrate(options);

        Assert.Equal(4, first.CopiedFileCount);
        Assert.Equal(0, second.CopiedFileCount);
        Assert.Equal(4, second.SkippedFileCount);
        Assert.Equal(0, second.FailedFileCount);
    }

    [Fact]
    public void DryRunDoesNotWriteFiles()
    {
        using var workspace = new TestWorkspace();
        var sessionsRoot = Path.Combine(workspace.Root, "local-agent-mode-sessions");
        SeedSourceTree(sessionsRoot, includeRpm: false);

        var result = new LocalAgentSessionsMigrator().Migrate(new LocalAgentSessionsMigrationOptions(
            SourceAccountUuid: SourceAccount,
            SourceOrgUuid: SourceOrg,
            TargetAccountUuid: TargetAccount,
            TargetOrgUuid: TargetOrg,
            SessionsRoot: sessionsRoot,
            DryRun: true));

        Assert.Equal(4, result.CopiedFileCount);
        var targetDir = Path.Combine(sessionsRoot, TargetAccount, TargetOrg);
        Assert.False(File.Exists(Path.Combine(targetDir, "scheduled-tasks.json")));
    }

    [Fact]
    public void ThrowsWhenSourceDirectoryMissing()
    {
        using var workspace = new TestWorkspace();
        var sessionsRoot = Path.Combine(workspace.Root, "local-agent-mode-sessions");

        Assert.Throws<DirectoryNotFoundException>(() => new LocalAgentSessionsMigrator().Migrate(new LocalAgentSessionsMigrationOptions(
            SourceAccountUuid: SourceAccount,
            SourceOrgUuid: SourceOrg,
            TargetAccountUuid: TargetAccount,
            TargetOrgUuid: TargetOrg,
            SessionsRoot: sessionsRoot)));
    }

    [Fact]
    public void CopiedFilesMatchSourceSha256()
    {
        using var workspace = new TestWorkspace();
        var sessionsRoot = Path.Combine(workspace.Root, "local-agent-mode-sessions");
        var sourceDir = SeedSourceTree(sessionsRoot, includeRpm: false);

        new LocalAgentSessionsMigrator().Migrate(new LocalAgentSessionsMigrationOptions(
            SourceAccountUuid: SourceAccount,
            SourceOrgUuid: SourceOrg,
            TargetAccountUuid: TargetAccount,
            TargetOrgUuid: TargetOrg,
            SessionsRoot: sessionsRoot));

        var targetDir = Path.Combine(sessionsRoot, TargetAccount, TargetOrg);
        var sourceFiles = Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories).ToArray();
        foreach (var sourceFile in sourceFiles)
        {
            var relative = Path.GetRelativePath(sourceDir, sourceFile);
            var targetFile = Path.Combine(targetDir, relative);
            Assert.True(File.Exists(targetFile), $"Missing copied file: {relative}");
            Assert.Equal(Sha256(sourceFile), Sha256(targetFile));
        }
    }

    private static string SeedSourceTree(string sessionsRoot, bool includeRpm)
    {
        var sourceDir = Path.Combine(sessionsRoot, SourceAccount, SourceOrg);
        Directory.CreateDirectory(sourceDir);

        File.WriteAllText(Path.Combine(sourceDir, "local_session-a.json"), "{\"sessionId\":\"a\"}");
        File.WriteAllText(Path.Combine(sourceDir, "scheduled-tasks.json"), "[]");
        File.WriteAllText(Path.Combine(sourceDir, "spaces.json"), "{}");

        var agentDir = Path.Combine(sourceDir, "agent");
        Directory.CreateDirectory(agentDir);
        File.WriteAllText(Path.Combine(agentDir, "agent-1.jsonl"), "{\"type\":\"event\"}");

        if (includeRpm)
        {
            var rpmDir = Path.Combine(sourceDir, "rpm", "plugin_legacy");
            Directory.CreateDirectory(rpmDir);
            File.WriteAllText(Path.Combine(rpmDir, "manifest.json"), "{\"legacy\":true}");
        }

        return sourceDir;
    }

    private static void AssertTreeEquals(string sourceDir, string targetDir)
    {
        var sourceFiles = Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(sourceDir, path))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var targetFiles = Directory.EnumerateFiles(targetDir, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(targetDir, path))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Assert.Equal(sourceFiles, targetFiles);
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
