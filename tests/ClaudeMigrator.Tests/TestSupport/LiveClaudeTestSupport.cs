using Xunit;

namespace ClaudeMigrator.Tests.TestSupport;

internal static class LiveClaudeTestEnvironment
{
    internal const string EnableVariable = "CLAUDEMIGRATOR_RUN_LIVE_CLAUDE";
    internal const string LiveExportZipVariable = "CLAUDEMIGRATOR_LIVE_EXPORT_ZIP";
    internal const string EdgeStorageStateVariable = "CLAUDEMIGRATOR_LIVE_EDGE_STORAGE_STATE";
    internal const string FirefoxStorageStateVariable = "CLAUDEMIGRATOR_LIVE_FIREFOX_STORAGE_STATE";
    internal const string EdgeDebugUrlVariable = "CLAUDEMIGRATOR_LIVE_EDGE_DEBUG_URL";
    internal const string EdgeProfileRootVariable = "CLAUDEMIGRATOR_LIVE_EDGE_PROFILE_ROOT";
    internal const string EdgeProfileDirectoryVariable = "CLAUDEMIGRATOR_LIVE_EDGE_PROFILE_DIRECTORY";

    internal static bool IsEnabled => IsTruthy(Environment.GetEnvironmentVariable(EnableVariable));

    internal static string? LiveExportZipPath => ResolvePath(LiveExportZipVariable);

    internal static string? EdgeStorageStatePath => ResolvePath(EdgeStorageStateVariable);

    internal static string? FirefoxStorageStatePath => ResolvePath(FirefoxStorageStateVariable);

    internal static string? EdgeDebugUrl => Environment.GetEnvironmentVariable(EdgeDebugUrlVariable);

    internal static string? EdgeProfileRootPath => ResolvePath(EdgeProfileRootVariable);

    internal static string EdgeProfileDirectory => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EdgeProfileDirectoryVariable))
        ? "Profile 1"
        : Environment.GetEnvironmentVariable(EdgeProfileDirectoryVariable)!;

    internal static string? EdgeSkipReason()
    {
        if (!IsEnabled)
        {
            return $"{EnableVariable}=1 is required to run the live Claude browser export test.";
        }

        if (!string.IsNullOrWhiteSpace(LiveExportZipPath))
        {
            return null;
        }

        return ValidateStorageState(EdgeStorageStateVariable, "Edge");
    }

    internal static string? RoundTripSkipReason()
    {
        if (!IsEnabled)
        {
            return $"{EnableVariable}=1 is required to run the live Claude browser round-trip test.";
        }

        if (string.IsNullOrWhiteSpace(EdgeDebugUrl))
        {
            return $"{EdgeDebugUrlVariable} must point to a Chrome DevTools URL such as http://127.0.0.1:9222.";
        }

        var profileRootReason = ValidateDirectory(EdgeProfileRootVariable, "Edge user-data directory");
        if (profileRootReason is not null)
        {
            return profileRootReason;
        }

        if (string.IsNullOrWhiteSpace(LiveExportZipPath))
        {
            var edgeReason = ValidateStorageState(EdgeStorageStateVariable, "Edge");
            if (edgeReason is not null)
            {
                return edgeReason;
            }
        }

        return null;
    }

    internal static string? ControllerSkipReason()
    {
        if (!IsEnabled)
        {
            return $"{EnableVariable}=1 is required to run the live Claude browser controller test.";
        }

        var edgeReason = ValidateStorageState(EdgeStorageStateVariable, "Edge");
        if (edgeReason is not null)
        {
            return edgeReason;
        }

        return ValidateStorageState(FirefoxStorageStateVariable, "Firefox");
    }

    internal static string CopyStorageStateToWorkspace(string sourcePath, string workspaceRoot, string fileName)
    {
        var liveStateRoot = Path.Combine(workspaceRoot, "live_state");
        Directory.CreateDirectory(liveStateRoot);
        var destinationPath = Path.Combine(liveStateRoot, fileName);
        File.Copy(sourcePath, destinationPath, overwrite: true);
        return destinationPath;
    }

    private static string? ValidateStorageState(string envVar, string browserName)
    {
        var path = ResolvePath(envVar);
        if (string.IsNullOrWhiteSpace(path))
        {
            return $"{envVar} must point to a saved Playwright storage_state JSON file for {browserName}.";
        }

        if (!File.Exists(path))
        {
            return $"{envVar} was set but the file does not exist: {path}";
        }

        return null;
    }

    private static string? ValidateDirectory(string envVar, string description)
    {
        var path = ResolvePath(envVar);
        if (string.IsNullOrWhiteSpace(path))
        {
            return $"{envVar} must point to the dedicated {description} used by the live browser round-trip test.";
        }

        if (!Directory.Exists(path))
        {
            return $"{envVar} was set but the directory does not exist: {path}";
        }

        return null;
    }

    private static string? ResolvePath(string variable)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(value);
        }
        catch
        {
            return value;
        }
    }

    private static bool IsTruthy(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Equals("1", StringComparison.OrdinalIgnoreCase)
            || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || value.Equals("on", StringComparison.OrdinalIgnoreCase);
    }
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class LiveClaudeEdgeFactAttribute : FactAttribute
{
    public LiveClaudeEdgeFactAttribute()
    {
        var skipReason = LiveClaudeTestEnvironment.EdgeSkipReason();
        if (!string.IsNullOrWhiteSpace(skipReason))
        {
            Skip = skipReason;
        }
    }
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class LiveClaudeRoundTripFactAttribute : FactAttribute
{
    public LiveClaudeRoundTripFactAttribute()
    {
        var skipReason = LiveClaudeTestEnvironment.RoundTripSkipReason();
        if (!string.IsNullOrWhiteSpace(skipReason))
        {
            Skip = skipReason;
        }
    }
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class LiveClaudeControllerFactAttribute : FactAttribute
{
    public LiveClaudeControllerFactAttribute()
    {
        var skipReason = LiveClaudeTestEnvironment.ControllerSkipReason();
        if (!string.IsNullOrWhiteSpace(skipReason))
        {
            Skip = skipReason;
        }
    }
}
