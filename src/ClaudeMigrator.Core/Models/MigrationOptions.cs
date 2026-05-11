using ClaudeMigrator.Core.RemoteTargets;

namespace ClaudeMigrator.Core.Models;

public enum SourceMode
{
    Zip,
    LocalSnapshot,
}

public enum TargetApp
{
    Claude,
    Codex,
}

public sealed record MigrationOptions
{
    public SourceMode SourceMode { get; init; } = SourceMode.Zip;
    public string SourceHome { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public string? SourceMachineName { get; init; }
    public string? SourceHost { get; init; }
    public string ConnectionMethod { get; init; } = RemoteMethods.Ssh;
    public string? SourceUser { get; init; }
    public string? SourceRepoRoot { get; init; }
    public string? ExportZipPath { get; init; }
    public IReadOnlyList<TargetApp> TargetApps { get; init; } = [TargetApp.Claude, TargetApp.Codex];

    public IReadOnlyList<string> TargetAppNames => TargetApps.Select(app => app.ToString().ToLowerInvariant()).ToArray();
}
