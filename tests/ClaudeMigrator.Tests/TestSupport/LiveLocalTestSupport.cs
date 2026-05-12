using Xunit;

namespace ClaudeMigrator.Tests.TestSupport;

internal static class LiveLocalTestEnvironment
{
    internal const string EnableVariable = "CLAUDEMIGRATOR_RUN_LIVE_LOCAL";
    internal const string SourceHomeVariable = "CLAUDEMIGRATOR_LIVE_LOCAL_SOURCE_HOME";

    internal static bool IsEnabled => IsTruthy(Environment.GetEnvironmentVariable(EnableVariable));

    internal static string SourceHome => ResolveSourceHome();

    internal static string? SkipReason()
    {
        if (!IsEnabled)
        {
            return $"{EnableVariable}=1 is required to run the live local Claude integration test.";
        }

        var sourceHome = SourceHome;
        var claudeRoot = Path.Combine(sourceHome, ".claude");
        var claudeJson = Path.Combine(sourceHome, ".claude.json");

        if (!Directory.Exists(claudeRoot))
        {
            return $"Live local Claude profile not found: {claudeRoot}";
        }

        if (!File.Exists(claudeJson))
        {
            return $"Live local Claude account file not found: {claudeJson}";
        }

        return null;
    }

    private static string ResolveSourceHome()
    {
        var overridePath = Environment.GetEnvironmentVariable(SourceHomeVariable);
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return Path.GetFullPath(overridePath);
        }

        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
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
internal sealed class LiveClaudeLocalFactAttribute : FactAttribute
{
    public LiveClaudeLocalFactAttribute()
    {
        var skipReason = LiveLocalTestEnvironment.SkipReason();
        if (!string.IsNullOrWhiteSpace(skipReason))
        {
            Skip = skipReason;
        }
    }
}
