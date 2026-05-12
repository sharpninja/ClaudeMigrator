using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace ClaudeMigrator.Tests.TestSupport;

internal static class FirefoxStorageStateBuilder
{
    private static readonly string[] Sqlite3Candidates =
    [
        Environment.GetEnvironmentVariable("CLAUDEMIGRATOR_SQLITE3_PATH") ?? string.Empty,
        @"C:\Program Files (x86)\Android\android-sdk\platform-tools\sqlite3.exe",
        @"C:\Android\android-sdk\platform-tools\sqlite3.exe",
    ];

    internal static string BuildFromProfile(string profileDir, string workspaceRoot, Action<string>? log = null)
    {
        var resolvedProfileDir = Path.GetFullPath(profileDir);
        if (!Directory.Exists(resolvedProfileDir))
        {
            throw new DirectoryNotFoundException($"Firefox profile not found: {resolvedProfileDir}");
        }

        var sqlite3 = ResolveSqlite3Executable();
        var outputDir = Path.Combine(workspaceRoot, "live_state");
        Directory.CreateDirectory(outputDir);
        var outputPath = Path.Combine(outputDir, "firefox.storage_state.json");

        var cookies = ReadCookies(sqlite3, Path.Combine(resolvedProfileDir, "cookies.sqlite"));
        var origins = ReadOrigins(sqlite3, resolvedProfileDir);

        var storageState = new PlaywrightStorageState(cookies, origins);
        var json = JsonSerializer.Serialize(storageState, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });

