using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ClaudeMigrator.Core.Utilities;

public static class PathUtils
{
    public static string TimestampTag() => DateTimeOffset.Now.ToString("yyyyMMdd_HHmmss");

    public static string TimestampMinuteTag() => DateTimeOffset.Now.ToString("yyyyMMdd_HHmm");

    public static string Slugify(string? value, string fallback = "item", int maxLength = 120)
    {
        var text = (value ?? string.Empty).Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(text.Length);
        var lastWasHyphen = false;

        foreach (var ch in text)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
                lastWasHyphen = false;
                continue;
            }

            if (!lastWasHyphen && builder.Length > 0)
            {
                builder.Append('-');
                lastWasHyphen = true;
            }
        }

        var result = builder.ToString().Trim('-');
        if (string.IsNullOrWhiteSpace(result))
        {
            result = fallback;
        }

        return result.Length <= maxLength ? result : result[..maxLength].Trim('-');
    }

    public static string SafeFilename(string? value, string fallback = "file", int maxLength = 120)
        => Slugify(value, fallback, maxLength);

    public static DirectoryInfo EnsureDirectory(DirectoryInfo directory)
    {
        directory.Create();
        return directory;
    }

    public static DirectoryInfo EnsureDirectory(string path) => EnsureDirectory(new DirectoryInfo(path));

    public static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        var digest = SHA256.HashData(stream);
        return Convert.ToHexString(digest).ToLowerInvariant();
    }
}
