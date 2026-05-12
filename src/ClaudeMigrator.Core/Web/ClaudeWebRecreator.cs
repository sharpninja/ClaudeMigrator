using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClaudeMigrator.Core.Utilities;
using Microsoft.Playwright;

namespace ClaudeMigrator.Core.Web;

public sealed record ClaudeWebRecreationOptions(
    string ExportZipPath,
    string EdgeDebugUrl,
    string OutputManifestPath,
    bool DryRun = false,
    string? TranscriptProjectName = null,
    string? Model = null);

public sealed record ClaudeWebRecreationVerificationOptions(
    string ManifestPath,
    string EdgeDebugUrl,
    string? OutputPath = null);

public sealed record ClaudeWebExport(
    string SourceArchive,
    string SourceSha256,
    string SourceAccountUuid,
    string SourceAccountEmail,
    string SourceAccountName,
    IReadOnlyList<ClaudeWebConversation> Conversations,
    IReadOnlyList<ClaudeWebProject> Projects,
    string ConversationsMemory,
    IReadOnlyDictionary<string, string> ProjectMemories);

public sealed record ClaudeWebConversation(
    string Uuid,
    string Name,
    string Summary,
    string CreatedAt,
    string UpdatedAt,
    IReadOnlyList<ClaudeWebMessage> Messages,
    string RawJson);

public sealed record ClaudeWebMessage(
    string Uuid,
    string Sender,
    string Text,
    string CreatedAt,
    string UpdatedAt,
    string ParentMessageUuid,
    string RawJson);

public sealed record ClaudeWebProject(
    string Uuid,
    string Name,
    string Description,
    bool IsPrivate,
    bool IsStarterProject,
    string PromptTemplate,
    string CreatedAt,
    string UpdatedAt,
    IReadOnlyList<ClaudeWebProjectDoc> Docs,
    string RawJson);

public sealed record ClaudeWebProjectDoc(
    string Uuid,
    string FileName,
    string Content,
    string RawJson);

public sealed record ClaudeWebRecreationResult(
    string ManifestPath,
    string TargetOrganizationUuid,
    string TargetOrganizationName,
    int SourceConversationCount,
    int SourceConversationMessageCount,
    int SourceProjectCount,
    int CreatedConversationCount,
    int ExistingConversationCount,
    int CreatedProjectCount,
    int ExistingProjectCount,
    int CreatedDocCount,
    int ExistingDocCount,
    int FailedOperationCount);

public sealed record ClaudeWebRecreationVerificationResult(
    string VerificationPath,
    string ManifestPath,
    string TargetOrganizationUuid,
    int ExpectedConversationCount,
    int VerifiedConversationCount,
    int ExpectedProjectCount,
    int VerifiedProjectCount,
    int ExpectedDocCount,
    int VerifiedDocCount,
    int FailedOperationCount);

public sealed class ClaudeWebExportReader
{
    public ClaudeWebExport Read(string archivePath)
    {
        archivePath = Path.GetFullPath(archivePath);
        if (!File.Exists(archivePath))
        {
            throw new FileNotFoundException($"Claude export archive does not exist: {archivePath}");
        }

        using var archive = ZipFile.OpenRead(archivePath);
        var conversations = ReadConversations(archive.GetEntry("conversations.json"));
        var projects = archive.Entries
            .Where(entry => entry.FullName.StartsWith("projects/", StringComparison.OrdinalIgnoreCase)
                && entry.FullName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .OrderBy(entry => entry.FullName, StringComparer.OrdinalIgnoreCase)
            .Select(ReadProject)
            .Where(project => project is not null)
            .Cast<ClaudeWebProject>()
            .ToArray();

        var (accountUuid, accountEmail, accountName) = ReadFirstUser(archive.GetEntry("users.json"));
        var (conversationsMemory, projectMemories, memoryAccountUuid) = ReadMemories(archive.GetEntry("memories.json"));
        if (string.IsNullOrWhiteSpace(accountUuid))
        {
            accountUuid = memoryAccountUuid;
        }

        return new ClaudeWebExport(
            SourceArchive: archivePath,
            SourceSha256: PathUtils.Sha256File(archivePath),
            SourceAccountUuid: accountUuid,
            SourceAccountEmail: accountEmail,
            SourceAccountName: accountName,
            Conversations: conversations,
            Projects: projects,
            ConversationsMemory: conversationsMemory,
            ProjectMemories: projectMemories);
    }

    private static IReadOnlyList<ClaudeWebConversation> ReadConversations(ZipArchiveEntry? entry)
    {
        if (entry is null)
        {
            return Array.Empty<ClaudeWebConversation>();
        }

        using var document = JsonDocument.Parse(ReadEntryText(entry));
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<ClaudeWebConversation>();
        }

        return document.RootElement.EnumerateArray()
            .Select(ReadConversation)
            .ToArray();
    }

    private static ClaudeWebConversation ReadConversation(JsonElement element)
    {
        var messages = element.TryGetProperty("chat_messages", out var messagesElement) && messagesElement.ValueKind == JsonValueKind.Array
            ? messagesElement.EnumerateArray().Select(ReadMessage).ToArray()
            : Array.Empty<ClaudeWebMessage>();

        return new ClaudeWebConversation(
            Uuid: ReadString(element, "uuid"),
            Name: ReadString(element, "name"),
            Summary: ReadString(element, "summary"),
            CreatedAt: ReadString(element, "created_at"),
            UpdatedAt: ReadString(element, "updated_at"),
            Messages: messages,
            RawJson: element.GetRawText());
    }

    private static ClaudeWebMessage ReadMessage(JsonElement element)
        => new(
            Uuid: ReadString(element, "uuid"),
            Sender: ReadString(element, "sender"),
            Text: ReadString(element, "text"),
            CreatedAt: ReadString(element, "created_at"),
            UpdatedAt: ReadString(element, "updated_at"),
            ParentMessageUuid: ReadString(element, "parent_message_uuid"),
            RawJson: element.GetRawText());

    private static ClaudeWebProject? ReadProject(ZipArchiveEntry entry)
    {
        using var document = JsonDocument.Parse(ReadEntryText(entry));
        var element = document.RootElement;
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var docs = element.TryGetProperty("docs", out var docsElement) && docsElement.ValueKind == JsonValueKind.Array
            ? docsElement.EnumerateArray().Select(ReadProjectDoc).ToArray()
            : Array.Empty<ClaudeWebProjectDoc>();

        return new ClaudeWebProject(
            Uuid: ReadString(element, "uuid"),
            Name: ReadString(element, "name"),
            Description: ReadString(element, "description"),
            IsPrivate: ReadBool(element, "is_private"),
            IsStarterProject: ReadBool(element, "is_starter_project"),
            PromptTemplate: ReadString(element, "prompt_template"),
            CreatedAt: ReadString(element, "created_at"),
            UpdatedAt: ReadString(element, "updated_at"),
            Docs: docs,
            RawJson: element.GetRawText());
    }

