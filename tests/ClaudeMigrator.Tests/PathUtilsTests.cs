using ClaudeMigrator.Core.Utilities;
using ClaudeMigrator.Tests.TestSupport;

namespace ClaudeMigrator.Tests;

public sealed class PathUtilsTests
{
    [Fact]
    public void SlugifyNormalizesTextAndFallsBackWhenEmpty()
    {
        Assert.Equal("resume-claude-migrator", PathUtils.Slugify("Résumé / Claude Migrator!"));
        Assert.Equal("item", PathUtils.Slugify("   ", fallback: "item"));
        Assert.Equal("file", PathUtils.SafeFilename(null));
    }

    [Fact]
    public void EnsureDirectoryCreatesMissingDirectory()
    {
        using var workspace = new TestWorkspace();
        var path = Path.Combine(workspace.Root, "nested", "folder");

        var directory = PathUtils.EnsureDirectory(path);

        Assert.True(Directory.Exists(path));
        Assert.Equal(Path.GetFullPath(path), directory.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    }

    [Fact]
    public void Sha256FileMatchesKnownDigest()
    {
        using var workspace = new TestWorkspace();
        var file = Path.Combine(workspace.Root, "payload.txt");
        File.WriteAllText(file, "hello", new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var digest = PathUtils.Sha256File(file);

        Assert.Equal("2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824", digest);
    }
}
