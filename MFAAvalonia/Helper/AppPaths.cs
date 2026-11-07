using System;
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
    public static string TempResourceUpdateDirectory => Path.Combine(TempDirectory, "temp_resource_update");
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
}
