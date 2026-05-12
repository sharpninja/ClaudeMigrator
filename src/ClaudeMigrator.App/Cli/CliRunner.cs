using ClaudeMigrator.Core.Local;
using ClaudeMigrator.Core.Migration;
using ClaudeMigrator.Core.Models;
using ClaudeMigrator.Core.Paths;
using ClaudeMigrator.Core.RemoteTargets;
using ClaudeMigrator.Core.Utilities;
using ClaudeMigrator.Core.Web;

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

        if (ContainsSwitch(args, "--recreate-web-export"))
        {
            try
            {
                var result = RunRecreateWebExport(ParseRecreateWebExportOptions(args));
                Environment.ExitCode = result.FailedOperationCount == 0 ? 0 : 2;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"ClaudeMigrator CLI failed: {ex.Message}");
                Console.Error.WriteLine(ex);
                Environment.ExitCode = 1;
            }

            return true;
        }

        if (ContainsSwitch(args, "--verify-web-recreation"))
        {
            try
            {
                var result = RunVerifyWebRecreation(ParseVerifyWebRecreationOptions(args));
                Environment.ExitCode = result.FailedOperationCount == 0 ? 0 : 2;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"ClaudeMigrator CLI failed: {ex.Message}");
                Console.Error.WriteLine(ex);
                Environment.ExitCode = 1;
            }

            return true;
        }

        if (ContainsSwitch(args, "--migrate-local-agent-sessions"))
        {
            try
            {
                var result = RunMigrateLocalAgentSessions(ParseLocalAgentSessionsOptions(args));
                Environment.ExitCode = result.FailedFileCount == 0 ? 0 : 2;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"ClaudeMigrator CLI failed: {ex.Message}");
                Console.Error.WriteLine(ex);
                Environment.ExitCode = 1;
            }

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

    private static ClaudeWebRecreationResult RunRecreateWebExport(ClaudeWebRecreationOptions options)
    {
        var recreator = new ClaudeWebRecreator(message => Console.WriteLine(message));
        var result = recreator.RecreateAsync(options).GetAwaiter().GetResult();

        Console.WriteLine($"Web recreation manifest written to {result.ManifestPath}");
        Console.WriteLine($"Target organization: {result.TargetOrganizationName} ({result.TargetOrganizationUuid})");
        Console.WriteLine($"Source conversations: {result.SourceConversationCount}; messages: {result.SourceConversationMessageCount}");
        Console.WriteLine($"Projects created/existing: {result.CreatedProjectCount}/{result.ExistingProjectCount}");
        Console.WriteLine($"Conversations created/existing: {result.CreatedConversationCount}/{result.ExistingConversationCount}");
        Console.WriteLine($"Docs created/existing: {result.CreatedDocCount}/{result.ExistingDocCount}");
        Console.WriteLine($"Failed operations: {result.FailedOperationCount}");

        return result;
    }

    private static LocalAgentSessionsMigrationResult RunMigrateLocalAgentSessions(LocalAgentSessionsMigrationOptions options)
    {
        var migrator = new LocalAgentSessionsMigrator(message => Console.WriteLine(message));
        var result = migrator.Migrate(options);

        Console.WriteLine($"Source directory: {result.SourceDirectory}");
        Console.WriteLine($"Target directory: {result.TargetDirectory}");
        Console.WriteLine($"Copied: {result.CopiedFileCount} files, {result.TotalBytesCopied} bytes");
        Console.WriteLine($"Skipped (already present): {result.SkippedFileCount}");
        Console.WriteLine($"Failed: {result.FailedFileCount}");
        if (result.FailedRelativePaths.Count > 0)
        {
            foreach (var path in result.FailedRelativePaths)
            {
                Console.WriteLine($"  failed: {path}");
            }
        }

        return result;
    }

    private static ClaudeWebRecreationVerificationResult RunVerifyWebRecreation(ClaudeWebRecreationVerificationOptions options)
    {
        var recreator = new ClaudeWebRecreator(message => Console.WriteLine(message));
        var result = recreator.VerifyAsync(options).GetAwaiter().GetResult();

        Console.WriteLine($"Web recreation verification written to {result.VerificationPath}");
        Console.WriteLine($"Target organization: {result.TargetOrganizationUuid}");
        Console.WriteLine($"Conversations verified/expected: {result.VerifiedConversationCount}/{result.ExpectedConversationCount}");
        Console.WriteLine($"Projects verified/expected: {result.VerifiedProjectCount}/{result.ExpectedProjectCount}");
        Console.WriteLine($"Docs verified/expected: {result.VerifiedDocCount}/{result.ExpectedDocCount}");
        Console.WriteLine($"Failed operations: {result.FailedOperationCount}");

        return result;
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
            DestinationHome = values.TryGetValue("destination-home", out var destinationHome) && !string.IsNullOrWhiteSpace(destinationHome)
                ? Path.GetFullPath(destinationHome)
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            SourceMachineName = values.GetValueOrDefault("source-machine-name"),
            SourceHost = values.GetValueOrDefault("source-host"),
            ConnectionMethod = values.GetValueOrDefault("connection-method") ?? RemoteMethods.Ssh,
            SourceUser = values.GetValueOrDefault("source-user"),
            SourceAccount = values.GetValueOrDefault("source-account"),
            TargetAccount = values.GetValueOrDefault("target-account"),
            SourceRepoRoot = values.GetValueOrDefault("source-repo-root"),
            TargetApps = targetApps,
        };
    }

    private static ClaudeWebRecreationOptions ParseRecreateWebExportOptions(string[] args)
    {
        var values = ParseNamedArguments(args);
        if (!values.TryGetValue("export-zip", out var exportZip) || string.IsNullOrWhiteSpace(exportZip))
        {
            throw new ArgumentException("--export-zip is required.");
        }

        var edgeDebugUrl = values.TryGetValue("edge-debug-url", out var explicitEdgeDebugUrl) && !string.IsNullOrWhiteSpace(explicitEdgeDebugUrl)
            ? explicitEdgeDebugUrl
            : Environment.GetEnvironmentVariable("CLAUDEMIGRATOR_LIVE_EDGE_DEBUG_URL") ?? "http://127.0.0.1:9222";

        var outputManifest = values.TryGetValue("output-manifest", out var explicitOutputManifest) && !string.IsNullOrWhiteSpace(explicitOutputManifest)
            ? Path.GetFullPath(explicitOutputManifest)
            : Path.Combine(
                Directory.GetCurrentDirectory(),
                "runtime",
                "web_recreation",
                $"claude_web_recreation_{PathUtils.TimestampTag()}.json");

        return new ClaudeWebRecreationOptions(
            ExportZipPath: Path.GetFullPath(exportZip),
            EdgeDebugUrl: edgeDebugUrl,
            OutputManifestPath: outputManifest,
            DryRun: ContainsSwitch(args, "--dry-run"),
            TranscriptProjectName: values.GetValueOrDefault("transcript-project-name"),
            Model: values.GetValueOrDefault("model"));
    }

    private static LocalAgentSessionsMigrationOptions ParseLocalAgentSessionsOptions(string[] args)
    {
        var values = ParseNamedArguments(args);

        return new LocalAgentSessionsMigrationOptions(
            SourceAccountUuid: RequireValue(values, "source-account-uuid"),
            SourceOrgUuid: RequireValue(values, "source-org-uuid"),
            TargetAccountUuid: RequireValue(values, "target-account-uuid"),
            TargetOrgUuid: RequireValue(values, "target-org-uuid"),
            SessionsRoot: values.GetValueOrDefault("sessions-root"),
            DryRun: ContainsSwitch(args, "--dry-run"),
            Overwrite: ContainsSwitch(args, "--overwrite"));
    }

    private static string RequireValue(IReadOnlyDictionary<string, string?> values, string key)
    {
        if (!values.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"--{key} is required.");
        }

        return value;
    }

    private static ClaudeWebRecreationVerificationOptions ParseVerifyWebRecreationOptions(string[] args)
    {
        var values = ParseNamedArguments(args);
        if (!values.TryGetValue("manifest", out var manifest) || string.IsNullOrWhiteSpace(manifest))
        {
            throw new ArgumentException("--manifest is required.");
        }

        var edgeDebugUrl = values.TryGetValue("edge-debug-url", out var explicitEdgeDebugUrl) && !string.IsNullOrWhiteSpace(explicitEdgeDebugUrl)
            ? explicitEdgeDebugUrl
            : Environment.GetEnvironmentVariable("CLAUDEMIGRATOR_LIVE_EDGE_DEBUG_URL") ?? "http://127.0.0.1:9222";

        var outputPath = values.TryGetValue("output-verification", out var explicitOutputPath) && !string.IsNullOrWhiteSpace(explicitOutputPath)
            ? Path.GetFullPath(explicitOutputPath)
            : null;

        return new ClaudeWebRecreationVerificationOptions(
            ManifestPath: Path.GetFullPath(manifest),
            EdgeDebugUrl: edgeDebugUrl,
            OutputPath: outputPath);
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
        Console.WriteLine("  --build-source-bundle           Snapshot the local .claude profile and create a local bundle");
        Console.WriteLine("  --recreate-web-export           Recreate Claude web export projects, chats, and transcript docs through an attached Edge session");
        Console.WriteLine("  --verify-web-recreation         Verify a web recreation manifest through an attached Edge session");
        Console.WriteLine("  --migrate-local-agent-sessions  Copy Claude Desktop Cowork/agent sessions (local_*.json, agent/, scheduled-tasks.json, spaces.json) between accounts; rpm/ excluded");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --source-home <path>");
        Console.WriteLine("  --destination-home <path>");
        Console.WriteLine("  --source-machine-name <name>");
        Console.WriteLine("  --source-host <host>");
        Console.WriteLine("  --source-user <user>");
        Console.WriteLine("  --source-account <label>");
        Console.WriteLine("  --target-account <label>");
        Console.WriteLine("  --source-repo-root <path>");
        Console.WriteLine("  --connection-method <ssh|wsman|local>");
        Console.WriteLine("  --target-apps <claude,codex>");
        Console.WriteLine("  --export-zip <path>");
        Console.WriteLine("  --edge-debug-url <url>       Defaults to CLAUDEMIGRATOR_LIVE_EDGE_DEBUG_URL or http://127.0.0.1:9222");
        Console.WriteLine("  --output-manifest <path>");
        Console.WriteLine("  --manifest <path>");
        Console.WriteLine("  --output-verification <path>");
        Console.WriteLine("  --transcript-project-name <name>");
        Console.WriteLine("  --model <model>");
        Console.WriteLine("  --source-account-uuid <uuid>");
        Console.WriteLine("  --source-org-uuid <uuid>");
        Console.WriteLine("  --target-account-uuid <uuid>");
        Console.WriteLine("  --target-org-uuid <uuid>");
        Console.WriteLine("  --sessions-root <path>          Defaults to %APPDATA%\\Claude\\local-agent-mode-sessions");
        Console.WriteLine("  --overwrite                     Overwrite existing destination files");
        Console.WriteLine("  --dry-run");
    }
}
