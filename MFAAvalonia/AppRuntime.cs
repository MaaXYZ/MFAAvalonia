using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MFAAvalonia.Helper;

namespace MFAAvalonia;

public static class AppRuntime
{
    public sealed record LaunchCommand(
        bool AutoStart,
        bool QuitAfterRun,
        bool ForceStart,
        string? InstanceSelector);

    public static Dictionary<string, string> Args { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

    private static Mutex? _mutex;
    private static bool _mutexReleased;
    private static readonly object _mutexLock = new();
    private static int _mutexOwnerThreadId = -1;
    private static bool _ownsMutex;
    private static string? _instanceKey;
    private static string? _executablePath;
    private static string? _instanceRecordPath;
    private static string? _pipeName;
    private static CancellationTokenSource? _pipeCancellation;
    private static readonly ConcurrentQueue<LaunchCommand> PendingLaunchCommands = new();
    private static Func<LaunchCommand, Task<bool>>? _launchCommandHandler;

    public static bool IsNewInstance { get; private set; } = true;

    public static bool IsAutoStart => Args.ContainsKey("autostart");

    public static bool QuitAfterRun => Args.ContainsKey("quit-after-run");

    public static bool ForceStart =>
        IsAutoStart
        && RequestedInstance != null
        && Args.ContainsKey("force-start");

    public static string? RequestedInstance =>
        Args.TryGetValue("instance", out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : null;

    public static Dictionary<string, string> ParseArguments(string[] args)
    {
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("-", StringComparison.Ordinal))
            {
                var token = args[i].TrimStart('-');
                var equalsIndex = token.IndexOf('=');
                var key = NormalizeKey(equalsIndex >= 0 ? token[..equalsIndex] : token);
                var inlineValue = equalsIndex >= 0 ? token[(equalsIndex + 1)..] : null;

                if (inlineValue != null)
                {
                    parameters[key] = inlineValue;
                }
                else if (i + 1 < args.Length && !args[i + 1].StartsWith("-", StringComparison.Ordinal))
                {
                    parameters[key] = args[i + 1];
                    i++;
                }
                else
                {
                    parameters[key] = "";
                }
            }
        }
        return parameters;
    }

    private static string NormalizeKey(string key)
    {
        return key.ToLowerInvariant() switch
        {
            "c" or "i" => "instance",
            "q" => "quit-after-run",
            "f" or "forcestart" => "force-start",
            "h" => "help",
            _ => key.ToLowerInvariant()
        };
    }

    public static bool IsHelpRequested(string[] args) => ParseArguments(args).ContainsKey("help");

    public static string GetHelpText(string? executablePath = null)
    {
        var executableName = string.IsNullOrWhiteSpace(executablePath)
            ? "MFAAvalonia"
            : Path.GetFileName(executablePath);

        return $"""
MFAAvalonia 命令行参数

用法:
  {executableName} [参数]

参数:
  -h, --help
      显示本帮助并退出

  --autostart
      启动后自动执行目标实例的任务

  -c, -i, --instance <实例名称或实例 ID>
      指定启动时激活的实例；与 --autostart 配合时指定自动执行的实例
      也支持 -c=<值>、-i=<值> 与 --instance=<值>

  -q, --quit-after-run
      本次命令行自动执行的任务完成后退出程序

  -f, --forceStart
      仅与 --autostart 和 -i/-c/--instance 同时使用时生效
      如果目标实例正在运行，先停止其当前任务，再重新启动

示例:
  {executableName} --instance "日常任务"
  {executableName} --autostart -i "日常任务" --quit-after-run
  {executableName} --autostart -i "日常任务" --forceStart
""";
    }