    private static ClaudeWebProjectDoc ReadProjectDoc(JsonElement element)
        => new(
            Uuid: ReadString(element, "uuid"),
            FileName: ReadString(element, "file_name"),
            Content: ReadString(element, "content"),
            RawJson: element.GetRawText());

    private static (string AccountUuid, string AccountEmail, string AccountName) ReadFirstUser(ZipArchiveEntry? entry)
    {
        if (entry is null)
        {
            return (string.Empty, string.Empty, string.Empty);
        }

        using var document = JsonDocument.Parse(ReadEntryText(entry));
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return (string.Empty, string.Empty, string.Empty);
        }

        var user = document.RootElement.EnumerateArray().FirstOrDefault();
        if (user.ValueKind != JsonValueKind.Object)
        {
            return (string.Empty, string.Empty, string.Empty);
        }

        return (
            ReadString(user, "uuid"),
            ReadString(user, "email_address"),
            ReadString(user, "full_name"));
    }

    private static (string ConversationsMemory, IReadOnlyDictionary<string, string> ProjectMemories, string AccountUuid) ReadMemories(ZipArchiveEntry? entry)
    {
        if (entry is null)
        {
            return (string.Empty, new Dictionary<string, string>(), string.Empty);
        }

        using var document = JsonDocument.Parse(ReadEntryText(entry));
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Array)
        {
            root = root.EnumerateArray().FirstOrDefault();
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return (string.Empty, new Dictionary<string, string>(), string.Empty);
        }

        var projectMemories = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("project_memories", out var memories) && memories.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in memories.EnumerateObject())
            {
                projectMemories[property.Name] = property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString() ?? string.Empty
                    : property.Value.GetRawText();
            }
        }

        return (
            ReadString(root, "conversations_memory"),
            projectMemories,
            ReadString(root, "account_uuid"));
    }

    private static string ReadEntryText(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static string ReadString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return string.Empty;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Null => string.Empty,
            _ => value.ToString() ?? string.Empty,
        };
    }

    private static bool ReadBool(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.True;
}

public static class ClaudeWebTranscriptRenderer
{
    public static string RenderConversation(ClaudeWebConversation conversation)
    {
        var lines = new List<string>
        {
            $"# {RenderTitle(conversation)}",
            string.Empty,
            $"Source conversation UUID: {conversation.Uuid}",
            $"Source created_at: {conversation.CreatedAt}",
            $"Source updated_at: {conversation.UpdatedAt}",
            $"Source message count: {conversation.Messages.Count}",
            string.Empty,
        };

        if (!string.IsNullOrWhiteSpace(conversation.Summary))
        {
            lines.Add("## Source Summary");
            lines.Add(conversation.Summary.Trim());
            lines.Add(string.Empty);
        }

        lines.Add("## Transcript");
        lines.Add(string.Empty);

        for (var index = 0; index < conversation.Messages.Count; index++)
        {
            var message = conversation.Messages[index];
            lines.Add($"### {index + 1}. {NormalizeRole(message.Sender)}");
            lines.Add($"Message UUID: {message.Uuid}");
            lines.Add($"Parent message UUID: {message.ParentMessageUuid}");
            lines.Add($"Created: {message.CreatedAt}");
            lines.Add($"Updated: {message.UpdatedAt}");
            lines.Add(string.Empty);
            lines.Add(message.Text.Trim().Length == 0 ? "_Empty message text._" : message.Text.Trim());
            lines.Add(string.Empty);
            lines.Add("<details>");
            lines.Add("<summary>Raw source message JSON</summary>");
            lines.Add(string.Empty);
            lines.Add("```json");
            lines.Add(PrettyJson(message.RawJson));
            lines.Add("```");
            lines.Add(string.Empty);
            lines.Add("</details>");
            lines.Add(string.Empty);
        }

        lines.Add("## Raw Source Conversation JSON");
        lines.Add(string.Empty);
        lines.Add("```json");
        lines.Add(PrettyJson(conversation.RawJson));
        lines.Add("```");
        lines.Add(string.Empty);
        return string.Join(Environment.NewLine, lines);
    }

    public static string RenderSourceMemory(ClaudeWebExport export)
    {
        var lines = new List<string>
        {
            "# Source Claude Memory",
            string.Empty,
            $"Source account UUID: {export.SourceAccountUuid}",
            $"Source account email: {export.SourceAccountEmail}",
            $"Source account name: {export.SourceAccountName}",
            string.Empty,
            "## Conversations Memory",
            string.Empty,
            string.IsNullOrWhiteSpace(export.ConversationsMemory) ? "_No conversations memory in export._" : export.ConversationsMemory.Trim(),
            string.Empty,
        };

        if (export.ProjectMemories.Count > 0)
        {
            lines.Add("## Project Memories");
            lines.Add(string.Empty);
            foreach (var item in export.ProjectMemories.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
            {
                lines.Add($"### Project {item.Key}");
                lines.Add(item.Value.Trim());
                lines.Add(string.Empty);
            }
        }

        return string.Join(Environment.NewLine, lines);
    }

    public static string RenderProjectMemory(ClaudeWebProject project, string memory)
        => string.Join(Environment.NewLine, new[]
        {
            $"# Source Project Memory: {project.Name}",
            string.Empty,
            $"Source project UUID: {project.Uuid}",
            string.Empty,
            memory.Trim(),
            string.Empty,
        });

    private static string RenderTitle(ClaudeWebConversation conversation)
        => string.IsNullOrWhiteSpace(conversation.Name)
            ? $"Untitled Conversation {conversation.Uuid}"
            : conversation.Name.Trim();

    private static string NormalizeRole(string sender)
        => string.IsNullOrWhiteSpace(sender) ? "unknown" : sender.Trim();

    private static string PrettyJson(string rawJson)
    {
        try
        {
            using var document = JsonDocument.Parse(rawJson);
            return JsonSerializer.Serialize(document.RootElement, JsonUtils.SnakeCaseIndented);
        }
        catch
        {
            return rawJson;
        }
    }
}

public sealed class ClaudeWebRecreator
{
    private const int DocChunkCharLimit = 350_000;
    private readonly Action<string> _log;
    private readonly ClaudeWebExportReader _reader = new();

    public ClaudeWebRecreator(Action<string>? log = null)
    {
        _log = log ?? (_ => { });
    }

