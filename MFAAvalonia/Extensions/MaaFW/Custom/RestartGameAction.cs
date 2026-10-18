using MaaFramework.Binding;
using MaaFramework.Binding.Custom;
using MFAAvalonia.Configuration;
using MFAAvalonia.Helper;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace MFAAvalonia.Extensions.MaaFW.Custom;

public class RestartGameAction : IMaaCustomAction
{
    private readonly MaaProcessor? _owner;

    public string Name { get; set; } = nameof(RestartGameAction);

    public RestartGameAction(MaaProcessor? owner = null)
    {
        _owner = owner;
    }

    /// <summary>模拟器无响应或游戏重启期间 ADB 超时时，必须跳过温和重启并强制重启实例。</summary>
    public static bool ShouldForceEmulatorRestart(bool emulatorUnresponsive, bool gameRestartAdbTimedOut)
        => emulatorUnresponsive || gameRestartAdbTimedOut;

    /// <summary>只有观察到旧 ADB 连接断开且随后恢复可用，才能确认模拟器已经重启。</summary>
    public static bool IsEmulatorRestartConfirmed(bool observedOldConnectionUnavailable, bool adbReady)
        => observedOldConnectionUnavailable && adbReady;

    private string? _adbPath;
    private string? _adbSerial;
    private string? _deviceName;
    private EmulatorKind _emulatorKind;
    private string? _emulatorInstallRoot;
    private string? _emulatorConsoleExe;
    private string? _emulatorLaunchExe;
    private string[] _emulatorProcessNames = [];
    private (string Shutdown, string Launch)? _emulatorConsoleCommands;
    private string _configuredLaunchArguments = string.Empty;
    private bool _mumuConsoleUsesVmIndexFlag;
    private int? _instanceIndex;

    // 模拟器重启的等待策略：挂死的实例不会响应控制命令，过长的轮询只会拖慢恢复
    private const int AdbReadyAttemptsPerRound = 12;
    private const int AdbReadyAttemptTimeoutMs = 3000;
    private const int AdbReadyIntervalMs = 2000;
    private const int EmulatorStartRounds = 2;
    private const int KillProcessWaitMs = 10000;
    private const int GracefulCloseWaitMs = 8000;

    private CancellationToken _recoveryToken;

    private string InstanceText => _instanceIndex?.ToString() ?? "默认";

    private void WaitForRecovery(int milliseconds)
    {
        if (_recoveryToken.WaitHandle.WaitOne(milliseconds))
            _recoveryToken.ThrowIfCancellationRequested();
    }

