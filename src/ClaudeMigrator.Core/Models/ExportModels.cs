namespace ClaudeMigrator.Core.Models;

public sealed record ConversationRecord(
    string Title,
    string Slug,
    string ProjectName,
    string ConversationId,
    string SourceFile,
    IReadOnlyList<Dictionary<string, object?>> Messages,
    string Summary = "",
    string SeedPrompt = "");

public sealed record ProjectRecord(
    string Name,
    string Slug,
    string SourceFile,
    string Instructions,
    string KnowledgeSummary,
    IReadOnlyList<string> ConversationTitles,
    string SeedPrompt = "");

public sealed record MemoryRecord(
    string SourceFile,
    string Title,
    string Text);

public sealed record ParsedClaudeExport(
    string SourceArchive,
    string SourceSha256,
    IReadOnlyList<(string SourceFile, object Document)> JsonDocuments,
    IReadOnlyList<ConversationRecord> Conversations,
    IReadOnlyList<ProjectRecord> Projects,
    IReadOnlyList<MemoryRecord> MemoryItems,
    IReadOnlyList<string> SourceMembers,
    IReadOnlyList<string> TopKeywords,
    IReadOnlyList<string> CodeFiles);

public sealed record PortableExportResult(
    string SourceArchive,
    string BundleRoot,
    string ZipPath,
    string ManifestPath,
    string MemoryPath,
    IReadOnlyList<string> ProjectDirs,
    IReadOnlyList<string> ConversationDirs,
    string ArtifactRoot,
    Dictionary<string, object?> Manifest,
    IReadOnlyDictionary<string, int> Counts);

public sealed record LocalBundleResult(
    string SourceHome,
    string ProfileRoot,
    string? AccountFile,
    string BundleRoot,
    string ZipPath,
    string ManifestPath,
    string SourceEnvironmentPath,
    string? SourceAccountPath,
    string RestorePlanPath,
    IReadOnlyList<string> Targets,
    Dictionary<string, object?> Manifest,
    IReadOnlyDictionary<string, int> Counts);
