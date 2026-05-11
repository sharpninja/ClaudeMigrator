using System.IO.Compression;
using System.Text;
using System.Text.Json;
using ClaudeMigrator.Core.Models;
using ClaudeMigrator.Core.Utilities;

namespace ClaudeMigrator.Core.Exporting;

public sealed class LocalClaudeBundleExporter
{
    private const string LocalBundleFormat = "claude_local_bundle";
    private const int LocalBundleVersion = 1;
    private const string LocalProfileRoot = ".claude";
    private const string LocalAccountFile = ".claude.json";

    private static readonly string[] DefaultTargetApps = ["claude", "codex"];
    private static readonly Dictionary<string, string> TargetRoots = new(StringComparer.OrdinalIgnoreCase)
    {
        ["claude"] = ".claude",
        ["codex"] = ".codex",
    };

    private static readonly JsonSerializerOptions JsonOptions = JsonUtils.SnakeCaseIndented;

    private readonly string _runtimeRoot;
    private readonly string _processingRoot;
    private readonly string _exportsRoot;
    private readonly Action<string> _log;

    public LocalClaudeBundleExporter(string runtimeRoot, Action<string>? log = null)
    {
        _runtimeRoot = runtimeRoot;
        _processingRoot = PathUtils.EnsureDirectory(Path.Combine(_runtimeRoot, "processing")).FullName;
        _exportsRoot = PathUtils.EnsureDirectory(Path.Combine(_runtimeRoot, "local_bundles")).FullName;
        _log = log ?? (_ => { });
    }

