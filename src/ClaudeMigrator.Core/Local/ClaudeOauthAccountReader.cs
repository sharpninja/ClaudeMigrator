using System.Text.Json;

namespace ClaudeMigrator.Core.Local;

public sealed record ClaudeOauthAccount(
    string AccountUuid,
    string EmailAddress,
    string DisplayName,
    string OrganizationUuid,
    string OrganizationName,
    string SourceFile,
    DateTimeOffset SourceTimestampUtc);

public sealed class ClaudeOauthAccountReader
{
    public static string DefaultClaudeJsonPath()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json");

    public static string DefaultBackupsFolder()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "backups");

    public ClaudeOauthAccount? ReadCurrent(string? claudeJsonPath = null)
    {
        var path = claudeJsonPath ?? DefaultClaudeJsonPath();
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var stream = File.OpenRead(path);
            using var document = JsonDocument.Parse(stream);
            return ExtractAccount(document.RootElement, path, File.GetLastWriteTimeUtc(path));
        }
        catch
        {
            return null;
        }
    }

    public IReadOnlyList<ClaudeOauthAccount> ReadFromBackups(string? backupsFolder = null)
    {
        var folder = backupsFolder ?? DefaultBackupsFolder();
        if (!Directory.Exists(folder))
        {
            return Array.Empty<ClaudeOauthAccount>();
        }

        var results = new List<ClaudeOauthAccount>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in Directory.EnumerateFiles(folder, ".claude.json.backup.*"))
        {
            try
            {
                using var stream = File.OpenRead(file);
                using var document = JsonDocument.Parse(stream);
                var account = ExtractAccount(document.RootElement, file, File.GetLastWriteTimeUtc(file));
                if (account is null)
                {
                    continue;
                }

                if (seen.Add(account.AccountUuid))
                {
                    results.Add(account);
                }
            }
            catch
            {
                // ignore unreadable backups
            }
        }

        results.Sort((left, right) => right.SourceTimestampUtc.CompareTo(left.SourceTimestampUtc));
        return results;
    }

    public IReadOnlyList<ClaudeOauthAccount> ReadAll(string? claudeJsonPath = null, string? backupsFolder = null)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<ClaudeOauthAccount>();

        var current = ReadCurrent(claudeJsonPath);
        if (current is not null && seen.Add(current.AccountUuid))
        {
            ordered.Add(current);
        }

        foreach (var account in ReadFromBackups(backupsFolder))
        {
            if (seen.Add(account.AccountUuid))
            {
                ordered.Add(account);
            }
        }

        return ordered;
    }

    private static ClaudeOauthAccount? ExtractAccount(JsonElement root, string sourceFile, DateTime sourceTimestampUtc)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("oauthAccount", out var account) || account.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var accountUuid = ReadString(account, "accountUuid");
        if (string.IsNullOrWhiteSpace(accountUuid))
        {
            return null;
        }

        return new ClaudeOauthAccount(
            AccountUuid: accountUuid,
            EmailAddress: ReadString(account, "emailAddress"),
            DisplayName: ReadString(account, "displayName"),
            OrganizationUuid: ReadString(account, "organizationUuid"),
            OrganizationName: ReadString(account, "organizationName"),
            SourceFile: sourceFile,
            SourceTimestampUtc: new DateTimeOffset(sourceTimestampUtc, TimeSpan.Zero));
    }

    private static string ReadString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return string.Empty;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString() ?? string.Empty,
            JsonValueKind.Null => string.Empty,
            _ => property.ToString(),
        };
    }
}
