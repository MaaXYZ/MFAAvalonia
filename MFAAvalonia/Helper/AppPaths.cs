using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MFAAvalonia.Helper;

public static class AppPaths
{
    private static bool _initialized;
    private static string _configDirectory = string.Empty;
    private static string _logsDirectory = string.Empty;
    private static string _tempDirectory = string.Empty;

    public static string InstallRoot => AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    public static string DataRoot { get; private set; } = InstallRoot;
    public static string ConfigDirectory => string.IsNullOrWhiteSpace(_configDirectory)
        ? Path.Combine(InstallRoot, "config")
        : _configDirectory;
    public static string InstancesDirectory => Path.Combine(ConfigDirectory, "instances");
    public static string ResourceDirectory => Path.Combine(DataRoot, "resource");
    public static string AgentDirectory => Path.Combine(DataRoot, "agent");
    public static string LogsDirectory => string.IsNullOrWhiteSpace(_logsDirectory)
        ? Path.Combine(InstallRoot, "logs")
        : _logsDirectory;
    public static string TempDirectory => string.IsNullOrWhiteSpace(_tempDirectory)
        ? Path.Combine(InstallRoot, "temp")
        : _tempDirectory;
    public static string TempResourceDirectory => Path.Combine(TempDirectory, "temp_res");
    public static string TempMfaDirectory => Path.Combine(TempDirectory, "temp_mfa");
    public static string TempMaaFwDirectory => Path.Combine(TempDirectory, "temp_maafw");
    public static string InterfaceJsonPath => Path.Combine(DataRoot, "interface.json");
    public static string InterfaceJsoncPath => Path.Combine(DataRoot, "interface.jsonc");
    public static string GlobalConfigPath => Path.Combine(Path.GetDirectoryName(ConfigDirectory) ?? InstallRoot, "appsettings.json");
    public static string ChangesPath => Path.Combine(DataRoot, "changes.json");
    public static string BackupDirectory => Path.Combine(DataRoot, "backup");
    public static bool IsUsingIndependentDataRoot => !string.Equals(DataRoot, InstallRoot, StringComparison.OrdinalIgnoreCase);
    public static bool IsUsingLocalConfigDirectory => string.Equals(
        ConfigDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
        Path.Combine(InstallRoot, "config").TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
        StringComparison.OrdinalIgnoreCase);

