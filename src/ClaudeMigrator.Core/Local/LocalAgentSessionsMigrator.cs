using System.Security.Cryptography;
using ClaudeMigrator.Core.Utilities;

namespace ClaudeMigrator.Core.Local;

public sealed record LocalAgentSessionsMigrationOptions(
    string SourceAccountUuid,
    string SourceOrgUuid,
    string TargetAccountUuid,
    string TargetOrgUuid,
    string? SessionsRoot = null,
    bool DryRun = false,
    bool Overwrite = false);

public sealed record LocalAgentSessionsMigrationResult(
    string SourceDirectory,
    string TargetDirectory,
    int CopiedFileCount,
    int SkippedFileCount,
    int FailedFileCount,
    long TotalBytesCopied,
    IReadOnlyList<string> FailedRelativePaths);

public sealed class LocalAgentSessionsMigrator
{
    public const string DefaultSubfolderName = "local-agent-mode-sessions";
    public const string SkippedSubfolderName = "rpm";

    private readonly Action<string> _log;

    public LocalAgentSessionsMigrator(Action<string>? log = null)
    {
        _log = log ?? (_ => { });
    }

    public static string DefaultSessionsRoot()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude", DefaultSubfolderName);

    public LocalAgentSessionsMigrationResult Migrate(LocalAgentSessionsMigrationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateUuid(options.SourceAccountUuid, nameof(options.SourceAccountUuid));
        ValidateUuid(options.SourceOrgUuid, nameof(options.SourceOrgUuid));
        ValidateUuid(options.TargetAccountUuid, nameof(options.TargetAccountUuid));
        ValidateUuid(options.TargetOrgUuid, nameof(options.TargetOrgUuid));

        var sessionsRoot = string.IsNullOrWhiteSpace(options.SessionsRoot)
            ? DefaultSessionsRoot()
            : Path.GetFullPath(options.SessionsRoot);

        var sourceDir = Path.Combine(sessionsRoot, options.SourceAccountUuid, options.SourceOrgUuid);
        var targetDir = Path.Combine(sessionsRoot, options.TargetAccountUuid, options.TargetOrgUuid);

        if (!Directory.Exists(sourceDir))
        {
            throw new DirectoryNotFoundException($"Source agent sessions folder does not exist: {sourceDir}");
        }

        if (!options.DryRun)
        {
            Directory.CreateDirectory(targetDir);
        }

        var copied = 0;
        var skipped = 0;
        var failed = 0;
        var totalBytes = 0L;
        var failures = new List<string>();

        foreach (var sourceFile in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDir, sourceFile);
            if (IsRpmPath(relative))
            {
                continue;
            }

            if (IsReparsePoint(sourceFile))
            {
                skipped++;
                _log($"skipped symlink {relative}");
                continue;
            }

            var destination = Path.Combine(targetDir, relative);

            try
            {
                if (File.Exists(destination) && !options.Overwrite && SameContent(sourceFile, destination))
                {
                    skipped++;
                    continue;
                }

                if (options.DryRun)
                {
                    copied++;
                    totalBytes += new FileInfo(sourceFile).Length;
                    _log($"[dry-run] copy {relative}");
                    continue;
                }

                var destinationDir = Path.GetDirectoryName(destination);
                if (!string.IsNullOrEmpty(destinationDir))
                {
                    Directory.CreateDirectory(destinationDir);
                }

                File.Copy(sourceFile, destination, overwrite: true);
                var sourceHash = PathUtils.Sha256File(sourceFile);
                var destinationHash = PathUtils.Sha256File(destination);
                if (!string.Equals(sourceHash, destinationHash, StringComparison.OrdinalIgnoreCase))
                {
                    failed++;
                    failures.Add(relative);
                    _log($"hash mismatch after copy: {relative}");
                    continue;
                }

                copied++;
                totalBytes += new FileInfo(destination).Length;
                _log($"copied {relative}");
            }
            catch (Exception ex)
            {
                failed++;
                failures.Add(relative);
                _log($"failed to copy {relative}: {ex.Message}");
            }
        }

        _log($"agent-mode-sessions migration: copied={copied} skipped={skipped} failed={failed} bytes={totalBytes} target={targetDir}");

        return new LocalAgentSessionsMigrationResult(
            SourceDirectory: sourceDir,
            TargetDirectory: targetDir,
            CopiedFileCount: copied,
            SkippedFileCount: skipped,
            FailedFileCount: failed,
            TotalBytesCopied: totalBytes,
            FailedRelativePaths: failures);
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsRpmPath(string relativePath)
    {
        var segment = relativePath.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        return string.Equals(segment, SkippedSubfolderName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool SameContent(string left, string right)
    {
        var leftInfo = new FileInfo(left);
        var rightInfo = new FileInfo(right);
        if (leftInfo.Length != rightInfo.Length)
        {
            return false;
        }

        return string.Equals(PathUtils.Sha256File(left), PathUtils.Sha256File(right), StringComparison.OrdinalIgnoreCase);
    }

    private static void ValidateUuid(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"{parameterName} is required.", parameterName);
        }
    }
}
