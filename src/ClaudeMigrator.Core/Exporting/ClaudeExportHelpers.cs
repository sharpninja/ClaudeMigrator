using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClaudeMigrator.Core.Models;
using ClaudeMigrator.Core.Utilities;

namespace ClaudeMigrator.Core.Exporting;

internal static class ClaudeExportHelpers
{
    internal static readonly HashSet<string> CodeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".c", ".cc", ".cpp", ".cs", ".css", ".go", ".h", ".hpp", ".html",
        ".java", ".js", ".json", ".jsx", ".kt", ".m", ".md", ".php", ".ps1",
        ".py", ".rb", ".rs", ".sh", ".sql", ".ts", ".tsx", ".txt", ".xml", ".yml", ".yaml",
    };

    internal static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "about", "after", "all", "also", "an", "and", "are", "as", "at",
        "be", "because", "been", "but", "by", "can", "could", "did", "do", "does",
        "for", "from", "had", "has", "have", "he", "her", "him", "his", "how",
        "i", "if", "in", "into", "is", "it", "its", "just", "like", "me", "more",
        "my", "not", "of", "on", "one", "or", "our", "out", "so", "than", "that",
        "the", "their", "them", "then", "there", "these", "they", "this", "to",
        "was", "we", "were", "what", "when", "where", "which", "who", "will",
        "with", "would", "you", "your",
    };

    internal static bool ShouldExtractAsCode(string member)
    {
        var lower = member.ToLowerInvariant();
        var suffix = System.IO.Path.GetExtension(lower);
        if (CodeExtensions.Contains(suffix))
        {
            return true;
        }

        return lower.Contains("code", StringComparison.OrdinalIgnoreCase)
            || lower.Contains("artifact", StringComparison.OrdinalIgnoreCase)
            || lower.Contains("script", StringComparison.OrdinalIgnoreCase)
            || lower.Contains("src", StringComparison.OrdinalIgnoreCase)
            || lower.Contains("source", StringComparison.OrdinalIgnoreCase);
    }

    internal static string SafeZipMemberPath(string member)
    {
        var parts = new List<string>();
        foreach (var part in member.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.IsNullOrWhiteSpace(part) || part is "." or "..")
            {
                continue;
            }

            parts.Add(PathUtils.SafeFilename(part, "item", 80));
        }

        if (parts.Count == 0)
        {
            parts.Add(PathUtils.SafeFilename(member, "artifact", 80));
        }

        return string.Join(System.IO.Path.DirectorySeparatorChar, parts);
    }

    internal static string FlattenValue(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.ToString(),
            JsonValueKind.Array => string.Join("\n", value.EnumerateArray().Select(FlattenValue).Where(item => !string.IsNullOrWhiteSpace(item))),
            JsonValueKind.Object => FlattenObject(value),
            _ => string.Empty,
        };
    }

    internal static string FlattenObject(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        foreach (var key in new[] { "text", "value", "content", "parts", "messages" })
        {
            if (value.TryGetProperty(key, out var nested))
            {
                var flattened = FlattenValue(nested);
                if (!string.IsNullOrWhiteSpace(flattened))
                {
                    return flattened;
                }
            }
        }

        var entries = new List<string>();
        foreach (var property in value.EnumerateObject())
        {
            if (property.NameEquals("raw") || property.NameEquals("metadata"))
            {
                continue;
            }

            var flattened = FlattenValue(property.Value);
            if (!string.IsNullOrWhiteSpace(flattened))
            {
                entries.Add(flattened);
            }
        }

        return string.Join("\n", entries);
    }

    internal static string FirstText(JsonElement node, IEnumerable<string> keys, string fallback = "")
    {
        foreach (var key in keys)
        {
            if (node.TryGetProperty(key, out var value))
            {
                var text = FlattenValue(value).Trim();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }
        }

        return fallback;
    }

    internal static string Fingerprint(params object?[] parts)
    {
        var payload = JsonSerializer.Serialize(parts, JsonUtils.SnakeCaseCompact);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    internal static IReadOnlyList<string> ExtractKeywords(IEnumerable<string> texts, int limit = 12)
    {
        var counter = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var text in texts)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            foreach (Match match in System.Text.RegularExpressions.Regex.Matches(text.ToLowerInvariant(), @"[a-z][a-z0-9_+-]{2,}"))
            {
                var token = match.Value;
                if (StopWords.Contains(token))
                {
                    continue;
                }

                counter.TryGetValue(token, out var count);
                counter[token] = count + 1;
            }
        }

        return counter
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .Select(pair => pair.Key)
            .ToList();
    }

    internal static IReadOnlyList<string> CountSourceTree(string root)
    {
        return Directory.Exists(root)
            ? Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).ToList()
            : [];
    }
}