    public LocalBundleResult ExportLocalBundle(
        string? outputZip = null,
        string? sourceHome = null,
        string? sourceMachineName = null,
        string? sourceHost = null,
        string connectionMethod = "local",
        string? sourceUser = null,
        string? sourceRepoRoot = null,
        IEnumerable<string>? targetApps = null,
        Action<int, string>? progressCallback = null,
        Action<string>? logCallback = null)
    {
        void Emit(int percent, string message) => progressCallback?.Invoke(percent, message);
        void LogLine(string message) => (logCallback ?? _log).Invoke(message);

        var normalizedSourceHome = string.IsNullOrWhiteSpace(sourceHome)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : sourceHome;
        var profileRoot = Path.Combine(normalizedSourceHome, LocalProfileRoot);
        var accountFile = Path.Combine(normalizedSourceHome, LocalAccountFile);
        var targets = NormalizeTargets(targetApps);

        LogLine(
            "Local bundle source: "
            + $"home={normalizedSourceHome} machine={sourceMachineName ?? string.Empty} host={sourceHost ?? string.Empty} "
            + $"method={connectionMethod} user={sourceUser ?? string.Empty} repo_root={sourceRepoRoot ?? string.Empty} "
            + $"targets={string.Join(',', targets)}");

        Emit(5, "Reading local Claude profile");
        var accountPayload = ReadJsonFile(accountFile);
        var accountSummary = BuildAccountSummary(accountPayload);
        var sourceEnvironment = BuildSourceEnvironment(
            sourceHome: normalizedSourceHome,
            sourceMachineName: sourceMachineName,
            sourceHost: sourceHost,
            connectionMethod: connectionMethod,
            sourceUser: sourceUser,
            sourceRepoRoot: sourceRepoRoot,
            targetApps: targets,
            accountSummary: accountSummary);

        var runName = $"claude_local_bundle_{PathUtils.TimestampTag()}";
        var bundleRoot = PathUtils.EnsureDirectory(Path.Combine(_processingRoot, runName)).FullName;
        var sourceRoot = PathUtils.EnsureDirectory(Path.Combine(bundleRoot, "source", "home")).FullName;
        var metadataRoot = PathUtils.EnsureDirectory(Path.Combine(bundleRoot, "metadata")).FullName;
        var restoreRoot = PathUtils.EnsureDirectory(Path.Combine(bundleRoot, "restore")).FullName;

        Emit(15, "Copying source profile tree");
        var profileSnapshotRoot = Path.Combine(sourceRoot, LocalProfileRoot);
        var profileStats = CopyTree(profileRoot, profileSnapshotRoot, LogLine);

        Emit(35, "Copying account metadata");
        var copiedAccountFiles = CopyAccountFiles(normalizedSourceHome, sourceRoot);
        var sourceAccountCopyPath = File.Exists(Path.Combine(sourceRoot, LocalAccountFile))
            ? Path.Combine(sourceRoot, LocalAccountFile)
            : null;

        Emit(55, "Writing source metadata");
        var sourceEnvironmentPath = Path.Combine(metadataRoot, "source_environment.json");
        File.WriteAllText(sourceEnvironmentPath, JsonSerializer.Serialize(sourceEnvironment, JsonOptions), Encoding.UTF8);

        var sourceAccountSummaryPath = WriteSourceAccountSummary(metadataRoot, accountPayload, accountSummary);
        var restorePlan = BuildRestorePlan(targets, normalizedSourceHome);
        var restorePlanPath = Path.Combine(metadataRoot, "restore_plan.json");
        File.WriteAllText(restorePlanPath, JsonSerializer.Serialize(restorePlan, JsonOptions), Encoding.UTF8);
        var restoreReadme = WriteRestoreReadme(restoreRoot, restorePlan, sourceEnvironment, accountSummary);

        var counts = new Dictionary<string, int>
        {
            ["profile_files"] = profileStats.Files,
            ["profile_directories"] = profileStats.Directories,
            ["profile_bytes"] = Saturate(profileStats.Bytes),
            ["account_files"] = copiedAccountFiles.Count,
            ["targets"] = targets.Count,
            ["skipped_entries"] = profileStats.Skipped,
        };

        Emit(75, "Writing manifest");
        var manifest = BuildManifest(
            runName: runName,
            bundleRoot: bundleRoot,
            sourceHome: normalizedSourceHome,
            profileRoot: profileSnapshotRoot,
            accountFile: sourceAccountCopyPath,
            sourceEnvironmentPath: sourceEnvironmentPath,
            restorePlanPath: restorePlanPath,
            restoreReadme: restoreReadme,
            accountSummary: accountSummary,
            targetApps: targets,
            counts: counts,
            sourceEnvironment: sourceEnvironment,
            copiedAccountFiles: copiedAccountFiles,
            sourceAccountSummaryPath: sourceAccountSummaryPath);

        var manifestPath = Path.Combine(bundleRoot, "manifest.json");
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, JsonOptions), Encoding.UTF8);

        if (string.IsNullOrWhiteSpace(outputZip))
        {
            outputZip = Path.Combine(_exportsRoot, $"{runName}.zip");
        }

        outputZip = Path.GetFullPath(outputZip);
        Directory.CreateDirectory(Path.GetDirectoryName(outputZip) ?? ".");
        if (File.Exists(outputZip))
        {
            File.Delete(outputZip);
        }

        Emit(95, "Packaging local bundle zip");
        ZipFile.CreateFromDirectory(bundleRoot, outputZip, CompressionLevel.Optimal, includeBaseDirectory: true);
        Emit(100, "Local bundle ready");
        LogLine($"Local bundle written to {outputZip} from bundle root {bundleRoot}.");

        return new LocalBundleResult(
            SourceHome: normalizedSourceHome,
            ProfileRoot: profileSnapshotRoot,
            AccountFile: sourceAccountCopyPath,
            BundleRoot: bundleRoot,
            ZipPath: outputZip,
            ManifestPath: manifestPath,
            SourceEnvironmentPath: sourceEnvironmentPath,
            SourceAccountPath: sourceAccountCopyPath,
            RestorePlanPath: restorePlanPath,
            Targets: targets,
            Manifest: manifest,
            Counts: counts);
    }

    public Dictionary<string, object?> RestoreLocalBundle(
        string bundlePath,
        string? destinationHome = null,
        IEnumerable<string>? targetApps = null,
        Action<int, string>? progressCallback = null,
        Action<string>? logCallback = null)
    {
        void Emit(int percent, string message) => progressCallback?.Invoke(percent, message);
        void LogLine(string message) => (logCallback ?? _log).Invoke(message);

        var normalizedDestinationHome = string.IsNullOrWhiteSpace(destinationHome)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : destinationHome;
        var (bundleRoot, cleanupDir) = ResolveBundleRoot(bundlePath);

        try
        {
            LogLine($"Restoring local bundle from {bundlePath} into {normalizedDestinationHome}");
            var manifestPath = Path.Combine(bundleRoot, "manifest.json");
            if (!File.Exists(manifestPath))
            {
                throw new FileNotFoundException($"Manifest not found in bundle: {manifestPath}");
            }

            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath, Encoding.UTF8));
            var manifest = document.RootElement;

            var paths = manifest.TryGetProperty("paths", out var pathNode) ? pathNode : default;
            var sourceProfileRelative = paths.ValueKind == JsonValueKind.Object && paths.TryGetProperty("source_profile_root", out var profileNode)
                ? profileNode.GetString() ?? "source/home/.claude"
                : "source/home/.claude";
            var sourceAccountRelative = paths.ValueKind == JsonValueKind.Object && paths.TryGetProperty("source_account_file", out var accountNode)
                ? accountNode.GetString() ?? string.Empty
                : string.Empty;

            var sourceProfileRoot = Path.Combine(bundleRoot, sourceProfileRelative.Replace('/', Path.DirectorySeparatorChar));
            var sourceAccountFile = string.IsNullOrWhiteSpace(sourceAccountRelative)
                ? null
                : Path.Combine(bundleRoot, sourceAccountRelative.Replace('/', Path.DirectorySeparatorChar));
            var sourceAccountRoot = Path.GetDirectoryName(sourceProfileRoot) ?? sourceProfileRoot;
            var targets = NormalizeTargets(targetApps ?? ReadTargetNames(manifest));
            var restored = new List<Dictionary<string, object?>>();
            var total = Math.Max(1, targets.Count);

            for (var index = 0; index < targets.Count; index++)
            {
                var target = targets[index];
                var targetRootName = TargetRoots[target];
                var destinationRoot = Path.Combine(normalizedDestinationHome, targetRootName);
                Emit(10 + (int)(((double)index / total) * 75), $"Restoring {target} bundle");
                CopyTree(sourceProfileRoot, destinationRoot, LogLine);

                if (string.Equals(target, "claude", StringComparison.OrdinalIgnoreCase))
                {
                    if (Directory.Exists(sourceAccountRoot))
                    {
                        foreach (var candidate in Directory.EnumerateFiles(sourceAccountRoot, $"{LocalAccountFile}*", SearchOption.TopDirectoryOnly))
                        {
                            try
                            {
                                if (File.Exists(candidate))
                                {
                                    File.Copy(candidate, Path.Combine(normalizedDestinationHome, Path.GetFileName(candidate)), overwrite: true);
                                }
                            }
                            catch (Exception ex)
                            {
                                LogLine($"Skipping account metadata file {candidate}: {ex.Message}");
                            }
                        }
                    }
                    else if (!string.IsNullOrWhiteSpace(sourceAccountFile) && File.Exists(sourceAccountFile))
                    {
                        File.Copy(sourceAccountFile, Path.Combine(normalizedDestinationHome, LocalAccountFile), overwrite: true);
                    }
                }

                restored.Add(
                    new Dictionary<string, object?>
                    {
                        ["target"] = target,
                        ["destination_root"] = destinationRoot,
                        ["source_root"] = sourceProfileRoot,
                    });
            }

            var result = new Dictionary<string, object?>
            {
                ["bundle_root"] = bundleRoot,
                ["destination_home"] = normalizedDestinationHome,
                ["restored_targets"] = restored,
                ["source_machine"] = ReadString(manifest, "source_environment", "source_machine_name"),
                ["source_account"] = ReadObject(manifest, "source_account"),
            };

            Emit(100, "Local bundle restored");
            LogLine($"Restored local bundle into {normalizedDestinationHome} (targets={string.Join(',', targets)}).");
            return result;
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(cleanupDir) && Directory.Exists(cleanupDir))
            {
                try
                {
                    Directory.Delete(cleanupDir, recursive: true);
                }
                catch
                {
                }
            }
        }
    }

    private static IReadOnlyList<string> NormalizeTargets(IEnumerable<string>? targetApps)
    {
        var targets = new List<string>();
        if (targetApps is null)
        {
            targets.AddRange(DefaultTargetApps);
        }
        else
        {
            foreach (var value in targetApps)
            {
                var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
                if (TargetRoots.ContainsKey(normalized) && !targets.Any(item => string.Equals(item, normalized, StringComparison.OrdinalIgnoreCase)))
                {
                    targets.Add(normalized);
                }
            }
        }

        if (targets.Count == 0)
        {
            targets.Add("claude");
        }

        return targets;
    }

    private static Dictionary<string, object?> ReadJsonFile(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? ConvertObject(document.RootElement)
                : [];
        }
        catch
        {
            return [];
        }
    }

    private static CopyStats CopyTree(string source, string destination, Action<string>? log = null)
    {
        var stats = new CopyStats();

        void LogSkip(string message)
        {
            stats = stats with { Skipped = stats.Skipped + 1 };
            log?.Invoke(message);
        }

        void CopyEntry(string sourcePath, string destinationPath)
        {
            try
            {
                var attributes = File.GetAttributes(sourcePath);
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    LogSkip($"Skipping reparse point {sourcePath}");
                    return;
                }

                if (Directory.Exists(sourcePath))
                {
                    Directory.CreateDirectory(destinationPath);
                    stats = stats with { Directories = stats.Directories + 1 };
                    foreach (var entry in Directory.EnumerateFileSystemEntries(sourcePath))
                    {
                        CopyEntry(entry, Path.Combine(destinationPath, Path.GetFileName(entry)));
                    }

                    return;
                }

                if (File.Exists(sourcePath))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? ".");
                    File.Copy(sourcePath, destinationPath, overwrite: true);
                    stats = stats with
                    {
                        Files = stats.Files + 1,
                        Bytes = stats.Bytes + new FileInfo(sourcePath).Length,
                    };
                    return;
                }

                LogSkip($"Skipping unsupported local profile entry {sourcePath}");
            }
            catch (FileNotFoundException ex)
            {
                LogSkip($"Skipping missing local profile entry {sourcePath}: {ex.Message}");
            }
            catch (DirectoryNotFoundException ex)
            {
                LogSkip($"Skipping missing local profile entry {sourcePath}: {ex.Message}");
            }
            catch (IOException ex)
            {
                LogSkip($"Skipping unreadable local profile entry {sourcePath}: {ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                LogSkip($"Skipping inaccessible local profile entry {sourcePath}: {ex.Message}");
            }
        }

        if (!Directory.Exists(source))
        {
            if (File.Exists(source))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? ".");
                File.Copy(source, destination, overwrite: true);
                return stats with { Files = 1, Bytes = new FileInfo(source).Length };
            }

            Directory.CreateDirectory(destination);
            return stats;
        }

        Directory.CreateDirectory(destination);
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            CopyEntry(entry, Path.Combine(destination, Path.GetFileName(entry)));
        }

        return stats;
    }

    private static IReadOnlyList<string> CopyAccountFiles(string sourceHome, string sourceRoot)
    {
        var copied = new List<string>();
        foreach (var candidate in Directory.EnumerateFiles(sourceHome, $"{LocalAccountFile}*", SearchOption.TopDirectoryOnly))
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            var destination = Path.Combine(sourceRoot, Path.GetFileName(candidate));
            File.Copy(candidate, destination, overwrite: true);
            copied.Add(Path.GetFileName(destination));
        }

        return copied;
    }

    private static Dictionary<string, object?> BuildAccountSummary(Dictionary<string, object?> accountPayload)
    {
        var oauth = ReadNestedDictionary(accountPayload, "oauthAccount");
        var projects = ReadNestedDictionary(accountPayload, "projects");
        return new Dictionary<string, object?>
        {
            ["email_address"] = ReadValue(oauth, "emailAddress"),
            ["display_name"] = ReadValue(oauth, "displayName"),
            ["account_uuid"] = ReadValue(oauth, "accountUuid"),
            ["organization_name"] = ReadValue(oauth, "organizationName"),
            ["organization_uuid"] = ReadValue(oauth, "organizationUuid"),
            ["project_count"] = projects.Count,
            ["project_paths"] = projects.Keys.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray(),
            ["keys"] = accountPayload.Keys.OrderBy(key => key, StringComparer.OrdinalIgnoreCase).ToArray(),
        };
    }

    private static Dictionary<string, object?> BuildSourceEnvironment(
        string sourceHome,
        string? sourceMachineName,
        string? sourceHost,
        string connectionMethod,
        string? sourceUser,
        string? sourceRepoRoot,
        IReadOnlyList<string> targetApps,
        Dictionary<string, object?> accountSummary)
    {
        var env = new Dictionary<string, object?>
        {
            ["computername"] = Environment.GetEnvironmentVariable("COMPUTERNAME") ?? string.Empty,
            ["appdata"] = Environment.GetEnvironmentVariable("APPDATA") ?? string.Empty,
            ["home"] = Environment.GetEnvironmentVariable("HOME") ?? string.Empty,
            ["homedrive"] = Environment.GetEnvironmentVariable("HOMEDRIVE") ?? string.Empty,
            ["homepath"] = Environment.GetEnvironmentVariable("HOMEPATH") ?? string.Empty,
            ["localappdata"] = Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? string.Empty,
            ["number_of_processors"] = Environment.GetEnvironmentVariable("NUMBER_OF_PROCESSORS") ?? string.Empty,
            ["os"] = Environment.GetEnvironmentVariable("OS") ?? string.Empty,
            ["processor_architecture"] = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE") ?? string.Empty,
            ["processor_identifier"] = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? string.Empty,
            ["programdata"] = Environment.GetEnvironmentVariable("PROGRAMDATA") ?? string.Empty,
            ["temp"] = Environment.GetEnvironmentVariable("TEMP") ?? string.Empty,
            ["tmp"] = Environment.GetEnvironmentVariable("TMP") ?? string.Empty,
            ["username"] = Environment.GetEnvironmentVariable("USERNAME") ?? string.Empty,
            ["userprofile"] = Environment.GetEnvironmentVariable("USERPROFILE") ?? string.Empty,
        };

        return new Dictionary<string, object?>
        {
            ["format"] = LocalBundleFormat,
            ["version"] = LocalBundleVersion,
            ["exported_at"] = PathUtils.TimestampTag(),
            ["source_machine_name"] = string.IsNullOrWhiteSpace(sourceMachineName) ? Environment.MachineName : sourceMachineName,
            ["source_host"] = string.IsNullOrWhiteSpace(sourceHost) ? Environment.MachineName : sourceHost,
            ["source_user"] = string.IsNullOrWhiteSpace(sourceUser) ? Environment.UserName : sourceUser,
            ["connection_method"] = string.IsNullOrWhiteSpace(connectionMethod) ? "local" : connectionMethod.Trim().ToLowerInvariant(),
            ["source_repo_root"] = sourceRepoRoot ?? string.Empty,
            ["source_home"] = sourceHome,
            ["source_profile_root"] = Path.Combine(sourceHome, LocalProfileRoot),
            ["source_account_file"] = Path.Combine(sourceHome, LocalAccountFile),
            ["source_working_directory"] = Environment.CurrentDirectory,
            ["platform"] = new Dictionary<string, object?>
            {
                ["system"] = Environment.OSVersion.Platform.ToString(),
                ["release"] = Environment.OSVersion.VersionString,
                ["version"] = Environment.OSVersion.Version.ToString(),
                ["machine"] = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE") ?? Environment.MachineName,
                ["architecture"] = Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit",
                ["dotnet"] = Environment.Version.ToString(),
            },
            ["environment"] = env,
            ["account"] = accountSummary,
            ["target_apps"] = targetApps.ToArray(),
        };
    }

    private static Dictionary<string, object?> BuildRestorePlan(IReadOnlyList<string> targetApps, string sourceHome)
    {
        var targets = new List<Dictionary<string, object?>>();
        foreach (var target in targetApps)
        {
            var targetRootName = TargetRoots[target];
            var targetRoot = Path.Combine(sourceHome, targetRootName);
            targets.Add(new Dictionary<string, object?>
            {
                ["app"] = target,
                ["target_profile_root"] = $"~/{targetRootName}",
                ["target_home"] = sourceHome,
                ["target_root_path"] = targetRoot,
                ["writeback_strategy"] = "mirror-profile-root",
            });
        }

        return new Dictionary<string, object?>
        {
            ["source_profile_root"] = $"~/{LocalProfileRoot}",
            ["source_account_file"] = $"~/{LocalAccountFile}",
            ["targets"] = targets,
            ["notes"] = new[]
            {
                "Mirror the source .claude profile tree into the selected target root.",
                "Claude targets also receive the account metadata file at the home root.",
            },
        };
    }

    private static string WriteSourceAccountSummary(string metadataRoot, Dictionary<string, object?> accountPayload, Dictionary<string, object?> accountSummary)
    {
        var path = Path.Combine(metadataRoot, "source_account.json");
        var payload = new Dictionary<string, object?>
        {
            ["summary"] = accountSummary,
            ["raw"] = accountPayload,
        };
        File.WriteAllText(path, JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8);
        return path;
    }

    private static string WriteRestoreReadme(string restoreRoot, Dictionary<string, object?> restorePlan, Dictionary<string, object?> sourceEnvironment, Dictionary<string, object?> accountSummary)
    {
        var lines = new List<string>
        {
            "# Local Claude Bundle Restore",
            "",
            "## Source Machine",
            $"- Name: `{ReadValue(sourceEnvironment, "source_machine_name")}`",
            $"- Host: `{ReadValue(sourceEnvironment, "source_host")}`",
            $"- User: `{ReadValue(sourceEnvironment, "source_user")}`",
            $"- Connection: `{ReadValue(sourceEnvironment, "connection_method")}`",
            "",
            "## Source Account",
            $"- Email: `{ReadValue(accountSummary, "email_address")}`",
            $"- Display name: `{ReadValue(accountSummary, "display_name")}`",
            $"- Account UUID: `{ReadValue(accountSummary, "account_uuid")}`",
            "",
            "## Restore Targets",
        };

        if (restorePlan.TryGetValue("targets", out var targetsObj) && targetsObj is IEnumerable<object?> targets)
        {
            foreach (var target in targets.OfType<Dictionary<string, object?>>())
            {
                lines.Add($"- `{ReadValue(target, "app")}` -> `{ReadValue(target, "target_profile_root")}`");
            }
        }

        lines.AddRange(new[]
        {
            "",
            "## Notes",
            "- Copy the profile tree into the selected root for the new account.",
            "- Claude restores also copy the account metadata file back to the home root.",
            "- Codex restores mirror the source profile tree into `~/.codex`.",
        });

        Directory.CreateDirectory(restoreRoot);
        var path = Path.Combine(restoreRoot, "README.md");
        File.WriteAllText(path, string.Join(Environment.NewLine, lines).Trim() + Environment.NewLine, Encoding.UTF8);
        return path;
    }

    private static Dictionary<string, object?> BuildManifest(
        string runName,
        string bundleRoot,
        string sourceHome,
        string profileRoot,
        string? accountFile,
        string sourceEnvironmentPath,
        string restorePlanPath,
        string restoreReadme,
        Dictionary<string, object?> accountSummary,
        IReadOnlyList<string> targetApps,
        IReadOnlyDictionary<string, int> counts,
        Dictionary<string, object?> sourceEnvironment,
        IReadOnlyList<string> copiedAccountFiles,
        string sourceAccountSummaryPath)
    {
        return new Dictionary<string, object?>
        {
            ["format"] = LocalBundleFormat,
            ["version"] = LocalBundleVersion,
            ["bundle_name"] = runName,
            ["created_at"] = PathUtils.TimestampTag(),
            ["source_environment"] = sourceEnvironment,
            ["source_account"] = accountSummary,
            ["source_home"] = sourceHome,
            ["source_profile_root"] = profileRoot,
            ["paths"] = new Dictionary<string, object?>
            {
                ["source_profile_root"] = RelativePath(bundleRoot, profileRoot),
                ["source_account_file"] = string.IsNullOrWhiteSpace(accountFile) ? string.Empty : RelativePath(bundleRoot, accountFile),
                ["source_environment"] = RelativePath(bundleRoot, sourceEnvironmentPath),
                ["source_account_summary"] = RelativePath(bundleRoot, sourceAccountSummaryPath),
                ["restore_plan"] = RelativePath(bundleRoot, restorePlanPath),
                ["restore_readme"] = RelativePath(bundleRoot, restoreReadme),
            },
            ["target_apps"] = targetApps.ToArray(),
            ["counts"] = counts.ToDictionary(pair => pair.Key, pair => (object?)pair.Value),
            ["account_files"] = copiedAccountFiles.ToArray(),
            ["restore_targets"] = targetApps.Select(target => new Dictionary<string, object?>
            {
                ["app"] = target,
                ["profile_root"] = $"~/{TargetRoots[target]}",
                ["writeback_strategy"] = "mirror-profile-root",
            }).ToArray(),
            ["import_guides"] = new Dictionary<string, object?>
            {
                ["claude"] = new[]
                {
                    "Open the Claude account and import the bundle metadata.",
                    "Restore the profile tree into the user home root if the UI supports it.",
                },
                ["codex"] = new[]
                {
                    "Copy the profile tree into ~/.codex on the target machine.",
                    "Use the seed prompts and project blueprints to continue work in Codex.",
                },
                ["copilot"] = new[]
                {
                    "Copy the relevant instructions and summaries into your Copilot workflow.",
                },
            },
        };
    }

    private static IReadOnlyList<string> ReadTargetNames(JsonElement manifest)
    {
        if (manifest.ValueKind == JsonValueKind.Object && manifest.TryGetProperty("target_apps", out var targets) && targets.ValueKind == JsonValueKind.Array)
        {
            return targets.EnumerateArray().Select(item => item.GetString() ?? string.Empty).Where(item => !string.IsNullOrWhiteSpace(item)).ToList();
        }

        return [];
    }

    private static string ReadString(JsonElement root, string parentKey, string childKey)
    {
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(parentKey, out var parent) && parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(childKey, out var value))
        {
            return value.GetString() ?? string.Empty;
        }

        return string.Empty;
    }

    private static Dictionary<string, object?> ReadObject(JsonElement root, string key)
    {
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Object)
        {
            return JsonSerializer.Deserialize<Dictionary<string, object?>>(value.GetRawText(), JsonUtils.SnakeCaseCompact) ?? [];
        }

        return [];
    }

    private static Dictionary<string, object?> ReadNestedDictionary(Dictionary<string, object?> root, string key)
    {
        if (root.TryGetValue(key, out var value) && value is Dictionary<string, object?> nested)
        {
            return nested;
        }

        return [];
    }

    private static Dictionary<string, object?> ConvertObject(JsonElement element)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in element.EnumerateObject())
        {
            result[property.Name] = ConvertElement(property.Value);
        }

        return result;
    }

    private static object? ConvertElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => ConvertObject(element),
            JsonValueKind.Array => element.EnumerateArray().Select(ConvertElement).ToArray(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number when element.TryGetInt64(out var longValue) => longValue,
            JsonValueKind.Number => element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => element.ToString(),
        };
    }

    private static string ReadValue(Dictionary<string, object?> root, string key)
    {
        if (!root.TryGetValue(key, out var value) || value is null)
        {
            return string.Empty;
        }

        return value switch
        {
            string text => text,
            JsonElement element => element.ValueKind == JsonValueKind.String ? element.GetString() ?? string.Empty : element.ToString(),
            _ => value.ToString() ?? string.Empty,
        };
    }

    private static string RelativePath(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative.Replace('\\', '/');
    }

    private static int Saturate(long value) => value > int.MaxValue ? int.MaxValue : (int)value;

    private static (string bundleRoot, string? cleanupDir) ResolveBundleRoot(string bundlePath)
    {
        if (Directory.Exists(bundlePath))
        {
            return (Path.GetFullPath(bundlePath), null);
        }

        if (!File.Exists(bundlePath))
        {
            throw new FileNotFoundException($"Bundle path not found: {bundlePath}");
        }

        if (!bundlePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Unsupported bundle path: {bundlePath}");
        }

        var cleanupDir = Path.Combine(Path.GetTempPath(), $"claude_local_bundle_{Guid.NewGuid():N}");
        Directory.CreateDirectory(cleanupDir);
        ZipFile.ExtractToDirectory(bundlePath, cleanupDir, overwriteFiles: true);
        var extractedRoot = cleanupDir;
        var bundleName = System.IO.Path.GetFileNameWithoutExtension(bundlePath);
        var namedRoot = Path.Combine(extractedRoot, bundleName);
        if (Directory.Exists(namedRoot))
        {
            return (namedRoot, cleanupDir);
        }

        var children = Directory.EnumerateDirectories(extractedRoot).ToList();
        if (children.Count == 1)
        {
            return (children[0], cleanupDir);
        }

        return (extractedRoot, cleanupDir);
    }

    private sealed record CopyStats(int Files = 0, int Directories = 0, long Bytes = 0, int Skipped = 0);
}
