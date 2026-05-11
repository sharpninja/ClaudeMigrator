using System.Text.Json.Serialization;
using ClaudeMigrator.Core.Utilities;

namespace ClaudeMigrator.Core.RemoteTargets;

public static class RemoteMethods
{
    public const string Ssh = "ssh";
    public const string Wsman = "wsman";

    public static readonly HashSet<string> All = new(StringComparer.OrdinalIgnoreCase)
    {
        Ssh,
        Wsman,
    };
}

public sealed record RemoteMachineSpec
{
    [JsonPropertyName("machine_id")]
    public string MachineId { get; init; } = string.Empty;

    [JsonPropertyName("display_name")]
    public string DisplayName { get; init; } = string.Empty;

    [JsonPropertyName("host")]
    public string Host { get; init; } = string.Empty;

    [JsonPropertyName("connection_method")]
    public string ConnectionMethod { get; init; } = RemoteMethods.Ssh;

    [JsonPropertyName("repo_root")]
    public string RepoRoot { get; init; } = string.Empty;

    [JsonPropertyName("username")]
    public string Username { get; init; } = string.Empty;

    [JsonPropertyName("port")]
    public int? Port { get; init; }

    [JsonPropertyName("notes")]
    public string Notes { get; init; } = string.Empty;

    [JsonPropertyName("created_at")]
    public string CreatedAt { get; init; } = string.Empty;

    [JsonPropertyName("updated_at")]
    public string UpdatedAt { get; init; } = string.Empty;

    public RemoteMachineSpec Normalized()
    {
        var method = (ConnectionMethod ?? string.Empty).Trim().ToLowerInvariant();
        if (!RemoteMethods.All.Contains(method))
        {
            method = RemoteMethods.Ssh;
        }

        var displayName = DisplayName.Trim();
        if (string.IsNullOrWhiteSpace(displayName))
        {
            displayName = Host.Trim();
        }

        if (string.IsNullOrWhiteSpace(displayName))
        {
            displayName = "Remote Machine";
        }

        var machineId = MachineId.Trim();
        if (string.IsNullOrWhiteSpace(machineId))
        {
            machineId = PathUtils.Slugify(displayName, "remote-machine");
        }

        var createdAt = string.IsNullOrWhiteSpace(CreatedAt) ? PathUtils.TimestampTag() : CreatedAt;

        return this with
        {
            MachineId = machineId,
            DisplayName = displayName,
            Host = Host.Trim(),
            ConnectionMethod = method,
            RepoRoot = RepoRoot.Trim(),
            Username = Username.Trim(),
            Notes = Notes.Trim(),
            CreatedAt = createdAt,
            UpdatedAt = PathUtils.TimestampTag(),
        };
    }
}
