using ClaudeMigrator.Core.RemoteTargets;

namespace ClaudeMigrator.App.ViewModels;

public sealed record RemoteMachineViewModel(
    string MachineId,
    string DisplayName,
    string Host,
    string ConnectionMethod,
    string RepoRoot,
    string Username,
    int? Port,
    string Notes,
    string CreatedAt,
    string UpdatedAt)
{
    public static RemoteMachineViewModel FromSpec(RemoteMachineSpec spec)
        => new(
            spec.MachineId,
            spec.DisplayName,
            spec.Host,
            spec.ConnectionMethod,
            spec.RepoRoot,
            spec.Username,
            spec.Port,
            spec.Notes,
            spec.CreatedAt,
            spec.UpdatedAt);

    public RemoteMachineSpec ToSpec()
        => new()
        {
            MachineId = MachineId,
            DisplayName = DisplayName,
            Host = Host,
            ConnectionMethod = ConnectionMethod,
            RepoRoot = RepoRoot,
            Username = Username,
            Port = Port,
            Notes = Notes,
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
        };

    public string Summary => string.IsNullOrWhiteSpace(DisplayName) ? Host : $"{DisplayName} ({Host})";
}
