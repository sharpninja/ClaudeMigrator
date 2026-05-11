using ClaudeMigrator.Core.Utilities;

namespace ClaudeMigrator.Core.Paths;

public sealed class AppPaths
{
    public AppPaths(string rootDir)
    {
        RootDir = Path.GetFullPath(rootDir);
        RuntimeDir = Path.Combine(RootDir, "migration_data");
        LogsDir = Path.Combine(RuntimeDir, "logs");
        SessionsDir = Path.Combine(RuntimeDir, "sessions");
        ProcessingDir = Path.Combine(RuntimeDir, "processing");
        ExportsDir = Path.Combine(RuntimeDir, "exports");
        LocalBundlesDir = Path.Combine(RuntimeDir, "local_bundles");
        PortableExportsDir = Path.Combine(RuntimeDir, "portable_exports");
        RestoresDir = Path.Combine(RuntimeDir, "restores");
        ErrorsDir = Path.Combine(RuntimeDir, "errors");
        InstallerDir = Path.Combine(RuntimeDir, "installer");
        RemoteTargetsPath = Path.Combine(RuntimeDir, "remote_machines.json");
    }

    public string RootDir { get; }
    public string RuntimeDir { get; }
    public string LogsDir { get; }
    public string SessionsDir { get; }
    public string ProcessingDir { get; }
    public string ExportsDir { get; }
    public string LocalBundlesDir { get; }
    public string PortableExportsDir { get; }
    public string RestoresDir { get; }
    public string ErrorsDir { get; }
    public string InstallerDir { get; }
    public string RemoteTargetsPath { get; }

    public AppPaths Ensure()
    {
        foreach (var directory in new[]
                 {
                     RuntimeDir,
                     LogsDir,
                     SessionsDir,
                     ProcessingDir,
                     ExportsDir,
                     LocalBundlesDir,
                     PortableExportsDir,
                     RestoresDir,
                     ErrorsDir,
                     InstallerDir,
                     Path.GetDirectoryName(RemoteTargetsPath) ?? RuntimeDir,
                 })
        {
            Directory.CreateDirectory(directory);
        }

        return this;
    }

    public IReadOnlyList<string> DefaultDownloadCandidates()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
            ExportsDir,
            RuntimeDir,
        };

        return candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public string? FindLatestExportZip()
    {
        (DateTimeOffset modified, string path)? newest = null;
        foreach (var folder in DefaultDownloadCandidates())
        {
            if (!Directory.Exists(folder))
            {
                continue;
            }

            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
            };

            foreach (var candidate in Directory.EnumerateFiles(folder, "*.zip", options))
            {
                try
                {
                    var modified = File.GetLastWriteTimeUtc(candidate);
                    var timestamp = new DateTimeOffset(modified, TimeSpan.Zero);
                    if (newest is null || timestamp > newest.Value.modified)
                    {
                        newest = (timestamp, candidate);
                    }
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }

        return newest?.path;
    }

    public string SuggestedOutputZip(string prefix = "claude_portable_export")
    {
        var stamp = PathUtils.TimestampTag();
        var candidate = Path.Combine(PortableExportsDir, $"{prefix}_{stamp}.zip");
        var counter = 2;
        while (File.Exists(candidate))
        {
            candidate = Path.Combine(PortableExportsDir, $"{prefix}_{stamp}_{counter}.zip");
            counter++;
        }

        return candidate;
    }

    public string SuggestedLocalBundleZip(string prefix = "claude_local_bundle")
    {
        var stamp = PathUtils.TimestampTag();
        var candidate = Path.Combine(LocalBundlesDir, $"{prefix}_{stamp}.zip");
        var counter = 2;
        while (File.Exists(candidate))
        {
            candidate = Path.Combine(LocalBundlesDir, $"{prefix}_{stamp}_{counter}.zip");
            counter++;
        }

        return candidate;
    }

    public string SuggestedProcessingFolder(string prefix)
    {
        var stamp = PathUtils.TimestampTag();
        var candidate = Path.Combine(ProcessingDir, $"{prefix}_{stamp}");
        var counter = 2;
        while (Directory.Exists(candidate))
        {
            candidate = Path.Combine(ProcessingDir, $"{prefix}_{stamp}_{counter}");
            counter++;
        }

        return candidate;
    }
}
