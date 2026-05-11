using ClaudeMigrator.Core.RemoteTargets;
using ClaudeMigrator.Tests.TestSupport;

namespace ClaudeMigrator.Tests;

public sealed class RemoteTargetsTests
{
    [Fact]
    public void NormalizedSpecFillsDefaultsAndSlugsMachineId()
    {
        var spec = new RemoteMachineSpec
        {
            MachineId = string.Empty,
            DisplayName = "Lab Box",
            Host = "lab.example.com",
            ConnectionMethod = "rdp",
            RepoRoot = "  F:\\GitHub\\ClaudeMigrator  ",
            Username = "  kingd  ",
            Notes = "  source machine  ",
        };

        var normalized = spec.Normalized();

        Assert.Equal("lab-box", normalized.MachineId);
        Assert.Equal("Lab Box", normalized.DisplayName);
        Assert.Equal("lab.example.com", normalized.Host);
        Assert.Equal(RemoteMethods.Ssh, normalized.ConnectionMethod);
        Assert.Equal("F:\\GitHub\\ClaudeMigrator", normalized.RepoRoot);
        Assert.Equal("kingd", normalized.Username);
        Assert.Equal("source machine", normalized.Notes);
        Assert.False(string.IsNullOrWhiteSpace(normalized.CreatedAt));
        Assert.False(string.IsNullOrWhiteSpace(normalized.UpdatedAt));
    }

    [Fact]
    public void StoreDeduplicatesDuplicateIds()
    {
        using var workspace = new TestWorkspace();
        var store = new RemoteTargetStore(Path.Combine(workspace.Root, "remote_machines.json"));

        var saved = store.Save(new[]
        {
            new RemoteMachineSpec
            {
                DisplayName = "Lab Box",
                Host = "lab.example.com",
                ConnectionMethod = RemoteMethods.Ssh,
            },
            new RemoteMachineSpec
            {
                DisplayName = "Lab Box",
                Host = "other.example.com",
                ConnectionMethod = RemoteMethods.Wsman,
            },
        });

        Assert.Equal(2, saved.Count);
        Assert.Equal("lab-box", saved[0].MachineId);
        Assert.Equal("lab-box-2", saved[1].MachineId);

        var loaded = store.Load();
        Assert.Equal(2, loaded.Count);
        Assert.Equal("lab.example.com", loaded[0].Host);
        Assert.Equal("other.example.com", loaded[1].Host);
    }

    [Fact]
    public void BuildRemoteExportCommandUsesDotnetCliAndTargetApps()
    {
        var spec = new RemoteMachineSpec
        {
            MachineId = "lab-box",
            DisplayName = "Lab Box",
            Host = "linux.example.com",
            ConnectionMethod = RemoteMethods.Ssh,
            RepoRoot = "/opt/claude-migrator",
            Username = "kingd",
            Port = 2222,
        };

        var command = RemoteCommandBuilder.BuildRemoteExportCommand(spec, targetApps: new[] { "codex", "claude" });

        Assert.StartsWith("ssh -p 2222 kingd@linux.example.com", command);
        Assert.Contains("dotnet run --project", command);
        Assert.Contains("src/ClaudeMigrator.App/ClaudeMigrator.App.csproj", command);
        Assert.Contains("--build-source-bundle", command);
        Assert.Contains("--source-machine-name \"Lab Box\"", command);
        Assert.Contains("--source-host \"linux.example.com\"", command);
        Assert.Contains("--source-user \"kingd\"", command);
        Assert.Contains("--source-repo-root \"/opt/claude-migrator\"", command);
        Assert.Contains("--target-apps \"claude,codex\"", command);
        Assert.DoesNotContain("local_claude_exporter.py", command);
    }

    [Fact]
    public void BuildRemoteExportCommandSupportsWsman()
    {
        var spec = new RemoteMachineSpec
        {
            MachineId = "lab-box",
            DisplayName = "Lab Box",
            Host = "windows.example.com",
            ConnectionMethod = RemoteMethods.Wsman,
            RepoRoot = @"F:\GitHub\ClaudeMigrator",
        };

        var command = RemoteCommandBuilder.BuildRemoteExportCommand(spec, targetApps: new[] { "claude" });

        Assert.Contains("Invoke-Command -ComputerName windows.example.com", command);
        Assert.Contains("Set-Location 'F:\\GitHub\\ClaudeMigrator'", command);
        Assert.Contains("dotnet run --project", command);
        Assert.Contains("--target-apps \"claude\"", command);
    }
}
