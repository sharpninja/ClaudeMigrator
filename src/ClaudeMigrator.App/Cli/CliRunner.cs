using ClaudeMigrator.Core.Migration;
using ClaudeMigrator.Core.Models;
using ClaudeMigrator.Core.Paths;
using ClaudeMigrator.Core.RemoteTargets;

namespace ClaudeMigrator.App.Cli;

public static class CliRunner
{
    public static bool TryRun(string[] args)
    {
        if (args.Length == 0)
        {
            return false;
        }

        if (ContainsSwitch(args, "--help") || ContainsSwitch(args, "-h") || ContainsSwitch(args, "/?"))
        {
            PrintUsage();
            Environment.ExitCode = 0;
            return true;
        }

        if (!ContainsSwitch(args, "--build-source-bundle"))
        {
            return false;
        }

        try
        {
            RunBuildSourceBundle(ParseBuildSourceBundleOptions(args));
            Environment.ExitCode = 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ClaudeMigrator CLI failed: {ex.Message}");
            Console.Error.WriteLine(ex);
            Environment.ExitCode = 1;
        }

        return true;
    }

    private static void RunBuildSourceBundle(MigrationOptions options)
    {
        var paths = new AppPaths(Directory.GetCurrentDirectory()).Ensure();
        using var controller = new MigrationController(paths, message => Console.WriteLine(message));
        controller.BuildSourceBundleAsync(options).GetAwaiter().GetResult();

        if (controller.LocalBundleResult is not null)
        {
            Console.WriteLine($"Local bundle written to {controller.LocalBundleResult.ZipPath}");
        }
    }

    private static MigrationOptions ParseBuildSourceBundleOptions(string[] args)
    {
        var values = ParseNamedArguments(args);
        var targetApps = ParseTargetApps(values.TryGetValue("target-apps", out var targetAppsValue) ? targetAppsValue : null);

        return new MigrationOptions
        {
            SourceMode = SourceMode.LocalSnapshot,
            SourceHome = values.TryGetValue("source-home", out var sourceHome) && !string.IsNullOrWhiteSpace(sourceHome)
                ? Path.GetFullPath(sourceHome)
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            SourceMachineName = values.GetValueOrDefault("source-machine-name"),
            SourceHost = values.GetValueOrDefault("source-host"),
            ConnectionMethod = values.GetValueOrDefault("connection-method") ?? RemoteMethods.Ssh,
            SourceUser = values.GetValueOrDefault("source-user"),
            SourceRepoRoot = values.GetValueOrDefault("source-repo-root"),
            TargetApps = targetApps,
        };
    }

    private static IReadOnlyList<TargetApp> ParseTargetApps(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [TargetApp.Claude, TargetApp.Codex];
        }

        var apps = new List<TargetApp>();
        foreach (var token in value.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Enum.TryParse<TargetApp>(token, ignoreCase: true, out var app) && !apps.Contains(app))
            {
                apps.Add(app);
            }
        }

        return apps.Count > 0 ? apps : [TargetApp.Claude, TargetApp.Codex];
    }

    private static Dictionary<string, string?> ParseNamedArguments(string[] args)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < args.Length; index++)
        {
            var token = args[index];
            if (!IsSwitch(token))
            {
                continue;
            }

            var trimmed = token.TrimStart('-', '/');
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                continue;
            }

            string key;
            string? value = null;
            var equalsIndex = trimmed.IndexOf('=');
            if (equalsIndex >= 0)
            {
                key = trimmed[..equalsIndex];
                value = trimmed[(equalsIndex + 1)..];
            }
            else
            {
                key = trimmed;
                if (index + 1 < args.Length && !IsSwitch(args[index + 1]))
                {
                    value = args[++index];
                }
            }

            values[key] = value;
        }

        return values;
    }

    private static bool ContainsSwitch(IEnumerable<string> args, string switchName)
        => args.Any(arg => string.Equals(arg, switchName, StringComparison.OrdinalIgnoreCase));

    private static bool IsSwitch(string value)
        => value.StartsWith("-", StringComparison.Ordinal) || value.StartsWith("/", StringComparison.Ordinal);

    private static void PrintUsage()
    {
        Console.WriteLine("ClaudeMigrator CLI");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  --build-source-bundle    Snapshot the local .claude profile and create a local bundle");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --source-home <path>");
        Console.WriteLine("  --source-machine-name <name>");
        Console.WriteLine("  --source-host <host>");
        Console.WriteLine("  --source-user <user>");
        Console.WriteLine("  --source-repo-root <path>");
        Console.WriteLine("  --connection-method <ssh|wsman|local>");
        Console.WriteLine("  --target-apps <claude,codex>");
    }
}
