using System.IO.Compression;
using System.Text;
using System.Text.Json;
using ClaudeMigrator.Core.Models;
using ClaudeMigrator.Core.Utilities;

namespace ClaudeMigrator.Core.Exporting;

public sealed class UniversalClaudeExporter
{
    private static readonly HashSet<string> DefaultImportGuideKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "claude",
        "codex",
        "copilot",
    };

    private static readonly JsonSerializerOptions JsonOptions = JsonUtils.SnakeCaseIndented;

    private readonly string _runtimeRoot;
    private readonly string _exportsRoot;
    private readonly string _processingRoot;
    private readonly Action<string> _log;

    public UniversalClaudeExporter(string runtimeRoot, Action<string>? log = null)
    {
        _runtimeRoot = runtimeRoot;
        _exportsRoot = PathUtils.EnsureDirectory(Path.Combine(_runtimeRoot, "portable_exports")).FullName;
        _processingRoot = PathUtils.EnsureDirectory(Path.Combine(_runtimeRoot, "processing")).FullName;
        _log = log ?? (_ => { });
    }

    public ParsedClaudeExport InspectArchive(
        string archivePath,
        Action<int, string>? progressCallback = null)
    {
        void Emit(int percent, string message) => progressCallback?.Invoke(percent, message);

        archivePath = Path.GetFullPath(archivePath);
        if (!File.Exists(archivePath))
        {
            throw new FileNotFoundException($"Export archive not found: {archivePath}");
        }

        if (!IsZipFile(archivePath))
        {
            throw new InvalidOperationException($"Not a valid zip archive: {archivePath}");
        }

        Emit(5, "Reading archive members");
        var sourceSha = PathUtils.Sha256File(archivePath);
        var jsonDocuments = new List<(string SourceFile, object Document)>();
        var sourceMembers = new List<string>();
        var codeFiles = new List<string>();

        using (var archive = ZipFile.OpenRead(archivePath))
        {
            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrWhiteSpace(entry.FullName) || entry.FullName.EndsWith("/"))
                {
                    continue;
                }

                sourceMembers.Add(entry.FullName);
                if (entry.FullName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        using var stream = entry.Open();
                        using var document = JsonDocument.Parse(stream);
                        jsonDocuments.Add((entry.FullName, document.RootElement.Clone()));
                    }
                    catch (Exception ex)
                    {
                        _log($"Skipping unreadable JSON member {entry.FullName}: {ex.Message}");
                    }
                }

                if (ClaudeExportHelpers.ShouldExtractAsCode(entry.FullName))
                {
                    codeFiles.Add(entry.FullName);
                }
            }
        }

        Emit(20, "Normalizing conversations, projects, and memory");
        var (conversations, projects, memoryItems) = CollectRecords(jsonDocuments);
        var topKeywords = ClaudeExportHelpers.ExtractKeywords(AllTextFromRecords(conversations, projects, memoryItems), limit: 20);
        AttachSummaries(conversations, projects, memoryItems, topKeywords);
        Emit(70, "Archive inspection complete");

        return new ParsedClaudeExport(
            SourceArchive: archivePath,
            SourceSha256: sourceSha,
            JsonDocuments: jsonDocuments,
            Conversations: conversations,
            Projects: projects,
            MemoryItems: memoryItems,
            SourceMembers: sourceMembers,
            TopKeywords: topKeywords,
            CodeFiles: codeFiles);
    }

    public PortableExportResult ExportPortableZip(
        string archivePath,
        string? outputZip = null,
        Action<int, string>? progressCallback = null,
        Action<string>? logCallback = null)
    {
        void Emit(int percent, string message) => progressCallback?.Invoke(percent, message);
        void LogLine(string message) => (logCallback ?? _log).Invoke(message);

        Emit(2, "Inspecting export archive");
        LogLine($"Portable export source archive: {archivePath}");
        var parsed = InspectArchive(archivePath, progressCallback);
        LogLine(
            $"Parsed {parsed.Conversations.Count} conversations, {parsed.Projects.Count} projects, "
            + $"{parsed.MemoryItems.Count} memory items, and {parsed.CodeFiles.Count} source files.");

        var runName = $"claude_portable_export_{PathUtils.TimestampTag()}";
        var bundleRoot = PathUtils.EnsureDirectory(Path.Combine(_processingRoot, runName)).FullName;
        var artifactRoot = PathUtils.EnsureDirectory(Path.Combine(bundleRoot, "artifacts", "extracted_code")).FullName;
        var memoryRoot = PathUtils.EnsureDirectory(Path.Combine(bundleRoot, "memory")).FullName;
        var projectsRoot = PathUtils.EnsureDirectory(Path.Combine(bundleRoot, "projects")).FullName;
        var conversationsRoot = PathUtils.EnsureDirectory(Path.Combine(bundleRoot, "conversations")).FullName;

        Emit(10, "Writing memory bundle");
        var memoryPath = WriteMemoryBundle(memoryRoot, parsed);

        Emit(30, "Writing project blueprints");
        var projectDirs = WriteProjectBundles(projectsRoot, parsed, bundleRoot);

        Emit(50, "Writing conversations");
        var conversationDirs = WriteConversationBundles(conversationsRoot, parsed, bundleRoot);

        Emit(72, "Extracting source artifacts");
        var extractedCount = ExtractCodeFiles(parsed, artifactRoot);

        Emit(85, "Writing manifest");
        var (manifestPath, manifest) = WriteManifest(
            bundleRoot: bundleRoot,
            parsed: parsed,
            memoryPath: memoryPath,
            projectDirs: projectDirs,
            conversationDirs: conversationDirs,
            artifactRoot: artifactRoot,
            extractedCount: extractedCount);

        if (string.IsNullOrWhiteSpace(outputZip))
        {
            outputZip = Path.Combine(_exportsRoot, $"{Path.GetFileName(bundleRoot)}.zip");
        }

        outputZip = Path.GetFullPath(outputZip);
        Directory.CreateDirectory(Path.GetDirectoryName(outputZip) ?? ".");
        if (File.Exists(outputZip))
        {
            File.Delete(outputZip);
        }

        Emit(95, "Packaging portable zip");
        ZipFile.CreateFromDirectory(bundleRoot, outputZip, CompressionLevel.Optimal, includeBaseDirectory: true);
        Emit(100, "Portable export ready");
        LogLine($"Portable export written to {outputZip} from bundle root {bundleRoot}.");

        var counts = new Dictionary<string, int>
        {
            ["conversations"] = parsed.Conversations.Count,
            ["projects"] = parsed.Projects.Count,
            ["memory_items"] = parsed.MemoryItems.Count,
            ["source_files"] = parsed.CodeFiles.Count,
            ["extracted_artifacts"] = extractedCount,
        };

        return new PortableExportResult(
            SourceArchive: parsed.SourceArchive,
            BundleRoot: bundleRoot,
            ZipPath: outputZip,
            ManifestPath: manifestPath,
            MemoryPath: memoryPath,
            ProjectDirs: projectDirs,
            ConversationDirs: conversationDirs,
            ArtifactRoot: artifactRoot,
            Manifest: manifest,
            Counts: counts);
    }

    private static bool IsZipFile(string path)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);
            return archive.Entries.Count >= 0;
        }
        catch
        {
            return false;
        }
    }

    private static (List<ConversationRecord> Conversations, List<ProjectRecord> Projects, List<MemoryRecord> MemoryItems)
        CollectRecords(IReadOnlyList<(string SourceFile, object Document)> jsonDocuments)
    {
        var conversations = new List<ConversationRecord>();
        var projects = new List<ProjectRecord>();
        var memoryItems = new List<MemoryRecord>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (sourceFile, document) in jsonDocuments)
        {
            if (document is not JsonElement element)
            {
                continue;
            }

            ScanJson(sourceFile, element, conversations, projects, memoryItems, seen);
        }

        if (conversations.Count == 0 && projects.Count == 0 && memoryItems.Count == 0)
        {
            var combinedText = AggregateText(jsonDocuments);
            if (!string.IsNullOrWhiteSpace(combinedText))
            {
                var slug = PathUtils.Slugify("general", "general");
                conversations.Add(
                    new ConversationRecord(
                        Title: "General Export",
                        Slug: slug,
                        ProjectName: "General",
                        ConversationId: "general",
                        SourceFile: "aggregate",
                        Messages: new[]
                        {
                            new Dictionary<string, object?>
                            {
                                ["role"] = "system",
                                ["text"] = "Archive did not expose a known export structure. Preserve this fallback summary.",
                            },
                            new Dictionary<string, object?>
                            {
                                ["role"] = "user",
                                ["text"] = combinedText[..Math.Min(4000, combinedText.Length)],
                            },
                        }));
            }
        }

        if (projects.Count == 0)
        {
            var grouped = conversations
                .GroupBy(conversation => string.IsNullOrWhiteSpace(conversation.ProjectName) ? "General" : conversation.ProjectName)
                .ToList();

            foreach (var group in grouped)
            {
                var projectConversations = group.ToList();
                var instructions = BuildProjectInstructions(group.Key, projectConversations, memoryItems, []);
                var knowledgeSummary = BuildProjectKnowledgeSummary(group.Key, projectConversations, memoryItems, []);
                projects.Add(
                    new ProjectRecord(
                        Name: group.Key,
                        Slug: PathUtils.Slugify(group.Key, "project"),
                        SourceFile: "derived",
                        Instructions: instructions,
                        KnowledgeSummary: knowledgeSummary,
                        ConversationTitles: projectConversations.Select(item => item.Title).ToList(),
                        SeedPrompt: BuildProjectSeedPrompt(group.Key, projectConversations, [], memoryItems)));
            }
        }

        return (conversations, projects, memoryItems);
    }

    private static void ScanJson(
        string sourceFile,
        JsonElement node,
        ICollection<ConversationRecord> conversations,
        ICollection<ProjectRecord> projects,
        ICollection<MemoryRecord> memoryItems,
        ISet<string> seen,
        int depth = 0)
    {
        if (depth > 12)
        {
            return;
        }

        if (node.ValueKind == JsonValueKind.Object)
        {
            var added = false;
            if (LooksLikeConversation(node))
            {
                var record = NormalizeConversation(node, sourceFile, conversations.Count);
                var fingerprint = ClaudeExportHelpers.Fingerprint(record.Messages, record.Title, record.ProjectName, record.ConversationId);
                if (seen.Add(fingerprint))
                {
                    conversations.Add(record);
                }

                added = true;
            }
            else if (LooksLikeProject(node))
            {
                var record = NormalizeProject(node, sourceFile, projects.Count);
                var fingerprint = ClaudeExportHelpers.Fingerprint(
                    record.Name,
                    record.Instructions,
                    record.KnowledgeSummary,
                    record.ConversationTitles);
                if (seen.Add(fingerprint))
                {
                    projects.Add(record);
                }

                added = true;
            }
            else if (LooksLikeMemory(node))
            {
                var record = NormalizeMemory(node, sourceFile, memoryItems.Count);
                var fingerprint = ClaudeExportHelpers.Fingerprint(record.Title, record.Text);
                if (seen.Add(fingerprint))
                {
                    memoryItems.Add(record);
                }

                added = true;
            }

            if (added)
            {
                return;
            }

            foreach (var property in node.EnumerateObject())
            {
                if (property.NameEquals("messages") || property.NameEquals("conversation") || property.NameEquals("chat") || property.NameEquals("turns"))
                {
                    continue;
                }

                if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                {
                    ScanJson(sourceFile, property.Value, conversations, projects, memoryItems, seen, depth + 1);
                }
            }
        }
        else if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in node.EnumerateArray())
            {
                if (item.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                {
                    ScanJson(sourceFile, item, conversations, projects, memoryItems, seen, depth + 1);
                }
            }
        }
    }

    private static bool LooksLikeConversation(JsonElement node)
    {
        if (node.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var keys = node.EnumerateObject().Select(property => property.Name.ToLowerInvariant()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var messageLike = keys.Overlaps(new[] { "messages", "conversation", "chat", "turns" });
        var titleLike = keys.Overlaps(new[] { "title", "name", "chat_title", "conversation_title" });
        return messageLike && titleLike;
    }

    private static bool LooksLikeProject(JsonElement node)
    {
        if (node.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var keys = node.EnumerateObject().Select(property => property.Name.ToLowerInvariant()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hasProjectShape = keys.Overlaps(new[] { "instructions", "knowledge", "project_name", "description" });
        var titleLike = keys.Overlaps(new[] { "name", "title", "project_name" });
        return hasProjectShape && titleLike && !LooksLikeConversation(node);
    }

    private static bool LooksLikeMemory(JsonElement node)
    {
        if (node.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var keys = node.EnumerateObject().Select(property => property.Name.ToLowerInvariant()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var textLike = keys.Overlaps(new[] { "text", "value", "content", "instruction", "prompt", "memory" });
        var titleLike = keys.Overlaps(new[] { "title", "name", "label" });
        return textLike && titleLike && !LooksLikeConversation(node);
    }

    private static ConversationRecord NormalizeConversation(JsonElement raw, string sourceFile, int index)
    {
        var title = ClaudeExportHelpers.FirstText(raw, new[] { "title", "name", "chat_title", "conversation_title" }, $"Conversation {index + 1}");
        var projectName = ClaudeExportHelpers.FirstText(raw, new[] { "project_name", "project", "workspace", "folder", "collection" }, "General");
        var conversationId = ClaudeExportHelpers.FirstText(raw, new[] { "id", "conversation_id", "chat_id", "thread_id" }, $"{sourceFile}:{index + 1}");
        var messagesSource = FindMessagesSource(raw);
        var messages = new List<Dictionary<string, object?>>();
        if (messagesSource.HasValue && messagesSource.Value.ValueKind == JsonValueKind.Array)
        {
            var position = 0;
            foreach (var message in messagesSource.Value.EnumerateArray())
            {
                messages.Add(NormalizeMessage(message, position));
                position++;
            }
        }

        return new ConversationRecord(
            Title: title,
            Slug: PathUtils.Slugify(title, "conversation"),
            ProjectName: string.IsNullOrWhiteSpace(projectName) ? "General" : projectName,
            ConversationId: conversationId,
            SourceFile: sourceFile,
            Messages: messages);
    }

    private static ProjectRecord NormalizeProject(JsonElement raw, string sourceFile, int index)
    {
        var name = ClaudeExportHelpers.FirstText(raw, new[] { "name", "title", "project_name" }, $"Project {index + 1}");
        var instructions = ClaudeExportHelpers.FlattenValue(FindProperty(raw, "instructions", "system_prompt", "prompt", "instruction", "description"));
        var knowledgeSummary = ClaudeExportHelpers.FlattenValue(FindProperty(raw, "knowledge", "knowledge_summary", "notes", "files"));
        var conversationTitles = ExtractTitlesFromProject(raw);

        return new ProjectRecord(
            Name: name,
            Slug: PathUtils.Slugify(name, "project"),
            SourceFile: sourceFile,
            Instructions: instructions.Trim(),
            KnowledgeSummary: knowledgeSummary.Trim(),
            ConversationTitles: conversationTitles);
    }

    private static MemoryRecord NormalizeMemory(JsonElement raw, string sourceFile, int index)
    {
        var title = ClaudeExportHelpers.FirstText(raw, new[] { "title", "name", "label" }, $"Memory {index + 1}");
        var text = ClaudeExportHelpers.FlattenValue(FindProperty(raw, "text", "value", "content", "instruction", "prompt", "memory"));
        return new MemoryRecord(sourceFile, title, text.Trim());
    }

    private static JsonElement? FindMessagesSource(JsonElement raw)
    {
        foreach (var key in new[] { "messages", "conversation", "chat", "turns", "items" })
        {
            if (raw.TryGetProperty(key, out var value))
            {
                if (value.ValueKind == JsonValueKind.Object)
                {
                    foreach (var nestedKey in new[] { "messages", "items" })
                    {
                        if (value.TryGetProperty(nestedKey, out var nested))
                        {
                            return nested;
                        }
                    }
                }

                return value;
            }
        }

        return null;
    }

    private static JsonElement FindProperty(JsonElement node, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (node.TryGetProperty(key, out var value))
            {
                return value;
            }
        }

        return default;
    }

    private static IReadOnlyList<string> ExtractTitlesFromProject(JsonElement raw)
    {
        var titles = new List<string>();
        foreach (var key in new[] { "conversations", "related_conversations", "conversation_titles", "items" })
        {
            if (!raw.TryGetProperty(key, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in value.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object)
                    {
                        var title = ClaudeExportHelpers.FirstText(item, new[] { "title", "name", "conversation_title" });
                        if (!string.IsNullOrWhiteSpace(title))
                        {
                            titles.Add(title);
                        }
                    }
                    else if (item.ValueKind == JsonValueKind.String)
                    {
                        var text = item.GetString();
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            titles.Add(text);
                        }
                    }
                }
            }
        }

        return titles.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static Dictionary<string, object?> NormalizeMessage(JsonElement message, int position)
    {
        var result = new Dictionary<string, object?>
        {
            ["position"] = position,
            ["role"] = ClaudeExportHelpers.FirstText(message, new[] { "role", "speaker", "sender", "author" }, "user"),
            ["text"] = ClaudeExportHelpers.FlattenValue(FindProperty(message, "text", "content", "value", "message", "parts")),
        };

        foreach (var key in new[] { "name", "type", "author", "timestamp", "id", "message_id" })
        {
            var text = ClaudeExportHelpers.FirstText(message, new[] { key });
            if (!string.IsNullOrWhiteSpace(text))
            {
                result[key] = text;
            }
        }

        return result;
    }

    private static IReadOnlyList<string> AllTextFromRecords(
        IReadOnlyList<ConversationRecord> conversations,
        IReadOnlyList<ProjectRecord> projects,
        IReadOnlyList<MemoryRecord> memoryItems)
    {
        var texts = new List<string>();
        foreach (var conversation in conversations)
        {
            texts.Add(conversation.Title);
            texts.AddRange(conversation.Messages.Select(message => message.TryGetValue("text", out var text) ? text?.ToString() ?? string.Empty : string.Empty));
        }

        foreach (var project in projects)
        {
            texts.Add(project.Name);
            texts.Add(project.Instructions);
            texts.Add(project.KnowledgeSummary);
            texts.Add(project.SeedPrompt);
            texts.AddRange(project.ConversationTitles);
        }

        foreach (var item in memoryItems)
        {
            texts.Add(item.Title);
            texts.Add(item.Text);
        }

        return texts;
    }

    private static void AttachSummaries(
        IList<ConversationRecord> conversations,
        IList<ProjectRecord> projects,
        IReadOnlyList<MemoryRecord> memoryItems,
        IReadOnlyList<string> keywords)
    {
        var byProject = new Dictionary<string, List<ConversationRecord>>(StringComparer.OrdinalIgnoreCase);
        foreach (var conversation in conversations)
        {
            if (!byProject.TryGetValue(conversation.ProjectName ?? "General", out var list))
            {
                list = [];
                byProject[conversation.ProjectName ?? "General"] = list;
            }

            list.Add(conversation);
        }

        for (var index = 0; index < conversations.Count; index++)
        {
            var conversation = conversations[index];
            var summary = SummarizeConversation(conversation, keywords);
            var seedPrompt = BuildConversationSeedPrompt(conversation, keywords);
            conversations[index] = conversation with
            {
                Summary = summary,
                SeedPrompt = seedPrompt,
            };
        }

        for (var index = 0; index < projects.Count; index++)
        {
            var project = projects[index];
            byProject.TryGetValue(project.Name, out var relatedConversations);
            relatedConversations ??= [];
            var instructions = string.IsNullOrWhiteSpace(project.Instructions)
                ? BuildProjectInstructions(project.Name, relatedConversations, memoryItems, keywords)
                : project.Instructions;
            var knowledgeSummary = string.IsNullOrWhiteSpace(project.KnowledgeSummary)
                ? BuildProjectKnowledgeSummary(project.Name, relatedConversations, memoryItems, keywords)
                : project.KnowledgeSummary;
            var titles = project.ConversationTitles.Count > 0
                ? project.ConversationTitles
                : relatedConversations.Select(item => item.Title).ToList();
            var seedPrompt = string.IsNullOrWhiteSpace(project.SeedPrompt)
                ? BuildProjectSeedPrompt(project.Name, relatedConversations, keywords, memoryItems)
                : project.SeedPrompt;

            projects[index] = project with
            {
                Instructions = instructions,
                KnowledgeSummary = knowledgeSummary,
                ConversationTitles = titles,
                SeedPrompt = seedPrompt,
            };
        }
    }

    private static string SummarizeConversation(ConversationRecord conversation, IReadOnlyList<string> keywords)
    {
        var textMessages = conversation.Messages
            .Select(message => message.TryGetValue("text", out var value) ? value?.ToString() ?? string.Empty : string.Empty)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .ToList();
        var userMessages = conversation.Messages
            .Where(message => string.Equals(GetMessageValue(message, "role"), "user", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(GetMessageValue(message, "role"), "human", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(GetMessageValue(message, "role"), "prompt", StringComparison.OrdinalIgnoreCase))
            .Select(message => GetMessageValue(message, "text"))
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .ToList();

        if (textMessages.Count == 0)
        {
            return string.Empty;
        }

        var summary = new List<string>();
        if (keywords.Count > 0)
        {
            summary.Add("Themes: " + string.Join(", ", keywords.Take(8)));
        }

        var highlights = userMessages.Count > 0 ? userMessages.Take(2).ToList() : textMessages.Take(2).ToList();
        if (highlights.Count > 0)
        {
            summary.Add("Recent context:");
            summary.AddRange(highlights.Select(highlight => "- " + TruncateWhitespace(highlight, 280)));
        }

        if (textMessages.Count > highlights.Count)
        {
            summary.Add($"Total messages captured: {textMessages.Count}");
        }

        return string.Join(Environment.NewLine, summary).Trim();
    }

    private static string BuildProjectInstructions(
        string projectName,
        IReadOnlyList<ConversationRecord> conversations,
        IReadOnlyList<MemoryRecord> memoryItems,
        IReadOnlyList<string> keywords)
    {
        var seed = BuildProjectSeedPrompt(projectName, conversations, keywords, memoryItems);
        return string.Join(Environment.NewLine, new[]
        {
            $"Project: {projectName}",
            string.Empty,
            "Instructions derived from the source Claude export.",
            string.Empty,
            "Seed Prompt:",
            seed,
        }).Trim();
    }

    private static string BuildProjectKnowledgeSummary(
        string projectName,
        IReadOnlyList<ConversationRecord> conversations,
        IReadOnlyList<MemoryRecord> memoryItems,
        IReadOnlyList<string> keywords)
    {
        var lines = new List<string> { $"Knowledge summary for {projectName}." };
        if (keywords.Count > 0)
        {
            lines.Add("Keywords: " + string.Join(", ", keywords.Take(12)));
        }

        if (conversations.Count > 0)
        {
            lines.Add("Relevant conversations:");
            lines.AddRange(conversations.Take(10).Select(conversation => "- " + conversation.Title));
        }

        if (memoryItems.Count > 0)
        {
            lines.Add("Memory items:");
            lines.AddRange(memoryItems.Take(8).Select(item => "- " + item.Title));
        }

        return string.Join(Environment.NewLine, lines).Trim();
    }

    private static string BuildProjectSeedPrompt(
        string projectName,
        IReadOnlyList<ConversationRecord> conversations,
        IReadOnlyList<string> keywords,
        IReadOnlyList<MemoryRecord> memoryItems)
    {
        var parts = new List<string>
        {
            $"You are resuming the Claude project '{projectName}'.",
            "Use the prior instructions, memory context, and project knowledge to continue without losing decisions.",
        };

        if (keywords.Count > 0)
        {
            parts.Add("Key themes: " + string.Join(", ", keywords.Take(10)) + ".");
        }

        if (conversations.Count > 0)
        {
            parts.Add("Relevant conversation anchors:");
            parts.AddRange(conversations.Take(6).Select(conversation => $"- {conversation.Title}: {TruncateWhitespace(conversation.Summary, 240) ?? "continue from the latest state."}"));
        }

        if (memoryItems.Count > 0)
        {
            parts.Add("Memory anchors:");
            parts.AddRange(memoryItems.Take(5).Select(item => $"- {item.Title}: {TruncateWhitespace(item.Text.Replace(Environment.NewLine, " "), 180)}"));
        }

        parts.Add("Continue from the last unresolved question or task.");
        parts.Add("Preserve file names, project structure, and any explicit constraints.");
        return string.Join(Environment.NewLine, parts).Trim();
    }

    private static string BuildConversationSeedPrompt(ConversationRecord conversation, IReadOnlyList<string> keywords)
    {
        var lines = new List<string>
        {
            $"You are resuming the Claude conversation titled '{conversation.Title}'.",
            $"Project: {conversation.ProjectName}.",
        };

        if (keywords.Count > 0)
        {
            lines.Add("Key themes: " + string.Join(", ", keywords.Take(10)) + ".");
        }

        if (!string.IsNullOrWhiteSpace(conversation.Summary))
        {
            lines.Add("Summary: " + conversation.Summary);
        }

        lines.Add("Continue from the latest unresolved point.");
        lines.Add("Keep named files, code blocks, and decisions intact.");
        return string.Join(Environment.NewLine, lines).Trim();
    }

    private static string WriteMemoryBundle(string memoryRoot, ParsedClaudeExport parsed)
    {
        var path = Path.Combine(memoryRoot, "memory.json");
        var payload = new Dictionary<string, object?>
        {
            ["source_archive"] = parsed.SourceArchive,
            ["source_sha256"] = parsed.SourceSha256,
            ["keywords"] = parsed.TopKeywords.ToArray(),
            ["items"] = parsed.MemoryItems.Select(item => new Dictionary<string, object?>
            {
                ["source_file"] = item.SourceFile,
                ["title"] = item.Title,
                ["text"] = item.Text,
            }).ToArray(),
        };
        File.WriteAllText(path, JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8);
        return path;
    }

    private static IReadOnlyList<string> WriteProjectBundles(string projectsRoot, ParsedClaudeExport parsed, string bundleRoot)
    {
        var directories = new List<string>();
        var blueprints = new List<Dictionary<string, object?>>();
        foreach (var project in parsed.Projects)
        {
            var projectDir = Path.Combine(projectsRoot, project.Slug);
            Directory.CreateDirectory(projectDir);
            var instructionsPath = Path.Combine(projectDir, "instructions.md");
            var knowledgePath = Path.Combine(projectDir, "knowledge_summary.md");
            var blueprintPath = Path.Combine(projectDir, "import_blueprint.md");

            File.WriteAllText(instructionsPath, project.Instructions.Trim() + Environment.NewLine, Encoding.UTF8);
            File.WriteAllText(knowledgePath, project.KnowledgeSummary.Trim() + Environment.NewLine, Encoding.UTF8);
            File.WriteAllText(
                blueprintPath,
                string.Join(Environment.NewLine, new[]
                {
                    $"# {project.Name}",
                    string.Empty,
                    project.SeedPrompt,
                    string.Empty,
                    "## Conversations",
                    string.Join(Environment.NewLine, project.ConversationTitles.Select(title => "- " + title)),
                }).Trim() + Environment.NewLine,
                Encoding.UTF8);

            directories.Add(projectDir);
            blueprints.Add(new Dictionary<string, object?>
            {
                ["name"] = project.Name,
                ["slug"] = project.Slug,
                ["path"] = RelativePath(bundleRoot, projectDir),
            });
        }

        var manifestPath = Path.Combine(bundleRoot, "projects_manifest.json");
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(blueprints, JsonOptions), Encoding.UTF8);
        return directories;
    }

    private static IReadOnlyList<string> WriteConversationBundles(string conversationsRoot, ParsedClaudeExport parsed, string bundleRoot)
    {
        var directories = new List<string>();
        var blueprints = new List<Dictionary<string, object?>>();
        foreach (var conversation in parsed.Conversations)
        {
            var conversationDir = Path.Combine(conversationsRoot, conversation.Slug);
            Directory.CreateDirectory(conversationDir);
            var chatMarkdownPath = Path.Combine(conversationDir, "chat.md");
            var chatJsonPath = Path.Combine(conversationDir, "chat.json");

            File.WriteAllText(chatMarkdownPath, RenderConversationMarkdown(conversation), Encoding.UTF8);
            File.WriteAllText(chatJsonPath, JsonSerializer.Serialize(conversation, JsonOptions), Encoding.UTF8);

            directories.Add(conversationDir);
            blueprints.Add(new Dictionary<string, object?>
            {
                ["title"] = conversation.Title,
                ["slug"] = conversation.Slug,
                ["project_name"] = conversation.ProjectName,
                ["path"] = RelativePath(bundleRoot, conversationDir),
            });
        }

        var manifestPath = Path.Combine(bundleRoot, "conversations_manifest.json");
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(blueprints, JsonOptions), Encoding.UTF8);
        return directories;
    }

    private static int ExtractCodeFiles(ParsedClaudeExport parsed, string artifactRoot)
    {
        var extracted = 0;
        if (parsed.SourceMembers.Count == 0)
        {
            return extracted;
        }

        using var archive = ZipFile.OpenRead(parsed.SourceArchive);
        foreach (var member in parsed.CodeFiles)
        {
            var entry = archive.GetEntry(member);
            if (entry is null)
            {
                continue;
            }

            var relative = ClaudeExportHelpers.SafeZipMemberPath(member);
            var destination = Path.Combine(artifactRoot, "source", relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? ".");
            using var input = entry.Open();
            using var output = File.Create(destination);
            input.CopyTo(output);
            extracted++;
        }

        return extracted;
    }

    private static (string ManifestPath, Dictionary<string, object?> Manifest) WriteManifest(
        string bundleRoot,
        ParsedClaudeExport parsed,
        string memoryPath,
        IReadOnlyList<string> projectDirs,
        IReadOnlyList<string> conversationDirs,
        string artifactRoot,
        int extractedCount)
    {
        var projectBlueprints = parsed.Projects
            .Select((project, index) => new Dictionary<string, object?>
            {
                ["name"] = project.Name,
                ["slug"] = project.Slug,
                ["path"] = RelativePath(bundleRoot, projectDirs.ElementAt(index)),
            })
            .ToArray();

        var conversationBlueprints = parsed.Conversations
            .Select((conversation, index) => new Dictionary<string, object?>
            {
                ["title"] = conversation.Title,
                ["slug"] = conversation.Slug,
                ["project_name"] = conversation.ProjectName,
                ["path"] = RelativePath(bundleRoot, conversationDirs.ElementAt(index)),
            })
            .ToArray();

        var manifest = new Dictionary<string, object?>
        {
            ["format"] = "claude_portable_export",
            ["version"] = 1,
            ["bundle_name"] = Path.GetFileName(bundleRoot),
            ["created_at"] = PathUtils.TimestampTag(),
            ["source_archive"] = parsed.SourceArchive,
            ["source_sha256"] = parsed.SourceSha256,
            ["top_keywords"] = parsed.TopKeywords.ToArray(),
            ["paths"] = new Dictionary<string, object?>
            {
                ["memory"] = RelativePath(bundleRoot, memoryPath),
                ["projects"] = "projects",
                ["conversations"] = "conversations",
                ["artifacts"] = RelativePath(bundleRoot, artifactRoot),
            },
            ["project_blueprints"] = projectBlueprints,
            ["conversation_blueprints"] = conversationBlueprints,
            ["seed_prompts"] = parsed.Projects.Select(project => new Dictionary<string, object?>
            {
                ["project_name"] = project.Name,
                ["slug"] = project.Slug,
                ["conversation_titles"] = project.ConversationTitles.ToArray(),
                ["prompt"] = project.SeedPrompt,
            }).ToArray(),
            ["import_guides"] = BuildImportGuides(),
            ["source_members"] = parsed.SourceMembers.ToArray(),
            ["code_files"] = parsed.CodeFiles.ToArray(),
            ["counts"] = new Dictionary<string, object?>
            {
                ["conversations"] = parsed.Conversations.Count,
                ["projects"] = parsed.Projects.Count,
                ["memory_items"] = parsed.MemoryItems.Count,
                ["source_files"] = parsed.CodeFiles.Count,
                ["extracted_artifacts"] = extractedCount,
            },
        };

        var manifestPath = Path.Combine(bundleRoot, "manifest.json");
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, JsonOptions), Encoding.UTF8);
        return (manifestPath, manifest);
    }

    private static Dictionary<string, object?> BuildImportGuides()
    {
        return new Dictionary<string, object?>
        {
            ["claude"] = new[]
            {
                "Open the Claude account.",
                "Import the memory bundle and recreate the projects manually if the UI does not expose a direct import path.",
            },
            ["codex"] = new[]
            {
                "Use the local portable bundle.",
                "Copy the relevant project blueprints and seed prompts into ~/.codex on the target machine.",
            },
            ["copilot"] = new[]
            {
                "Copy the relevant instructions and summaries into your Copilot workflow.",
            },
        };
    }

    private static string RenderConversationMarkdown(ConversationRecord conversation)
    {
        var lines = new List<string>
        {
            $"# {conversation.Title}",
            string.Empty,
            $"Project: {conversation.ProjectName}",
            $"Conversation ID: {conversation.ConversationId}",
            string.Empty,
        };

        foreach (var message in conversation.Messages)
        {
            lines.Add($"## {GetMessageValue(message, "role")}");
            var text = GetMessageValue(message, "text");
            lines.Add(string.IsNullOrWhiteSpace(text) ? "_Empty_" : text);
            lines.Add(string.Empty);
        }

        return string.Join(Environment.NewLine, lines).Trim() + Environment.NewLine;
    }

    private static string AggregateText(IReadOnlyList<(string SourceFile, object Document)> jsonDocuments)
    {
        var fragments = new List<string>();
        foreach (var (sourceFile, document) in jsonDocuments)
        {
            fragments.Add($"Source: {sourceFile}");
            if (document is JsonElement element)
            {
                fragments.Add(ClaudeExportHelpers.FlattenValue(element));
            }
        }

        return string.Join(Environment.NewLine, fragments.Where(fragment => !string.IsNullOrWhiteSpace(fragment))).Trim();
    }

    private static string GetMessageValue(Dictionary<string, object?> message, string key)
    {
        if (!message.TryGetValue(key, out var value) || value is null)
        {
            return string.Empty;
        }

        return value switch
        {
            string text => text,
            JsonElement element => element.ValueKind == JsonValueKind.String ? element.GetString() ?? string.Empty : element.ToString(),
            _ => value.ToString() ?? string.Empty,
        };
    }

    private static string TruncateWhitespace(string? text, int limit)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var flattened = string.Join(" ", text.Split(new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries)).Trim();
        return flattened.Length <= limit ? flattened : flattened[..limit];
    }

    private static string RelativePath(string root, string path)
        => Path.GetRelativePath(root, path).Replace('\\', '/');
}
