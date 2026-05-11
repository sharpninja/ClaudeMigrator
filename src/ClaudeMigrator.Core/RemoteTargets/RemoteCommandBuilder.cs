namespace ClaudeMigrator.Core.RemoteTargets;

public static class RemoteCommandBuilder
{
    public static string BuildRemoteExportCommand(
        RemoteMachineSpec spec,
        string appProjectPath = "src/ClaudeMigrator.App/ClaudeMigrator.App.csproj",
        IEnumerable<string>? targetApps = null)
    {
        static string DoubleQuote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";
        static string SingleQuote(string value) => "'" + value.Replace("'", "''") + "'";

        var normalized = spec.Normalized();
        var repoRoot = string.IsNullOrWhiteSpace(normalized.RepoRoot) ? "." : normalized.RepoRoot;
        var machineName = string.IsNullOrWhiteSpace(normalized.DisplayName) ? normalized.Host : normalized.DisplayName;
        var method = normalized.ConnectionMethod;

        var sourceArgs = new List<string>
        {
            "--build-source-bundle",
            $"--source-machine-name {DoubleQuote(machineName)}",
            $"--connection-method {DoubleQuote(method)}",
            $"--source-host {DoubleQuote(normalized.Host)}",
        };

        if (!string.IsNullOrWhiteSpace(normalized.Username))
        {
            sourceArgs.Add($"--source-user {DoubleQuote(normalized.Username)}");
        }

        if (!string.IsNullOrWhiteSpace(normalized.RepoRoot))
        {
            sourceArgs.Add($"--source-repo-root {DoubleQuote(normalized.RepoRoot)}");
        }

        var normalizedTargets = targetApps?
            .Select(item => item?.Trim().ToLowerInvariant())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalizedTargets is { Length: > 0 })
        {
            sourceArgs.Add($"--target-apps {DoubleQuote(string.Join(",", normalizedTargets))}");
        }

        var exportArgs = string.Join(' ', sourceArgs);
        if (string.Equals(method, RemoteMethods.Ssh, StringComparison.OrdinalIgnoreCase))
        {
            var userPrefix = string.IsNullOrWhiteSpace(normalized.Username) ? string.Empty : $"{normalized.Username}@";
            var portPart = normalized.Port.HasValue ? $" -p {normalized.Port.Value}" : string.Empty;
            var remoteCommand = $"cd {DoubleQuote(repoRoot)} && dotnet run --project {DoubleQuote(appProjectPath)} -- {exportArgs}";
            return $"ssh{portPart} {userPrefix}{normalized.Host} \"{remoteCommand}\"";
        }

        var remoteScript = $"Set-Location {SingleQuote(repoRoot)}; dotnet run --project {SingleQuote(appProjectPath)} -- {exportArgs}";
        return $"Invoke-Command -ComputerName {normalized.Host} -ScriptBlock {{ {remoteScript} }}";
    }
}
