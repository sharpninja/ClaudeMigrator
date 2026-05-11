using ClaudeMigrator.Core.Browser;
using ClaudeMigrator.Tests.TestSupport;

namespace ClaudeMigrator.Tests;

public sealed class BrowserManagerIntegrationTests
{
    [Fact]
    public async Task BrowserManagerUsesRealPlaywrightBrowsers()
    {
        using var workspace = new TestWorkspace();
        var runtimeRoot = Path.Combine(workspace.Root, "runtime");
        var browserPage = SampleData.CreateSampleBrowserPage(workspace.Root);
        var pageUrl = new Uri(browserPage).AbsoluteUri;
        var logs = new List<string>();
        var manager = new BrowserManager(runtimeRoot, logs.Add);

        var chromiumSpec = new BrowserAccountSpec(
            Key: "chromium",
            Email: "chromium@example.com",
            BrowserName: "Chromium",
            Channel: null,
            StorageStatePath: Path.Combine(runtimeRoot, "sessions", "chromium.storage.json"),
            StartUrl: pageUrl);

        var firefoxSpec = new BrowserAccountSpec(
            Key: "firefox",
            Email: "firefox@example.com",
            BrowserName: "Firefox",
            Channel: null,
            StorageStatePath: Path.Combine(runtimeRoot, "sessions", "firefox.storage.json"),
            StartUrl: pageUrl);

        var chromium = await manager.OpenSessionAsync(chromiumSpec, headless: true);
        var firefox = await manager.OpenSessionAsync(firefoxSpec, headless: true);

        try
        {
            Assert.NotNull(manager.GetSession("chromium"));
            Assert.NotNull(manager.GetSession("firefox"));
            Assert.Contains(logs, line => line.Contains("Launching Chromium", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(logs, line => line.Contains("Launching Firefox", StringComparison.OrdinalIgnoreCase));

            var filled = await manager.FillFirstTextControlAsync(chromium.Page, "Hello from Playwright");
            Assert.True(filled);
            Assert.Equal("Hello from Playwright", await chromium.Page.Locator("#notes").InputValueAsync());

            var clicked = await manager.ClickCandidatesAsync(chromium.Page, new[] { "Export data" });
            Assert.True(clicked);
            Assert.Equal("export-clicked", await chromium.Page.Locator("#status").TextContentAsync());

            var uploadPath = Path.Combine(workspace.Root, "upload.txt");
            File.WriteAllText(uploadPath, "upload me", System.Text.Encoding.UTF8);
            var uploaded = await manager.SetFirstFileInputAsync(chromium.Page, uploadPath);
            Assert.True(uploaded);
            Assert.Equal("files:1", await chromium.Page.Locator("#status").TextContentAsync());

            var saved = await manager.SaveAllSessionStatesAsync();
            Assert.Contains(chromiumSpec.StorageStatePath, saved);
            Assert.Contains(firefoxSpec.StorageStatePath, saved);
            Assert.True(File.Exists(chromiumSpec.StorageStatePath));
            Assert.True(File.Exists(firefoxSpec.StorageStatePath));

            var screenshot = await manager.CaptureFailureScreenshotAsync("browser integration", new InvalidOperationException("boom"), "chromium");
            Assert.NotNull(screenshot);
            Assert.True(File.Exists(screenshot));
        }
        finally
        {
            await manager.CloseAllAsync();
        }
    }
}
