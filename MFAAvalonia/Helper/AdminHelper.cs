using MFAAvalonia.Extensions.MaaFW;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace MFAAvalonia.Helper;

/// <summary>
/// 管理员权限检查辅助类
/// </summary>
public static class AdminHelper
{
    #region 管理员权限检查

    /// <summary>
    /// 检查当前进程是否以管理员身份运行
    /// </summary>
    /// <returns>如果以管理员身份运行则返回 true</returns>
    [SupportedOSPlatform("windows")]
    public static bool IsRunningAsAdministrator()
    {
        if (!OperatingSystem.IsWindows())
            return false;

        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception ex)
        {
            LoggerHelper.Warning($"检查管理员权限失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 检查指定进程是否以管理员身份运行
    /// </summary>
    /// <param name="processId">进程 ID</param>
    /// <returns>如果以管理员身份运行则返回 true，无法确定时返回 null</returns>
    [SupportedOSPlatform("windows")]
    public static bool? IsProcessRunningAsAdministrator(int processId)
    {
        if (!OperatingSystem.IsWindows())
            return null;

        try
        {
            var process = Process.GetProcessById(processId);
            return IsProcessRunningAsAdministrator(process);
        }
        catch (Exception ex)
        {
            LoggerHelper.Warning($"检查进程 {processId} 管理员权限失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 检查指定进程是否以管理员身份运行
    /// </summary>
    /// <param name="process">进程对象</param>
    /// <returns>如果以管理员身份运行则返回 true，无法确定时返回 null</returns>
    [SupportedOSPlatform("windows")]
    public static bool? IsProcessRunningAsAdministrator(Process process)
    {
        if (!OperatingSystem.IsWindows())
            return null;

        try
        {
            var processHandle = process.Handle;
            if (!OpenProcessToken(processHandle, TOKEN_QUERY, out var tokenHandle))
            {
                LoggerHelper.Warning($"无法打开进程 {process.Id} 的令牌");
                return null;
            }

            try
            {
                // 检查令牌是否已提升
                var elevation = new TOKEN_ELEVATION();
                var elevationSize = Marshal.SizeOf(elevation);
                var elevationPtr = Marshal.AllocHGlobal(elevationSize);

                try
                {
                    if (GetTokenInformation(tokenHandle, TOKEN_INFORMATION_CLASS.TokenElevation,
                        elevationPtr, elevationSize, out _))
                    {
                        elevation = Marshal.PtrToStructure<TOKEN_ELEVATION>(elevationPtr);return elevation.TokenIsElevated != 0;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(elevationPtr);
                }

                return null;
            }
            finally
            {
                CloseHandle(tokenHandle);
            }
        }
        catch (Exception ex)
        {
            LoggerHelper.Warning($"检查进程 {process.Id} 管理员权限失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 检查指定窗口句柄对应的进程是否以管理员身份运行
    /// </summary>
    /// <param name="hwnd">窗口句柄</param>
    /// <returns>如果以管理员身份运行则返回 true，无法确定时返回 null</returns>
    [SupportedOSPlatform("windows")]
    public static bool? IsWindowProcessRunningAsAdministrator(nint hwnd)
    {
        if (!OperatingSystem.IsWindows())
            return null;

        if (hwnd == nint.Zero || !IsWindow(hwnd))
            return null;

        if (GetWindowThreadProcessId(hwnd, out var pid) == 0 || pid == 0)
            return null;

        return IsProcessRunningAsAdministrator((int)pid);
    }

    /// <summary>
    /// 以管理员身份重启当前应用程序
    /// </summary>
    /// <returns>如果成功启动新进程则返回 true</returns>
    [SupportedOSPlatform("windows")]
    public static bool RestartAsAdministrator()
    {
        if (!OperatingSystem.IsWindows())
            return false;

        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath))
            {
                LoggerHelper.Error("无法获取当前进程路径");
                return false;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = true,
                Verb = "runas", // 请求管理员权限
                WorkingDirectory = AppContext.BaseDirectory
            };

            startInfo.ArgumentList.Add($"--wait-for-process-exit={Environment.ProcessId}");
            foreach (var argument in Environment.GetCommandLineArgs().Skip(1))
                startInfo.ArgumentList.Add(argument);

            var process = Process.Start(startInfo);
            return process != null;
        }
        catch (Exception ex)
        {
            LoggerHelper.Error($"以管理员身份重启失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 以管理员身份启动指定程序
    /// </summary>
    /// <param name="filePath">程序路径</param>
    /// <param name="arguments">启动参数</param>
    /// <param name="workingDirectory">工作目录</param>
    /// <returns>启动的进程，失败时返回 null</returns>
    [SupportedOSPlatform("windows")]
    public static Process? StartAsAdministrator(string filePath, string? arguments = null, string? workingDirectory = null)
    {
        if (!OperatingSystem.IsWindows())
            return null;

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = filePath,
                UseShellExecute = true,
                Verb = "runas", // 请求管理员权限
                WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(filePath) ?? AppContext.BaseDirectory
            };

            if (!string.IsNullOrEmpty(arguments))
            {
                startInfo.Arguments = arguments;
            }

            return Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            LoggerHelper.Error($"以管理员身份启动程序失败: {ex.Message}");
            return null;
        }
    }

    #endregion

    #region Windows API声明

    private const uint TOKEN_QUERY = 0x0008;

    [SupportedOSPlatform("windows")]
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(nint ProcessHandle, uint DesiredAccess, out nint TokenHandle);

    [SupportedOSPlatform("windows")]
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(nint TokenHandle, TOKEN_INFORMATION_CLASS TokenInformationClass,
        nint TokenInformation, int TokenInformationLength, out int ReturnLength);

    [SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint hObject);

    [SupportedOSPlatform("windows")]
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [SupportedOSPlatform("windows")]
    [DllImport("user32.dll")]
    private static extern bool IsWindow(nint hWnd);

    [SupportedOSPlatform("windows")]
    private enum TOKEN_INFORMATION_CLASS
    {
        TokenUser = 1,
        TokenGroups,
        TokenPrivileges,
        TokenOwner,
        TokenPrimaryGroup,
        TokenDefaultDacl,
        TokenSource,
        TokenType,
        TokenImpersonationLevel,
        TokenStatistics,
        TokenRestrictedSids,
        TokenSessionId,
        TokenGroupsAndPrivileges,
        TokenSessionReference,
        TokenSandBoxInert,
        TokenAuditPolicy,
        TokenOrigin,
        TokenElevationType,
        TokenLinkedToken,
        TokenElevation,
        TokenHasRestrictions,
        TokenAccessInformation,
        TokenVirtualizationAllowed,
        TokenVirtualizationEnabled,
        TokenIntegrityLevel,
        TokenUIAccess,
        TokenMandatoryPolicy,
        TokenLogonSid,
        MaxTokenInfoClass
    }

    [SupportedOSPlatform("windows")]
    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_ELEVATION
    {
        public int TokenIsElevated;
    }

    #endregion
}
