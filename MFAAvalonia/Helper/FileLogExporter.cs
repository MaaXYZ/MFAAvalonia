using Avalonia.Platform.Storage;
using MFAAvalonia.Extensions;
using MFAAvalonia.Extensions.MaaFW;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MFAAvalonia.Helper;

public static class FileLogExporter
{
    public const int MAX_LINES = 42000;
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
    public async static Task CompressRecentLogs(IStorageProvider? storageProvider)
    {
        if (!await ExportSemaphore.WaitAsync(0))
        {
            ToastHelper.Info(LangKeys.ExportLog.ToLocalization(), LangKeys.ExportLogInProgress.ToLocalization());
            return;
        }

        try
        {
            if (Instances.RootViewModel.IsRunning)
            {
                ToastHelper.Warn(
                    LangKeys.Warning.ToLocalization(),
                    LangKeys.StopTaskBeforeExportLog.ToLocalization());
                return;
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
                return;
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
                    return; // 用户取消了操作

                // 获取应用程序基目录
                string baseDirectory = AppContext.BaseDirectory;

                // 获取符合条件的日志文件和图片文件
                var eligibleFiles = await Task.Run(() => GetEligibleFiles(baseDirectory));

                if (!eligibleFiles.Any())
                {
                    LoggerHelper.Warning("未找到符合条件的日志文件或图片。");
                    ToastHelper.Warn(LangKeys.ExportLog.ToLocalization(), LangKeys.ExportLogNoEligibleFiles.ToLocalization());
                    return;
                }

                // 创建临时目录用于压缩
                var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
                Directory.CreateDirectory(tempDir);

                try
                {
                    await Task.Run(() =>
                    {
                        // 处理每个文件（日志/图片）
                        foreach (var file in eligibleFiles)
                        {
                            var destDir = Path.Combine(tempDir, file.RelativePath ?? string.Empty);
                            Directory.CreateDirectory(destDir);
                            var destPath = Path.Combine(destDir, Path.GetFileName(file.FullName ?? string.Empty));

                            if (file.IsImage)
                            {
                                // 图片文件：直接复制，无行数限制
                                File.Copy(file.FullName ?? string.Empty, destPath, overwrite: true);
                            }
                            else
                            {
                                // 日志文件：按行数限制处理
                                // if (file.LineCount > MAX_LINES)
                                // {
                                //     ExtractLastLines(file.FullName, destPath, MAX_LINES);
                                // }
                                // else
                                // {
                                File.Copy(file.FullName ?? string.Empty, destPath, overwrite: true);
                                // }
                            }
                        }
                    });

                    await using (var stream = await saveFile.OpenWriteAsync())
                    {
                        await Task.Run(() =>
                        {
                            using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
                            foreach (var file in Directory.GetFiles(tempDir, "*", SearchOption.AllDirectories))
                            {
                                var entryName = Path.GetRelativePath(tempDir, file).Replace('\\', '/');
                                archive.CreateEntryFromFile(file, entryName);
                            }
                        });
                    }

                    LoggerHelper.Info($"日志和图片已成功压缩到：\n{saveFile.Name}");
                    ToastHelper.Success(LangKeys.ExportLog.ToLocalization(), LangKeys.ExportLogSuccess.ToLocalization());
                }
                catch (Exception ex)
                {
                    LoggerHelper.Error($"压缩过程中发生错误：\n{ex}");
                    ToastHelper.Error(LangKeys.ExportLog.ToLocalization(), LangKeys.ExportLogFailed.ToLocalization());
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

    // 从日志文件末尾提取指定行数（图片文件不调用此方法）
    private static void ExtractLastLines(string sourcePath, string destPath, int lineCount)
    {
        try
        {
            var queue = new Queue<string>(lineCount);

            using (var stream = new FileStream(
                       sourcePath,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (queue.Count >= lineCount)
                        queue.Dequeue();
                    queue.Enqueue(line);
                }
            }

            using var writer = new StreamWriter(destPath, false, Encoding.UTF8);
            foreach (var line in queue)
                writer.WriteLine(line);
        }
        catch (Exception ex)
        {
            LoggerHelper.Error($"提取文件 {sourcePath} 的最后 {lineCount} 行时出错: {ex}");
            // 提取失败时尝试复制原始文件
            try { File.Copy(sourcePath, destPath, overwrite: true); }
            catch (Exception e)
            {
                LoggerHelper.Error(e);
            }
        }
    }
}

// 扩展文件信息类（支持区分图片/日志，记录行数）
public class FileInfoEx
{
    public string? FullName { get; set; } // 文件完整路径
    public string? RelativePath { get; set; } // 相对于应用基目录的路径
    public int LineCount { get; set; } // 行数（仅日志文件有效）
    public bool IsImage { get; set; } // 是否为图片文件
}