    private void EnsureEmulatorEnvironment(MaaProcessor? owner = null)
    {
        if (_adbPath != null) return;

        var processor = owner ?? MaaProcessorManager.Instance.Current;
        int? configuredIndex = null;
        string? configuredEmulatorPath = null;
        var configuredSoftwarePath = string.Empty;
        if (processor != null)
        {
            _adbPath = processor.Config.AdbDevice.AdbPath;
            _adbSerial = processor.Config.AdbDevice.AdbSerial;
            _deviceName = processor.Config.AdbDevice.Name;

            // 启动设置里的软件路径与启动参数是用户显式配置，优先级高于任何自动探测
            configuredSoftwarePath = processor.InstanceConfiguration.GetValue(ConfigurationKeys.SoftwarePath, string.Empty);
            _configuredLaunchArguments = processor.InstanceConfiguration.GetValue(ConfigurationKeys.EmulatorConfig, string.Empty);

            var configStr = processor.Config.AdbDevice.Config;
            if (!string.IsNullOrWhiteSpace(configStr))
            {
                try
                {
                    using var doc = JsonDocument.Parse(configStr);
                    if (doc.RootElement.TryGetProperty("extras", out var extras) &&
                        extras.TryGetProperty("mumu", out var mumu))
                    {
                        if (mumu.TryGetProperty("path", out var path))
                            configuredEmulatorPath = path.GetString();
                        if (mumu.TryGetProperty("index", out var index) &&
                            index.ValueKind == JsonValueKind.Number &&
                            index.TryGetInt32(out var parsedIndex))
                            configuredIndex = parsedIndex;
                    }
                }
                catch { }
            }
        }
        _adbPath ??= "adb";
        _adbSerial ??= "";

        var softwareExePath = ResolveConfiguredExecutable(configuredSoftwarePath);
        var (runningKind, runningExePath) = EmulatorEnvironmentHelper.DetectRunningEmulator();

        // 类型识别顺序：启动设置的软件路径 → 设备名 → 连接配置里的 MuMu 环境 → 运行中的模拟器进程
        _emulatorKind = EmulatorEnvironmentHelper.DetectKindByExecutablePath(softwareExePath);
        if (_emulatorKind == EmulatorKind.Unknown)
            _emulatorKind = EmulatorEnvironmentHelper.DetectKindByDeviceName(Path.GetFileName(configuredSoftwarePath));
        if (_emulatorKind == EmulatorKind.Unknown)
            _emulatorKind = EmulatorEnvironmentHelper.DetectKindByDeviceName(_deviceName);
        if (_emulatorKind == EmulatorKind.Unknown && !string.IsNullOrWhiteSpace(configuredEmulatorPath))
            _emulatorKind = EmulatorKind.MuMu;
        if (_emulatorKind == EmulatorKind.Unknown)
            _emulatorKind = runningKind;

        _emulatorProcessNames = EmulatorEnvironmentHelper.GetProcessNames(_emulatorKind);
        _emulatorConsoleCommands = EmulatorEnvironmentHelper.GetConsoleCommands(_emulatorKind);

        var probeRoots = BuildProbeRoots(softwareExePath, configuredEmulatorPath, runningExePath);
        foreach (var root in probeRoots)
        {
            _emulatorConsoleExe = EmulatorEnvironmentHelper.FindConsole(_emulatorKind, root, runningExePath);
            if (_emulatorConsoleExe == null) continue;
            _emulatorInstallRoot = root;
            break;
        }

        _emulatorLaunchExe = ResolveLaunchExecutable(softwareExePath, configuredSoftwarePath, probeRoots, runningExePath);
        if (_emulatorInstallRoot == null)
        {
            foreach (var root in probeRoots)
            {
                if (EmulatorEnvironmentHelper.FindLaunchExecutable(_emulatorKind, root, runningExePath) == null) continue;
                _emulatorInstallRoot = root;
                break;
            }
        }
        _emulatorInstallRoot ??= probeRoots.FirstOrDefault();

        // 没有控制入口时只能靠结束主程序进程，MuMu 旧版需要连带结束 MuMuPlayer.exe
        if (_emulatorKind == EmulatorKind.MuMu && _emulatorConsoleExe == null)
            _emulatorProcessNames = [.. _emulatorProcessNames, EmulatorEnvironmentHelper.MuMuLegacyProcessName];

        _mumuConsoleUsesVmIndexFlag = _emulatorConsoleExe != null
                                      && Path.GetFileName(_emulatorConsoleExe)
                                          .Equals("mumu-cli.exe", StringComparison.OrdinalIgnoreCase);

        // 实例序号优先取启动设置的启动参数，其次连接配置，最后按 ADB 端口反推
        _instanceIndex = EmulatorEnvironmentHelper.ResolveInstanceIndexFromArguments(_configuredLaunchArguments)
                         ?? configuredIndex
                         ?? EmulatorEnvironmentHelper.ResolveInstanceIndex(_emulatorKind, _adbSerial);

        LoggerHelper.Info(
            $"[RestartGameAction] 模拟器环境：类型={_emulatorKind}，设备名={_deviceName ?? "未知"}，" +
            $"安装目录={_emulatorInstallRoot ?? "未解析"}，实例={InstanceText}，" +
            $"控制台={_emulatorConsoleExe ?? "无"}，主程序={_emulatorLaunchExe ?? "无"}，" +
            $"启动设置路径={configuredSoftwarePath ?? string.Empty}，启动参数={_configuredLaunchArguments}");
    }

    /// <summary>启动设置的软件路径可能为空或指向快捷方式，这里只接受真实存在的可执行文件。</summary>
    private static string? ResolveConfiguredExecutable(string? configuredSoftwarePath)
        => !string.IsNullOrWhiteSpace(configuredSoftwarePath)
           && File.Exists(configuredSoftwarePath)
           && configuredSoftwarePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? configuredSoftwarePath
            : null;