    public async Task<ClaudeWebRecreationResult> RecreateAsync(
        ClaudeWebRecreationOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var source = _reader.Read(options.ExportZipPath);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.OutputManifestPath)) ?? ".");

        using var playwright = await Playwright.CreateAsync().ConfigureAwait(false);
        var browser = await playwright.Chromium.ConnectOverCDPAsync(options.EdgeDebugUrl).ConfigureAwait(false);
        var context = browser.Contexts.FirstOrDefault()
            ?? throw new InvalidOperationException($"No browser context is available from {options.EdgeDebugUrl}.");
        var page = context.Pages.FirstOrDefault() ?? await context.NewPageAsync().ConfigureAwait(false);

        var targetOrg = await ResolveTargetOrganizationAsync(page).ConfigureAwait(false);
        _log($"Target Claude organization: {targetOrg.Name} ({targetOrg.Uuid})");

        var projectResults = new List<Dictionary<string, object?>>();
        var conversationResults = new List<Dictionary<string, object?>>();
        var docResults = new List<Dictionary<string, object?>>();
        var failures = 0;

        var targetProjects = await FetchProjectsAsync(page, targetOrg.Uuid).ConfigureAwait(false);
        var targetConversations = await FetchConversationsAsync(page, targetOrg.Uuid).ConfigureAwait(false);
        var sourceProjectMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var createdProjectCount = 0;
        var existingProjectCount = 0;

        foreach (var project in source.Projects)
        {
            var projectResult = await EnsureProjectAsync(
                page,
                targetOrg.Uuid,
                project,
                targetProjects,
                options.DryRun).ConfigureAwait(false);
            projectResults.Add(projectResult.Manifest);
            if (!string.IsNullOrWhiteSpace(projectResult.TargetUuid))
            {
                sourceProjectMap[project.Uuid] = projectResult.TargetUuid;
            }

            if (projectResult.Failed)
            {
                failures++;
            }
            else if (projectResult.Created)
            {
                createdProjectCount++;
                targetProjects = await FetchProjectsAsync(page, targetOrg.Uuid).ConfigureAwait(false);
            }
            else
            {
                existingProjectCount++;
            }
        }

        var transcriptProjectName = options.TranscriptProjectName
            ?? $"Migrated Claude Web Export - {FallbackAccountLabel(source)}";
        var transcriptProject = new ClaudeWebProject(
            Uuid: "transcript-project",
            Name: transcriptProjectName,
            Description: $"Exact Claude export transcripts from {FallbackAccountLabel(source)}.",
            IsPrivate: true,
            IsStarterProject: false,
            PromptTemplate: string.Empty,
            CreatedAt: string.Empty,
            UpdatedAt: string.Empty,
            Docs: [],
            RawJson: "{}");
        var transcriptProjectResult = await EnsureProjectAsync(
            page,
            targetOrg.Uuid,
            transcriptProject,
            targetProjects,
            options.DryRun).ConfigureAwait(false);
        projectResults.Add(transcriptProjectResult.Manifest);
        if (transcriptProjectResult.Failed)
        {
            failures++;
        }
        else if (transcriptProjectResult.Created)
        {
            createdProjectCount++;
        }
        else
        {
            existingProjectCount++;
        }

        if (!options.DryRun)
        {
            targetProjects = await FetchProjectsAsync(page, targetOrg.Uuid).ConfigureAwait(false);
        }

        var model = string.IsNullOrWhiteSpace(options.Model) ? "claude-sonnet-4-6" : options.Model!;
        var createdConversationCount = 0;
        var existingConversationCount = 0;
        foreach (var conversation in source.Conversations)
        {
            var conversationResult = await EnsureConversationAsync(
                page,
                targetOrg.Uuid,
                conversation,
                targetConversations,
                model,
                options.DryRun).ConfigureAwait(false);
            conversationResults.Add(conversationResult.Manifest);
            if (conversationResult.Failed)
            {
                failures++;
            }
            else if (conversationResult.Created)
            {
                createdConversationCount++;
                targetConversations = await FetchConversationsAsync(page, targetOrg.Uuid).ConfigureAwait(false);
            }
            else
            {
                existingConversationCount++;
            }
        }

        var createdDocCount = 0;
        var existingDocCount = 0;
        if (!string.IsNullOrWhiteSpace(transcriptProjectResult.TargetUuid))
        {
            var memoryDocResult = await EnsureDocAsync(
                page,
                targetOrg.Uuid,
                transcriptProjectResult.TargetUuid,
                "source-memory.md",
                ClaudeWebTranscriptRenderer.RenderSourceMemory(source),
                options.DryRun).ConfigureAwait(false);
            docResults.AddRange(memoryDocResult.ManifestEntries);
            CountDocResult(memoryDocResult, ref createdDocCount, ref existingDocCount, ref failures);

            foreach (var conversation in source.Conversations)
            {
                var title = string.IsNullOrWhiteSpace(conversation.Name) ? "untitled" : conversation.Name;
                var fileName = $"chat-{conversation.Uuid}-{PathUtils.SafeFilename(title, "conversation", 80)}.md";
                var docResult = await EnsureDocAsync(
                    page,
                    targetOrg.Uuid,
                    transcriptProjectResult.TargetUuid,
                    fileName,
                    ClaudeWebTranscriptRenderer.RenderConversation(conversation),
                    options.DryRun).ConfigureAwait(false);
                docResults.AddRange(docResult.ManifestEntries);
                CountDocResult(docResult, ref createdDocCount, ref existingDocCount, ref failures);
            }
        }

        foreach (var project in source.Projects)
        {
            if (!sourceProjectMap.TryGetValue(project.Uuid, out var targetProjectUuid))
            {
                continue;
            }

            foreach (var doc in project.Docs)
            {
                var fileName = string.IsNullOrWhiteSpace(doc.FileName)
                    ? $"source-doc-{doc.Uuid}.md"
                    : doc.FileName;
                var docResult = await EnsureDocAsync(
                    page,
                    targetOrg.Uuid,
                    targetProjectUuid,
                    fileName,
                    doc.Content,
                    options.DryRun).ConfigureAwait(false);
                docResults.AddRange(docResult.ManifestEntries);
                CountDocResult(docResult, ref createdDocCount, ref existingDocCount, ref failures);
            }

            if (source.ProjectMemories.TryGetValue(project.Uuid, out var projectMemory) && !string.IsNullOrWhiteSpace(projectMemory))
            {
                var memoryResult = await EnsureDocAsync(
                    page,
                    targetOrg.Uuid,
                    targetProjectUuid,
                    $"source-project-memory-{project.Uuid}.md",
                    ClaudeWebTranscriptRenderer.RenderProjectMemory(project, projectMemory),
                    options.DryRun).ConfigureAwait(false);
                docResults.AddRange(memoryResult.ManifestEntries);
                CountDocResult(memoryResult, ref createdDocCount, ref existingDocCount, ref failures);
            }
        }

        var manifest = new Dictionary<string, object?>
        {
            ["format"] = "claude_web_recreation_manifest",
            ["version"] = 1,
            ["created_at"] = DateTimeOffset.Now.ToString("O"),
            ["dry_run"] = options.DryRun,
            ["source"] = new Dictionary<string, object?>
            {
                ["archive"] = source.SourceArchive,
                ["sha256"] = source.SourceSha256,
                ["account_uuid"] = source.SourceAccountUuid,
                ["account_email"] = source.SourceAccountEmail,
                ["account_name"] = source.SourceAccountName,
                ["conversation_count"] = source.Conversations.Count,
                ["conversation_message_count"] = source.Conversations.Sum(item => item.Messages.Count),
                ["project_count"] = source.Projects.Count,
                ["project_doc_count"] = source.Projects.Sum(item => item.Docs.Count),
                ["project_memory_count"] = source.ProjectMemories.Count,
                ["has_conversations_memory"] = !string.IsNullOrWhiteSpace(source.ConversationsMemory),
            },
            ["target"] = new Dictionary<string, object?>
            {
                ["organization_uuid"] = targetOrg.Uuid,
                ["organization_name"] = targetOrg.Name,
                ["edge_debug_url"] = options.EdgeDebugUrl,
            },
            ["counts"] = new Dictionary<string, object?>
            {
                ["created_conversations"] = createdConversationCount,
                ["existing_conversations"] = existingConversationCount,
                ["created_projects"] = createdProjectCount,
                ["existing_projects"] = existingProjectCount,
                ["created_docs"] = createdDocCount,
                ["existing_docs"] = existingDocCount,
                ["failed_operations"] = failures,
            },
            ["projects"] = projectResults,
            ["conversations"] = conversationResults,
            ["docs"] = docResults,
        };

        await File.WriteAllTextAsync(
            options.OutputManifestPath,
            JsonSerializer.Serialize(manifest, JsonUtils.SnakeCaseIndented),
            Encoding.UTF8,
            cancellationToken).ConfigureAwait(false);
        _log($"Claude web recreation manifest written to {options.OutputManifestPath}");

        return new ClaudeWebRecreationResult(
            ManifestPath: options.OutputManifestPath,
            TargetOrganizationUuid: targetOrg.Uuid,
            TargetOrganizationName: targetOrg.Name,
            SourceConversationCount: source.Conversations.Count,
            SourceConversationMessageCount: source.Conversations.Sum(item => item.Messages.Count),
            SourceProjectCount: source.Projects.Count,
            CreatedConversationCount: createdConversationCount,
            ExistingConversationCount: existingConversationCount,
            CreatedProjectCount: createdProjectCount,
            ExistingProjectCount: existingProjectCount,
            CreatedDocCount: createdDocCount,
            ExistingDocCount: existingDocCount,
            FailedOperationCount: failures);
    }

    public async Task<ClaudeWebRecreationVerificationResult> VerifyAsync(
        ClaudeWebRecreationVerificationOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var manifestPath = Path.GetFullPath(options.ManifestPath);
        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException($"Claude web recreation manifest does not exist: {manifestPath}");
        }

        using var manifestDocument = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath, Encoding.UTF8, cancellationToken).ConfigureAwait(false));
        var manifestRoot = manifestDocument.RootElement;
        var target = manifestRoot.GetProperty("target");
        var targetOrgUuid = ReadString(target, "organization_uuid");
        if (string.IsNullOrWhiteSpace(targetOrgUuid))
        {
            throw new InvalidOperationException($"Manifest does not contain target.organization_uuid: {manifestPath}");
        }

        var outputPath = string.IsNullOrWhiteSpace(options.OutputPath)
            ? Path.Combine(
                Path.GetDirectoryName(manifestPath) ?? Directory.GetCurrentDirectory(),
                $"{Path.GetFileNameWithoutExtension(manifestPath)}.verification.json")
            : Path.GetFullPath(options.OutputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");

        using var playwright = await Playwright.CreateAsync().ConfigureAwait(false);
        var browser = await playwright.Chromium.ConnectOverCDPAsync(options.EdgeDebugUrl).ConfigureAwait(false);
        var context = browser.Contexts.FirstOrDefault()
            ?? throw new InvalidOperationException($"No browser context is available from {options.EdgeDebugUrl}.");
        var page = context.Pages.FirstOrDefault() ?? await context.NewPageAsync().ConfigureAwait(false);

        var missingConversations = new List<Dictionary<string, object?>>();
        var missingProjects = new List<Dictionary<string, object?>>();
        var missingDocs = new List<Dictionary<string, object?>>();
        var verifiedConversationCount = 0;
        var verifiedProjectCount = 0;
        var verifiedDocCount = 0;

        var conversations = ReadManifestArray(manifestRoot, "conversations")
            .Where(IsVerifiableManifestItem)
            .ToArray();
        foreach (var conversation in conversations)
        {
            var targetUuid = ReadString(conversation, "target_uuid");
            if (string.IsNullOrWhiteSpace(targetUuid))
            {
                missingConversations.Add(DescribeMissing(conversation, "missing_target_uuid"));
                continue;
            }

            var response = await FetchAsync(
                page,
                $"/api/organizations/{targetOrgUuid}/chat_conversations/{targetUuid}?tree=True&rendering_mode=messages&render_all_tools=true&consistency=strong",
                "GET").ConfigureAwait(false);
            if (response.Ok)
            {
                verifiedConversationCount++;
            }
            else
            {
                var item = DescribeMissing(conversation, "not_found");
                item["http_status"] = response.Status;
                item["response"] = response.BodyText;
                missingConversations.Add(item);
            }
        }

        var projects = ReadManifestArray(manifestRoot, "projects")
            .Where(IsVerifiableManifestItem)
            .ToArray();
        var targetProjects = await FetchProjectsAsync(page, targetOrgUuid).ConfigureAwait(false);
        foreach (var project in projects)
        {
            var targetUuid = ReadString(project, "target_uuid");
            if (string.IsNullOrWhiteSpace(targetUuid))
            {
                missingProjects.Add(DescribeMissing(project, "missing_target_uuid"));
                continue;
            }

            var exists = targetProjects.Any(item =>
                string.Equals(ReadString(item, "uuid"), targetUuid, StringComparison.OrdinalIgnoreCase));
            if (exists)
            {
                verifiedProjectCount++;
            }
            else
            {
                missingProjects.Add(DescribeMissing(project, "not_found"));
            }
        }

        var docs = ReadManifestArray(manifestRoot, "docs")
            .Where(IsVerifiableManifestItem)
            .ToArray();
        var docsByProject = new Dictionary<string, IReadOnlyList<JsonElement>>(StringComparer.OrdinalIgnoreCase);
        foreach (var doc in docs)
        {
            var projectUuid = ReadString(doc, "target_project_uuid");
            var fileName = ReadString(doc, "file_name");
            var expectedHash = ReadString(doc, "content_sha256");
            if (string.IsNullOrWhiteSpace(projectUuid) || string.IsNullOrWhiteSpace(fileName))
            {
                missingDocs.Add(DescribeMissing(doc, "missing_project_or_file_name"));
                continue;
            }

            if (!docsByProject.TryGetValue(projectUuid, out var targetDocs))
            {
                var response = await FetchAsync(page, $"/api/organizations/{targetOrgUuid}/projects/{projectUuid}/docs", "GET").ConfigureAwait(false);
                targetDocs = response.Ok ? ReadBodyArray(response.Body) : Array.Empty<JsonElement>();
                docsByProject[projectUuid] = targetDocs;
                if (!response.Ok)
                {
                    var item = DescribeMissing(doc, "project_docs_fetch_failed");
                    item["http_status"] = response.Status;
                    item["response"] = response.BodyText;
                    missingDocs.Add(item);
                    continue;
                }
            }

            var matches = targetDocs
                .Where(item => string.Equals(ReadString(item, "file_name"), fileName, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (matches.Length == 0)
            {
                missingDocs.Add(DescribeMissing(doc, "not_found"));
                continue;
            }

            var hashMatches = string.IsNullOrWhiteSpace(expectedHash)
                || matches.Any(item => string.Equals(Sha256Text(ReadString(item, "content")), expectedHash, StringComparison.OrdinalIgnoreCase));
            if (hashMatches)
            {
                verifiedDocCount++;
                continue;
            }

            var hashMismatch = DescribeMissing(doc, "content_hash_mismatch");
            hashMismatch["actual_hashes"] = matches
                .Select(item => Sha256Text(ReadString(item, "content")))
                .ToArray();
            missingDocs.Add(hashMismatch);
        }

        var failedOperations = missingConversations.Count + missingProjects.Count + missingDocs.Count;
        var verification = new Dictionary<string, object?>
        {
            ["format"] = "claude_web_recreation_verification",
            ["version"] = 1,
            ["created_at"] = DateTimeOffset.Now.ToString("O"),
            ["manifest"] = manifestPath,
            ["target"] = new Dictionary<string, object?>
            {
                ["organization_uuid"] = targetOrgUuid,
                ["edge_debug_url"] = options.EdgeDebugUrl,
            },
            ["counts"] = new Dictionary<string, object?>
            {
                ["expected_conversations"] = conversations.Length,
                ["verified_conversations"] = verifiedConversationCount,
                ["missing_conversations"] = missingConversations.Count,
                ["expected_projects"] = projects.Length,
                ["verified_projects"] = verifiedProjectCount,
                ["missing_projects"] = missingProjects.Count,
                ["expected_docs"] = docs.Length,
                ["verified_docs"] = verifiedDocCount,
                ["missing_docs"] = missingDocs.Count,
                ["failed_operations"] = failedOperations,
            },
            ["missing_conversations"] = missingConversations,
            ["missing_projects"] = missingProjects,
            ["missing_docs"] = missingDocs,
        };

        await File.WriteAllTextAsync(
            outputPath,
            JsonSerializer.Serialize(verification, JsonUtils.SnakeCaseIndented),
            Encoding.UTF8,
            cancellationToken).ConfigureAwait(false);
        _log($"Claude web recreation verification written to {outputPath}");

        return new ClaudeWebRecreationVerificationResult(
            VerificationPath: outputPath,
            ManifestPath: manifestPath,
            TargetOrganizationUuid: targetOrgUuid,
            ExpectedConversationCount: conversations.Length,
            VerifiedConversationCount: verifiedConversationCount,
            ExpectedProjectCount: projects.Length,
            VerifiedProjectCount: verifiedProjectCount,
            ExpectedDocCount: docs.Length,
            VerifiedDocCount: verifiedDocCount,
            FailedOperationCount: failedOperations);
    }

    private static void CountDocResult(DocWriteResult result, ref int created, ref int existing, ref int failures)
    {
        created += result.CreatedCount;
        existing += result.ExistingCount;
        failures += result.FailedCount;
    }

    private static IReadOnlyList<JsonElement> ReadManifestArray(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<JsonElement>();
        }

        return array.EnumerateArray().Select(item => item.Clone()).ToArray();
    }

    private static bool IsVerifiableManifestItem(JsonElement item)
    {
        var status = ReadString(item, "status");
        return !status.Equals("failed", StringComparison.OrdinalIgnoreCase)
            && !status.Equals("would_create", StringComparison.OrdinalIgnoreCase)
            && !status.Equals("failed_fetch_docs", StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, object?> DescribeMissing(JsonElement item, string reason)
    {
        var description = new Dictionary<string, object?>
        {
            ["reason"] = reason,
        };

        AddIfPresent(description, "source_uuid", ReadString(item, "source_uuid"));
        AddIfPresent(description, "target_uuid", ReadString(item, "target_uuid"));
        AddIfPresent(description, "target_project_uuid", ReadString(item, "target_project_uuid"));
        AddIfPresent(description, "target_doc_uuid", ReadString(item, "target_doc_uuid"));
        AddIfPresent(description, "name", ReadString(item, "name"));
        AddIfPresent(description, "file_name", ReadString(item, "file_name"));
        AddIfPresent(description, "content_sha256", ReadString(item, "content_sha256"));
        AddIfPresent(description, "status", ReadString(item, "status"));
        return description;
    }

    private static void AddIfPresent(IDictionary<string, object?> target, string key, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            target[key] = value;
        }
    }

    private async Task<ProjectWriteResult> EnsureProjectAsync(
        IPage page,
        string orgUuid,
        ClaudeWebProject project,
        IReadOnlyList<JsonElement> targetProjects,
        bool dryRun)
    {
        var existing = FindProject(targetProjects, project);
        if (existing.HasValue)
        {
            var existingTargetUuid = ReadString(existing.Value, "uuid");
            _log($"Project exists: {project.Name} -> {existingTargetUuid}");
            return new ProjectWriteResult(existingTargetUuid, Created: false, Failed: false, new Dictionary<string, object?>
            {
                ["source_uuid"] = project.Uuid,
                ["target_uuid"] = existingTargetUuid,
                ["name"] = project.Name,
                ["status"] = "existing",
                ["url"] = $"https://claude.ai/project/{existingTargetUuid}",
            });
        }

        if (dryRun)
        {
            return new ProjectWriteResult(string.Empty, Created: false, Failed: false, new Dictionary<string, object?>
            {
                ["source_uuid"] = project.Uuid,
                ["name"] = project.Name,
                ["status"] = "would_create",
            });
        }

        var response = await FetchAsync(page, $"/api/organizations/{orgUuid}/projects", "POST", new Dictionary<string, object?>
        {
            ["name"] = project.Name,
            ["description"] = project.Description,
            ["is_private"] = project.IsPrivate,
            ["prompt_template"] = project.PromptTemplate,
        }).ConfigureAwait(false);

        if (!response.Ok)
        {
            _log($"Project create failed: {project.Name} -> HTTP {response.Status}");
            return new ProjectWriteResult(string.Empty, Created: false, Failed: true, new Dictionary<string, object?>
            {
                ["source_uuid"] = project.Uuid,
                ["name"] = project.Name,
                ["status"] = "failed",
                ["http_status"] = response.Status,
                ["response"] = response.BodyText,
            });
        }

        var createdTargetUuid = ReadString(response.Body, "uuid");
        _log($"Project created: {project.Name} -> {createdTargetUuid}");
        return new ProjectWriteResult(createdTargetUuid, Created: true, Failed: false, new Dictionary<string, object?>
        {
            ["source_uuid"] = project.Uuid,
            ["target_uuid"] = createdTargetUuid,
            ["name"] = project.Name,
            ["status"] = "created",
            ["url"] = $"https://claude.ai/project/{createdTargetUuid}",
        });
    }

    private async Task<ConversationWriteResult> EnsureConversationAsync(
        IPage page,
        string orgUuid,
        ClaudeWebConversation conversation,
        IReadOnlyList<JsonElement> targetConversations,
        string model,
        bool dryRun)
    {
        var deterministicTargetUuid = DeterministicMigratedConversationUuid(orgUuid, conversation.Uuid);
        var existingSourceUuid = targetConversations.FirstOrDefault(item =>
            string.Equals(ReadString(item, "uuid"), conversation.Uuid, StringComparison.OrdinalIgnoreCase));
        if (existingSourceUuid.ValueKind == JsonValueKind.Object
            || await ConversationExistsAsync(page, orgUuid, conversation.Uuid).ConfigureAwait(false))
        {
            _log($"Conversation exists: {conversation.Uuid} ({conversation.Name})");
            return new ConversationWriteResult(Created: false, Failed: false, new Dictionary<string, object?>
            {
                ["source_uuid"] = conversation.Uuid,
                ["target_uuid"] = conversation.Uuid,
                ["uuid_strategy"] = "source_uuid",
                ["name"] = conversation.Name,
                ["message_count"] = conversation.Messages.Count,
                ["status"] = "existing",
                ["url"] = $"https://claude.ai/chat/{conversation.Uuid}",
            });
        }

        var existingDeterministicUuid = targetConversations.FirstOrDefault(item =>
            string.Equals(ReadString(item, "uuid"), deterministicTargetUuid, StringComparison.OrdinalIgnoreCase));
        if (existingDeterministicUuid.ValueKind == JsonValueKind.Object
            || await ConversationExistsAsync(page, orgUuid, deterministicTargetUuid).ConfigureAwait(false))
        {
            _log($"Conversation exists with deterministic migration UUID: {conversation.Uuid} -> {deterministicTargetUuid} ({conversation.Name})");
            return new ConversationWriteResult(Created: false, Failed: false, new Dictionary<string, object?>
            {
                ["source_uuid"] = conversation.Uuid,
                ["target_uuid"] = deterministicTargetUuid,
                ["uuid_strategy"] = "deterministic_migration_uuid",
                ["name"] = conversation.Name,
                ["message_count"] = conversation.Messages.Count,
                ["status"] = "existing",
                ["url"] = $"https://claude.ai/chat/{deterministicTargetUuid}",
            });
        }

        if (dryRun)
        {
            return new ConversationWriteResult(Created: false, Failed: false, new Dictionary<string, object?>
            {
                ["source_uuid"] = conversation.Uuid,
                ["target_uuid"] = conversation.Uuid,
                ["fallback_target_uuid"] = deterministicTargetUuid,
                ["uuid_strategy"] = "source_uuid_or_deterministic_migration_uuid",
                ["name"] = conversation.Name,
                ["message_count"] = conversation.Messages.Count,
                ["status"] = "would_create",
            });
        }

        var response = await CreateConversationAsync(page, orgUuid, conversation, conversation.Uuid, model).ConfigureAwait(false);
        if (!response.Ok && string.Equals(ReadErrorCode(response.Body), "conversation_already_exists", StringComparison.OrdinalIgnoreCase))
        {
            _log($"Source conversation UUID is reserved; retrying with deterministic migration UUID: {conversation.Uuid} -> {deterministicTargetUuid}");
            response = await CreateConversationAsync(page, orgUuid, conversation, deterministicTargetUuid, model).ConfigureAwait(false);
            if (response.Ok)
            {
                _log($"Conversation created: {conversation.Uuid} -> {deterministicTargetUuid} ({conversation.Name})");
                return new ConversationWriteResult(Created: true, Failed: false, new Dictionary<string, object?>
                {
                    ["source_uuid"] = conversation.Uuid,
                    ["target_uuid"] = deterministicTargetUuid,
                    ["uuid_strategy"] = "deterministic_migration_uuid",
                    ["name"] = conversation.Name,
                    ["message_count"] = conversation.Messages.Count,
                    ["status"] = "created",
                    ["url"] = $"https://claude.ai/chat/{deterministicTargetUuid}",
                });
            }
        }

        if (!response.Ok)
        {
            _log($"Conversation create failed: {conversation.Uuid} ({conversation.Name}) -> HTTP {response.Status}");
            return new ConversationWriteResult(Created: false, Failed: true, new Dictionary<string, object?>
            {
                ["source_uuid"] = conversation.Uuid,
                ["target_uuid"] = conversation.Uuid,
                ["fallback_target_uuid"] = deterministicTargetUuid,
                ["name"] = conversation.Name,
                ["message_count"] = conversation.Messages.Count,
                ["status"] = "failed",
                ["http_status"] = response.Status,
                ["error_code"] = ReadErrorCode(response.Body),
                ["response"] = response.BodyText,
            });
        }

        _log($"Conversation created: {conversation.Uuid} ({conversation.Name})");
        return new ConversationWriteResult(Created: true, Failed: false, new Dictionary<string, object?>
        {
            ["source_uuid"] = conversation.Uuid,
            ["target_uuid"] = conversation.Uuid,
            ["uuid_strategy"] = "source_uuid",
            ["name"] = conversation.Name,
            ["message_count"] = conversation.Messages.Count,
            ["status"] = "created",
            ["url"] = $"https://claude.ai/chat/{conversation.Uuid}",
        });
    }

    private async Task<DocWriteResult> EnsureDocAsync(
        IPage page,
        string orgUuid,
        string projectUuid,
        string fileName,
        string content,
        bool dryRun)
    {
        var docsResponse = await FetchAsync(page, $"/api/organizations/{orgUuid}/projects/{projectUuid}/docs", "GET").ConfigureAwait(false);
        if (!docsResponse.Ok)
        {
            return DocWriteResult.Failed(new Dictionary<string, object?>
            {
                ["target_project_uuid"] = projectUuid,
                ["file_name"] = fileName,
                ["status"] = "failed_fetch_docs",
                ["http_status"] = docsResponse.Status,
                ["response"] = docsResponse.BodyText,
            });
        }

        var existingDocs = ReadBodyArray(docsResponse.Body);
        var expectedHash = Sha256Text(content);
        var existing = existingDocs.FirstOrDefault(doc =>
            string.Equals(ReadString(doc, "file_name"), fileName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(Sha256Text(ReadString(doc, "content")), expectedHash, StringComparison.OrdinalIgnoreCase));
        if (existing.ValueKind == JsonValueKind.Object)
        {
            return DocWriteResult.Existing(new Dictionary<string, object?>
            {
                ["target_project_uuid"] = projectUuid,
                ["target_doc_uuid"] = ReadString(existing, "uuid"),
                ["file_name"] = fileName,
                ["content_sha256"] = expectedHash,
                ["content_length"] = content.Length,
                ["status"] = "existing",
            });
        }

        var sameNameDifferentContent = existingDocs.Any(doc =>
            string.Equals(ReadString(doc, "file_name"), fileName, StringComparison.OrdinalIgnoreCase));
        if (sameNameDifferentContent)
        {
            fileName = WithHashSuffix(fileName, expectedHash);
            existing = existingDocs.FirstOrDefault(doc =>
                string.Equals(ReadString(doc, "file_name"), fileName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(Sha256Text(ReadString(doc, "content")), expectedHash, StringComparison.OrdinalIgnoreCase));
            if (existing.ValueKind == JsonValueKind.Object)
            {
                return DocWriteResult.Existing(new Dictionary<string, object?>
                {
                    ["target_project_uuid"] = projectUuid,
                    ["target_doc_uuid"] = ReadString(existing, "uuid"),
                    ["file_name"] = fileName,
                    ["content_sha256"] = expectedHash,
                    ["content_length"] = content.Length,
                    ["status"] = "existing",
                    ["name_collision"] = true,
                });
            }
        }

        if (dryRun)
        {
            return DocWriteResult.Existing(new Dictionary<string, object?>
            {
                ["target_project_uuid"] = projectUuid,
                ["file_name"] = fileName,
                ["content_sha256"] = expectedHash,
                ["content_length"] = content.Length,
                ["status"] = "would_create",
            });
        }

        var single = await CreateDocAsync(page, orgUuid, projectUuid, fileName, content).ConfigureAwait(false);
        if (single.Ok)
        {
            return DocWriteResult.Created(new Dictionary<string, object?>
            {
                ["target_project_uuid"] = projectUuid,
                ["target_doc_uuid"] = ReadString(single.Body, "uuid"),
                ["file_name"] = fileName,
                ["content_sha256"] = expectedHash,
                ["content_length"] = content.Length,
                ["status"] = "created",
            });
        }

        if (content.Length <= DocChunkCharLimit)
        {
            return DocWriteResult.Failed(new Dictionary<string, object?>
            {
                ["target_project_uuid"] = projectUuid,
                ["file_name"] = fileName,
                ["content_sha256"] = expectedHash,
                ["content_length"] = content.Length,
                ["status"] = "failed",
                ["http_status"] = single.Status,
                ["response"] = single.BodyText,
            });
        }

        var chunkResults = new List<Dictionary<string, object?>>();
        var created = 0;
        var failed = 0;
        var chunks = SplitContent(content, DocChunkCharLimit).ToArray();
        for (var index = 0; index < chunks.Length; index++)
        {
            var chunkFileName = WithPartSuffix(fileName, index + 1, chunks.Length);
            var response = await CreateDocAsync(page, orgUuid, projectUuid, chunkFileName, chunks[index]).ConfigureAwait(false);
            if (response.Ok)
            {
                created++;
                chunkResults.Add(new Dictionary<string, object?>
                {
                    ["target_project_uuid"] = projectUuid,
                    ["target_doc_uuid"] = ReadString(response.Body, "uuid"),
                    ["file_name"] = chunkFileName,
                    ["content_sha256"] = Sha256Text(chunks[index]),
                    ["content_length"] = chunks[index].Length,
                    ["part"] = index + 1,
                    ["parts"] = chunks.Length,
                    ["status"] = "created",
                });
            }
            else
            {
                failed++;
                chunkResults.Add(new Dictionary<string, object?>
                {
                    ["target_project_uuid"] = projectUuid,
                    ["file_name"] = chunkFileName,
                    ["content_sha256"] = Sha256Text(chunks[index]),
                    ["content_length"] = chunks[index].Length,
                    ["part"] = index + 1,
                    ["parts"] = chunks.Length,
                    ["status"] = "failed",
                    ["http_status"] = response.Status,
                    ["response"] = response.BodyText,
                });
            }
        }

        return new DocWriteResult(chunkResults, created, ExistingCount: 0, failed);
    }

    private static JsonElement? FindProject(IReadOnlyList<JsonElement> targetProjects, ClaudeWebProject project)
    {
        foreach (var candidate in targetProjects)
        {
            var nameMatches = string.Equals(ReadString(candidate, "name"), project.Name, StringComparison.OrdinalIgnoreCase);
            if (!nameMatches)
            {
                continue;
            }

            if (project.IsStarterProject && ReadBool(candidate, "is_starter_project"))
            {
                return candidate;
            }

            if (!project.IsStarterProject)
            {
                return candidate;
            }
        }

        return null;
    }

    private static async Task<TargetOrg> ResolveTargetOrganizationAsync(IPage page)
    {
        var orgUuid = await page.EvaluateAsync<string>(
            """
            () => {
              const cookie = document.cookie.split('; ').find(item => item.startsWith('lastActiveOrg='));
              return cookie ? decodeURIComponent(cookie.split('=').slice(1).join('=')) : '';
            }
            """).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(orgUuid))
        {
            await page.GotoAsync("https://claude.ai/", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded }).ConfigureAwait(false);
            orgUuid = await page.EvaluateAsync<string>(
                """
                () => {
                  const cookie = document.cookie.split('; ').find(item => item.startsWith('lastActiveOrg='));
                  return cookie ? decodeURIComponent(cookie.split('=').slice(1).join('=')) : '';
                }
                """).ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(orgUuid))
        {
            throw new InvalidOperationException("Could not resolve Claude lastActiveOrg cookie from the attached Edge session.");
        }

        var response = await FetchAsync(page, $"/api/organizations/{orgUuid}", "GET").ConfigureAwait(false);
        if (!response.Ok)
        {
            throw new InvalidOperationException($"Could not read target Claude organization {orgUuid}: HTTP {response.Status} {response.BodyText}");
        }

        return new TargetOrg(orgUuid, ReadString(response.Body, "name"));
    }

    private static async Task<IReadOnlyList<JsonElement>> FetchProjectsAsync(IPage page, string orgUuid)
    {
        var response = await FetchAsync(page, $"/api/organizations/{orgUuid}/projects", "GET").ConfigureAwait(false);
        if (!response.Ok)
        {
            throw new InvalidOperationException($"Could not fetch Claude projects: HTTP {response.Status} {response.BodyText}");
        }

        return ReadBodyArray(response.Body);
    }

    private static async Task<IReadOnlyList<JsonElement>> FetchConversationsAsync(IPage page, string orgUuid)
    {
        var results = new List<JsonElement>();
        var offset = 0;
        const int limit = 100;
        while (true)
        {
            var response = await FetchAsync(page, $"/api/organizations/{orgUuid}/chat_conversations_v2?limit={limit}&offset={offset}&starred=false&consistency=strong", "GET").ConfigureAwait(false);
            if (!response.Ok)
            {
                throw new InvalidOperationException($"Could not fetch Claude conversations: HTTP {response.Status} {response.BodyText}");
            }

            if (response.Body.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                results.AddRange(data.EnumerateArray().Select(item => item.Clone()));
            }

            var hasMore = response.Body.TryGetProperty("has_more", out var hasMoreElement)
                && hasMoreElement.ValueKind == JsonValueKind.True;
            if (!hasMore)
            {
                return results;
            }

            offset += limit;
        }
    }

    private static async Task<ApiResponse> CreateDocAsync(IPage page, string orgUuid, string projectUuid, string fileName, string content)
        => await FetchAsync(page, $"/api/organizations/{orgUuid}/projects/{projectUuid}/docs", "POST", new Dictionary<string, object?>
        {
            ["file_name"] = fileName,
            ["content"] = content,
        }).ConfigureAwait(false);

    private static async Task<ApiResponse> CreateConversationAsync(
        IPage page,
        string orgUuid,
        ClaudeWebConversation conversation,
        string targetUuid,
        string model)
        => await FetchAsync(page, $"/api/organizations/{orgUuid}/chat_conversations", "POST", new Dictionary<string, object?>
        {
            ["uuid"] = targetUuid,
            ["name"] = conversation.Name,
            ["model"] = model,
            ["project_uuid"] = null,
            ["is_temporary"] = false,
        }).ConfigureAwait(false);

    private static async Task<bool> ConversationExistsAsync(IPage page, string orgUuid, string conversationUuid)
    {
        var response = await FetchAsync(
            page,
            $"/api/organizations/{orgUuid}/chat_conversations/{conversationUuid}?tree=True&rendering_mode=messages&render_all_tools=true&consistency=strong",
            "GET").ConfigureAwait(false);
        return response.Ok;
    }

    private static async Task<ApiResponse> FetchAsync(
        IPage page,
        string url,
        string method,
        object? body = null)
    {
        var result = await page.EvaluateAsync<JsonElement>(
            """
            async ({ url, method, body }) => {
              const options = {
                method,
                credentials: 'include',
                headers: method === 'GET' ? {} : { 'content-type': 'application/json' },
              };
              if (body !== null && body !== undefined && method !== 'GET') {
                options.body = JSON.stringify(body);
              }

              const response = await fetch(url, options);
              const text = await response.text();
              let parsed = null;
              try {
                parsed = text ? JSON.parse(text) : null;
              } catch {
                parsed = text;
              }

              return {
                status: response.status,
                ok: response.ok,
                body: parsed,
                bodyText: text,
              };
            }
            """,
            new { url, method, body }).ConfigureAwait(false);

        return new ApiResponse(
            Status: result.GetProperty("status").GetInt32(),
            Ok: result.GetProperty("ok").GetBoolean(),
            Body: result.GetProperty("body").Clone(),
            BodyText: ReadString(result, "bodyText"));
    }

    private static IReadOnlyList<JsonElement> ReadBodyArray(JsonElement body)
    {
        if (body.ValueKind == JsonValueKind.Array)
        {
            return body.EnumerateArray().Select(item => item.Clone()).ToArray();
        }

        if (body.ValueKind == JsonValueKind.Object
            && body.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Array)
        {
            return data.EnumerateArray().Select(item => item.Clone()).ToArray();
        }

        return Array.Empty<JsonElement>();
    }

    private static IEnumerable<string> SplitContent(string content, int chunkSize)
    {
        for (var index = 0; index < content.Length; index += chunkSize)
        {
            yield return content.Substring(index, Math.Min(chunkSize, content.Length - index));
        }
    }

    private static string WithPartSuffix(string fileName, int part, int parts)
    {
        var extension = Path.GetExtension(fileName);
        var stem = string.IsNullOrWhiteSpace(extension) ? fileName : fileName[..^extension.Length];
        return $"{stem}.part-{part:D2}-of-{parts:D2}{extension}";
    }

    private static string WithHashSuffix(string fileName, string sha256)
    {
        var extension = Path.GetExtension(fileName);
        var stem = string.IsNullOrWhiteSpace(extension) ? fileName : fileName[..^extension.Length];
        var suffix = string.IsNullOrWhiteSpace(sha256) ? "updated" : sha256[..Math.Min(12, sha256.Length)];
        return $"{stem}.{suffix}{extension}";
    }

    private static string FallbackAccountLabel(ClaudeWebExport export)
    {
        if (!string.IsNullOrWhiteSpace(export.SourceAccountEmail))
        {
            return export.SourceAccountEmail;
        }

        if (!string.IsNullOrWhiteSpace(export.SourceAccountName))
        {
            return export.SourceAccountName;
        }

        return string.IsNullOrWhiteSpace(export.SourceAccountUuid) ? "source-account" : export.SourceAccountUuid;
    }

    private static string Sha256Text(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string DeterministicMigratedConversationUuid(string orgUuid, string sourceConversationUuid)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"claude-web-recreation:{orgUuid}:{sourceConversationUuid}"));
        var guidBytes = bytes.Take(16).ToArray();
        guidBytes[6] = (byte)((guidBytes[6] & 0x0F) | 0x40);
        guidBytes[8] = (byte)((guidBytes[8] & 0x3F) | 0x80);

        return new Guid(guidBytes).ToString();
    }

    private static string ReadErrorCode(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        if (!body.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        if (!error.TryGetProperty("details", out var details) || details.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        return ReadString(details, "error_code");
    }

    private static string ReadString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return string.Empty;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString() ?? string.Empty,
            JsonValueKind.Null => string.Empty,
            _ => property.ToString(),
        };
    }

    private static bool ReadBool(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.True;

    private sealed record ApiResponse(int Status, bool Ok, JsonElement Body, string BodyText);

    private sealed record TargetOrg(string Uuid, string Name);

    private sealed record ProjectWriteResult(
        string TargetUuid,
        bool Created,
        bool Failed,
        Dictionary<string, object?> Manifest);

    private sealed record ConversationWriteResult(
        bool Created,
        bool Failed,
        Dictionary<string, object?> Manifest);

    private sealed record DocWriteResult(
        IReadOnlyList<Dictionary<string, object?>> ManifestEntries,
        int CreatedCount,
        int ExistingCount,
        int FailedCount)
    {
        public static DocWriteResult Created(Dictionary<string, object?> manifest)
            => new([manifest], CreatedCount: 1, ExistingCount: 0, FailedCount: 0);

        public static DocWriteResult Existing(Dictionary<string, object?> manifest)
            => new([manifest], CreatedCount: 0, ExistingCount: 1, FailedCount: 0);

        public static DocWriteResult Failed(Dictionary<string, object?> manifest)
            => new([manifest], CreatedCount: 0, ExistingCount: 0, FailedCount: 1);
    }
}
