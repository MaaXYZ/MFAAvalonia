using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace MFAAvalonia.Helper;

internal static class ContentResolver
{
    private static readonly string[] TextFileExtensions = [".md", ".markdown", ".txt", ".text", ".html", ".htm"];

    internal static async Task<string> ResolveAsync(string content, string projectDir,
        Func<string, Task<string>> fetchUrl, Action<string>? onMissingFile = null)
    {
        if (string.IsNullOrWhiteSpace(content))
            return string.Empty;

        if (Uri.TryCreate(content, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            return HasTextFileExtension(uri.AbsolutePath)
                ? await fetchUrl(content).ConfigureAwait(false)
                : $"[{content}]({content})";
        }

        // HTML and Markdown are valid inline content. Preserve the original
        // markup, including closing-tag slashes, without passing it to Path APIs.
        if (content.StartsWith("$", StringComparison.Ordinal) || IsInlineMarkup(content))
            return content;

        var projectRoot = Path.GetFullPath(projectDir);
        var expanded = content.Replace("{PROJECT_DIR}", projectRoot)
            .Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        var filePath = Path.Combine(projectRoot, expanded);
        if (File.Exists(filePath))
            return await File.ReadAllTextAsync(filePath).ConfigureAwait(false);

        // Resource projects authored on Windows may use different casing than
        // the files on disk. Keep this fallback confined to the project root.
        var compatibleFilePath = ResolveProjectFileIgnoringCase(filePath, projectRoot, content);
        if (compatibleFilePath != null)
            return await File.ReadAllTextAsync(compatibleFilePath).ConfigureAwait(false);

        // A failed optional probe is normal for inline text. Only a recognized
        // content-file reference warrants a missing-file diagnostic.
        if (HasTextFileExtension(content)
            || content.StartsWith("{PROJECT_DIR}", StringComparison.Ordinal)
            || expanded.StartsWith($".{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || expanded.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || Path.IsPathRooted(expanded))
            onMissingFile?.Invoke(filePath);

        return content;
    }

    private static bool HasTextFileExtension(string content) =>
        TextFileExtensions.Any(extension => content.EndsWith(extension, StringComparison.OrdinalIgnoreCase));

    private static bool IsInlineMarkup(string content) =>
        Regex.IsMatch(content,
            @"</?[a-z][^>]*>|<!--|!\[|\]\(|\[/?color[:\]]|[\r\n`]|^\s{0,3}(?:#{1,6}\s|>\s|[-+*]\s)",
            RegexOptions.IgnoreCase);

    private static string? ResolveProjectFileIgnoringCase(string filePath, string projectDir, string content)
    {
        if (!HasTextFileExtension(content))
            return null;

        var root = Path.GetFullPath(projectDir);
        var relativePath = Path.GetRelativePath(root, Path.GetFullPath(filePath));
        if (Path.IsPathRooted(relativePath)
            || relativePath.Equals("..", StringComparison.Ordinal)
            || relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            return null;

        var current = root;
        foreach (var segment in relativePath.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!Directory.Exists(current))
                return null;

            var exact = Path.Combine(current, segment);
            if (File.Exists(exact) || Directory.Exists(exact))
            {
                current = exact;
                continue;
            }

            current = Directory.EnumerateFileSystemEntries(current)
                .FirstOrDefault(entry => string.Equals(Path.GetFileName(entry), segment, StringComparison.OrdinalIgnoreCase));
            if (current == null)
                return null;
        }

        return File.Exists(current) ? current : null;
    }
}
