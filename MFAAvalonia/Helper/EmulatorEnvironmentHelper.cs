using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace MFAAvalonia.Helper;

/// <summary>卡死恢复时可自动重启的模拟器类型。</summary>
public enum EmulatorKind
{
    Unknown = 0,
    MuMu = 1,
    LDPlayer = 2,
    Nox = 3,
    MEmu = 4,
    BlueStacks = 5,
}

/// <summary>
/// 模拟器环境探测：类型识别、安装目录、控制台入口、主程序与实例序号。
/// 连接配置可能缺失或过期，这里统一用设备名、运行中的进程与 ADB 端口兜底，
/// 避免卡死恢复时因为拿不到控制入口而让重启链路空转。
/// </summary>
public static class EmulatorEnvironmentHelper
{
    /// <summary>MuMu 新版设备进程名。</summary>
    public const string MuMuDeviceProcessName = "MuMuNxDevice";

    /// <summary>MuMu 新版虚拟机进程名。</summary>
    public const string MuMuVmProcessName = "MuMuVMMHeadless";

    /// <summary>MuMu 旧版主程序进程名。</summary>
    public const string MuMuLegacyProcessName = "MuMuPlayer";

    /// <summary>实例序号上限：超过该值的端口不属于对应模拟器，避免指令落到错误实例。</summary>
    private const int MaxInstanceIndex = 99;

    /// <summary>已知的模拟器进程名与类型，按探测优先级排列。</summary>
    private static readonly (EmulatorKind Kind, string ProcessName)[] KnownProcesses =
    [
        (EmulatorKind.MuMu, MuMuDeviceProcessName),
        (EmulatorKind.MuMu, MuMuVmProcessName),
        (EmulatorKind.MuMu, MuMuLegacyProcessName),
        (EmulatorKind.LDPlayer, "dnplayer"),
        (EmulatorKind.LDPlayer, "ld9boxHeadless"),
        (EmulatorKind.Nox, "Nox"),
        (EmulatorKind.MEmu, "MEmu"),
        (EmulatorKind.BlueStacks, "HD-Player"),
    ];

    private static readonly string[] BlueStacksProcessNames = ["HD-Player"];

    /// <summary>按设备名识别模拟器类型，识别不出来时返回 Unknown。</summary>
    public static EmulatorKind DetectKindByDeviceName(string? deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
            return EmulatorKind.Unknown;
        if (deviceName.Contains("MuMu", StringComparison.OrdinalIgnoreCase))
            return EmulatorKind.MuMu;
        if (deviceName.Contains("LDPlayer", StringComparison.OrdinalIgnoreCase)
            || deviceName.Contains("雷电", StringComparison.Ordinal))
            return EmulatorKind.LDPlayer;
        if (deviceName.Contains("Nox", StringComparison.OrdinalIgnoreCase))
            return EmulatorKind.Nox;
        if (deviceName.Contains("MEmu", StringComparison.OrdinalIgnoreCase)
            || deviceName.Contains("XYAZ", StringComparison.OrdinalIgnoreCase)
            || deviceName.Contains("逍遥", StringComparison.Ordinal))
            return EmulatorKind.MEmu;
        if (deviceName.Contains("BlueStacks", StringComparison.OrdinalIgnoreCase)
            || deviceName.Contains("蓝叠", StringComparison.Ordinal))
            return EmulatorKind.BlueStacks;
        return EmulatorKind.Unknown;
    }

    /// <summary>按进程名识别模拟器类型，识别不出来时返回 Unknown。</summary>
    public static EmulatorKind DetectKindByProcessName(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
            return EmulatorKind.Unknown;

        return KnownProcesses
            .FirstOrDefault(entry => entry.ProcessName.Equals(processName, StringComparison.OrdinalIgnoreCase))
            .Kind;
    }

    /// <summary>按可执行文件名识别模拟器类型，识别不出来时返回 Unknown。</summary>
    public static EmulatorKind DetectKindByExecutablePath(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
            return EmulatorKind.Unknown;

        return Path.GetFileName(executablePath).ToLowerInvariant() switch
        {
            "dnplayer.exe" or "ld9boxheadless.exe" or "ldconsole.exe" or "dnconsole.exe" => EmulatorKind.LDPlayer,
            "mumuplayer.exe" or "mumunxdevice.exe" or "mumuvmmheadless.exe"
                or "mumu-cli.exe" or "mumumanager.exe" => EmulatorKind.MuMu,
            "nox.exe" or "noxconsole.exe" or "noxlauncher.exe" => EmulatorKind.Nox,
            "memu.exe" or "memuc.exe" or "memuheadless.exe" or "memuconsole.exe" => EmulatorKind.MEmu,
            "hd-player.exe" or "bluestacks.exe" or "bluestacksplayer.exe" => EmulatorKind.BlueStacks,
            _ => EmulatorKind.Unknown,
        };
    }

    /// <summary>启动设置里的启动参数可能不带实例序号，无法解析时返回 null。</summary>
    private static readonly string[] InstanceArgumentPrefixes =
    [
        "--vmindex",
        "--instance",
        "--index",
        "-index",
        "-v",
        "-i",
    ];