    public static void Initialize()
    {
        if (_initialized)
            return;

        DataRoot = InstallRoot;
        _configDirectory = ResolveConfigDirectory();
        _logsDirectory = ResolveLogsDirectory();
        _tempDirectory = ResolveTempDirectory();

        Directory.CreateDirectory(ConfigDirectory);
        Directory.CreateDirectory(InstancesDirectory);
        Directory.CreateDirectory(ResourceDirectory);
        Directory.CreateDirectory(AgentDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(TempDirectory);
        Directory.CreateDirectory(BackupDirectory);

        LogInitializationStatus();

        _initialized = true;
    }

    private static string ResolveConfigDirectory()
    {
        var installConfig = Path.Combine(InstallRoot, "config");
        if (CanUseDirectory(installConfig))
            return installConfig;

        return installConfig;
    }

    private static string ResolveLogsDirectory()
    {
        var installLogs = Path.Combine(InstallRoot, "logs");
        if (CanUseDirectory(installLogs))
            return installLogs;

        return installLogs;
    }

    private static string ResolveTempDirectory()
    {
        var installTemp = Path.Combine(InstallRoot, "temp");
        if (CanUseDirectory(installTemp))
            return installTemp;

        return installTemp;
    }

    private static bool CanUseDirectory(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            var probePath = Path.Combine(path, ".write_probe");
            File.WriteAllText(probePath, DateTime.UtcNow.ToString("O"));
            File.Delete(probePath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void LogInitializationStatus()
    {
        try
        {
            LoggerHelper.Info($"应用安装目录：{InstallRoot}");
            LoggerHelper.Info($"应用数据目录：{DataRoot}");
            LoggerHelper.Info($"是否使用独立数据目录：{IsUsingIndependentDataRoot}");
            LoggerHelper.Info($"配置目录：{ConfigDirectory}");
            LoggerHelper.Info($"是否使用本地配置目录：{IsUsingLocalConfigDirectory}");
            LoggerHelper.Info($"日志目录：{LogsDirectory}");

            var backupCount = Directory.Exists(DataRoot)
                ? Directory.EnumerateFiles(DataRoot, "*.backupMFA", SearchOption.AllDirectories).Count()
                : 0;
            var tempDirExists = Directory.Exists(TempDirectory);
            LoggerHelper.Info($"备份文件数量：{backupCount}");
            LoggerHelper.Info($"临时目录是否存在：{tempDirExists}");
        }
        catch
        {
            // Avoid breaking startup for diagnostics.
        }
    }

    public static void CleanupObsoleteExecutableBackups(Action<string>? logInfo = null, Action<string>? logWarning = null)
    {
        try
        {
            if (!Directory.Exists(InstallRoot))
                return;

            foreach (var backupFile in Directory.EnumerateFiles(InstallRoot, "*.backupMFA", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    File.SetAttributes(backupFile, FileAttributes.Normal);
                    File.Delete(backupFile);
                    logInfo?.Invoke($"已清理 backupMFA 文件：文件={backupFile}");
                }
                catch (Exception ex)
                {
                    logWarning?.Invoke($"清理 backupMFA 文件失败：文件={backupFile}，原因={ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            logWarning?.Invoke($"处理旧主程序 backupMFA 清理失败：原因={ex.Message}");
        }
    }

    /// <summary>
    /// 清理过期的 MaaFramework 调试日志和截图，并限制调试目录占用。
    /// </summary>
    public static void CleanupOldDebugLogs(int retainDays = 3, Action<string>? logInfo = null, Action<string>? logWarning = null)
    {
        try
        {
            var debugDirectory = Path.Combine(InstallRoot, "debug");
            if (!Directory.Exists(debugDirectory))
                return;

            var cutoff = DateTime.Now.AddDays(-retainDays);
            var activeLog = Path.Combine(debugDirectory, "maafw.log");
            if (File.Exists(activeLog) && new FileInfo(activeLog).Length > 0)
            {
                try
                {
                    var backupName = $"maafw.bak.{DateTime.Now:yyyy.MM.dd-HH.mm.ss.fff}.log";
                    File.Move(activeLog, Path.Combine(debugDirectory, backupName));
                    logInfo?.Invoke($"已轮转调试日志：{backupName}");
                }
                catch (Exception ex)
                {
                    logWarning?.Invoke($"轮转调试日志失败：原因={ex.Message}");
                }
            }

            foreach (var file in Directory.EnumerateFiles(debugDirectory, "maafw.bak.*.log", SearchOption.AllDirectories))
            {
                try
                {
                    if (File.GetLastWriteTime(file) >= cutoff)
                        continue;

                    File.SetAttributes(file, FileAttributes.Normal);
                    File.Delete(file);
                    logInfo?.Invoke($"已清理过期调试日志：文件={Path.GetFileName(file)}");
                }
                catch (Exception ex)
                {
                    logWarning?.Invoke($"清理调试日志失败：文件={file}，原因={ex.Message}");
                }
            }

            var directorySize = Directory.EnumerateFiles(debugDirectory, "*", SearchOption.AllDirectories)
                .Sum(file =>
                {
                    try { return new FileInfo(file).Length; }
                    catch { return 0L; }
                });
            if (directorySize > 500L * 1024 * 1024)
            {
                DeleteExcessFiles(
                    Directory.EnumerateFiles(debugDirectory, "maafw.bak.*.log", SearchOption.TopDirectoryOnly),
                    10,
                    "debug 目录超过 500 MiB，已清理备份日志",
                    logInfo,
                    logWarning);

                var errorDirectory = Path.Combine(debugDirectory, "on_error");
                if (Directory.Exists(errorDirectory))
                {
                    DeleteExcessFiles(
                        Directory.EnumerateFiles(errorDirectory, "*.png", SearchOption.TopDirectoryOnly),
                        50,
                        "debug 目录超过 500 MiB，已清理错误截图",
                        logInfo,
                        logWarning);
                }
            }

            foreach (var pattern in new[] { "*.png", "*.jpg", "*.jpeg" })
            {
                foreach (var file in Directory.EnumerateFiles(debugDirectory, pattern, SearchOption.AllDirectories))
                {
                    try
                    {
                        if (File.GetLastWriteTime(file) >= cutoff)
                            continue;

                        File.SetAttributes(file, FileAttributes.Normal);
                        File.Delete(file);
                    }
                    catch (Exception ex)
                    {
                        logWarning?.Invoke($"清理调试截图失败：文件={file}，原因={ex.Message}");
                    }
                }
            }

            logInfo?.Invoke("调试文件清理完成");
        }
        catch (Exception ex)
        {
            logWarning?.Invoke($"清理调试日志异常：原因={ex.Message}");
        }
    }

    private static void DeleteExcessFiles(
        IEnumerable<string> files,
        int retainCount,
        string message,
        Action<string>? logInfo,
        Action<string>? logWarning)
    {
        foreach (var file in files.OrderByDescending(file => file).Skip(retainCount))
        {
            try
            {
                File.SetAttributes(file, FileAttributes.Normal);
                File.Delete(file);
                logInfo?.Invoke($"{message}：文件={Path.GetFileName(file)}");
            }
            catch (Exception ex)
            {
                logWarning?.Invoke($"清理调试文件失败：文件={file}，原因={ex.Message}");
            }
        }
    }
}
