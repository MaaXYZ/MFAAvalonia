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
            var link = $"[{content}]({content})";
            if (!HasTextFileExtension(uri.AbsolutePath))
                return link;

            var fetched = await fetchUrl(content).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(fetched) ? link : fetched;
        }

        // HTML and Markdown are valid inline content. Preserve the original
        // markup, including closing-tag slashes, without passing it to Path APIs.
        if (IsInlineMarkup(content))
            return content;

        var projectRoot = Path.GetFullPath(projectDir);
        var expanded = content.Replace("{PROJECT_DIR}", projectRoot)
            .Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        var filePath = Path.Combine(projectRoot, expanded);
        if (File.Exists(filePath))
            return await File.ReadAllTextAsync(filePath).ConfigureAwait(false);

        // Preserve the existing working-directory fallback for relative files.
        // Exact project files take precedence; explicit absolute/project paths
        // must not resolve to an unrelated file in the working directory.
        if (!Path.IsPathRooted(expanded) && File.Exists(expanded))
            return await File.ReadAllTextAsync(expanded).ConfigureAwait(false);

        // Resource projects authored on Windows may use different casing than
        // the files on disk. Keep this fallback confined to the project root.
        var compatibleFilePath = ResolveProjectFileIgnoringCase(filePath, projectRoot, content);
        if (compatibleFilePath != null)
            return await File.ReadAllTextAsync(compatibleFilePath).ConfigureAwait(false);

        // Localization has already run. Preserve existing dollar-prefixed files,
        // but do not warn for an unresolved localization key.
        if (content.StartsWith("$", StringComparison.Ordinal))
            return content;

        // A failed optional probe is normal for inline text. Only a recognized
        // content-file reference warrants a missing-file diagnostic.
        if (HasTextFileExtension(content)
            || content.StartsWith("{PROJECT_DIR}", StringComparison.Ordinal)
            || expanded.StartsWith($".{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || expanded.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || Path.IsPathRooted(expanded)
            || HasRelativePathSegments(expanded))
            onMissingFile?.Invoke(filePath);

        return content;
    }

    private static bool HasTextFileExtension(string content) =>
        TextFileExtensions.Any(extension => content.EndsWith(extension, StringComparison.OrdinalIgnoreCase));

    private static bool HasRelativePathSegments(string path)
    {
        var segments = path.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        // Spaces within a filename are valid (resource/my folder). Whitespace
        // beside a separator instead suggests prose such as "成功 / 失败".
        return segments.Length > 1 && segments.All(segment => segment == segment.Trim());
    }

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