    /// <summary>
    /// 从启动设置的启动参数中解析实例序号，支持 `-v 0`、`--vmindex 1`、`-index:2`、`-i 3` 等写法；
    /// 解析不出来时返回 null，由调用方继续用连接配置或 ADB 端口兜底。
    /// </summary>
    public static int? ResolveInstanceIndexFromArguments(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
            return null;

        var tokens = arguments.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < tokens.Length; i++)
        {
            foreach (var prefix in InstanceArgumentPrefixes)
            {
                var token = tokens[i];
                if (!token.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                var remainder = token[prefix.Length..];
                // 余下部分必须为空或以分隔符开头，避免把 -index 当成 -i
                if (remainder.Length > 0 && remainder[0] is not ('=' or ':'))
                    continue;

                var inlineText = remainder.TrimStart('=', ':');
                if (int.TryParse(inlineText, out var inlineIndex) && IsValidInstanceIndex(inlineIndex))
                    return inlineIndex;
                if (i + 1 < tokens.Length && int.TryParse(tokens[i + 1], out var nextIndex) && IsValidInstanceIndex(nextIndex))
                    return nextIndex;
            }
        }

        return null;
    }

    private static bool IsValidInstanceIndex(int index) => index >= 0 && index <= MaxInstanceIndex;

    /// <summary>探测正在运行的模拟器，返回类型与其可执行文件路径。</summary>
    public static (EmulatorKind Kind, string? ExePath) DetectRunningEmulator()
    {
        foreach (var (kind, processName) in KnownProcesses)
        {
            var exePath = TryGetExecutablePath(processName);
            if (exePath != null)
                return (kind, exePath);
        }

        return (EmulatorKind.Unknown, null);
    }

    /// <summary>该类型在强制结束时需要一并结束的进程名。</summary>
    public static string[] GetProcessNames(EmulatorKind kind) => kind switch
    {
        EmulatorKind.MuMu => [MuMuDeviceProcessName, MuMuVmProcessName],
        EmulatorKind.LDPlayer => ["dnplayer", "ld9boxHeadless"],
        EmulatorKind.Nox => ["Nox", "NoxVMHandle"],
        EmulatorKind.MEmu => ["MEmu", "MEmuHeadless"],
        EmulatorKind.BlueStacks => BlueStacksProcessNames,
        _ => [],
    };

    /// <summary>关闭与启动的控制台命令模板，{0} 为实例序号；没有控制台时返回 null。</summary>
    public static (string Shutdown, string Launch)? GetConsoleCommands(EmulatorKind kind) => kind switch
    {
        EmulatorKind.LDPlayer => ("quit --index {0}", "launch --index {0}"),
        EmulatorKind.Nox => ("quit -index:{0}", "launch -index:{0}"),
        EmulatorKind.MEmu => ("stop -i {0}", "start -i {0}"),
        _ => null,
    };

    /// <summary>从 ADB 地址反推实例序号；无法确定时返回 null，由调用方回退默认实例。</summary>
    public static int? ResolveInstanceIndex(EmulatorKind kind, string? adbSerial)
    {
        var port = ParsePort(adbSerial);
        if (port == null)
            return null;

        return kind switch
        {
            EmulatorKind.MuMu => ResolveMuMuIndex(port.Value),
            EmulatorKind.LDPlayer => ResolveSteppedIndex(port.Value, 5555, 2),
            EmulatorKind.Nox => ResolveNoxIndex(port.Value),
            EmulatorKind.MEmu => ResolveSteppedIndex(port.Value, 21503, 10),
            _ => null,
        };
    }

    /// <summary>从模拟器进程路径推导安装目录，返回第一个包含控制台或主程序的候选目录。</summary>
    public static string? ResolveInstallRoot(EmulatorKind kind, string? emulatorExePath)
    {
        if (string.IsNullOrWhiteSpace(emulatorExePath))
            return null;

        try
        {
            var directory = Path.GetDirectoryName(emulatorExePath);
            if (string.IsNullOrWhiteSpace(directory))
                return null;

            var fileName = Path.GetFileName(emulatorExePath);
            var candidates = GetInstallRootCandidates(fileName, directory);

            // 先看哪个候选目录能定位到控制台，其次看主程序，最后退回存在的目录
            foreach (var candidate in candidates)
            {
                if (FindConsole(kind, candidate, emulatorExePath) != null)
                    return candidate;
            }
            foreach (var candidate in candidates)
            {
                if (FindLaunchExecutable(kind, candidate, emulatorExePath) != null)
                    return candidate;
            }

            return candidates.FirstOrDefault(Directory.Exists);
        }
        catch
        {
            // 路径异常或权限不足时按未识别处理，由调用方决定后续兜底
            return null;
        }
    }