    /// <summary>收集候选安装目录：启动设置的软件路径 → 连接配置中的模拟器路径 → 运行中进程反推。</summary>
    private List<string> BuildProbeRoots(string? softwareExePath, string? configuredEmulatorPath, string? runningExePath)
    {
        var roots = new List<string>();
        AddProbeRoot(roots, string.IsNullOrWhiteSpace(softwareExePath)
            ? null
            : EmulatorEnvironmentHelper.ResolveInstallRoot(_emulatorKind, softwareExePath));
        AddProbeRoot(roots, string.IsNullOrWhiteSpace(softwareExePath) ? null : Path.GetDirectoryName(softwareExePath));
        AddProbeRoot(roots, configuredEmulatorPath);
        AddProbeRoot(roots, EmulatorEnvironmentHelper.ResolveInstallRoot(_emulatorKind, runningExePath));
        return roots;
    }

    private static void AddProbeRoot(List<string> roots, string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate) || !Directory.Exists(candidate)) return;
        if (!roots.Contains(candidate, StringComparer.OrdinalIgnoreCase)) roots.Add(candidate);
    }

    private string? ResolveLaunchExecutable(string? softwareExePath, string configuredSoftwarePath,
        List<string> probeRoots, string? runningExePath)
    {
        if (!string.IsNullOrWhiteSpace(softwareExePath))
            return softwareExePath;

        foreach (var root in probeRoots)
        {
            var launchExe = EmulatorEnvironmentHelper.FindLaunchExecutable(_emulatorKind, root, runningExePath);
            if (launchExe != null)
                return launchExe;
        }

        // 启动设置允许填快捷方式，直接交给 ShellExecute 启动
        return File.Exists(configuredSoftwarePath)
               && configuredSoftwarePath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)
            ? configuredSoftwarePath
            : null;
    }

    private static string GetPackageName()
    {
        return TaskRecoveryPolicy.TargetPackageName;
    }

    /// <summary>
    /// 重启模拟器。force 为 true 表示模拟器已判定无响应（挂起形态），
    /// 此时不做温和重启：挂起的实例不会响应控制命令，直接强杀进程后重新拉起。
    /// </summary>
    private bool RestartEmulator(bool force)
    {
        _recoveryToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            LoggerHelper.Warning("[RestartGameAction] 当前平台不支持模拟器重启命令");
            return false;
        }

        switch (_emulatorKind)
        {
            case EmulatorKind.MuMu:
                return RestartMuMuEmulator(force);
            case EmulatorKind.LDPlayer:
            case EmulatorKind.Nox:
            case EmulatorKind.MEmu:
                return RestartConsoleEmulator(force);
            case EmulatorKind.BlueStacks:
                return RestartProcessLevelEmulator();
            default:
                LoggerHelper.Error(
                    $"[RestartGameAction] 未识别模拟器类型（设备名={_deviceName ?? "未知"}），无法自动重启模拟器；" +
                    "请手动重启模拟器后继续任务");
                return false;
        }
    }

    /// <summary>MuMu：mumu-cli 走温和重启，MuMuManager 与无响应形态直接强制重启。</summary>
    private bool RestartMuMuEmulator(bool force)
    {
        if (_emulatorConsoleExe == null && _emulatorLaunchExe == null)
        {
            LoggerHelper.Error("[RestartGameAction] 未找到 MuMu 控制入口与主程序，无法重启模拟器");
            return false;
        }

        if (force || !_mumuConsoleUsesVmIndexFlag)
        {
            if (!force)
                LoggerHelper.Info("[RestartGameAction] 控制入口为 MuMuManager，直接走强制重启路径");
            return RestartMuMuForce();
        }

        LoggerHelper.Info($"[RestartGameAction] 通过控制台重启模拟器实例 {InstanceText}...");
        if (RunConsoleCommand(BuildMuMuArguments("restart"), 30000))
        {
            LoggerHelper.Info("[RestartGameAction] 控制命令已提交，等待旧 ADB 连接断开...");
            var oldConnectionUnavailable = WaitForAdbDisconnected(
                AdbReadyAttemptsPerRound, AdbReadyAttemptTimeoutMs, AdbReadyIntervalMs);
            if (oldConnectionUnavailable)
            {
                LoggerHelper.Info("[RestartGameAction] 已观察到旧 ADB 连接断开，等待模拟器重新就绪...");
                var adbReady = WaitForAdbReady(
                    AdbReadyAttemptsPerRound, AdbReadyAttemptTimeoutMs, AdbReadyIntervalMs);
                if (IsEmulatorRestartConfirmed(oldConnectionUnavailable, adbReady))
                {
                    LoggerHelper.Info("[RestartGameAction] 已确认模拟器重启完成");
                    return true;
                }
            }

            LoggerHelper.Warning("[RestartGameAction] 未确认控制命令实际重启模拟器，转为强制重启");
        }
        else
        {
            LoggerHelper.Warning("[RestartGameAction] 控制命令重启未成功，转为强制重启");
        }

        return RestartMuMuForce();
    }

    /// <summary>
    /// MuMu 强制重启：先强杀设备进程（挂起进程也能强杀），确认进程退出后再拉起实例，
    /// 并以 ADB 就绪作为唯一判定标准；一次未就绪会再提交一次启动命令。
    /// </summary>
    private bool RestartMuMuForce()
    {
        StopAndWaitEmulatorExit(KillProcessWaitMs);
        return LaunchAndWaitReady();
    }

    /// <summary>
    /// 雷电、夜神、逍遥：优先用各自控制台关闭实例，控制台不可用或未生效时强制结束进程，
    /// 再通过控制台拉起实例。
    /// </summary>
    private bool RestartConsoleEmulator(bool force)
    {
        var closed = false;
        if (!force && _emulatorConsoleExe != null && _emulatorConsoleCommands != null)
        {
            LoggerHelper.Info($"[RestartGameAction] 通过控制台关闭模拟器实例 {InstanceText}...");
            closed = RunConsoleCommand(BuildConsoleArguments(_emulatorConsoleCommands.Value.Shutdown), 20000)
                     && WaitForEmulatorProcessesExit(GracefulCloseWaitMs);
            if (!closed)
                LoggerHelper.Warning("[RestartGameAction] 控制台关闭未生效，改为强制结束模拟器进程");
        }

        if (!closed)
            StopAndWaitEmulatorExit(KillProcessWaitMs);

        return LaunchAndWaitReady();
    }

    /// <summary>蓝叠没有按实例控制的稳定接口，只能结束主程序再重新启动，不保证只影响目标实例。</summary>
    private bool RestartProcessLevelEmulator()
    {
        LoggerHelper.Warning("[RestartGameAction] 蓝叠不支持按实例控制，按进程级别重启模拟器");
        StopAndWaitEmulatorExit(KillProcessWaitMs);
        return LaunchAndWaitReady();
    }

    private bool LaunchAndWaitReady()
    {
        for (var round = 0; round < EmulatorStartRounds; round++)
        {
            if (round > 0)
                LoggerHelper.Warning("[RestartGameAction] 模拟器仍未就绪，重新提交一次启动命令");

            if (!LaunchEmulatorInstance())
                LoggerHelper.Warning("[RestartGameAction] 模拟器启动命令未成功提交，继续等待 ADB 就绪");

            if (WaitForAdbReady(AdbReadyAttemptsPerRound, AdbReadyAttemptTimeoutMs, AdbReadyIntervalMs))
            {
                LoggerHelper.Info("[RestartGameAction] 模拟器已重新就绪");
                return true;
            }
        }

        LoggerHelper.Error("[RestartGameAction] 模拟器重启后仍未就绪");
        return false;
    }

    /// <summary>拉起模拟器实例：优先控制台命令，控制台不可用时启动主程序。</summary>
    private bool LaunchEmulatorInstance()
    {
        var arguments = BuildLaunchArguments();
        if (arguments != null && RunConsoleCommand(arguments, 15000))
            return true;

        if (_emulatorLaunchExe == null)
            return false;

        LoggerHelper.Info($"[RestartGameAction] 启动模拟器主程序（{_emulatorLaunchExe}）...");
        try
        {
            Process.Start(new ProcessStartInfo(_emulatorLaunchExe, BuildLaunchExecutableArguments())
            {
                UseShellExecute = true,
            });
            return true;
        }
        catch (Exception e)
        {
            LoggerHelper.Info($"[RestartGameAction] 启动模拟器主程序异常: {e.Message}");
            return false;
        }
    }

    private string? BuildLaunchArguments()
    {
        if (_emulatorConsoleExe == null)
            return null;
        if (_emulatorKind == EmulatorKind.MuMu)
            return BuildMuMuArguments("launch");

        var template = _emulatorConsoleCommands?.Launch;
        return template == null ? null : BuildConsoleArguments(template);
    }

    /// <summary>
    /// 主程序启动参数：优先用启动设置里的启动参数（用户配置的实例选择），
    /// 其次为 MuMu 旧版补 -v，其余模拟器主程序不带实例参数。
    /// </summary>
    private string BuildLaunchExecutableArguments()
    {
        if (!string.IsNullOrWhiteSpace(_configuredLaunchArguments))
            return _configuredLaunchArguments;

        return _emulatorKind == EmulatorKind.MuMu && (_instanceIndex ?? 0) > 0 ? $"-v {_instanceIndex}" : "";
    }

    private string BuildMuMuArguments(string command)
    {
        var index = _instanceIndex ?? 0;
        return _mumuConsoleUsesVmIndexFlag
            ? $"control --vmindex {index} {command}"
            : $"control -v {index} {command}";
    }

    private string BuildConsoleArguments(string template)
        => string.Format(CultureInfo.InvariantCulture, template, _instanceIndex ?? 0);

    /// <summary>结束该模拟器的全部相关进程；进程不存在时跳过。</summary>
    private void ForceStopEmulatorProcesses()
    {
        foreach (var processName in _emulatorProcessNames)
        {
            if (!IsProcessRunning(processName)) continue;
            LoggerHelper.Info($"[RestartGameAction] 强制结束 {processName}.exe...");
            RunHiddenProcess("taskkill", $"/F /IM {processName}.exe /T", 8000);
        }
    }

    private void StopAndWaitEmulatorExit(int waitMs)
    {
        ForceStopEmulatorProcesses();
        if (!WaitForEmulatorProcessesExit(waitMs))
            LoggerHelper.Warning(
                $"[RestartGameAction] 模拟器进程在 {waitMs / 1000} 秒内仍未退出，继续尝试启动实例");
        WaitForRecovery(2000);
    }

    private bool WaitForEmulatorProcessesExit(int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (!AnyEmulatorProcessRunning())
                return true;
            WaitForRecovery(500);
        }

        return !AnyEmulatorProcessRunning();
    }

    private bool AnyEmulatorProcessRunning() => _emulatorProcessNames.Any(IsProcessRunning);

    private static bool IsProcessRunning(string processName)
    {
        try
        {
            return Process.GetProcessesByName(processName).Length > 0;
        }
        catch
        {
            // 查询进程失败时按已退出处理，避免重启流程被查询异常打断
            return false;
        }
    }

    /// <summary>执行模拟器控制台命令，返回是否成功（退出码 0）。</summary>
    private bool RunConsoleCommand(string arguments, int timeoutMs)
    {
        if (_emulatorConsoleExe == null)
            return false;

        LoggerHelper.Info($"[RestartGameAction] 执行 {Path.GetFileName(_emulatorConsoleExe)} {arguments}");
        var (timedOut, exitCode, standardOutput, standardError) =
            RunHiddenProcess(_emulatorConsoleExe, arguments, timeoutMs);
        if (timedOut)
        {
            LoggerHelper.Info($"[RestartGameAction] 控制台命令超时（{timeoutMs / 1000} 秒），按失败处理");
            return false;
        }
        if (exitCode == 0)
            return true;

        var detail = string.Join(' ', new[] { standardOutput, standardError }
            .Where(text => !string.IsNullOrWhiteSpace(text)));
        LoggerHelper.Info($"[RestartGameAction] 控制台命令返回异常: code={exitCode} {detail}".TrimEnd());
        return false;
    }

    /// <summary>轮询 ADB，确认控制命令提交后旧连接曾经不可用。</summary>
    private bool WaitForAdbDisconnected(int maxAttempts, int attemptTimeoutMs, int intervalMs)
    {
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            _recoveryToken.ThrowIfCancellationRequested();
            if (!IsAdbReady(attemptTimeoutMs))
                return true;

            WaitForRecovery(intervalMs);
        }

        return false;
    }

    /// <summary>轮询 ADB 判断模拟器是否恢复响应，返回是否就绪。</summary>
    private bool WaitForAdbReady(int maxAttempts, int attemptTimeoutMs, int intervalMs)
    {
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            _recoveryToken.ThrowIfCancellationRequested();

            if (IsAdbReady(attemptTimeoutMs))
            {
                LoggerHelper.Info("[RestartGameAction] 模拟器已就绪");
                return true;
            }

            WaitForRecovery(intervalMs);
        }

        return false;
    }

    private bool IsAdbReady(int timeoutMs)
    {
        var arguments = string.IsNullOrWhiteSpace(_adbSerial)
            ? "shell echo ready"
            : $"-s {_adbSerial} shell echo ready";
        var (timedOut, exitCode, _, _) = RunHiddenProcess(_adbPath!, arguments, timeoutMs);
        return !timedOut && exitCode == 0;
    }

    /// <summary>
    /// 运行外部命令并等待退出：异步读取输出，避免子进程因管道写满而挂住；
    /// 超时后结束进程并返回 TimedOut=true。
    /// </summary>
    private static (bool TimedOut, int ExitCode, string StandardOutput, string StandardError) RunHiddenProcess(
        string fileName, string arguments, int timeoutMs)
    {
        var startInfo = new ProcessStartInfo(fileName, arguments)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        Process? process = null;
        try
        {
            process = Process.Start(startInfo);
            if (process == null)
                return (false, -1, "", "无法启动进程");

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(timeoutMs))
            {
                try
                {
                    process.Kill(true);
                    process.WaitForExit(2000);
                }
                catch { }
                return (true, -1, outputTask.GetAwaiter().GetResult(), errorTask.GetAwaiter().GetResult());
            }

            return (false, process.ExitCode,
                outputTask.GetAwaiter().GetResult().Trim(),
                errorTask.GetAwaiter().GetResult().Trim());
        }
        catch (Exception e)
        {
            return (false, -1, "", e.Message);
        }
        finally
        {
            process?.Dispose();
        }
    }

    /// <summary>
    /// 执行一条 adb 命令，并返回命令是否成功
    /// </summary>
    private bool RunAdbCommand(string adbPath, string adbSerial, string args, out string output, out bool timedOut)
    {
        _recoveryToken.ThrowIfCancellationRequested();
        var arguments = string.IsNullOrWhiteSpace(adbSerial) ? args : $"-s {adbSerial} {args}";
        var (commandTimedOut, exitCode, standardOutput, standardError) = RunHiddenProcess(adbPath, arguments, 10000);
        output = standardOutput;
        timedOut = commandTimedOut;

        if (commandTimedOut)
        {
            LoggerHelper.Error($"[RestartGameAction] ADB 命令执行超时: {args}");
            return false;
        }

        if (exitCode != 0)
        {
            LoggerHelper.Error($"[RestartGameAction] ADB 命令执行失败: code={exitCode} out={standardOutput} err={standardError}");
            return false;
        }

        if (!string.IsNullOrWhiteSpace(standardError))
            LoggerHelper.Warning($"[RestartGameAction] ADB 命令返回警告: {standardError}");
        return true;
    }

    private bool RunAdbCommand(string adbPath, string adbSerial, string args, out string output)
        => RunAdbCommand(adbPath, adbSerial, args, out output, out _);

    private bool RunAdbCommand(string adbPath, string adbSerial, string args)
    {
        return RunAdbCommand(adbPath, adbSerial, args, out _);
    }

    private bool TryResolveLaunchActivity(string package, out string launchActivity, out bool adbTimedOut)
    {
        launchActivity = "";
        adbTimedOut = false;
        if (!RunAdbCommand(
                _adbPath!,
                _adbSerial ?? "",
                $"shell cmd package resolve-activity --brief -a android.intent.action.MAIN -c android.intent.category.LAUNCHER {package}",
                out var output,
                out adbTimedOut))
            return false;

        var component = output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .LastOrDefault(line => line.StartsWith($"{package}/", StringComparison.Ordinal));
        if (string.IsNullOrWhiteSpace(component))
            return false;

        launchActivity = component;
        LoggerHelper.Info($"[RestartGameAction] 已解析游戏启动 Activity: {launchActivity}");
        return true;
    }

    private bool IsGameRunning(string package)
    {
        if (RunAdbCommand(_adbPath!, _adbSerial ?? "", $"shell pidof {package} || true", out var processIds))
        {
            var processId = processIds
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(value => int.TryParse(value, out _));
            if (processId != null)
            {
                LoggerHelper.Info($"[RestartGameAction] pidof 检测到游戏进程：{processId}");
                return true;
            }
        }

        if (!RunAdbCommand(_adbPath!, _adbSerial ?? "", "shell ps -A", out var processList)
            || string.IsNullOrWhiteSpace(processList))
        {
            LoggerHelper.Warning("[RestartGameAction] 无法确认游戏进程状态，将继续重启游戏");
            return false;
        }

        var matchingProcess = processList
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(line =>
            {
                var processName = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
                return processName != null
                    && (string.Equals(processName, package, StringComparison.OrdinalIgnoreCase)
                        || processName.StartsWith($"{package}:", StringComparison.OrdinalIgnoreCase));
            });

        if (matchingProcess != null)
        {
            LoggerHelper.Info($"[RestartGameAction] ps 检测到游戏进程：{matchingProcess.Trim()}");
            return true;
        }

        LoggerHelper.Info($"[RestartGameAction] pidof 与 ps 均未发现游戏进程：{package}");
        return false;
    }

    private bool TryRestartGame(string package, out bool adbTimedOut)
    {
        adbTimedOut = false;
        _recoveryToken.ThrowIfCancellationRequested();
        LoggerHelper.Info($"[RestartGameAction] 强制停止游戏进程: {package}");
        if (!RunAdbCommand(_adbPath!, _adbSerial ?? "", $"shell am force-stop {package}", out _, out var stopTimedOut))
            LoggerHelper.Info("[RestartGameAction] 强制停止游戏失败，继续尝试启动游戏");
        adbTimedOut |= stopTimedOut;
        WaitForRecovery(2000);

        LoggerHelper.Info($"[RestartGameAction] 重新启动游戏: {package}");
        if (TryResolveLaunchActivity(package, out var launchActivity, out var resolveTimedOut))
        {
            if (!RunAdbCommand(_adbPath!, _adbSerial ?? "", $"shell am start -n {launchActivity}", out _, out var startTimedOut))
            {
                adbTimedOut |= startTimedOut;
                LoggerHelper.Info("[RestartGameAction] 使用已解析的 Activity 启动游戏失败");
                return false;
            }
        }
        else
        {
            adbTimedOut |= resolveTimedOut;
            if (!RunAdbCommand(
                    _adbPath!,
                    _adbSerial ?? "",
                    $"shell am start -a android.intent.action.MAIN -c android.intent.category.LAUNCHER -p {package}",
                    out _,
                    out var startTimedOut))
            {
                adbTimedOut |= startTimedOut;
                LoggerHelper.Info("[RestartGameAction] 游戏启动失败");
                return false;
            }
        }

        LoggerHelper.Info("[RestartGameAction] 游戏重启完成");
        return true;
    }

    /// <summary>
    /// 从当前处理器收集模拟器环境，优先重启游戏；仅在游戏重启失败时重启模拟器后重试。
    /// 供 pipeline node 与 MATR 层卡死循环检测恢复复用。
    /// emulatorUnresponsive 为 true 表示模拟器已判定无响应（Maa 回调静默超时），
    /// 此时先强制重启模拟器再启动游戏，避免在挂起的实例上白等 ADB 超时。
    /// skipIfRunning 为 true 时，检测到游戏进程已运行则跳过重启。
    /// </summary>
    public static void RestartAndReloadGame(bool logAutoRecovery = true, MaaProcessor? processor = null,
        CancellationToken token = default, bool emulatorUnresponsive = false, bool skipIfRunning = false)
    {
        token.ThrowIfCancellationRequested();
        processor ??= MaaProcessorManager.Instance.Current;
        if (!token.CanBeCanceled && processor?.CancellationTokenSource != null)
            token = processor.CancellationTokenSource.Token;
        token.ThrowIfCancellationRequested();
        if (processor != null) processor.IsGameRecoveryRunning = true;
        try
        {
            if (logAutoRecovery)
                processor?.LogRestartEvent("重启游戏", "检测到游戏疑似卡死", true);
            var action = new RestartGameAction { _recoveryToken = token };
            action.EnsureEmulatorEnvironment(processor);

            var package = GetPackageName();

            if (skipIfRunning && !emulatorUnresponsive && action.IsGameRunning(package))
            {
                LoggerHelper.Info("[RestartGameAction] 游戏已运行，根据设置跳过重启");
                return;
            }

            if (emulatorUnresponsive)
            {
                processor?.LogRestartEvent("重启模拟器", "模拟器无响应，强制重启模拟器", true);
                if (!action.RestartEmulator(force: true))
                {
                    processor?.LogRestartEvent("重启模拟器", "模拟器重启失败，停止任务", true);
                    throw new InvalidOperationException("模拟器重启失败，请检查模拟器路径、实例状态和 ADB 连接");
                }

                if (action.TryRestartGame(package, out _))
                    processor?.LogRestartEvent("重启游戏", "模拟器重启完成，游戏已重新启动", false);
                else
                {
                    processor?.LogRestartEvent("重启游戏", "模拟器已恢复，但游戏启动失败，停止任务", true);
                    throw new InvalidOperationException("模拟器恢复后无法启动游戏");
                }
                return;
            }

            if (action.TryRestartGame(package, out var gameRestartAdbTimedOut))
            {
                processor?.LogRestartEvent("重启游戏", "游戏重启完成", false);
                return;
            }

            var forceRestart = ShouldForceEmulatorRestart(emulatorUnresponsive, gameRestartAdbTimedOut);
            processor?.LogRestartEvent("重启模拟器",
                forceRestart ? "游戏重启期间 ADB 超时，强制重启模拟器" : "游戏重启失败，重启模拟器", true);
            if (!action.RestartEmulator(force: forceRestart))
            {
                processor?.LogRestartEvent("重启模拟器", "模拟器重启失败，停止任务", true);
                throw new InvalidOperationException("模拟器重启失败，请检查模拟器路径、实例状态和 ADB 连接");
            }

            if (!action.TryRestartGame(package, out _))
            {
                processor?.LogRestartEvent("重启游戏", "模拟器已恢复，但游戏启动失败，停止任务", true);
                throw new InvalidOperationException("模拟器恢复后无法启动游戏");
            }

            processor?.LogRestartEvent("重启游戏", "游戏重启完成", false);
        }
        finally
        {
            if (processor != null) processor.IsGameRecoveryRunning = false;
        }
    }

    public bool Run<T>(T context, in RunArgs args, in RunResults results) where T : IMaaContext
    {
        try
        {
            ActionParamHelper.ThrowIfStopping(context);
            var parameters = ActionParamHelper.Parse(args.ActionParam);
            var logAutoRecovery = (bool?)parameters["log_auto_recovery"] ?? true;
            var skipIfRunning = (bool?)parameters["skip_if_running"] ?? false;
            if (skipIfRunning)
                LoggerHelper.Info("[RestartGameAction] 已启用游戏运行检测");
            RestartAndReloadGame(logAutoRecovery, processor: _owner, skipIfRunning: skipIfRunning);
            return true;
        }
        catch (MaaStopException)
        {
            LoggerHelper.Info("[RestartGameAction] 检测到手动停止，已取消执行");
            return false;
        }
        catch (Exception e)
        {
            LoggerHelper.Error($"[RestartGameAction] 错误: {e.Message}");
            return false;
        }
    }
}
