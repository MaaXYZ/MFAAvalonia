using Avalonia.Platform.Storage;
using MFAAvalonia.Extensions;
using MFAAvalonia.Extensions.MaaFW;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MFAAvalonia.Helper;

public static class FileLogExporter
{
    public const int MAX_LINES = 42000;
    private const long MaxArchivePayloadBytes = 23_750_000;
    private const long ZipEntryOverheadBytes = 128;
    private static readonly SemaphoreSlim ExportSemaphore = new(1, 1);
    // 定义需要处理的图片文件扩展名
    private static readonly string[] ImageExtensions =
    {
        ".jpg",
        ".jpeg",
        ".png",
        ".bmp",
        ".gif",
        ".webp"
    };
    private static readonly string ExcludedFolder = "vision";

    public async static Task<ExportLogResult> CompressRecentLogs(
        IStorageProvider? storageProvider,
        ExportLogPackageOptions? options = null)
    {
        if (!await ExportSemaphore.WaitAsync(0))
        {
            ToastHelper.Info(LangKeys.ExportLog.ToLocalization(), LangKeys.ExportLogInProgress.ToLocalization());
            return ExportLogResult.InProgress;
        }

        try
        {
            if (Instances.RootViewModel.IsRunning)
            {
                ToastHelper.Warn(
                    LangKeys.Warning.ToLocalization(),
                    LangKeys.StopTaskBeforeExportLog.ToLocalization());
                return ExportLogResult.Blocked;
            }

        // try
        // {
        //     MaaProcessorManager.Instance.Current.SetTasker();
        // }
        // catch (Exception ex)
        // {
        //     LoggerHelper.Error($"SetTasker failed before log export: {ex}");
        //     ToastHelper.Error(
        //         LangKeys.ExportLog.ToLocalization(),
        //         ex.Message);
        //     return;
        // }

            if (storageProvider == null)
            {
                ToastHelper.Error(LangKeys.ExportLog.ToLocalization(), LangKeys.ExportLogFailed.ToLocalization());
                LoggerHelper.Error("storageProvider is null!");
                return ExportLogResult.Failed;
            }

            try
            {
                // 获取用户选择的保存路径
                var saveFile = await storageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = LangKeys.ExportLog.ToLocalization(),
                    DefaultExtension = "zip",
                    SuggestedFileName = $"log_{DateTime.Now:yyyyMMdd_HHmmss}"
                });

                if (saveFile == null)
                    return ExportLogResult.Cancelled;

                // 获取应用程序基目录
                string baseDirectory = AppPaths.DataRoot;

                // 获取符合条件的日志文件和图片文件
                var eligibleFiles = await Task.Run(() => options == null
                    ? GetEligibleFiles(baseDirectory)
                    : GetEligibleFiles(baseDirectory, options));

                if (!eligibleFiles.Any())
                {
                    LoggerHelper.Warning("未找到符合条件的日志文件或图片。");
                    ToastHelper.Warn(LangKeys.ExportLog.ToLocalization(), LangKeys.ExportLogNoEligibleFiles.ToLocalization());
                    return ExportLogResult.NoEligibleFiles;
                }

                // 创建临时目录用于压缩
                var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
                Directory.CreateDirectory(tempDir);

                try
                {
                    var copiedCount = 0;
                    var skippedCount = 0;

                    await Task.Run(() =>
                    {
                        // 处理每个文件（日志/图片）
                        foreach (var file in eligibleFiles)
                        {
                            if (string.IsNullOrWhiteSpace(file.FullName))
                            {
                                skippedCount++;
                                continue;
                            }

                            try
                            {
                                var destDir = Path.Combine(tempDir, file.RelativePath ?? string.Empty);
                                Directory.CreateDirectory(destDir);
                                var destPath = Path.Combine(destDir, Path.GetFileName(file.FullName));

                                CopyFileSnapshot(file.FullName, destPath);
                                copiedCount++;
                            }
                            catch (Exception ex)
                            {
                                skippedCount++;
                                LoggerHelper.Warning($"导出日志时跳过文件（可能正在占用）: {file.FullName}\n{ex.Message}");
                            }
                        }
                    });

                    if (copiedCount == 0)
                    {
                        LoggerHelper.Warning("日志导出失败：没有任何文件成功复制到临时目录。");
                        ToastHelper.Error(LangKeys.ExportLog.ToLocalization(), LangKeys.ExportLogFailed.ToLocalization());
                        return ExportLogResult.Failed;
                    }

                    // Android's storage picker usually returns a content URI. It can write the
                    // selected document, but cannot create sibling files for split volumes.
                    var allowSplitArchives = saveFile.Path.IsFile;
                    var archiveFiles = await Task.Run(() => CreateArchiveVolumes(tempDir, allowSplitArchives));
                    await PublishArchiveVolumesAsync(saveFile, archiveFiles);

                    if (skippedCount > 0)
                    {
                        LoggerHelper.Warning($"日志导出完成，但有 {skippedCount} 个文件未导出（可能正在占用）。");
                    }
                    LoggerHelper.Info(archiveFiles.Count == 1
                        ? $"日志和图片已成功压缩到：\n{saveFile.Name}"
                        : $"日志和图片已成功压缩为 {archiveFiles.Count} 个分卷：\n{GetVolumeFileName(saveFile.Name, 1, archiveFiles.Count)}");
                    ToastHelper.Success(LangKeys.ExportLog.ToLocalization(), LangKeys.ExportLogSuccess.ToLocalization());
                    return ExportLogResult.Success;
                }
                catch (Exception ex)
                {
                    LoggerHelper.Error($"压缩过程中发生错误：\n{ex}");
                    ToastHelper.Error(LangKeys.ExportLog.ToLocalization(), LangKeys.ExportLogFailed.ToLocalization());
                    return ExportLogResult.Failed;
                }
                finally
                {
                    // 清理临时目录
                    try { Directory.Delete(tempDir, true); }
                    catch
                    {
                        /* 忽略清理错误 */
                    }
                }
            }
            catch (Exception ex)
            {
                LoggerHelper.Error($"发生错误：\n{ex}");
                ToastHelper.Error(LangKeys.ExportLog.ToLocalization(), LangKeys.ExportLogFailed.ToLocalization());
                return ExportLogResult.Failed;
            }
        }
        finally
        {
            ExportSemaphore.Release();
        }
    }

    // 获取符合条件的文件（日志+图片）
    private static List<FileInfoEx> GetEligibleFiles(string baseDirectory)
    {
        var eligibleFiles = new List<FileInfoEx>();
        var twoDaysAgo = DateTime.Now.AddDays(-5); // 日期限制：仅保留两天内的文件

        // 1. 获取日志文件（.log 和 .txt）
        var debugDir = Path.Combine(baseDirectory, "debug");
        var logFiles = Directory.Exists(debugDir)
            ? Directory.GetFiles(debugDir, "*.log", SearchOption.AllDirectories)
                .Where(file => !file.Contains(ExcludedFolder, StringComparison.OrdinalIgnoreCase)) // 排除vision路径
            : [];

        var logsDir = Path.Combine(baseDirectory, "logs");
        var txtFiles = Directory.Exists(logsDir)
            ? Directory.GetFiles(logsDir, "*.log", SearchOption.AllDirectories)
            : [];

        // 2. 获取 debug 目录下的图片文件（指定扩展名）
        var imageFiles = Directory.Exists(debugDir)
            ? Directory.GetFiles(debugDir, "*.*", SearchOption.AllDirectories)
                .Where(file =>
                    ImageExtensions.Contains(Path.GetExtension(file).ToLowerInvariant()) && !file.Contains(ExcludedFolder, StringComparison.OrdinalIgnoreCase)) // 排除vision路径
            : [];


        // 合并所有文件并处理
        var allFiles = logFiles.Concat(txtFiles).Concat(imageFiles).Distinct().ToArray();

        foreach (var file in allFiles)
        {
            try
            {
                var fileInfo = new FileInfo(file);

                // 过滤：仅保留两天内修改的文件
                if (fileInfo.LastWriteTime < twoDaysAgo)
                    continue;

                // 计算相对路径（相对于应用基目录）
                var relativePath = (Path.GetDirectoryName(file) ?? string.Empty)
                    .Replace(baseDirectory, "")
                    .TrimStart(Path.DirectorySeparatorChar);

                // 判断是否为图片文件
                var isImage = ImageExtensions.Contains(Path.GetExtension(file).ToLowerInvariant());

                // 日志文件需要计算行数，图片文件无需计算
                var lineCount = isImage ? 0 : CountLines(file);

                eligibleFiles.Add(new FileInfoEx
                {
                    FullName = file,
                    RelativePath = relativePath,
                    LineCount = lineCount,
                    IsImage = isImage
                });
            }
            catch (Exception ex)
            {
                LoggerHelper.Error($"处理文件 {file} 时出错: {ex}");
                // 继续处理其他文件
            }
        }

        return eligibleFiles;
    }

    private static List<FileInfoEx> GetEligibleFiles(string baseDirectory, ExportLogPackageOptions options)
    {
        var eligibleFiles = new List<FileInfoEx>();
        var candidateFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var debugDir = Path.Combine(baseDirectory, "debug");
        var logsDir = Path.Combine(baseDirectory, "logs");

        if (options.IncludeMaaLog)
        {
            foreach (var file in GetMaaLogFiles(debugDir))
                candidateFiles.Add(file);
        }

        if (options.IncludeGuiLog && Directory.Exists(logsDir))
        {
            foreach (var file in Directory.GetFiles(logsDir, "*.log", SearchOption.AllDirectories))
                candidateFiles.Add(file);
        }

        if (options.IncludeCustomLog)
        {
            foreach (var file in GetCustomLogFiles(baseDirectory))
                candidateFiles.Add(file);
        }

        if (Directory.Exists(debugDir))
        {
            foreach (var file in Directory.GetFiles(debugDir, "*.*", SearchOption.AllDirectories)
                         .Where(IsImageFile))
            {
                if (!ShouldIncludeImage(file, options))
                    continue;

                candidateFiles.Add(file);
            }
        }

        foreach (var file in candidateFiles)
        {
            TryAddEligibleFile(eligibleFiles, baseDirectory, file);
        }

        return eligibleFiles;
    }

    private static IEnumerable<string> GetMaaLogFiles(string debugDir)
    {
        if (!Directory.Exists(debugDir))
            return [];

        var allFiles = Directory.GetFiles(debugDir, "*", SearchOption.AllDirectories);
        var namedMaaLogs = allFiles.Where(file =>
        {
            var fileName = Path.GetFileName(file);
            return fileName.StartsWith("maa.log", StringComparison.OrdinalIgnoreCase)
                   || fileName.StartsWith("maafw.log", StringComparison.OrdinalIgnoreCase);
        });

        if (namedMaaLogs.Any())
            return namedMaaLogs;

        return allFiles.Where(file =>
            Path.GetExtension(file).Equals(".log", StringComparison.OrdinalIgnoreCase)
            && !Path.GetFileName(file).StartsWith("custom.log", StringComparison.OrdinalIgnoreCase)
            && !IsPathUnderFolder(file, ExcludedFolder));
    }

    private static IEnumerable<string> GetCustomLogFiles(string baseDirectory)
    {
        if (!Directory.Exists(baseDirectory))
            return [];

        return Directory.GetFiles(baseDirectory, "*", SearchOption.AllDirectories)
            .Where(file => Path.GetFileName(file).StartsWith("custom.log", StringComparison.OrdinalIgnoreCase));
    }

    private static bool ShouldIncludeImage(string file, ExportLogPackageOptions options)
    {
        var category = GetImageCategory(file);
        var fileInfo = new FileInfo(file);
        var now = DateTime.Now;

        return category switch
        {
            ExportImageCategory.OnError => options.IncludeOnErrorImages
                && IsWithinTimeRange(fileInfo.LastWriteTime, options.OnErrorImageTimeRange, now),
            ExportImageCategory.Vision => options.IncludeVisionImages
                && IsWithinTimeRange(fileInfo.LastWriteTime, options.VisionImageTimeRange, now),
            ExportImageCategory.Other => options.IncludeOtherImages
                && IsWithinTimeRange(fileInfo.LastWriteTime, options.OtherImageTimeRange, now),
            _ => false
        };
    }

    private static ExportImageCategory GetImageCategory(string file)
    {
        if (IsPathUnderFolder(file, "on_error"))
            return ExportImageCategory.OnError;

        if (IsPathUnderFolder(file, "vision"))
            return ExportImageCategory.Vision;

        return ExportImageCategory.Other;
    }

    private static bool IsWithinTimeRange(DateTime lastWriteTime, ExportLogTimeRange range, DateTime now)
    {
        var cutoff = range switch
        {
            ExportLogTimeRange.All => (DateTime?)null,
            ExportLogTimeRange.Last24Hours => now.AddDays(-1),
            ExportLogTimeRange.Last3Days => now.AddDays(-3),
            ExportLogTimeRange.Last7Days => now.AddDays(-7),
            _ => null
        };

        return !cutoff.HasValue || lastWriteTime >= cutoff.Value;
    }

    private static bool IsImageFile(string file)
    {
        return ImageExtensions.Contains(Path.GetExtension(file).ToLowerInvariant());
    }

    private static bool IsPathUnderFolder(string file, string folderName)
    {
        var directory = Path.GetDirectoryName(file);
        if (string.IsNullOrWhiteSpace(directory))
            return false;

        return directory
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => segment.Equals(folderName, StringComparison.OrdinalIgnoreCase));
    }

    private static void TryAddEligibleFile(List<FileInfoEx> eligibleFiles, string baseDirectory, string file)
    {
        try
        {
            var fileInfo = new FileInfo(file);
            var relativePath = (Path.GetDirectoryName(file) ?? string.Empty)
                .Replace(baseDirectory, "")
                .TrimStart(Path.DirectorySeparatorChar);
            var isImage = IsImageFile(file);
            var lineCount = isImage ? 0 : CountLines(file);

            eligibleFiles.Add(new FileInfoEx
            {
                FullName = file,
                RelativePath = relativePath,
                LineCount = lineCount,
                IsImage = isImage
            });
        }
        catch (Exception ex)
        {
            LoggerHelper.Error($"处理文件 {file} 时出错: {ex}");
        }
    }

    // 计算日志文件行数（图片文件不调用此方法）
    private static int CountLines(string filePath)
    {
        try
        {
            using var stream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            using var reader = new StreamReader(stream);
            int count = 0;

            // 限制最大计数，避免超大文件占用过多内存
            while (reader.ReadLine() != null && count <= MAX_LINES + 1)
                count++;

            return count;
        }
        catch (FileNotFoundException)
        {
            LoggerHelper.Warning($"文件不存在: {filePath}");
            return int.MaxValue;
        }
        catch (UnauthorizedAccessException)
        {
            LoggerHelper.Warning($"无权访问文件: {filePath}");
            return int.MaxValue;
        }
        catch (Exception ex)
        {
            LoggerHelper.Error($"读取文件失败: {filePath}", ex);
            return int.MaxValue;
        }
    }

    // 尝试以共享方式读取文件快照，降低“文件占用导致复制失败”的概率
    private static void CopyFileSnapshot(string sourcePath, string destPath)
    {
        using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var destination = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);
        source.CopyTo(destination);
    }

    private static List<string> CreateArchiveVolumes(string sourceDirectory, bool allowSplitArchives)
    {
        var files = Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories)
            .OrderBy(file => Path.GetRelativePath(sourceDirectory, file), StringComparer.Ordinal)
            .ToArray();
        var archiveDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(archiveDirectory);

        try
        {
            var groups = allowSplitArchives
                ? GroupFilesIntoVolumes(sourceDirectory, files)
                : [files.ToList()];
            if (groups.Count == 1)
            {
                var singleArchive = Path.Combine(archiveDirectory, "archive.zip");
                CreateArchive(singleArchive, sourceDirectory, groups[0]);
                return [singleArchive];
            }

            var width = groups.Count >= 100 ? 3 : 2;
            var volumes = new List<string>(groups.Count);

            for (var index = 0; index < groups.Count; index++)
            {
                var volumePath = Path.Combine(
                    archiveDirectory,
                    $"archive-part{(index + 1).ToString($"D{width}")}.zip");
                CreateArchive(volumePath, sourceDirectory, groups[index]);
                volumes.Add(volumePath);
            }

            return volumes;
        }
        catch
        {
            try { Directory.Delete(archiveDirectory, true); }
            catch { /* ignore cleanup errors */ }
            throw;
        }
    }

    private static List<List<string>> GroupFilesIntoVolumes(string sourceDirectory, IEnumerable<string> files)
    {
        var groups = new List<List<string>>();
        var currentGroup = new List<string>();
        long currentSize = 22;

        foreach (var file in files)
        {
            var entryName = Path.GetRelativePath(sourceDirectory, file).Replace('\\', '/');
            // Images are already compressed. Using their source size is both a reliable
            // volume estimate and avoids a full throw-away Deflate pass for every file.
            var estimatedSize = new FileInfo(file).Length
                                + ZipEntryOverheadBytes
                                + System.Text.Encoding.UTF8.GetByteCount(entryName) * 2L;

            if (currentGroup.Count > 0 && currentSize + estimatedSize > MaxArchivePayloadBytes)
            {
                groups.Add(currentGroup);
                currentGroup = [];
                currentSize = 22;
            }

            currentGroup.Add(file);
            currentSize += estimatedSize;
        }

        if (currentGroup.Count > 0)
            groups.Add(currentGroup);

        return groups;
    }

    private static void CreateArchive(string archivePath, string sourceDirectory, IEnumerable<string> files)
    {
        using var stream = new FileStream(archivePath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var file in files)
        {
            var entryName = Path.GetRelativePath(sourceDirectory, file).Replace('\\', '/');
            archive.CreateEntryFromFile(
                file,
                entryName,
                IsImageFile(file) ? CompressionLevel.NoCompression : CompressionLevel.Fastest);
        }
    }

    private static async Task PublishArchiveVolumesAsync(IStorageFile saveFile, IReadOnlyList<string> archiveFiles)
    {
        var archiveDirectory = Path.GetDirectoryName(archiveFiles[0]);
        try
        {
            if (archiveFiles.Count == 1)
            {
                await using var source = new FileStream(archiveFiles[0], FileMode.Open, FileAccess.Read, FileShare.Read);
                await using var destination = await saveFile.OpenWriteAsync();
                await source.CopyToAsync(destination);
                await destination.FlushAsync();
                return;
            }

            if (!saveFile.Path.IsFile)
                throw new IOException("The selected storage provider does not support writing split archives.");

            var selectedPath = saveFile.Path.LocalPath;
            var outputDirectory = Path.GetDirectoryName(selectedPath)
                ?? throw new IOException("Unable to determine the export directory.");
            var createdFiles = new List<string>();

            try
            {
                for (var index = 0; index < archiveFiles.Count; index++)
                {
                    var outputPath = Path.Combine(
                        outputDirectory,
                        GetVolumeFileName(saveFile.Name, index + 1, archiveFiles.Count));
                    File.Copy(archiveFiles[index], outputPath, overwrite: true);
                    createdFiles.Add(outputPath);
                }

                if (File.Exists(selectedPath))
                    File.Delete(selectedPath);
            }
            catch
            {
                foreach (var createdFile in createdFiles)
                {
                    try { File.Delete(createdFile); }
                    catch { /* ignore cleanup errors */ }
                }
                throw;
            }
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(archiveDirectory))
            {
                try { Directory.Delete(archiveDirectory, true); }
                catch { /* ignore cleanup errors */ }
            }
        }
    }

    private static string GetVolumeFileName(string selectedFileName, int volumeNumber, int volumeCount)
    {
        var baseName = Path.GetFileNameWithoutExtension(selectedFileName);
        var width = volumeCount >= 100 ? 3 : 2;
        return $"{baseName}-part{volumeNumber.ToString($"D{width}")}.zip";
    }

}