    /// <summary>
    /// 安装目录候选顺序：MuMu 新版为 nx_device/&lt;版本&gt;/shell/xxx.exe 需上溯三级，
    /// MuMu 旧版主程序位于 shell/ 下需上溯一级，其余模拟器进程一般就在安装目录内。
    /// </summary>
    private static string[] GetInstallRootCandidates(string fileName, string directory)
    {
        if (fileName.Equals("MuMuNxDevice.exe", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("MuMuVMMHeadless.exe", StringComparison.OrdinalIgnoreCase))
            return [Up(directory, 3), Up(directory, 1), directory];
        if (fileName.Equals("MuMuPlayer.exe", StringComparison.OrdinalIgnoreCase))
            return [Up(directory, 1), Up(directory, 2), directory];
        return [directory, Up(directory, 1), Up(directory, 2)];
    }

    /// <summary>查找模拟器控制台可执行文件。</summary>
    public static string? FindConsole(EmulatorKind kind, string? installRoot, string? runningExePath)
        => FindFirstExisting(GetConsoleFileNames(kind), installRoot, runningExePath);

    /// <summary>查找模拟器主程序可执行文件。</summary>
    public static string? FindLaunchExecutable(EmulatorKind kind, string? installRoot, string? runningExePath)
        => FindFirstExisting(GetLaunchFileNames(kind), installRoot, runningExePath);

    private static string? FindFirstExisting(string[] relativeNames, string? installRoot, string? runningExePath)
    {
        if (relativeNames.Length == 0)
            return null;

        foreach (var directory in BuildProbeDirectories(installRoot, runningExePath))
        {
            foreach (var relativeName in relativeNames)
            {
                var path = Path.Combine(directory, relativeName);
                if (File.Exists(path))
                    return path;
            }
        }

        return null;
    }

    private static string[] GetConsoleFileNames(EmulatorKind kind) => kind switch
    {
        EmulatorKind.MuMu =>
        [
            Path.Combine("nx_main", "mumu-cli.exe"),
            Path.Combine("nx_main", "MuMuManager.exe"),
            Path.Combine("shell", "MuMuManager.exe"),
        ],
        EmulatorKind.LDPlayer => ["ldconsole.exe", "dnconsole.exe"],
        EmulatorKind.Nox => ["NoxConsole.exe", Path.Combine("bin", "NoxConsole.exe")],
        EmulatorKind.MEmu => ["memuc.exe", "MEmuConsole.exe"],
        _ => [],
    };

    private static string[] GetLaunchFileNames(EmulatorKind kind) => kind switch
    {
        EmulatorKind.MuMu => ["MuMuPlayer.exe"],
        EmulatorKind.LDPlayer => ["dnplayer.exe", "ld9boxHeadless.exe"],
        EmulatorKind.Nox => [Path.Combine("bin", "Nox.exe"), "Nox.exe", "NoxLauncher.exe"],
        EmulatorKind.MEmu => ["MEmu.exe"],
        EmulatorKind.BlueStacks => ["HD-Player.exe", "Bluestacks.exe"],
        _ => [],
    };

    private static IEnumerable<string> BuildProbeDirectories(string? installRoot, string? runningExePath)
    {
        var directories = new List<string>();
        var exeDirectory = string.IsNullOrWhiteSpace(runningExePath) ? null : Path.GetDirectoryName(runningExePath);
        if (!string.IsNullOrWhiteSpace(exeDirectory))
            directories.Add(exeDirectory);
        if (!string.IsNullOrWhiteSpace(installRoot) && !directories.Contains(installRoot, StringComparer.OrdinalIgnoreCase))
            directories.Add(installRoot);
        return directories;
    }

    private static string? TryGetExecutablePath(string processName)
    {
        try
        {
            return Process.GetProcessesByName(processName).FirstOrDefault()?.MainModule?.FileName;
        }
        catch
        {
            // 位数不同或权限不足时读取模块路径会失败，按未运行处理
            return null;
        }
    }

    private static string Up(string directory, int levels)
    {
        var path = directory;
        for (var i = 0; i < levels; i++)
            path = Path.Combine(path, "..");
        return Path.GetFullPath(path);
    }

    private static int? ParsePort(string? adbSerial)
    {
        if (string.IsNullOrWhiteSpace(adbSerial))
            return null;

        var address = adbSerial.Trim();
        if (address.StartsWith("emulator-", StringComparison.OrdinalIgnoreCase))
        {
            // emulator-5554 的控制台端口实际对应 5555 这个 ADB 端口
            return int.TryParse(address["emulator-".Length..], out var consolePort) ? consolePort + 1 : null;
        }

        var separatorIndex = address.LastIndexOf(':');
        return separatorIndex >= 0 && int.TryParse(address[(separatorIndex + 1)..], out var port) ? port : null;
    }

    private static int? ResolveMuMuIndex(int port)
    {
        if (port == 7555)
            return 0;
        if (port >= 16384)
            return ResolveSteppedIndex(port, 16384, 32);
        return port >= 5555 ? ResolveSteppedIndex(port, 5555, 2) : null;
    }

    private static int? ResolveNoxIndex(int port)
    {
        if (port == 62001)
            return 0;
        var index = port - 62024;
        return index > 0 && index <= MaxInstanceIndex ? index : null;
    }

    private static int? ResolveSteppedIndex(int port, int basePort, int step)
    {
        var offset = port - basePort;
        if (offset < 0 || offset % step != 0)
            return null;

        var index = offset / step;
        return index <= MaxInstanceIndex ? index : null;
    }
}
