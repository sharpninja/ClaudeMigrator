using System.Collections.Concurrent;
using Microsoft.Playwright;

namespace ClaudeMigrator.Core.Browser;

public sealed record BrowserAccountSpec(
    string Key,
    string Email,
    string BrowserName,
    string? Channel,
    string StorageStatePath,
    string StartUrl = "https://claude.ai/");

public sealed record BrowserSessionHandle(
    BrowserAccountSpec Spec,
    IPlaywright Playwright,
    IBrowser Browser,
    IBrowserContext Context,
    IPage Page);

public sealed class BrowserManager
{
    public const string ClaudeDataPrivacyControlsUrl = "https://claude.ai/settings/data-privacy-controls";

    private readonly object _gate = new();
    private readonly SemaphoreSlim _playwrightGate = new(1, 1);
    private readonly ConcurrentDictionary<string, BrowserSessionHandle> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _sessionsDir;
    private readonly string _errorsDir;
    private readonly Action<string> _log;
    private IPlaywright? _playwright;

    public BrowserManager(string runtimeRoot, Action<string>? log = null)
    {
        _sessionsDir = Path.Combine(runtimeRoot, "sessions");
        _errorsDir = Path.Combine(runtimeRoot, "errors");
        _log = log ?? (_ => { });
    }

    public string SessionsDir => _sessionsDir;
    public string ErrorsDir => _errorsDir;

    public IReadOnlyList<BrowserAccountSpec> DefaultAccountSpecs()
    {
        var edgeChannel = OperatingSystem.IsWindows() ? "msedge" : null;
        return new[]
        {
            new BrowserAccountSpec(
                Key: "edge_original",
                Email: "ninja@thesharp.ninja",
                BrowserName: "Edge",
                Channel: edgeChannel,
                StorageStatePath: Path.Combine(_sessionsDir, "edge_original.storage.json")),
            new BrowserAccountSpec(
                Key: "firefox_new",
                Email: "plbyrd@gmail.com",
                BrowserName: "Firefox",
                Channel: null,
                StorageStatePath: Path.Combine(_sessionsDir, "firefox_new.storage.json")),
        };
    }

    public BrowserSessionHandle? GetSession(string key)
        => _sessions.TryGetValue(key, out var session) ? session : null;

    public IPage? GetPage(string key) => GetSession(key)?.Page;

    public async Task<BrowserSessionHandle> OpenSessionAsync(BrowserAccountSpec spec, bool headless = false, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_sessionsDir);
        Directory.CreateDirectory(_errorsDir);

        if (_sessions.TryRemove(spec.Key, out var existing))
        {
            await CloseHandleAsync(existing).ConfigureAwait(false);
        }

        _log($"Launching {spec.BrowserName} for {spec.Email} (headless={headless}, channel={spec.Channel ?? "default"}, storage_state={spec.StorageStatePath}).");
        var playwright = await EnsurePlaywrightAsync().ConfigureAwait(false);

