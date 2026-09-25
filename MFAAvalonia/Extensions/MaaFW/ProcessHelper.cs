using MFAAvalonia.Helper;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;
using System.Management;


namespace MFAAvalonia.Extensions.MaaFW;

/// <summary>
/// 进程管理辅助类
/// </summary>
public static class ProcessHelper
{
    #region ADB 进程管理

    /// <summary>
    /// 重启 ADB 服务
    /// </summary>
    public static void RestartAdb(string adbPath)
    {
        if (string.IsNullOrEmpty(adbPath))
            return;

        if (OperatingSystem.IsWindows())
        {
            RestartAdbWindows(adbPath);
        }
        else
        {
            RestartAdbUnix(adbPath);
        }
    }

    /// <summary>
    /// 重启 ADB 服务（异步版本）
    /// </summary>
    public static async Task RestartAdbAsync(string adbPath)
    {
        if (string.IsNullOrEmpty(adbPath))
            return;

        if (OperatingSystem.IsWindows())
        {
            await Task.Run(() =>
            {
                if (OperatingSystem.IsWindows()) RestartAdbWindows(adbPath);
            });
        }
        else
        {
            await RestartAdbUnixAsync(adbPath);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void RestartAdbWindows(string adbPath)
    {
        var processStartInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
            UseShellExecute = false,
        };

        using var process = new Process
        {
            StartInfo = processStartInfo
        };
        process.Start();
        process.StandardInput.WriteLine($"\"{adbPath}\" kill-server");
        process.StandardInput.WriteLine($"\"{adbPath}\" start-server");
        process.StandardInput.WriteLine("exit");
        process.WaitForExit();
    }

    private static void RestartAdbUnix(string adbPath)
    {
        // 在 Unix 上直接执行 adb 命令
        ExecuteCommand(adbPath, "kill-server");
        ExecuteCommand(adbPath, "start-server");
    }

    private static async Task RestartAdbUnixAsync(string adbPath)
    {
        await ExecuteCommandAsync(adbPath, "kill-server");
        await ExecuteCommandAsync(adbPath, "start-server");
    }

    /// <summary>
    /// 通过 ADB 重新连接
    /// </summary>
    public static void ReconnectByAdb(string adbPath, string address)
    {
        if (string.IsNullOrWhiteSpace(adbPath) || string.IsNullOrWhiteSpace(address))
            return;

        if (OperatingSystem.IsWindows())
        {
            ReconnectByAdbWindows(adbPath, address);
        }
        else
        {
            ReconnectByAdbUnix(adbPath, address);
        }
    }

    /// <summary>
    /// 通过 ADB 重新连接（异步版本）
    /// </summary>
    public static async Task ReconnectByAdbAsync(string adbPath, string address)
    {
        if (string.IsNullOrWhiteSpace(adbPath) || string.IsNullOrWhiteSpace(address))
            return;

        if (OperatingSystem.IsWindows())
        {
            await Task.Run(() =>
            {
                if (OperatingSystem.IsWindows())
                    ReconnectByAdbWindows(adbPath, address);
            });
        }
        else
        {
            await ReconnectByAdbUnixAsync(adbPath, address);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void ReconnectByAdbWindows(string adbPath, string address)
    {
        var processStartInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
            UseShellExecute = false,
        };

        using var process = new Process
        {
            StartInfo = processStartInfo
        };
        process.Start();
        process.StandardInput.WriteLine($"\"{adbPath}\" disconnect {address}");
        process.StandardInput.WriteLine($"\"{adbPath}\" connect {address}");
        process.StandardInput.WriteLine("exit");
        process.WaitForExit();
    }

    private static void ReconnectByAdbUnix(string adbPath, string address)
    {
        ExecuteCommand(adbPath, $"disconnect {address}");
        ExecuteCommand(adbPath, $"connect {address}");
    }

    private static async Task ReconnectByAdbUnixAsync(string adbPath, string address)
    {
        await ExecuteCommandAsync(adbPath, $"disconnect {address}");
        await ExecuteCommandAsync(adbPath, $"connect {address}");
    }

    /// <summary>
    /// 硬重启 ADB（终止所有 ADB 进程）
    /// </summary>
    public static void HardRestartAdb(string adbPath)
    {
        if (string.IsNullOrEmpty(adbPath))
            return;

        try
        {
            if (OperatingSystem.IsWindows())
            {
                HardRestartAdbWindows(adbPath);
            }
            else
            {
                HardRestartAdbUnix(adbPath);
            }
        }
        catch (Exception ex)
        {
            LoggerHelper.Error($"HardRestartAdb 失败: {ex.Message}");
        }
    }

    [SupportedOSPlatform("windows")]
    private static void HardRestartAdbWindows(string adbPath)
    {

        try
        {
            const string WmiQueryString = "SELECT ProcessId, ExecutablePath, CommandLine FROM Win32_Process";
            using var searcher = new ManagementObjectSearcher(WmiQueryString);
            using var results = searcher.Get();

            var query = from p in Process.GetProcesses()
                        join mo in results.Cast<ManagementObject>()
                            on p.Id equals (int)(uint)mo["ProcessId"]
                        where ((string?)mo["ExecutablePath"])?.Equals(adbPath, StringComparison.OrdinalIgnoreCase) == true
                        select p;

            foreach (var process in query)
            {
                try
                {
                    process.Kill();
                    process.WaitForExit();
                }
                catch (Exception ex)
                {
                    LoggerHelper.Error($"终止 ADB 进程失败: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            LoggerHelper.Error($"HardRestartAdbWindows 失败: {ex.Message}");
        }

    }

    private static void HardRestartAdbUnix(string adbPath)
    {
        var processes = Process.GetProcessesByName("adb")
            .Where(p =>
            {
                try
                {
                    return GetUnixProcessPath(p.Id)?.Equals(adbPath, StringComparison.Ordinal) == true;
                }
                catch
                {
                    return false;
                }
            });

        KillProcesses(processes);
    }

    #endregion

    #region 通用进程管理

    /// <summary>
    /// 跨平台终止指定进程
    /// </summary>
    public static void CloseProcessesByName(string processName, string? commandLineKeyword = null)
    {
        var processes = Process.GetProcesses()
            .Where(p => IsTargetProcess(p, processName, commandLineKeyword))
            .ToList();

        foreach (var process in processes)
        {
            SafeTerminateProcess(process);
        }
    }

    /// <summary>
    /// Windows: 通过窗口句柄终止进程
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static bool CloseProcessesByHWnd(nint hwnd)
    {
        if (!OperatingSystem.IsWindows())
            return false;

        if (hwnd == nint.Zero || !IsWindow(hwnd))
            return false;

        if (GetWindowThreadProcessId(hwnd, out var pid) == 0 || pid == 0)
            return false;

        try
        {
            var process = Process.GetProcessById((int)pid);
            SafeTerminateProcess(process);
            return true;
        }
        catch (Exception ex)
        {
            LoggerHelper.Warning($"按窗口句柄关闭进程失败：hWnd=0x{hwnd.ToInt64():X}，原因：{ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 终止进程列表
    /// </summary>
    public static void KillProcesses(IEnumerable<Process> processes)
    {
        foreach (var process in processes)
        {
            try
            {
                process.Kill();
                process.WaitForExit();
            }
            catch
            {
                // 记录日志或忽略异常
            }
        }
    }

    /// <summary>
    /// 安全终止进程
    /// </summary>
    public static void SafeTerminateProcess(Process process)
    {
        try
        {
            if (process.HasExited) return;

            if ((OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) && NeedElevation(process))
            {
                ElevateKill(process.Id);
            }
            else
            {
                process.Kill();
                process.WaitForExit(5000);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Error] 终止进程失败: {process.ProcessName} ({process.Id}) - {ex.Message}");
        }
        finally
        {
            process.Dispose();
        }
    }

    #endregion

    #region 进程查询辅助方法

    private static bool IsTargetProcess(Process process, string processName, string? keyword)
    {
        try
        {
            if (!IsProcessNameMatch(process, processName))
                return false;

            return string.IsNullOrWhiteSpace(keyword) || GetCommandLine(process).Contains(keyword, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsProcessNameMatch(Process process, string targetName)
    {
        var actualName = Path.GetFileNameWithoutExtension(process.ProcessName);
        return actualName.Equals(targetName, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetCommandLine(Process process)
    {
        return RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? GetWindowsCommandLine(process)
            : OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()
                ? GetUnixCommandLine(process.Id)
                : "";
    }

    [SupportedOSPlatform("windows")]
    private static string GetWindowsCommandLine(Process process)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {process.Id}");
            return searcher.Get().Cast<ManagementObject>()
                    .FirstOrDefault()?["CommandLine"]?.ToString()
                ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    private static string GetUnixCommandLine(int pid)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            try
            {
                var cmdlinePath = $"/proc/{pid}/cmdline";
                return File.Exists(cmdlinePath)
                    ? File.ReadAllText(cmdlinePath, Encoding.UTF8).Replace('\0', ' ')
                    : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }
        else // macOS
        {
            var output = ExecuteShellCommand($"ps -p {pid} -o command=");
            return output?.Trim() ?? string.Empty;
        }
    }

    /// <summary>
    /// 获取 Unix 进程路径
    /// </summary>
    public static string? GetUnixProcessPath(int pid)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            var exePath = $"/proc/{pid}/exe";
            return File.Exists(exePath) ? new FileInfo(exePath).LinkTarget : null;
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            var output = ExecuteShellCommand($"ps -p {pid} -o comm=");
            return string.IsNullOrWhiteSpace(output) ? null : output.Trim();
        }
        return null;
    }

    #endregion

    #region 权限提升

    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    private static bool NeedElevation(Process process)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return false;

        try
        {
            var uid = GetUnixUserId();
            var processUid = GetProcessUid(process.Id);
            return uid != processUid;
        }
        catch
        {
            return true;
        }
    }

    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    private static void ElevateKill(int pid)
    {
        ExecuteShellCommand($"sudo kill -9 {pid}");
    }

    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    [DllImport("libc", EntryPoint = "getuid")]
    private static extern uint GetUid();

    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    private static uint GetUnixUserId() => GetUid();

    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    private static uint GetProcessUid(int pid)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            var statusPath = $"/proc/{pid}/status";
            var uidLine = File.ReadLines(statusPath)
                .FirstOrDefault(l => l.StartsWith("Uid:"));
            return uint.Parse(uidLine?.Split('\t')[1] ?? "0");
        }
        else // macOS
        {
            var output = ExecuteShellCommand($"ps -p {pid} -o uid=");
            return uint.TryParse(output?.Trim(), out var uid) ? uid : 0;
        }
    }

    #endregion

    #region 平台特定终止方法

    [SupportedOSPlatform("windows")]
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [SupportedOSPlatform("windows")]
    [DllImport("user32.dll")]
    private static extern bool IsWindow(nint hWnd);

    /// <summary>
    /// Windows 平台强制终止进程
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static void ForceKillProcessWindows(int processId, string processName)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "taskkill",
                Arguments = $"/F /PID {processId}",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(psi);
            process?.WaitForExit(2000);

            LoggerHelper.Info($"已使用 taskkill 强制终止进程：进程名={processName}，进程ID={processId}");
        }
        catch (Exception ex)
        {
            LoggerHelper.Warning($"taskkill 终止进程失败，准备尝试 WMI：进程名={processName}，进程ID={processId}，原因={ex.Message}");

            try
            {
                using var searcher = new ManagementObjectSearcher(
                    $"SELECT * FROM Win32_Process WHERE ProcessId = {processId}");

                foreach (ManagementObject obj in searcher.Get())
                {
                    obj.InvokeMethod("Terminate", null);
                    LoggerHelper.Info($"已使用 WMI 强制终止进程：进程名={processName}，进程ID={processId}");
                }
            }
            catch (Exception wmiEx)
            {
                LoggerHelper.Error($"使用 WMI 终止进程失败：进程名={processName}，进程ID={processId}，原因={wmiEx.Message}", wmiEx);
            }
        }
    }

    /// <summary>
    /// Unix 平台强制终止进程
    /// </summary>
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    public static void ForceKillProcessUnix(int processId)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "/bin/bash",
                Arguments = $"-c \"kill -9 {processId}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(psi);
            process?.WaitForExit(2000);

            LoggerHelper.Info($"已使用 kill -9 强制终止进程：进程ID={processId}");
        }
        catch (Exception ex)
        {
            LoggerHelper.Error($"执行 kill -9 失败：进程ID={processId}，原因={ex.Message}", ex);
        }
    }

    #endregion

    #region 命令执行

    /// <summary>
    /// 执行命令（同步）
    /// </summary>
    /// <param name="fileName">可执行文件路径</param>
    /// <param name="arguments">命令参数</param>
    /// <returns>命令输出</returns>
    public static string? ExecuteCommand(string fileName, string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null) return null;

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return output;
        }
        catch (Exception ex)
        {
            LoggerHelper.Error($"执行命令失败：file={fileName}, args={arguments}, 原因：{ex.Message}", ex);
            return null;
        }
    }

    /// <summary>
    /// 执行命令（异步）
    /// </summary>
    /// <param name="fileName">可执行文件路径</param>
    /// <param name="arguments">命令参数</param>
    /// <returns>命令输出</returns>
    public static async Task<string?> ExecuteCommandAsync(string fileName, string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null) return null;

            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            return output;
        }
        catch (Exception ex)
        {
            LoggerHelper.Error($"异步执行命令失败：file={fileName}, args={arguments}, 原因：{ex.Message}", ex);
            return null;
        }
    }

    /// <summary>
    /// 执行 Shell 命令（跨平台）
    /// </summary>
    /// <param name="command">Shell 命令</param>
    /// <returns>命令输出</returns>
    public static string? ExecuteShellCommand(string command)
    {
        try
        {
            ProcessStartInfo psi;

            if (OperatingSystem.IsWindows())
            {
                psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c {command}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
            }
            else
            {
                psi = new ProcessStartInfo
                {
                    FileName = "/bin/bash",
                    Arguments = $"-c \"{command}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
            }

            using var process = Process.Start(psi);
            return process?.StandardOutput.ReadToEnd();
        }
        catch (Exception ex)
        {
            LoggerHelper.Error($"执行 Shell 命令失败：command={command}, 原因：{ex.Message}", ex);
            return null;
        }
    }

    /// <summary>
    /// 执行 Shell 命令（异步，跨平台）
    /// </summary>
    /// <param name="command">Shell 命令</param>
    /// <returns>命令输出</returns>
    public static async Task<string?> ExecuteShellCommandAsync(string command)
    {
        try
        {
            ProcessStartInfo psi;

            if (OperatingSystem.IsWindows())
            {
                psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c {command}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
            }
            else
            {
                psi = new ProcessStartInfo
                {
                    FileName = "/bin/bash",
                    Arguments = $"-c \"{command}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
            }

            using var process = Process.Start(psi);
            if (process == null) return null;

            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            return output;
        }
        catch (Exception ex)
        {
            LoggerHelper.Error($"异步执行 Shell 命令失败：command={command}, 原因：{ex.Message}", ex);
            return null;
        }
    }

    #endregion
}