        File.WriteAllText(outputPath, json, Encoding.UTF8);
        log?.Invoke($"Synthesized Firefox storage state from {resolvedProfileDir} into {outputPath}.");
        return outputPath;
    }

    private static IReadOnlyList<PlaywrightCookie> ReadCookies(string sqlite3, string cookiesDbPath)
    {
        if (!File.Exists(cookiesDbPath))
        {
            throw new FileNotFoundException($"Firefox cookie database not found: {cookiesDbPath}");
        }

        var sql = """
                  SELECT json_object(
                      'host', host,
                      'path', path,
                      'name', name,
                      'value', value,
                      'expiry', expiry,
                      'isSecure', isSecure,
                      'isHttpOnly', isHttpOnly
                  )
                  FROM moz_cookies
                  WHERE host LIKE '%claude%' OR host LIKE '%anthropic%'
                  ORDER BY host, name;
                  """;

        var cookies = new List<PlaywrightCookie>();
        foreach (var line in RunSqliteLines(sqlite3, cookiesDbPath, sql))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            using var document = JsonDocument.Parse(line);
            var element = document.RootElement;
            var host = element.GetProperty("host").GetString() ?? string.Empty;
            var path = element.GetProperty("path").GetString() ?? "/";
            var name = element.GetProperty("name").GetString() ?? string.Empty;
            var value = element.GetProperty("value").GetString() ?? string.Empty;
            var expiryMs = element.GetProperty("expiry").GetDouble();
            var isSecure = element.GetProperty("isSecure").GetInt32() != 0;
            var isHttpOnly = element.GetProperty("isHttpOnly").GetInt32() != 0;

            if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            cookies.Add(new PlaywrightCookie(
                Name: name,
                Value: value,
                Domain: host,
                Path: path,
                Expires: expiryMs / 1000.0,
                HttpOnly: isHttpOnly,
                Secure: isSecure));
        }

        return cookies;
    }

    private static IReadOnlyList<PlaywrightOrigin> ReadOrigins(string sqlite3, string profileDir)
    {
        var storageDefaultDir = Path.Combine(profileDir, "storage", "default");
        if (!Directory.Exists(storageDefaultDir))
        {
            return [];
        }

        var origins = new List<PlaywrightOrigin>();
        foreach (var originDir in Directory.EnumerateDirectories(storageDefaultDir, "https+++*", SearchOption.TopDirectoryOnly))
        {
            var originName = Path.GetFileName(originDir);
            if (!TryConvertStorageOrigin(originName, out var origin))
            {
                continue;
            }

            var dataDbPath = Path.Combine(originDir, "ls", "data.sqlite");
            if (!File.Exists(dataDbPath))
            {
                continue;
            }

            var localStorage = ReadLocalStorageEntries(sqlite3, dataDbPath);
            if (localStorage.Count > 0)
            {
                origins.Add(new PlaywrightOrigin(origin, localStorage));
            }
        }

        return origins;
    }

    private static IReadOnlyList<PlaywrightLocalStorageItem> ReadLocalStorageEntries(string sqlite3, string dataDbPath)
    {
        var sql = """
                  SELECT key, hex(value)
                  FROM data
                  ORDER BY key;
                  """;

        var items = new List<PlaywrightLocalStorageItem>();
        foreach (var line in RunSqliteLines(sqlite3, dataDbPath, sql))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var separator = line.IndexOf(" | ", StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator];
            var hexValue = line[(separator + 3)..];
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(hexValue))
            {
                continue;
            }

            if (!TryDecodePrintableText(hexValue, out var value))
            {
                continue;
            }

            items.Add(new PlaywrightLocalStorageItem(Name: key, Value: value));
        }

        return items;
    }

    private static IEnumerable<string> RunSqliteLines(string sqlite3, string databasePath, string sql)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = sqlite3,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        startInfo.ArgumentList.Add("-noheader");
        startInfo.ArgumentList.Add("-separator");
        startInfo.ArgumentList.Add(" | ");
        startInfo.ArgumentList.Add(databasePath);
        startInfo.ArgumentList.Add(sql);

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Failed to start sqlite3 at {sqlite3}.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"sqlite3 failed for {databasePath}: {stderr}".Trim());
        }

        return stdout
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static bool TryConvertStorageOrigin(string storageDirName, out string origin)
    {
        origin = string.Empty;
        if (!storageDirName.StartsWith("https+++", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var hostPart = storageDirName["https+++".Length..];
        var host = hostPart.Split('^', 2, StringSplitOptions.None)[0];
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        if (!host.Equals("claude.ai", StringComparison.OrdinalIgnoreCase)
            && !host.Equals("a.claude.ai", StringComparison.OrdinalIgnoreCase)
            && !host.Equals("www.claude.ai", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        origin = $"https://{host}";
        return true;
    }

    private static bool TryDecodePrintableText(string hexValue, out string value)
    {
        value = string.Empty;
        if (string.IsNullOrWhiteSpace(hexValue) || (hexValue.Length % 2) != 0)
        {
            return false;
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromHexString(hexValue);
        }
        catch
        {
            return false;
        }

        var candidate = Encoding.UTF8.GetString(bytes);
        if (candidate.Contains('\uFFFD', StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var ch in candidate)
        {
            if (char.IsControl(ch) && ch is not '\r' and not '\n' and not '\t')
            {
                return false;
            }
        }

        value = candidate;
        return true;
    }

    private static string ResolveSqlite3Executable()
    {
        foreach (var candidate in Sqlite3Candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        throw new FileNotFoundException(
            "Could not find sqlite3.exe. Set CLAUDEMIGRATOR_SQLITE3_PATH or install Android platform-tools.");
    }

    private sealed record PlaywrightStorageState(
        IReadOnlyList<PlaywrightCookie> Cookies,
        IReadOnlyList<PlaywrightOrigin> Origins);

    private sealed record PlaywrightCookie(
        string Name,
        string Value,
        string Domain,
        string Path,
        double Expires,
        bool HttpOnly,
        bool Secure);

    private sealed record PlaywrightOrigin(
        string Origin,
        IReadOnlyList<PlaywrightLocalStorageItem> LocalStorage);

    private sealed record PlaywrightLocalStorageItem(
        string Name,
        string Value);
}
