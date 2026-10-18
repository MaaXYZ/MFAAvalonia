using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MaaFramework.Binding;
using MFAAvalonia.Extensions.MaaFW;

namespace MFAAvalonia.Helper;

/// <summary>
/// 按文件大小轮转 MaaFramework 调试日志。
/// </summary>
public static class MaaLogRotator
{
    public const long MaxLogChunkBytes = 20L * 1024 * 1024;

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    private static readonly SemaphoreSlim RotateGate = new(1, 1);
    private static CancellationTokenSource? _cancellationTokenSource;
    private static Task? _pollTask;
    private static string? _activeLogDirectory;

    /// <summary>
    /// 启动日志轮转检查。
    /// </summary>
    public static void Start()
    {
        if (_pollTask is { IsCompleted: false })
            return;

        var cancellationTokenSource = new CancellationTokenSource();
        _cancellationTokenSource = cancellationTokenSource;
        _pollTask = Task.Run(async () =>
        {
            while (!cancellationTokenSource.IsCancellationRequested)
            {
                try
                {
                    TryRotateIfNeeded();
                }
                catch (Exception ex)
                {
                    LoggerHelper.Warning($"MaaFramework 日志轮转检查异常：{ex.Message}");
                }

                try
                {
                    await Task.Delay(PollInterval, cancellationTokenSource.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }, cancellationTokenSource.Token);
    }

    /// <summary>
    /// 停止日志轮转检查。
    /// </summary>
    public static void Stop()
    {
        var cancellationTokenSource = Interlocked.Exchange(ref _cancellationTokenSource, null);
        _pollTask = null;
        cancellationTokenSource?.Cancel();
    }

    /// <summary>
    /// 超过大小阈值时切换日志目录并保存当前日志。
    /// </summary>
    public static void TryRotateIfNeeded()
    {
        if (!RotateGate.Wait(0))
            return;

        try
        {
            var debugDirectory = Path.Combine(AppPaths.InstallRoot, "debug");
            var activeDirectory = _activeLogDirectory ?? debugDirectory;
            var activeLog = Path.Combine(activeDirectory, "maafw.log");
            if (!File.Exists(activeLog) || new FileInfo(activeLog).Length <= MaxLogChunkBytes)
                return;

            var tasker = FindAvailableTasker();
            if (tasker == null)
                return;

            RotateCore(tasker, debugDirectory, activeLog);
        }
        catch (Exception ex)
        {
            LoggerHelper.Warning($"MaaFramework 日志轮转失败：{ex.Message}");
        }
        finally
        {
            RotateGate.Release();
        }
    }

    private static MaaTasker? FindAvailableTasker()
    {
        if (!MaaProcessorManager.IsInstanceCreated)
            return null;

        return MaaProcessorManager.Instance.Instances
            .SelectMany(processor => new[] { processor.MaaTasker, processor.ScreenshotTasker })
            .FirstOrDefault(tasker => tasker != null);
    }

    private static void RotateCore(MaaTasker tasker, string debugDirectory, string activeLog)
    {
        var rotateDirectory = Path.Combine(AppPaths.TempMaaFwDirectory, $"rotate_{DateTime.Now:yyyyMMdd_HHmmss_fff}");
        var switchedToTemporaryDirectory = false;
        var switchedBackToDebugDirectory = false;

        try
        {
            Directory.CreateDirectory(rotateDirectory);
            LoggerHelper.Info($"MaaFramework 日志超过阈值，切换日志目录：{rotateDirectory}");

            if (!tasker.Global.SetOption_LogDir(rotateDirectory))
            {
                LoggerHelper.Warning("MaaFramework 日志轮转失败：切换到临时目录失败。");
                return;
            }
            switchedToTemporaryDirectory = true;

            var backupPath = MoveLogToBackup(activeLog, debugDirectory);
            if (tasker.Global.SetOption_LogDir(debugDirectory))
            {
                switchedBackToDebugDirectory = true;
                _activeLogDirectory = null;
                MergeTransitionLog(rotateDirectory, backupPath);
            }
            else
            {
                _activeLogDirectory = rotateDirectory;
                LoggerHelper.Warning($"MaaFramework 日志轮转后未能切回目录：{debugDirectory}");
            }

            LoggerHelper.Info("MaaFramework 日志轮转完成。");
        }
        catch (Exception ex)
        {
            LoggerHelper.Warning($"MaaFramework 日志轮转异常：{ex.Message}");
            if (switchedToTemporaryDirectory && !switchedBackToDebugDirectory)
                _activeLogDirectory = rotateDirectory;
        }
        finally
        {
            try
            {
                if (Directory.Exists(rotateDirectory)
                    && !string.Equals(_activeLogDirectory, rotateDirectory, StringComparison.Ordinal)
                    && !Directory.EnumerateFileSystemEntries(rotateDirectory).Any())
                {
                    Directory.Delete(rotateDirectory);
                }
            }
            catch (Exception ex)
            {
                LoggerHelper.Warning($"清理日志轮转临时目录失败：{ex.Message}");
            }
        }
    }

    private static void MergeTransitionLog(string rotateDirectory, string backupPath)
    {
        var transitionLog = Path.Combine(rotateDirectory, "maafw.log");
        if (!File.Exists(transitionLog) || new FileInfo(transitionLog).Length == 0)
            return;

        try
        {
            using (var appendStream = new FileStream(backupPath, FileMode.Append, FileAccess.Write, FileShare.Read))
            using (var sourceStream = new FileStream(transitionLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                sourceStream.CopyTo(appendStream);
            }

            File.Delete(transitionLog);
        }
        catch (Exception ex)
        {
            LoggerHelper.Warning($"合并 MaaFramework 轮转过渡日志失败：{ex.Message}");
        }
    }

    private static string MoveLogToBackup(string sourcePath, string targetDirectory)
    {
        var timestamp = DateTime.Now.ToString("yyyy.MM.dd-HH.mm.ss.fff");
        var backupPath = Path.Combine(targetDirectory, $"maafw.bak.{timestamp}.log");
        for (var suffix = 2; File.Exists(backupPath); suffix++)
            backupPath = Path.Combine(targetDirectory, $"maafw.bak.{timestamp}_{suffix}.log");

        File.Move(sourcePath, backupPath);
        LoggerHelper.Info($"已保存 MaaFramework 日志分卷：{Path.GetFileName(backupPath)}");
        return backupPath;
    }
}
