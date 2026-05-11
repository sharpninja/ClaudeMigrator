namespace ClaudeMigrator.Tests.TestSupport;

internal sealed class TestWorkspace : IDisposable
{
    public TestWorkspace(string? name = null)
    {
        Root = Path.Combine(Path.GetTempPath(), "ClaudeMigrator.Tests", name ?? Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch
        {
        }
    }
}
