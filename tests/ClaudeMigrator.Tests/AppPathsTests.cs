using ClaudeMigrator.Core.Paths;
using ClaudeMigrator.Tests.TestSupport;

namespace ClaudeMigrator.Tests;

public sealed class AppPathsTests
{
    [Fact]
    public void EnsureCreatesRuntimeFolders()
    {
        using var workspace = new TestWorkspace();
        var paths = new AppPaths(workspace.Root);

        paths.Ensure();

        Assert.True(Directory.Exists(paths.RuntimeDir));
        Assert.True(Directory.Exists(paths.LogsDir));
        Assert.True(Directory.Exists(paths.SessionsDir));
        Assert.True(Directory.Exists(paths.ProcessingDir));
        Assert.True(Directory.Exists(paths.ExportsDir));
        Assert.True(Directory.Exists(paths.LocalBundlesDir));
        Assert.True(Directory.Exists(paths.PortableExportsDir));
        Assert.True(Directory.Exists(paths.RestoresDir));
        Assert.True(Directory.Exists(paths.ErrorsDir));
        Assert.True(Directory.Exists(paths.InstallerDir));
    }

    [Fact]
    public void SuggestedZipAndFolderPathsLiveUnderRuntimeDirectories()
    {
        using var workspace = new TestWorkspace();
        var paths = new AppPaths(workspace.Root).Ensure();

        var output = paths.SuggestedOutputZip();
        var localBundle = paths.SuggestedLocalBundleZip();
        var processing = paths.SuggestedProcessingFolder("bundle");

        Assert.StartsWith(paths.PortableExportsDir, output, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(".zip", output, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(paths.LocalBundlesDir, localBundle, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(".zip", localBundle, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(paths.ProcessingDir, processing, StringComparison.OrdinalIgnoreCase);
    }
}
