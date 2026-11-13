using System;
using System.Linq;

namespace MFAAvalonia.Extensions.MaaFW;

internal static class FocusContentPath
{
    // Focus accepts inline Markdown/HTML as well as file references. A slash or a
    // period in display text alone is not evidence of a missing content file.
    internal static bool LooksLikeFilePath(string input)
    {
        if (string.IsNullOrWhiteSpace(input)
            || input.IndexOfAny(['<', '>', '[', ']', '`', '|', '\r', '\n']) >= 0
            || input.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || input.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || input.StartsWith("$", StringComparison.Ordinal))
            return false;

        if (input.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
            || input.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase)
            || input.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
            || input.EndsWith(".text", StringComparison.OrdinalIgnoreCase))
            return true;

        // Recognize explicit paths on every platform, including Windows paths
        // when running on Unix. Relative file names may contain spaces.
        if (input.StartsWith("{PROJECT_DIR}", StringComparison.Ordinal)
            || input.StartsWith("/", StringComparison.Ordinal)
            || input.StartsWith("\\", StringComparison.Ordinal)
            || input.StartsWith("./", StringComparison.Ordinal)
            || input.StartsWith(".\\", StringComparison.Ordinal)
            || input.StartsWith("../", StringComparison.Ordinal)
            || input.StartsWith("..\\", StringComparison.Ordinal)
            || (input.Length >= 3 && char.IsAsciiLetter(input[0]) && input[1] == ':'
                && (input[2] == '/' || input[2] == '\\')))
            return true;

        return !input.Any(char.IsWhiteSpace) && (input.Contains('/') || input.Contains('\\'));
    }
}