        IBrowser? browser = null;
        IBrowserContext? context = null;
        IPage? page = null;
        try
        {
            browser = await LaunchBrowserAsync(playwright, spec, headless, cancellationToken).ConfigureAwait(false);
            var contextOptions = new BrowserNewContextOptions
            {
                ViewportSize = new ViewportSize { Width = 1440, Height = 1000 },
                IgnoreHTTPSErrors = true,
                AcceptDownloads = true,
            };

            if (File.Exists(spec.StorageStatePath))
            {
                contextOptions.StorageStatePath = spec.StorageStatePath;
                _log($"Loaded session state from {spec.StorageStatePath}.");
            }

            context = await browser.NewContextAsync(contextOptions).ConfigureAwait(false);
            page = await context.NewPageAsync().ConfigureAwait(false);
            await page.GotoAsync(spec.StartUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded }).ConfigureAwait(false);
            _log($"Opened {spec.StartUrl} in {spec.BrowserName} for {spec.Email}.");
            try
            {
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new PageWaitForLoadStateOptions { Timeout = 10000 }).ConfigureAwait(false);
            }
            catch
            {
            }

            var handle = new BrowserSessionHandle(spec, playwright, browser, context, page);
            _sessions[spec.Key] = handle;
            return handle;
        }
        catch
        {
            if (page is not null)
            {
                await SafeCloseAsync(page).ConfigureAwait(false);
            }

            if (context is not null)
            {
                await SafeCloseAsync(context).ConfigureAwait(false);
            }

            if (browser is not null)
            {
                await SafeCloseAsync(browser).ConfigureAwait(false);
            }

            if (_sessions.IsEmpty)
            {
                await StopPlaywrightAsync().ConfigureAwait(false);
            }

            throw;
        }
    }

    public async Task<string?> SaveSessionStateAsync(string key, CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGetValue(key, out var session))
        {
            return null;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(session.Spec.StorageStatePath) ?? _sessionsDir);
        await session.Context.StorageStateAsync(new BrowserContextStorageStateOptions
        {
            Path = session.Spec.StorageStatePath,
        }).ConfigureAwait(false);

        var currentUrl = string.Empty;
        try
        {
            currentUrl = session.Page.Url;
        }
        catch
        {
        }

        if (!string.IsNullOrWhiteSpace(currentUrl))
        {
            _log($"Saved session state to {session.Spec.StorageStatePath} from {currentUrl}.");
        }
        else
        {
            _log($"Saved session state to {session.Spec.StorageStatePath}.");
        }

        return session.Spec.StorageStatePath;
    }

    public async Task<IReadOnlyList<string>> SaveAllSessionStatesAsync(CancellationToken cancellationToken = default)
    {
        var saved = new List<string>();
        foreach (var key in _sessions.Keys.ToList())
        {
            var path = await SaveSessionStateAsync(key, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(path))
            {
                saved.Add(path);
            }
        }

        return saved;
    }

    public async Task CloseSessionAsync(string key)
    {
        if (_sessions.TryRemove(key, out var session))
        {
            await CloseHandleAsync(session).ConfigureAwait(false);
        }

        if (_sessions.IsEmpty)
        {
            await StopPlaywrightAsync().ConfigureAwait(false);
        }
    }

    public async Task CloseAllAsync()
    {
        foreach (var key in _sessions.Keys.ToList())
        {
            await CloseSessionAsync(key).ConfigureAwait(false);
        }

        await StopPlaywrightAsync().ConfigureAwait(false);
    }

    public async Task<bool> ClickCandidatesAsync(IPage page, IEnumerable<string> labels, int timeoutMs = 5000, CancellationToken cancellationToken = default)
    {
        foreach (var label in labels)
        {
            if (await ClickCandidateAsync(page, label, 0, timeoutMs, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }
        }

        return false;
    }

    public async Task<bool> ClickNthCandidateAsync(IPage page, string label, int occurrenceIndex, int timeoutMs = 5000, CancellationToken cancellationToken = default)
    {
        if (occurrenceIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(occurrenceIndex));
        }

        return await ClickCandidateAsync(page, label, occurrenceIndex, timeoutMs, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> FillFirstTextControlAsync(IPage page, string value, CancellationToken cancellationToken = default)
    {
        foreach (var selector in new[] { "textarea", "input[type='text']", "input:not([type])", "[contenteditable='true']" })
        {
            var locator = page.Locator(selector);
            var count = await locator.CountAsync().ConfigureAwait(false);
            for (var index = 0; index < Math.Min(count, 10); index++)
            {
                var candidate = locator.Nth(index);
                try
                {
                    if (!await candidate.IsVisibleAsync().ConfigureAwait(false))
                    {
                        continue;
                    }

                    try
                    {
                        await candidate.FillAsync(value).ConfigureAwait(false);
                    }
                    catch
                    {
                        await candidate.ClickAsync().ConfigureAwait(false);
                        await page.Keyboard.InsertTextAsync(value).ConfigureAwait(false);
                    }

                    return true;
                }
                catch
                {
                }
            }
        }

        return false;
    }

    public async Task<bool> SetFirstFileInputAsync(IPage page, string filePath, CancellationToken cancellationToken = default)
    {
        var locator = page.Locator("input[type='file']");
        var count = await locator.CountAsync().ConfigureAwait(false);
        for (var index = 0; index < Math.Min(count, 10); index++)
        {
            var candidate = locator.Nth(index);
            try
            {
                await candidate.SetInputFilesAsync(filePath).ConfigureAwait(false);
                return true;
            }
            catch
            {
            }
        }

        return false;
    }

    public async Task<string?> CaptureFailureScreenshotAsync(string stepName, Exception exception, string? key = null)
    {
        var sanitized = ClaudeMigrator.Core.Utilities.PathUtils.SafeFilename(stepName, "step", 80);
        var screenshotPath = Path.Combine(_errorsDir, $"{sanitized}_{ClaudeMigrator.Core.Utilities.PathUtils.TimestampTag()}.png");
        try
        {
            var page = key is null ? _sessions.Values.Select(session => session.Page).FirstOrDefault(page => page is not null) : GetPage(key);
            if (page is null)
            {
                _log($"Failed to capture screenshot for {stepName}: no active page. Error: {exception.Message}");
                return null;
            }

            await page.ScreenshotAsync(new PageScreenshotOptions { Path = screenshotPath, FullPage = true }).ConfigureAwait(false);
            _log($"Captured failure screenshot for {stepName} at {screenshotPath}.");
            return screenshotPath;
        }
        catch (Exception screenshotError)
        {
            _log($"Failed to capture screenshot for {stepName}: {screenshotError.Message}");
            return null;
        }
    }

    private async Task<IPlaywright> EnsurePlaywrightAsync()
    {
        if (_playwright is not null)
        {
            return _playwright;
        }

        await _playwrightGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_playwright is null)
            {
                _playwright = await Playwright.CreateAsync().ConfigureAwait(false);
            }

            return _playwright;
        }
        finally
        {
            _playwrightGate.Release();
        }
    }

    private async Task<IBrowser> LaunchBrowserAsync(IPlaywright playwright, BrowserAccountSpec spec, bool headless, CancellationToken cancellationToken)
    {
        if (spec.BrowserName.Equals("Firefox", StringComparison.OrdinalIgnoreCase))
        {
            return await playwright.Firefox.LaunchAsync(new BrowserTypeLaunchOptions { Headless = headless }).ConfigureAwait(false);
        }

        var launchOptions = new BrowserTypeLaunchOptions { Headless = headless };
        if (OperatingSystem.IsWindows() && !string.IsNullOrWhiteSpace(spec.Channel))
        {
            launchOptions.Channel = spec.Channel;
        }

        try
        {
            return await playwright.Chromium.LaunchAsync(launchOptions).ConfigureAwait(false);
        }
        catch when (OperatingSystem.IsWindows() && !string.IsNullOrWhiteSpace(spec.Channel))
        {
            _log($"Failed to launch {spec.Channel}; falling back to Chromium for {spec.BrowserName}.");
            launchOptions.Channel = null;
            return await playwright.Chromium.LaunchAsync(launchOptions).ConfigureAwait(false);
        }
    }

    private async Task<bool> ClickCandidateAsync(IPage page, string label, int occurrenceIndex, int timeoutMs, CancellationToken cancellationToken)
    {
        var selectors = new List<Func<ILocator>>
        {
            () => page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = label }),
            () => page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = label, Exact = false }),
            () => page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = label }),
            () => page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = label, Exact = false }),
            () => page.GetByText(label, new PageGetByTextOptions { Exact = false }),
            () => page.Locator($"button:has-text('{label.Replace("'", "\\'")}')"),
            () => page.Locator($"a:has-text('{label.Replace("'", "\\'")}')"),
        };

        foreach (var factory in selectors)
        {
            try
            {
                var locator = factory();
                if (await locator.CountAsync().ConfigureAwait(false) <= occurrenceIndex)
                {
                    continue;
                }

                var candidate = locator.Nth(occurrenceIndex);
                try
                {
                    await candidate.ClickAsync(new LocatorClickOptions { Timeout = timeoutMs }).ConfigureAwait(false);
                }
                catch
                {
                    await candidate.ClickAsync(new LocatorClickOptions { Timeout = timeoutMs, Force = true }).ConfigureAwait(false);
                }

                return true;
            }
            catch (PlaywrightException)
            {
            }
            catch
            {
            }
        }

        return false;
    }

    private static async Task SafeCloseAsync(IPage page)
    {
        try
        {
            await page.CloseAsync().ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private static async Task SafeCloseAsync(IBrowserContext context)
    {
        try
        {
            await context.CloseAsync().ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private static async Task SafeCloseAsync(IBrowser browser)
    {
        try
        {
            await browser.CloseAsync().ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private async Task CloseHandleAsync(BrowserSessionHandle handle)
    {
        var errors = new List<string>();
        foreach (var (label, action) in new (string label, Func<Task> action)[]
                 {
                     ("page", () => SafeCloseAsync(handle.Page)),
                     ("context", () => SafeCloseAsync(handle.Context)),
                     ("browser", () => SafeCloseAsync(handle.Browser)),
                 })
        {
            try
            {
                await action().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                errors.Add($"{label}: {ex.Message}");
            }
        }

        if (errors.Count > 0)
        {
            _log("Browser close warnings: " + string.Join("; ", errors));
        }
    }

    private async Task StopPlaywrightAsync()
    {
        var playwright = _playwright;
        _playwright = null;
        if (playwright is null)
        {
            return;
        }

        try
        {
            playwright.Dispose();
        }
        catch (Exception ex)
        {
            _log($"Browser runtime shutdown warnings: {ex.Message}");
        }
    }
}