    public static string CreateInstanceKey(string executablePath)
    {
        var normalizedPath = Path.GetFullPath(executablePath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (OperatingSystem.IsWindows())
            normalizedPath = normalizedPath.ToUpperInvariant();

        var executableHash = TryHashFile(executablePath);
        var coreAssemblyHash = TryHashFile(typeof(AppRuntime).Assembly.Location);

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{normalizedPath}\n{executableHash}\n{coreAssemblyHash}")));
    }

    private static string TryHashFile(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        catch
        {
            return "UNAVAILABLE";
        }
    }

    public static void Initialize(string[] args, string instanceKey)
    {
        Args = ParseArguments(args);
        _instanceKey = instanceKey;
        _executablePath = Environment.ProcessPath;
        // Unix domain socket paths have a short platform limit; keep the
        // full key for mutexes and instance records, but bound the pipe name.
        var pipeInstanceKey = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? instanceKey
            : instanceKey.Length > 12 ? instanceKey[..12] : instanceKey;
        _pipeName = $"MFAAvalonia_{pipeInstanceKey}";
        _instanceRecordPath = Path.Combine(Path.GetTempPath(), "MFAAvalonia", "instances", $"{instanceKey}.json");
        _mutex = new Mutex(true, $"MFAAvalonia_{instanceKey}", out var isNewInstance);
        IsNewInstance = isNewInstance;
        _ownsMutex = isNewInstance;
        _mutexOwnerThreadId = Environment.CurrentManagedThreadId;
        _mutexReleased = false;

        if (isNewInstance)
        {
            WriteInstanceRecord();
            _pipeCancellation = new CancellationTokenSource();
            _ = RunCommandPipeServerAsync(_pipeCancellation.Token);
        }
    }

    public static bool TryForwardLaunchCommand(int timeoutMilliseconds = 3000)
    {
        if (IsNewInstance || string.IsNullOrEmpty(_pipeName))
            return false;

        var command = new LaunchCommand(IsAutoStart, QuitAfterRun, ForceStart, RequestedInstance);
        var deadline = Environment.TickCount64 + timeoutMilliseconds;

        while (Environment.TickCount64 < deadline)
        {
            var connected = false;
            try
            {
                using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                var remaining = (int)Math.Max(1, deadline - Environment.TickCount64);
                client.Connect(Math.Min(remaining, 500));
                connected = true;

                using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                using var reader = new StreamReader(client, Encoding.UTF8, leaveOpen: true);
                writer.WriteLine(JsonSerializer.Serialize(command));
                var response = reader.ReadLineAsync()
                    .WaitAsync(TimeSpan.FromMilliseconds(remaining))
                    .GetAwaiter()
                    .GetResult();
                return string.Equals(response, "OK", StringComparison.Ordinal);
            }
            catch (TimeoutException)
            {
                if (connected) return false;
            }
            catch (IOException)
            {
                if (connected) return false;
            }

            Thread.Sleep(50);
        }

        return false;
    }

    public static bool TryRecoverUnresponsiveInstance(int waitMilliseconds = 5000)
    {
        if (IsNewInstance || string.IsNullOrEmpty(_instanceRecordPath) || string.IsNullOrEmpty(_instanceKey))
            return false;

        InstanceProcessRecord? record;
        try
        {
            record = JsonSerializer.Deserialize<InstanceProcessRecord>(File.ReadAllText(_instanceRecordPath));
        }
        catch
        {
            return false;
        }

        if (record == null || record.ProcessId == Environment.ProcessId || string.IsNullOrWhiteSpace(record.ExecutablePath))
            return false;

        try
        {
            using var process = Process.GetProcessById(record.ProcessId);
            if (process.HasExited
                || process.StartTime.ToUniversalTime().Ticks != record.StartTimeUtcTicks
                || !PathsReferToSameExecutable(process.MainModule?.FileName, record.ExecutablePath)
                || !PathsReferToSameExecutable(record.ExecutablePath, _executablePath))
            {
                return false;
            }

            process.Kill(entireProcessTree: true);
            if (!process.WaitForExit(waitMilliseconds))
                return false;
        }
        catch
        {
            return false;
        }

        try
        {
            _mutex?.Close();
            _mutex = new Mutex(true, $"MFAAvalonia_{_instanceKey}", out var isNewInstance);
            if (!isNewInstance)
            {
                _mutex.Close();
                _mutex = null;
                return false;
            }

            IsNewInstance = true;
            _ownsMutex = true;
            _mutexOwnerThreadId = Environment.CurrentManagedThreadId;
            _mutexReleased = false;
            WriteInstanceRecord();
            _pipeCancellation = new CancellationTokenSource();
            _ = RunCommandPipeServerAsync(_pipeCancellation.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool PathsReferToSameExecutable(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), comparison);
    }

    private static void WriteInstanceRecord()
    {
        if (string.IsNullOrEmpty(_instanceRecordPath) || string.IsNullOrEmpty(_executablePath)) return;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_instanceRecordPath)!);
            var record = new InstanceProcessRecord(
                Environment.ProcessId,
                Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks,
                Path.GetFullPath(_executablePath));
            File.WriteAllText(_instanceRecordPath, JsonSerializer.Serialize(record));
        }
        catch
        {
        }
    }

    private sealed record InstanceProcessRecord(int ProcessId, long StartTimeUtcTicks, string ExecutablePath);

    public static void RegisterLaunchCommandHandler(Func<LaunchCommand, Task<bool>> handler)
    {
        _launchCommandHandler = handler;
        while (PendingLaunchCommands.TryDequeue(out var command))
            _ = handler(command);
    }

    private static async Task RunCommandPipeServerAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && !string.IsNullOrEmpty(_pipeName))
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(cancellationToken);

                using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
                using var writer = new StreamWriter(server, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                var payload = await reader.ReadLineAsync(cancellationToken);
                var command = string.IsNullOrWhiteSpace(payload)
                    ? null
                    : JsonSerializer.Deserialize<LaunchCommand>(payload);

                if (command != null)
                {
                    var handler = _launchCommandHandler;
                    if (handler != null)
                    {
                        var handled = await handler(command)
                            .WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
                        await writer.WriteLineAsync(handled ? "OK" : "FAILED");
                    }
                    else
                    {
                        PendingLaunchCommands.Enqueue(command);
                        await writer.WriteLineAsync("OK");
                    }
                }
                else
                {
                    await writer.WriteLineAsync("INVALID");
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception e)
            {
                try
                {
                    LoggerHelper.Warning($"启动命令管道异常：{e.Message}");
                }
                catch
                {
                }
            }
        }
    }

    public static void ReleaseMutex()
    {
        _pipeCancellation?.Cancel();
        if (_mutexReleased || _mutex == null)
        {
            return;
        }

        if (Environment.CurrentManagedThreadId != _mutexOwnerThreadId)
        {
            try
            {
                _ = DispatcherHelper.RunOnMainThreadAsync(ReleaseMutexInternal);
            }
            catch (Exception)
            {
                try
                {
                    _mutex?.Close();
                    _mutex = null;
                    _mutexReleased = true;
                }
                catch
                {
                }
            }
            return;
        }

        ReleaseMutexInternal();
    }

    private static void ReleaseMutexInternal()
    {
        lock (_mutexLock)
        {
            if (_mutexReleased || _mutex == null)
            {
                return;
            }

            try
            {
                if (_ownsMutex)
                    _mutex.ReleaseMutex();
                _mutex.Close();
                _mutex = null;
                _mutexReleased = true;
                if (_ownsMutex && !string.IsNullOrEmpty(_instanceRecordPath))
                {
                    try
                    {
                        File.Delete(_instanceRecordPath);
                    }
                    catch
                    {
                    }
                }
                _ownsMutex = false;
            }
            catch (ApplicationException)
            {
                try
                {
                    _mutex?.Close();
                    _mutex = null;
                    _mutexReleased = true;
                }
                catch (Exception)
                {
                }
            }
            catch (Exception e)
            {
                LoggerHelper.Error($"释放应用互斥锁失败：原因={e.Message}", e);
            }
        }
    }
}
