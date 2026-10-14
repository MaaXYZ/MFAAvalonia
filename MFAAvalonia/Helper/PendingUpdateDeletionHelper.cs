using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MFAAvalonia.Helper;

public static class PendingUpdateDeletionHelper
{
    private const string CacheFileName = ".pending_update_deletions.json";
    private static readonly object SyncRoot = new();

    private sealed class PendingDeletionEntry
    {
        [JsonProperty("root")]
        public string Root { get; set; } = "install";

        [JsonProperty("path")]
        public string RelativePath { get; set; } = string.Empty;
    }

    private static string CachePath => Path.Combine(AppPaths.InstallRoot, CacheFileName);

    public static void EnqueueDirectory(string relativePath)
    {
        var normalizedPath = NormalizeRelativePath(relativePath);
        var root = IsDataRootRelativePath(normalizedPath) ? "data" : "install";

        lock (SyncRoot)
        {
            var entries = LoadEntries();
            if (!entries.Any(entry =>
                    entry.Root.Equals(root, StringComparison.OrdinalIgnoreCase)
                    && entry.RelativePath.Equals(normalizedPath, StringComparison.OrdinalIgnoreCase)))
            {
                entries.Add(new PendingDeletionEntry { Root = root, RelativePath = normalizedPath });
            }

            SaveEntries(entries);
        }
    }

    public static void ProcessPendingDirectories()
    {
        try
        {
            lock (SyncRoot)
            {
                var entries = LoadEntries();
                if (entries.Count == 0)
                    return;

                var remaining = new List<PendingDeletionEntry>();
                foreach (var entry in entries.OrderByDescending(entry => GetPathDepth(entry.RelativePath)))
                {
                    try
                    {
                        var directoryPath = ResolveSafePath(entry);
                        // Only remove an empty directory. The update may have written new files to
                        // the same path after this entry was queued; recursively deleting here
                        // would remove those new files on the next launch.
                        if (Directory.Exists(directoryPath))
                            Directory.Delete(directoryPath, recursive: false);
                        LoggerHelper.Info($"已处理待删除更新目录：目录={directoryPath}");
                    }
                    catch (Exception ex)
                    {
                        remaining.Add(entry);
                        LoggerHelper.Warning($"启动时删除待处理更新目录失败，将保留到下次启动：路径={entry.RelativePath}，原因={ex.Message}");
                    }
                }

                SaveEntries(remaining);
            }
        }
        catch (Exception ex)
        {
            LoggerHelper.Warning($"处理待删除更新目录清单失败，已跳过本次清理：原因={ex.Message}");
        }
    }

    private static List<PendingDeletionEntry> LoadEntries()
    {
        if (!File.Exists(CachePath))
            return [];

        try
        {
            return JsonConvert.DeserializeObject<List<PendingDeletionEntry>>(File.ReadAllText(CachePath)) ?? [];
        }
        catch (Exception ex)
        {
            LoggerHelper.Warning($"读取待删除更新目录清单失败：文件={CachePath}，原因={ex.Message}");
            return [];
        }
    }

    private static void SaveEntries(List<PendingDeletionEntry> entries)
    {
        if (entries.Count == 0)
        {
            if (File.Exists(CachePath))
                File.Delete(CachePath);
            return;
        }

        var tempPath = CachePath + ".tmp";
        File.WriteAllText(tempPath, JsonConvert.SerializeObject(entries, Formatting.Indented));
        File.Move(tempPath, CachePath, overwrite: true);
    }

    private static string ResolveSafePath(PendingDeletionEntry entry)
    {
        var baseDirectory = entry.Root.Equals("data", StringComparison.OrdinalIgnoreCase)
            ? AppPaths.DataRoot
            : entry.Root.Equals("install", StringComparison.OrdinalIgnoreCase)
                ? AppPaths.InstallRoot
                : throw new InvalidDataException($"未知的待删除目录根类型：{entry.Root}");
        var normalizedBase = Path.GetFullPath(baseDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(baseDirectory, NormalizeRelativePath(entry.RelativePath)));
        if (!fullPath.StartsWith(normalizedBase, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"待删除目录越界：{entry.RelativePath}");
        return fullPath;
    }

    private static string NormalizeRelativePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            throw new InvalidDataException($"无效的待删除目录相对路径：{relativePath}");
        return relativePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .TrimStart(Path.DirectorySeparatorChar);
    }

    private static bool IsDataRootRelativePath(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/').TrimStart('/');
        return normalized.StartsWith("resource/", StringComparison.OrdinalIgnoreCase)
               || normalized.StartsWith("agent/", StringComparison.OrdinalIgnoreCase)
               || normalized.StartsWith("backup/", StringComparison.OrdinalIgnoreCase);
    }

    private static int GetPathDepth(string path) => path.Count(c => c == '/' || c == '\\');
}