public sealed class ExportLogPackageOptions
{
    public bool IncludeMaaLog { get; init; } = true;
    public bool IncludeGuiLog { get; init; } = true;
    public bool IncludeCustomLog { get; init; } = true;
    public bool IncludeOnErrorImages { get; init; } = true;
    public bool IncludeVisionImages { get; init; }
    public bool IncludeOtherImages { get; init; } = true;
    public ExportLogTimeRange OnErrorImageTimeRange { get; init; } = ExportLogTimeRange.All;
    public ExportLogTimeRange VisionImageTimeRange { get; init; } = ExportLogTimeRange.All;
    public ExportLogTimeRange OtherImageTimeRange { get; init; } = ExportLogTimeRange.All;
}

public enum ExportLogTimeRange
{
    All,
    Last24Hours,
    Last3Days,
    Last7Days
}

public enum ExportLogResult
{
    Success,
    Cancelled,
    NoEligibleFiles,
    Failed,
    Blocked,
    InProgress
}

public enum ExportImageCategory
{
    OnError,
    Vision,
    Other
}

// 扩展文件信息类（支持区分图片/日志，记录行数）
public class FileInfoEx
{
    public string? FullName { get; set; } // 文件完整路径
    public string? RelativePath { get; set; } // 相对于应用基目录的路径
    public int LineCount { get; set; } // 行数（仅日志文件有效）
    public bool IsImage { get; set; } // 是否为图片文件
}
