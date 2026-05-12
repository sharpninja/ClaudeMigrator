using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using ClaudeMigrator.Core.Browser;
using ClaudeMigrator.Core.Exporting;
using ClaudeMigrator.Core.Migration;
using ClaudeMigrator.Core.Models;
using ClaudeMigrator.Core.Paths;
using ClaudeMigrator.Tests.TestSupport;
using ClaudeMigrator.Core.Web;
using Microsoft.Playwright;

namespace ClaudeMigrator.Tests;

public sealed class LiveClaudeMigrationTests
{
    private static readonly string[] ExportButtonLabels =
    [
        "Export data",
        "Download data",
        "Export",
        "Request export",
    ];

    [LiveClaudeEdgeFact]
    public async Task LiveEdgeGeneratesPortableArchiveFromClaudeAsync()
    {
        using var workspace = new TestWorkspace("live-edge-export");
        var appPaths = new AppPaths(workspace.Root).Ensure();
        var logs = new List<string>();
        var rawExportZip = await ResolveLiveExportZipAsync(appPaths, logs.Add);
        Assert.True(File.Exists(rawExportZip));

        var exporter = new UniversalClaudeExporter(appPaths.RuntimeDir, logs.Add);
        var parsed = exporter.InspectArchive(rawExportZip);
        Assert.True(parsed.Conversations.Count > 0 || parsed.Projects.Count > 0 || parsed.MemoryItems.Count > 0);

        var portable = exporter.ExportPortableZip(
            rawExportZip,
            outputZip: appPaths.SuggestedOutputZip("claude_live_portable_export"));

        AssertPortableExportMatchesParsedData(portable, parsed);
        Assert.Contains(logs, line => line.Contains("Portable export", StringComparison.OrdinalIgnoreCase) || line.Contains("Using provided live export archive", StringComparison.OrdinalIgnoreCase));
    }

    [LiveClaudeRoundTripFact]
    public async Task LiveEdgeImportsPortableArchiveFromClaudeAsync()
    {
        using var workspace = new TestWorkspace("live-edge-import");
        var appPaths = new AppPaths(workspace.Root).Ensure();
        var logs = new List<string>();
        var manager = new BrowserManager(appPaths.RuntimeDir, logs.Add);
        var edgeDebuggerUrl = LiveClaudeTestEnvironment.EdgeDebugUrl
            ?? throw new InvalidOperationException($"{LiveClaudeTestEnvironment.EdgeDebugUrlVariable} is required.");
        var edgeProfileRoot = LiveClaudeTestEnvironment.EdgeProfileRootPath
            ?? throw new InvalidOperationException($"{LiveClaudeTestEnvironment.EdgeProfileRootVariable} is required.");
        var edgeProfileDirectory = LiveClaudeTestEnvironment.EdgeProfileDirectory;
        var edgeSession = await AttachToEdgeDebuggerAsync(edgeDebuggerUrl, logs.Add);

        AttachPageDiagnostics(edgeSession.Page, logs.Add);

        try
        {
            var rawExportZip = await ResolveLiveExportZipAsync(appPaths, logs.Add);
            var portable = await BuildLivePortableArchiveAsync(rawExportZip, appPaths);
            Assert.True(File.Exists(portable.ZipPath));
            Assert.True(File.Exists(portable.MemoryPath));

            var importedMemorySnapshot = ReadPortableMemorySnapshot(portable.MemoryPath);
            var importedMemoryTitles = importedMemorySnapshot.Select(item => item.Title).ToArray();
            Assert.NotEmpty(importedMemoryTitles);

            try
            {
                await edgeSession.Page.GotoAsync(BrowserManager.ClaudeDataPrivacyControlsUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
            }
            catch (Exception ex)
            {
                logs.Add($"Navigation to Claude settings failed: {ex.Message}");
            }

            try
            {
                await edgeSession.Page.WaitForLoadStateAsync(LoadState.NetworkIdle, new PageWaitForLoadStateOptions { Timeout = 15000 });
            }
            catch
            {
            }

            await WaitForClaudeDataPrivacyControlsReadyAsync(edgeSession.Page, logs.Add, CancellationToken.None);
            var memoryApiBaseUrl = await ResolveClaudeMemoryApiBaseUrlAsync(edgeSession.Page, logs.Add, CancellationToken.None);
            var baselineMemoryText = await FetchClaudeMemoryTextAsync(edgeSession.Page, memoryApiBaseUrl, CancellationToken.None);

            var opened = await manager.ClickNthCandidateAsync(edgeSession.Page, "Manage", 1, timeoutMs: 15000, cancellationToken: default);
            if (!opened)
            {
                await WaitForClaudeDataPrivacyControlsReadyAsync(edgeSession.Page, logs.Add, CancellationToken.None);
                opened = await manager.ClickCandidatesAsync(edgeSession.Page, new[] { "Manage" }, timeoutMs: 15000, cancellationToken: default);
            }

            if (!opened)
            {
                var buttons = await edgeSession.Page.GetByRole(AriaRole.Button).AllInnerTextsAsync();
                var links = await edgeSession.Page.GetByRole(AriaRole.Link).AllInnerTextsAsync();
                var bodyText = await edgeSession.Page.Locator("body").InnerTextAsync();
                var excerpt = bodyText.Length > 2000 ? bodyText[..2000] : bodyText;
                Assert.True(
                    opened,
                    $"Import controls not found. Buttons: {string.Join(" | ", buttons.Take(30))}. Links: {string.Join(" | ", links.Take(30))}. Body: {excerpt}");
            }

            try
            {
                await edgeSession.Page.WaitForTimeoutAsync(2000);
            }
            catch
            {
            }

            var started = await manager.ClickCandidatesAsync(edgeSession.Page, new[] { "Start import" }, timeoutMs: 15000, cancellationToken: default);
            if (!started)
            {
                var buttons = await edgeSession.Page.GetByRole(AriaRole.Button).AllInnerTextsAsync();
                var links = await edgeSession.Page.GetByRole(AriaRole.Link).AllInnerTextsAsync();
                var bodyText = await edgeSession.Page.Locator("body").InnerTextAsync();
                var excerpt = bodyText.Length > 2000 ? bodyText[..2000] : bodyText;
                Assert.True(
                    started,
                    $"Start import control not found. Buttons: {string.Join(" | ", buttons.Take(30))}. Links: {string.Join(" | ", links.Take(30))}. Body: {excerpt}");
            }

            var completed = await TryCompleteMemoryImportAsync(manager, edgeSession.Page, importedMemorySnapshot, logs.Add, CancellationToken.None);
            if (!completed)
            {
                var buttons = await edgeSession.Page.GetByRole(AriaRole.Button).AllInnerTextsAsync();
                var links = await edgeSession.Page.GetByRole(AriaRole.Link).AllInnerTextsAsync();
                var bodyText = await edgeSession.Page.Locator("body").InnerTextAsync();
                var excerpt = bodyText.Length > 2000 ? bodyText[..2000] : bodyText;
                Assert.True(
                    completed,
                    $"Memory import control not found. Buttons: {string.Join(" | ", buttons.Take(30))}. Links: {string.Join(" | ", links.Take(30))}. Body: {excerpt}");
            }

            await AssertClaudeMemoryImportAcceptedAsync(
                edgeSession.Page,
                memoryApiBaseUrl,
                baselineMemoryText,
                importedMemorySnapshot,
                logs.Add,
                CancellationToken.None);

            edgeSession = await RestartEdgeDebuggerSessionAsync(
                edgeSession,
                edgeDebuggerUrl,
                edgeProfileRoot,
                edgeProfileDirectory,
                logs.Add,
                CancellationToken.None);
            AttachPageDiagnostics(edgeSession.Page, logs.Add);
            await WaitForClaudeDataPrivacyControlsReadyAsync(edgeSession.Page, logs.Add, CancellationToken.None);
            await AssertClaudeMemoryImportAcceptedAsync(
                edgeSession.Page,
                memoryApiBaseUrl,
                baselineMemoryText,
                importedMemorySnapshot,
                logs.Add,
                CancellationToken.None);

            Assert.Contains(logs, line => line.Contains("Attached to Edge debugger", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(logs, line => line.Contains("Using provided live export archive", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            try
            {
                var diagnosticsPath = Path.Combine(Path.GetTempPath(), $"ClaudeMigrator.LiveClaude.{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}.log");
                File.WriteAllLines(diagnosticsPath, logs);
            }
            catch
            {
            }

            try
            {
                await edgeSession.Page.ScreenshotAsync(new PageScreenshotOptions
                {
                    Path = Path.Combine(appPaths.ErrorsDir, $"live-edge-import_{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}.png"),
                    FullPage = true,
                });
            }
            catch
            {
            }

            throw;
        }
        finally
        {
            try
            {
                await CloseEdgeDebuggerSessionAsync(edgeSession);
            }
            catch
            {
            }
        }
    }

    [LiveClaudeRoundTripFact]
    public async Task LiveEdgeRecreatesWebExportIntoClaudeProjectsAndDocsPreciselyAsync()
    {
        using var workspace = new TestWorkspace("live-edge-web-recreation");
        var appPaths = new AppPaths(workspace.Root).Ensure();
        var logs = new List<string>();
        var edgeDebuggerUrl = LiveClaudeTestEnvironment.EdgeDebugUrl
            ?? throw new InvalidOperationException($"{LiveClaudeTestEnvironment.EdgeDebugUrlVariable} is required.");
        var rawExportZip = await ResolveLiveExportZipAsync(appPaths, logs.Add);
        var manifestPath = Path.Combine(appPaths.RuntimeDir, "web_recreation", "live_web_recreation_manifest.json");
        var verificationPath = Path.Combine(appPaths.RuntimeDir, "web_recreation", "live_web_recreation_verification.json");
        var recreator = new ClaudeWebRecreator(logs.Add);

        var recreation = await recreator.RecreateAsync(new ClaudeWebRecreationOptions(
            ExportZipPath: rawExportZip,
            EdgeDebugUrl: edgeDebuggerUrl,
            OutputManifestPath: manifestPath));

        Assert.Equal(0, recreation.FailedOperationCount);
        Assert.True(File.Exists(manifestPath));
        Assert.True(recreation.SourceConversationCount > 0 || recreation.SourceProjectCount > 0);

        var verification = await recreator.VerifyAsync(new ClaudeWebRecreationVerificationOptions(
            ManifestPath: manifestPath,
            EdgeDebugUrl: edgeDebuggerUrl,
            OutputPath: verificationPath));

        Assert.Equal(0, verification.FailedOperationCount);
        Assert.Equal(recreation.SourceConversationCount, verification.ExpectedConversationCount);
        Assert.Equal(verification.ExpectedConversationCount, verification.VerifiedConversationCount);
        Assert.Equal(verification.ExpectedProjectCount, verification.VerifiedProjectCount);
        Assert.Equal(verification.ExpectedDocCount, verification.VerifiedDocCount);
        Assert.True(File.Exists(verificationPath));
    }

    private static async Task WaitForClaudeDataPrivacyControlsReadyAsync(
        IPage page,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(30);
        var warned = false;
        var waitingForLogin = false;

        while (DateTimeOffset.UtcNow < deadline)
        {
            string bodyText;
            string pageUrl;
            try
            {
                bodyText = await page.Locator("body").InnerTextAsync().ConfigureAwait(false);
            }
            catch
            {
                bodyText = string.Empty;
            }

            try
            {
                pageUrl = page.Url;
            }
            catch
            {
                pageUrl = string.Empty;
            }

            if (await HasClaudeDataPrivacyControlsAsync(page).ConfigureAwait(false))
            {
                if (warned)
                {
                    log("Claude data privacy controls ready.");
                }

                return;
            }

            if (LooksLikeCloudflareChallenge(pageUrl, bodyText))
            {
                if (!warned)
                {
                    log("Cloudflare challenge detected in Edge. Waiting for manual clearance.");
                    warned = true;
                }
            }
            else if (!waitingForLogin)
            {
                log("Waiting for Claude data privacy controls to finish loading in the new Edge profile. Sign in there if needed, then leave the page open.");
                waitingForLogin = true;
            }

            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException("Timed out waiting for Claude data privacy controls in Edge.");
    }

    private static void AttachPageDiagnostics(IPage page, Action<string> log)
    {
        page.Console += (_, message) =>
        {
            log($"Edge console[{message.Type}]: {message.Text}");
        };

        page.Request += (_, request) =>
        {
            if (request.Url.Contains("/memory", StringComparison.OrdinalIgnoreCase)
                || request.Url.Contains("/settings", StringComparison.OrdinalIgnoreCase)
                || request.Url.Contains("claude.ai/api", StringComparison.OrdinalIgnoreCase))
            {
                var body = request.PostData ?? string.Empty;
                if (body.Length > 1500)
                {
                    body = body[..1500] + "...";
                }

                log($"Edge request: {request.Method} {request.Url} body={body}");
            }
        };

        page.Response += async (_, response) =>
        {
            if (!response.Url.Contains("claude.ai/api", StringComparison.OrdinalIgnoreCase)
                && !response.Url.Contains("/memory", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            string body = string.Empty;
            try
            {
                body = await response.TextAsync().ConfigureAwait(false);
            }
            catch
            {
            }

            if (body.Length > 1500)
            {
                body = body[..1500] + "...";
            }

            log($"Edge response: {response.Status} {response.Url} body={body}");
        };

        page.PageError += (_, exception) =>
        {
            log($"Edge page error: {exception}");
        };

        page.RequestFailed += (_, request) =>
        {
            log($"Edge request failed: {request}");
        };
    }

    private static async Task<bool> HasClaudeDataPrivacyControlsAsync(IPage page)
    {
        try
        {
            var bodyText = await page.Locator("body").InnerTextAsync().ConfigureAwait(false);
            if (bodyText.Contains("Export data", StringComparison.OrdinalIgnoreCase)
                || bodyText.Contains("Manage", StringComparison.OrdinalIgnoreCase)
                || bodyText.Contains("Start import", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        catch
        {
        }

        return false;
    }

    private static async Task<bool> TryCompleteMemoryImportAsync(
        BrowserManager manager,
        IPage page,
        IReadOnlyList<MemorySnapshot> memorySnapshot,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        try
        {
            await page.WaitForTimeoutAsync(2000).ConfigureAwait(false);
        }
        catch
        {
        }

        var memoryDetails = RenderPortableMemoryText(memorySnapshot);
        ILocator memoryDetailsInput = page.GetByPlaceholder("Paste your memory details here");
        if (await memoryDetailsInput.CountAsync().ConfigureAwait(false) == 0)
        {
            memoryDetailsInput = page.Locator("textarea").First;
        }

        try
        {
            await memoryDetailsInput.FillAsync(memoryDetails, new LocatorFillOptions { Timeout = 15000 }).ConfigureAwait(false);
            log("Loaded memory details into the Claude import textarea.");

            try
            {
                var actualValue = await memoryDetailsInput.InputValueAsync().ConfigureAwait(false);
                log($"Claude import textarea length after fill: {actualValue.Length}.");
            }
            catch (Exception ex)
            {
                log($"Could not read back the Claude memory import textarea value: {ex.Message}");
            }

            try
            {
                var buttonState = await page.EvaluateAsync<string>(
                    """
                    () => Array.from(document.querySelectorAll('button'))
                        .map((element, index) => {
                            const text = (element.innerText || element.textContent || '').trim().replace(/\s+/g, ' ');
                            const aria = (element.getAttribute('aria-label') || '').trim().replace(/\s+/g, ' ');
                            const visible = !!(element.offsetWidth || element.offsetHeight || element.getClientRects().length);
                            return `${index}: text=${text} aria=${aria} disabled=${element.disabled} visible=${visible}`;
                        })
                        .join('\n')
                    """
                ).ConfigureAwait(false);
                log("Claude button states after memory fill:" + Environment.NewLine + buttonState);
            }
            catch (Exception ex)
            {
                log($"Could not enumerate Claude button states: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            log($"Could not fill the Claude memory import textarea: {ex.Message}");
            return false;
        }

        var completed = await manager.ClickCandidatesAsync(page, new[] { "Add to memory" }, timeoutMs: 15000, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (completed)
        {
            log("Clicked Add to memory.");
            return true;
        }

        try
        {
            var bodyText = await page.Locator("body").InnerTextAsync().ConfigureAwait(false);
            var excerpt = bodyText.Length > 2000 ? bodyText[..2000] : bodyText;
            log($"Add to memory control not found after opening the memory import panel. Body: {excerpt}");
        }
        catch
        {
        }

        return false;
    }

    private static IReadOnlyList<string> ReadPortableMemoryTitles(string memoryPath)
        => ReadPortableMemorySnapshot(memoryPath).Select(item => item.Title).ToArray();

    private static IReadOnlyList<MemorySnapshot> ReadPortableMemorySnapshot(string memoryPath)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(memoryPath, Encoding.UTF8));
        if (!document.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<MemorySnapshot>();
        }

        return items.EnumerateArray()
            .Select(item => new MemorySnapshot(
                Title: ReadJsonString(item, "title"),
                Text: ReadJsonString(item, "text"),
                SourceFile: ReadJsonString(item, "source_file")))
            .Where(item => !string.IsNullOrWhiteSpace(item.Title) || !string.IsNullOrWhiteSpace(item.Text) || !string.IsNullOrWhiteSpace(item.SourceFile))
            .OrderBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.SourceFile, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Text, StringComparer.Ordinal)
            .ToArray();
    }

    private static async Task<string> ResolveClaudeMemoryApiBaseUrlAsync(
        IPage page,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(2);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string[] resourceUrls;
            try
            {
                resourceUrls = await page.EvaluateAsync<string[]>("() => performance.getEntriesByType('resource').map(entry => entry.name)").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                log($"Reading Claude resource URLs failed: {ex.Message}");
                resourceUrls = Array.Empty<string>();
            }

            foreach (var resourceUrl in resourceUrls)
            {
                if (TryParseClaudeMemoryApiBaseUrl(resourceUrl, out var memoryApiBaseUrl))
                {
                    log($"Resolved Claude memory API base URL: {memoryApiBaseUrl}");
                    return memoryApiBaseUrl;
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException("Timed out resolving the Claude memory API URL from the page resources.");
    }

    private static bool TryParseClaudeMemoryApiBaseUrl(string resourceUrl, out string memoryApiBaseUrl)
    {
        memoryApiBaseUrl = string.Empty;
        if (!Uri.TryCreate(resourceUrl, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index + 2 < segments.Length; index++)
        {
            if (!segments[index].Equals("organizations", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!segments[index + 2].Equals("memory", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var organizationId = segments[index + 1];
            if (string.IsNullOrWhiteSpace(organizationId))
            {
                continue;
            }

            memoryApiBaseUrl = $"{uri.Scheme}://{uri.Authority}/api/organizations/{organizationId}/memory";
            return true;
        }

        return false;
    }

    private static async Task<string> FetchClaudeMemoryTextAsync(
        IPage page,
        string memoryApiBaseUrl,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var responseText = await page.EvaluateAsync<string>(
            """
            async url => {
                const response = await fetch(url, { credentials: 'include' });
                if (!response.ok) {
                    throw new Error(`Claude memory API request failed: ${response.status} ${response.statusText}`);
                }
                return await response.text();
            }
            """,
            memoryApiBaseUrl).ConfigureAwait(false);

        using var document = JsonDocument.Parse(responseText);
        if (!document.RootElement.TryGetProperty("memory", out var memoryProperty) || memoryProperty.ValueKind != JsonValueKind.String)
        {
            return string.Empty;
        }

        return memoryProperty.GetString() ?? string.Empty;
    }

    private static async Task AssertClaudeMemoryImportAcceptedAsync(
        IPage page,
        string memoryApiBaseUrl,
        string baselineMemoryText,
        IReadOnlyList<MemorySnapshot> expectedSnapshot,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        var expectedMemoryText = RenderPortableMemoryText(expectedSnapshot);
        var evidenceTokens = ExtractEvidenceTokens(expectedMemoryText);
        var normalizedBaseline = NormalizeMemoryText(baselineMemoryText);
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(5);
        var lastActualText = string.Empty;

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var actualText = await FetchClaudeMemoryTextAsync(page, memoryApiBaseUrl, cancellationToken).ConfigureAwait(false);
            lastActualText = actualText;
            var normalizedActual = NormalizeMemoryText(actualText);
            var changed = !string.Equals(normalizedActual, normalizedBaseline, StringComparison.Ordinal);
            var containsEvidence = evidenceTokens.Any(token => normalizedActual.Contains(token, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(normalizedActual) && (changed || containsEvidence))
            {
                log("Claude memory API accepted the import. Claude rewrites imported memory, so exact portable text is verified through the project-doc recreation test instead.");
                return;
            }

            log("Claude memory API has not reflected the lossy memory import yet.");
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        }

        var actualExcerpt = NormalizeMemoryText(lastActualText);
        if (actualExcerpt.Length > 2000)
        {
            actualExcerpt = actualExcerpt[..2000];
        }

        throw new TimeoutException(
            $"Claude memory API did not reflect the memory import at {memoryApiBaseUrl}. Expected one of these evidence tokens or changed memory text: {string.Join(" | ", evidenceTokens.Take(10))}. Actual excerpt: {actualExcerpt}");
    }

    private static async Task AssertClaudeMemoryApiMatchesPortableSnapshotAsync(
        IPage page,
        string memoryApiBaseUrl,
        IReadOnlyList<MemorySnapshot> expectedSnapshot,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        var expectedMemoryText = RenderPortableMemoryText(expectedSnapshot);
        var expectedTitles = expectedSnapshot
            .Select(item => item.Title)
            .Where(title => !string.IsNullOrWhiteSpace(title))
            .ToArray();

        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(5);
        var lastActualText = string.Empty;
        var lastActualTitles = Array.Empty<string>();

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var actualText = await FetchClaudeMemoryTextAsync(page, memoryApiBaseUrl, cancellationToken).ConfigureAwait(false);
                lastActualText = actualText;

                var normalizedActual = NormalizeMemoryText(actualText);
                var normalizedExpected = NormalizeMemoryText(expectedMemoryText);
                lastActualTitles = ExtractMemoryTitlesFromText(normalizedActual);

                if (string.Equals(normalizedActual, normalizedExpected, StringComparison.Ordinal))
                {
                    AssertSameMembers(expectedTitles, lastActualTitles);
                    log($"Claude memory API matched expected portable memory snapshot at {memoryApiBaseUrl}.");
                    return;
                }

                var missingTitles = expectedTitles
                    .Where(title => !lastActualTitles.Contains(title, StringComparer.OrdinalIgnoreCase))
                    .ToArray();
                if (missingTitles.Length > 0)
                {
                    log($"Claude memory API not matched yet at {memoryApiBaseUrl}. Missing titles: {string.Join(" | ", missingTitles.Take(10))}");
                }
                else
                {
                    var excerpt = normalizedActual.Length > 2000 ? normalizedActual[..2000] : normalizedActual;
                    log($"Claude memory API text still differs from the expected portable snapshot at {memoryApiBaseUrl}. Current excerpt: {excerpt}");
                }
            }
            catch (Exception ex)
            {
                log($"Reading Claude memory API failed: {ex.Message}");
            }

            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        }

        var expectedExcerpt = NormalizeMemoryText(expectedMemoryText);
        if (expectedExcerpt.Length > 2000)
        {
            expectedExcerpt = expectedExcerpt[..2000];
        }

        var actualExcerpt = NormalizeMemoryText(lastActualText);
        if (actualExcerpt.Length > 2000)
        {
            actualExcerpt = actualExcerpt[..2000];
        }

        throw new TimeoutException(
            $"Claude memory API did not match the portable memory snapshot at {memoryApiBaseUrl}. Expected titles: {string.Join(" | ", expectedTitles.Take(10))}. Last titles: {string.Join(" | ", lastActualTitles.Take(10))}. Expected excerpt: {expectedExcerpt}. Actual excerpt: {actualExcerpt}");
    }

    private static string RenderPortableMemoryText(IReadOnlyList<MemorySnapshot> snapshots)
    {
        var blocks = new List<string>();
        foreach (var item in snapshots)
        {
            var title = item.Title.Trim();
            var text = item.Text.Trim();
            if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var blockLines = new List<string>();
            if (!string.IsNullOrWhiteSpace(title))
            {
                blockLines.Add($"**{title}**");
            }

            if (!string.IsNullOrWhiteSpace(text))
            {
                if (blockLines.Count > 0)
                {
                    blockLines.Add(string.Empty);
                }

                blockLines.Add(text);
            }

            blocks.Add(string.Join(Environment.NewLine, blockLines).Trim());
        }

        return NormalizeMemoryText(string.Join(Environment.NewLine + Environment.NewLine, blocks));
    }

    private static string[] ExtractMemoryTitlesFromText(string text)
    {
        return text.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("**", StringComparison.Ordinal) && line.EndsWith("**", StringComparison.Ordinal) && line.Length > 4)
            .Select(line => line[2..^2].Trim())
            .Where(title => !string.IsNullOrWhiteSpace(title))
            .ToArray();
    }

    private static string[] ExtractEvidenceTokens(string text)
    {
        var normalized = new string(text
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : ' ')
            .ToArray());
        return normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => token.Length >= 10)
            .Where(token => !token.All(char.IsDigit))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(50)
            .ToArray();
    }

    private static string NormalizeMemoryText(string text)
    {
        return string.Join(
                "\n",
                text.Replace("\r\n", "\n", StringComparison.Ordinal)
                    .Replace("\r", "\n", StringComparison.Ordinal)
                    .Split('\n')
                    .Select(line => line.TrimEnd()))
            .Trim();
    }

    private static async Task VerifyImportedMemoryVisibleAsync(
        IPage page,
        IReadOnlyList<string> expectedTitles,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(3);
        var lastBodyExcerpt = string.Empty;

        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                await page.GotoAsync(BrowserManager.ClaudeDataPrivacyControlsUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                log($"Reloading Claude memory page failed: {ex.Message}");
            }

            try
            {
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new PageWaitForLoadStateOptions { Timeout = 15000 }).ConfigureAwait(false);
            }
            catch
            {
            }

            try
            {
                await page.WaitForTimeoutAsync(2000).ConfigureAwait(false);
            }
            catch
            {
            }

            try
            {
                await page.WaitForTimeoutAsync(1000).ConfigureAwait(false);
            }
            catch
            {
            }

            try
            {
                await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Manage", Exact = false }).Nth(1).ClickAsync(new LocatorClickOptions { Timeout = 15000, Force = true }).ConfigureAwait(false);
            }
            catch
            {
            }

            try
            {
                await page.WaitForTimeoutAsync(1000).ConfigureAwait(false);
            }
            catch
            {
            }

            string bodyText;
            try
            {
                bodyText = await page.Locator("body").InnerTextAsync().ConfigureAwait(false);
            }
            catch
            {
                bodyText = string.Empty;
            }

            lastBodyExcerpt = bodyText.Length > 3000 ? bodyText[..3000] : bodyText;
            var missingTitles = expectedTitles
                .Where(title => !bodyText.Contains(title, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (missingTitles.Length == 0)
            {
                log("Verified imported memory titles on Claude data privacy controls page.");
                return;
            }

            log($"Imported memory titles not visible yet after reload: {string.Join(" | ", missingTitles.Take(10))}");
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException(
            $"Imported memory was not visible after reloading Claude data privacy controls. Looked for: {string.Join(" | ", expectedTitles.Take(10))}. Body: {lastBodyExcerpt}");
    }

    private static bool LooksLikeCloudflareChallenge(string pageUrl, string bodyText)
    {
        if (pageUrl.Contains("/api/challenge_redirect", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return bodyText.Contains("Performing security verification", StringComparison.OrdinalIgnoreCase)
            || bodyText.Contains("Just a moment", StringComparison.OrdinalIgnoreCase)
            || bodyText.Contains("verify you are not a bot", StringComparison.OrdinalIgnoreCase)
            || bodyText.Contains("Cloudflare", StringComparison.OrdinalIgnoreCase);
    }

    [LiveClaudeControllerFact]
    public async Task LiveControllerAutoContinuesSaveSessionsCheckpointAsync()
    {
        using var workspace = new TestWorkspace("live-controller-save-sessions");
        var appPaths = new AppPaths(workspace.Root).Ensure();
        var logs = new List<string>();
        var manager = new BrowserManager(appPaths.RuntimeDir, logs.Add);
        var edgeStorageState = LiveClaudeTestEnvironment.CopyStorageStateToWorkspace(
            LiveClaudeTestEnvironment.EdgeStorageStatePath!,
            appPaths.RuntimeDir,
            "edge.storage.json");
        var firefoxStorageState = LiveClaudeTestEnvironment.CopyStorageStateToWorkspace(
            LiveClaudeTestEnvironment.FirefoxStorageStatePath!,
            appPaths.RuntimeDir,
            "firefox.storage.json");

        var browserName = OperatingSystem.IsWindows() ? "Edge" : "Chromium";
        var channel = OperatingSystem.IsWindows() ? "msedge" : null;
        var edgeSpec = new BrowserAccountSpec(
            Key: "live_edge",
            Email: "live-edge",
            BrowserName: browserName,
            Channel: channel,
            StorageStatePath: edgeStorageState,
            StartUrl: "https://claude.ai/settings");

        var edge = await manager.OpenSessionAsync(edgeSpec, headless: true);

        try
        {
            var rawExportZip = await CaptureLiveClaudeExportAsync(manager, edge.Page, appPaths);
            Assert.True(File.Exists(rawExportZip));

            await ExerciseSaveSessionsCheckpointAsync(
                appPaths,
                rawExportZip,
                edgeStorageState,
                firefoxStorageState);

            Assert.Contains(logs, line => line.Contains("Launching", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            await manager.CaptureFailureScreenshotAsync("live controller save sessions", ex, edgeSpec.Key);
            throw;
        }
        finally
        {
            await manager.CloseAllAsync();
        }
    }

    private static Task<PortableExportResult> BuildLivePortableArchiveAsync(
        string rawExportZip,
        AppPaths appPaths)
    {
        var exporter = new UniversalClaudeExporter(appPaths.RuntimeDir);
        var parsed = exporter.InspectArchive(rawExportZip);
        Assert.True(parsed.Conversations.Count > 0 || parsed.Projects.Count > 0 || parsed.MemoryItems.Count > 0);

        var portable = exporter.ExportPortableZip(
            rawExportZip,
            outputZip: appPaths.SuggestedOutputZip("claude_live_portable_export"),
            logCallback: _ => { });

        AssertPortableExportMatchesParsedData(portable, parsed);
        return Task.FromResult(portable);
    }

    private static async Task<string> ResolveLiveExportZipAsync(
        AppPaths appPaths,
        Action<string> log,
        CancellationToken cancellationToken = default)
    {
        var provided = LiveClaudeTestEnvironment.LiveExportZipPath;
        if (!string.IsNullOrWhiteSpace(provided))
        {
            var resolved = Path.GetFullPath(provided);
            if (!File.Exists(resolved))
            {
                throw new FileNotFoundException($"Live export ZIP not found: {resolved}");
            }

            log($"Using provided live export archive {resolved}.");
            return resolved;
        }

        var workspaceManager = new BrowserManager(appPaths.RuntimeDir, log);
        var edgeStorageState = LiveClaudeTestEnvironment.CopyStorageStateToWorkspace(
            LiveClaudeTestEnvironment.EdgeStorageStatePath!,
            appPaths.RuntimeDir,
            "edge.storage.json");

        var browserName = OperatingSystem.IsWindows() ? "Edge" : "Chromium";
        var channel = OperatingSystem.IsWindows() ? "msedge" : null;
        var edgeSpec = new BrowserAccountSpec(
            Key: "live_edge",
            Email: "live-edge",
            BrowserName: browserName,
            Channel: channel,
            StorageStatePath: edgeStorageState,
            StartUrl: "https://claude.ai/settings");

        var edge = await workspaceManager.OpenSessionAsync(edgeSpec, headless: true, cancellationToken).ConfigureAwait(false);
        try
        {
            return await CaptureLiveClaudeExportAsync(workspaceManager, edge.Page, appPaths, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await workspaceManager.CloseAllAsync().ConfigureAwait(false);
        }
    }

    private static async Task ExerciseSaveSessionsCheckpointAsync(
        AppPaths appPaths,
        string exportZipPath,
        string edgeStorageStateSource,
        string firefoxStorageStateSource,
        CancellationToken cancellationToken = default)
    {
        var controllerEdgeStorageState = LiveClaudeTestEnvironment.CopyStorageStateToWorkspace(
            edgeStorageStateSource,
            appPaths.RuntimeDir,
            "edge_original.storage.json");
        var controllerFirefoxStorageState = LiveClaudeTestEnvironment.CopyStorageStateToWorkspace(
            firefoxStorageStateSource,
            appPaths.RuntimeDir,
            "firefox_new.storage.json");

        using var controller = new MigrationController(appPaths);
        var sawSaveSessionsCheckpoint = false;
        controller.ManualActionRequested = action =>
        {
            if (string.Equals(action.Kind, "save_sessions", StringComparison.OrdinalIgnoreCase))
            {
                sawSaveSessionsCheckpoint = true;
                controller.ContinueCurrentStep();
            }
        };

        await controller.StartFullMigrationAsync(new MigrationOptions
        {
            SourceMode = SourceMode.Zip,
            ExportZipPath = exportZipPath,
            TargetApps = [TargetApp.Codex],
        }, cancellationToken).ConfigureAwait(false);

        Assert.True(sawSaveSessionsCheckpoint);
        Assert.True(File.Exists(controllerEdgeStorageState));
        Assert.True(File.Exists(controllerFirefoxStorageState));
    }

    private static async Task<string> CaptureLiveClaudeExportAsync(
        BrowserManager manager,
        IPage page,
        AppPaths appPaths,
        CancellationToken cancellationToken = default)
    {
            await page.GotoAsync(BrowserManager.ClaudeDataPrivacyControlsUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        try
        {
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new PageWaitForLoadStateOptions { Timeout = 10000 });
        }
        catch
        {
        }

        var snapshot = SnapshotLatestZip(appPaths);
        var rawExportZip = Path.Combine(appPaths.ExportsDir, $"claude_live_export_{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(rawExportZip) ?? appPaths.ExportsDir);

        try
        {
            var download = await page.RunAndWaitForDownloadAsync(async () =>
            {
                var clicked = await manager.ClickCandidatesAsync(page, ExportButtonLabels, timeoutMs: 15000, cancellationToken: cancellationToken);
                if (!clicked)
                {
                    throw new InvalidOperationException("Could not find a Claude export control on the live settings page.");
                }
            }, new PageRunAndWaitForDownloadOptions { Timeout = 300000 });

            await download.SaveAsAsync(rawExportZip);
            return rawExportZip;
        }
        catch (Exception ex) when (ex is TimeoutException or PlaywrightException or InvalidOperationException)
        {
            var fallback = await WaitForNewLatestZipAsync(appPaths, snapshot, TimeSpan.FromMinutes(10), cancellationToken);
            File.Copy(fallback, rawExportZip, overwrite: true);
            return rawExportZip;
        }
    }

    private static ZipSnapshot SnapshotLatestZip(AppPaths appPaths)
    {
        var path = appPaths.FindLatestExportZip();
        if (string.IsNullOrWhiteSpace(path))
        {
            return new ZipSnapshot(null, null);
        }

        try
        {
            return new ZipSnapshot(path, File.GetLastWriteTimeUtc(path));
        }
        catch
        {
            return new ZipSnapshot(path, null);
        }
    }

    private static async Task<string> WaitForNewLatestZipAsync(
        AppPaths appPaths,
        ZipSnapshot before,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var snapshot = SnapshotLatestZip(appPaths);
            if (!string.IsNullOrWhiteSpace(snapshot.Path) && File.Exists(snapshot.Path))
            {
                if (before.Path is null || !string.Equals(snapshot.Path, before.Path, StringComparison.OrdinalIgnoreCase))
                {
                    return snapshot.Path;
                }

                if (before.ModifiedUtc is null)
                {
                    return snapshot.Path;
                }

                try
                {
                    var modified = File.GetLastWriteTimeUtc(snapshot.Path);
                    if (modified > before.ModifiedUtc.Value)
                    {
                        return snapshot.Path;
                    }
                }
                catch
                {
                    return snapshot.Path;
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        }

        throw new TimeoutException(
            $"Timed out waiting for a Claude export ZIP under {string.Join(", ", appPaths.DefaultDownloadCandidates())}.");
    }

    private static void AssertPortableExportMatchesParsedData(PortableExportResult portable, ParsedClaudeExport parsed)
    {
        Assert.Equal(parsed.Conversations.Count, portable.Counts["conversations"]);
        Assert.Equal(parsed.Projects.Count, portable.Counts["projects"]);
        Assert.Equal(parsed.MemoryItems.Count, portable.Counts["memory_items"]);
        Assert.Equal(parsed.CodeFiles.Count, portable.Counts["source_files"]);
        Assert.Equal(parsed.CodeFiles.Count, portable.Counts["extracted_artifacts"]);
        Assert.Equal(parsed.CodeFiles.Count, CountFiles(portable.ArtifactRoot));

        using var manifestDocument = JsonDocument.Parse(File.ReadAllText(portable.ManifestPath, Encoding.UTF8));
        var manifest = manifestDocument.RootElement;
        var counts = manifest.GetProperty("counts");
        Assert.Equal(parsed.Conversations.Count, counts.GetProperty("conversations").GetInt32());
        Assert.Equal(parsed.Projects.Count, counts.GetProperty("projects").GetInt32());
        Assert.Equal(parsed.MemoryItems.Count, counts.GetProperty("memory_items").GetInt32());
        Assert.Equal(parsed.CodeFiles.Count, counts.GetProperty("source_files").GetInt32());
        Assert.Equal(portable.Counts["extracted_artifacts"], counts.GetProperty("extracted_artifacts").GetInt32());

        AssertSameMembers(parsed.Conversations.Select(item => item.Title), ReadObjectFieldArray(manifest.GetProperty("conversation_blueprints"), "title"));
        AssertSameMembers(parsed.Projects.Select(item => item.Name), ReadObjectFieldArray(manifest.GetProperty("project_blueprints"), "name"));

        using var memoryDocument = JsonDocument.Parse(File.ReadAllText(portable.MemoryPath, Encoding.UTF8));
        AssertSameMembers(parsed.MemoryItems.Select(item => item.Title), ReadObjectFieldArray(memoryDocument.RootElement.GetProperty("items"), "title"));

        AssertSameMembers(parsed.SourceMembers, ReadStringArray(manifest.GetProperty("source_members")));
        AssertSameMembers(parsed.CodeFiles, ReadStringArray(manifest.GetProperty("code_files")));

        var seedPrompts = manifest.GetProperty("seed_prompts");
        Assert.Equal(parsed.Projects.Count, seedPrompts.GetArrayLength());
        AssertSameMembers(parsed.Projects.Select(item => item.Name), ReadObjectFieldArray(seedPrompts, "project_name"));
        Assert.All(seedPrompts.EnumerateArray(), item => Assert.False(string.IsNullOrWhiteSpace(item.GetProperty("prompt").GetString())));

        AssertPortableArchiveMatchesBundle(portable.BundleRoot, portable.ZipPath);
    }

    private static void AssertPortableArchiveMatchesBundle(string bundleRoot, string zipPath)
    {
        var expectedFiles = Directory.EnumerateFiles(bundleRoot, "*", SearchOption.AllDirectories)
            .Select(path => NormalizePath(Path.GetRelativePath(bundleRoot, path)))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        using var archive = ZipFile.OpenRead(zipPath);
        var prefix = NormalizePath(Path.GetFileName(bundleRoot)) + "/";
        var actualFiles = archive.Entries
            .Where(entry => !string.IsNullOrEmpty(entry.Name))
            .Select(entry => entry.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? entry.FullName[prefix.Length..]
                : entry.FullName)
            .Select(NormalizePath)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.Equal(expectedFiles, actualFiles);
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement array)
    {
        return array.EnumerateArray()
            .Select(item => item.GetString() ?? string.Empty)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();
    }

    private static IReadOnlyList<string> ReadObjectFieldArray(JsonElement array, string propertyName)
    {
        return array.EnumerateArray()
            .Select(item => item.TryGetProperty(propertyName, out var property) ? property.GetString() ?? string.Empty : string.Empty)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();
    }

    private static void AssertSameMembers(IEnumerable<string> expected, IEnumerable<string> actual)
    {
        var normalizedExpected = expected.Select(NormalizePath).OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
        var normalizedActual = actual.Select(NormalizePath).OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
        Assert.Equal(normalizedExpected, normalizedActual);
    }

    private static string NormalizePath(string value)
    {
        return value.Replace('\\', '/');
    }

    private static int CountFiles(string root)
    {
        return Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Count()
            : 0;
    }

    private static void AssertPortableCountsMatch(
        IReadOnlyDictionary<string, int> expected,
        IReadOnlyDictionary<string, int> actual)
    {
        Assert.Equal(expected.Keys.OrderBy(key => key, StringComparer.OrdinalIgnoreCase), actual.Keys.OrderBy(key => key, StringComparer.OrdinalIgnoreCase));
        foreach (var key in expected.Keys)
        {
            Assert.True(actual.ContainsKey(key), $"Missing count key: {key}");
            Assert.Equal(expected[key], actual[key]);
        }
    }

    private static void AssertPortableMemorySnapshotsMatch(string expectedMemoryPath, string actualMemoryPath)
    {
        var expected = ReadPortableMemorySnapshot(expectedMemoryPath);
        var actual = ReadPortableMemorySnapshot(actualMemoryPath);
        Assert.Equal(expected, actual);
    }

    private static string ReadJsonString(JsonElement item, string propertyName)
        => item.TryGetProperty(propertyName, out var property) ? property.GetString() ?? string.Empty : string.Empty;

    private sealed record ZipSnapshot(string? Path, DateTimeOffset? ModifiedUtc);

    private sealed record MemorySnapshot(string Title, string Text, string SourceFile);

    private sealed record EdgeDebuggerSession(IPlaywright Playwright, IBrowser Browser, IPage Page);

    private static async Task<EdgeDebuggerSession> AttachToEdgeDebuggerAsync(
        string debuggerUrl,
        Action<string> log)
    {
        var playwright = await Playwright.CreateAsync().ConfigureAwait(false);
        try
        {
            var browser = await playwright.Chromium.ConnectOverCDPAsync(debuggerUrl).ConfigureAwait(false);
            var context = browser.Contexts.FirstOrDefault() ?? throw new InvalidOperationException($"No browser context available from {debuggerUrl}.");
            var page = context.Pages.FirstOrDefault() ?? await context.NewPageAsync().ConfigureAwait(false);
            log($"Attached to Edge debugger at {debuggerUrl}.");
            return new EdgeDebuggerSession(playwright, browser, page);
        }
        catch
        {
            playwright.Dispose();
            throw;
        }
    }

    private static async Task<EdgeDebuggerSession> RestartEdgeDebuggerSessionAsync(
        EdgeDebuggerSession currentSession,
        string debuggerUrl,
        string profileRoot,
        string profileDirectory,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        log("Restarting Edge for fresh-session verification.");
        await CloseEdgeDebuggerSessionAsync(currentSession).ConfigureAwait(false);
        var endpointClosed = await TryWaitForDebuggerEndpointToCloseAsync(debuggerUrl, TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        if (!endpointClosed)
        {
            log("Edge debugger endpoint stayed open after graceful close; force-closing the dedicated test profile.");
            await ForceCloseEdgeProfileProcessesAsync(profileRoot, log, cancellationToken).ConfigureAwait(false);
            await WaitForDebuggerEndpointToCloseAsync(debuggerUrl, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        }

        LaunchEdgeProfile(debuggerUrl, profileRoot, profileDirectory, log);
        await WaitForDebuggerEndpointAsync(debuggerUrl, cancellationToken).ConfigureAwait(false);
        return await AttachToEdgeDebuggerAsync(debuggerUrl, log).ConfigureAwait(false);
    }

    private static void LaunchEdgeProfile(
        string debuggerUrl,
        string profileRoot,
        string profileDirectory,
        Action<string> log)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The live Edge restart verification requires Windows.");
        }

        var edgeExecutable = ResolveEdgeExecutablePath();
        var startUrl = BrowserManager.ClaudeDataPrivacyControlsUrl;
        var debugUri = new Uri(debuggerUrl, UriKind.Absolute);
        var psi = new ProcessStartInfo
        {
            FileName = edgeExecutable,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add($"--user-data-dir={profileRoot}");
        psi.ArgumentList.Add($"--profile-directory={profileDirectory}");
        psi.ArgumentList.Add($"--remote-debugging-port={debugUri.Port}");
        psi.ArgumentList.Add("--no-first-run");
        psi.ArgumentList.Add("--new-window");
        psi.ArgumentList.Add(startUrl);

        var process = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start Edge using {edgeExecutable}.");
        log($"Started Edge PID {process.Id} for profile root {profileRoot}.");
    }

    private static async Task WaitForDebuggerEndpointAsync(string debuggerUrl, CancellationToken cancellationToken)
    {
        var versionUrl = new Uri(new Uri(debuggerUrl, UriKind.Absolute), "/json/version");
        using var http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(2),
        };

        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(60);
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                using var response = await http.GetAsync(versionUrl, cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch
            {
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException($"Timed out waiting for the Edge debugger endpoint at {debuggerUrl}.");
    }

    private static async Task<bool> TryWaitForDebuggerEndpointToCloseAsync(string debuggerUrl, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            await WaitForDebuggerEndpointToCloseAsync(debuggerUrl, timeout, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private static async Task WaitForDebuggerEndpointToCloseAsync(string debuggerUrl, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var versionUrl = new Uri(new Uri(debuggerUrl, UriKind.Absolute), "/json/version");
        using var http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(2),
        };

        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                using var response = await http.GetAsync(versionUrl, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch
            {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException($"Timed out waiting for the Edge debugger endpoint at {debuggerUrl} to close.");
    }

    private static async Task ForceCloseEdgeProfileProcessesAsync(string profileRoot, Action<string> log, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The live Edge restart verification requires Windows.");
        }

        var escapedProfileRoot = profileRoot.Replace("'", "''", StringComparison.Ordinal);
        var script = $$"""
$profileRoot = '{{escapedProfileRoot}}'
Get-CimInstance Win32_Process |
    Where-Object { $_.Name -eq 'msedge.exe' -and $_.CommandLine -match [regex]::Escape($profileRoot) } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
""";

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-NoLogo");
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(script);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start PowerShell to close the Edge test profile.");
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        var stderr = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        log($"Force-close PowerShell exited with code {process.ExitCode}.");
        if (!string.IsNullOrWhiteSpace(stdout))
        {
            log($"Force-close stdout: {stdout.Trim()}");
        }

        if (!string.IsNullOrWhiteSpace(stderr))
        {
            log($"Force-close stderr: {stderr.Trim()}");
        }
    }

    private static string ResolveEdgeExecutablePath()
    {
        var explicitPath = Environment.GetEnvironmentVariable("CLAUDEMIGRATOR_LIVE_EDGE_EXE_PATH");
        if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath))
        {
            return Path.GetFullPath(explicitPath);
        }

        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "EdgeCore", "Optimized", "msedge.exe"),
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        throw new FileNotFoundException("Microsoft Edge executable not found. Set CLAUDEMIGRATOR_LIVE_EDGE_EXE_PATH if Edge is installed in a non-standard location.");
    }

    private static async Task CloseEdgeDebuggerSessionAsync(EdgeDebuggerSession session)
    {
        try
        {
            await session.Browser.CloseAsync().ConfigureAwait(false);
        }
        catch
        {
        }

        try
        {
            session.Playwright.Dispose();
        }
        catch
        {
        }
    }

}
