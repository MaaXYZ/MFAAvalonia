using MaaFramework.Binding;
using MaaFramework.Binding.Custom;
using MFAAvalonia.Configuration;
using MFAAvalonia.Extensions;
using MFAAvalonia.Helper;
using MFAAvalonia.ViewModels.Pages;
using MFAAvalonia.ViewModels.Windows;
using Microsoft.Win32.SafeHandles;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MaaAgentClient = MaaFramework.Binding.MaaAgentClient;
using MaaTasker = MaaFramework.Binding.MaaTasker;

namespace MFAAvalonia.Extensions.MaaFW;

/// <summary>
/// 单个 Agent 的运行时上下文
/// </summary>
public class AgentContext
{
    public MaaAgentClient? Client { get; set; }
    public Process? Process { get; set; }
    public IPlatformAgentSession? PlatformSession { get; set; }
    public CancellationTokenSource? ReadCancellationTokenSource { get; set; }
    public readonly Lock ReadLock = new();
    public SafeJobHandle? JobHandle { get; set; }
    public readonly Lock JobLock = new();
    public MaaInterface.MaaInterfaceAgent? Config { get; set; }

    [SupportedOSPlatform("windows")]
    public sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeJobHandle() : base(true) { }
        protected override bool ReleaseHandle() => AgentHelper.CloseHandle(handle);
    }
}

/// <summary>
/// Agent 启动/停止/管理的静态辅助类，支持多 Agent 同时运行
/// </summary>
public static class AgentHelper
{
    private static readonly Random Random = new();
    private const string PiInterfaceVersion = "v2.5.0";
    /// <summary>
    /// 静态锁，防止多实例同时调用 MaaAgentClient.Create 导致内部字典键冲突
    /// </summary>
    private static readonly Lock AgentCreateLock = new();
    /// <summary>
    /// 标记是否有过 AgentClient 创建历史，用于键冲突重试
    /// </summary>
    private static volatile bool _hasPreviousAgentClient = false;
    private const int MaxAgentCreateAttempts = 2;
    private const string PiClientName = "MFAAvalonia";
    private static readonly JsonSerializerSettings CompactJsonSettings = new()
    {
        Formatting = Formatting.None,
        NullValueHandling = NullValueHandling.Ignore,
        DefaultValueHandling = DefaultValueHandling.Ignore
    };

    private static IDisposable PushAgentLogContext(MaaProcessor processor, string operation)
    {
        return LoggerHelper.PushContext(
            source: "Agent",
            operation: operation,
            instanceId: processor.InstanceId,
            instanceName: MaaProcessorManager.Instance.GetInstanceName(processor.InstanceId));
    }

    private static MaaAgentClient CreateAgentClient(
        MaaTasker tasker,
        InstanceConfiguration instanceConfig,
        string identifier,
        bool retryOnFirstArgumentException)
    {
        for (var attempt = 1; attempt <= MaxAgentCreateAttempts; attempt++)
        {
            try
            {
                lock (AgentCreateLock)
                {
                    // Android hosts python-for-android in a separate application process.
                    // MaaAgent's IPC transport works on AOSP but can never finish its
                    // cross-process handshake on vendor emulators such as MuMu. TCP uses
                    // an automatically selected loopback port and is supported natively by
                    // MaaAgentServer, so make it the Android default without changing Desktop.
                    var useTcp = OperatingSystem.IsAndroid()
                                 || instanceConfig.GetValue(ConfigurationKeys.AgentTcpMode, false);
                    // Do not register Android's continuously active controller/tasker
                    // event sinks before the startup handshake. The virtual-display
                    // preview emits screencap events immediately; with TCP those events
                    // can overtake StartUpResponse and keep MaaAgentClientConnect stuck
                    // draining ControllerEventResponse messages forever. Resource is the
                    // only binding required by MaaAgentClient::connect(). The remaining
                    // sinks are attached after the handshake succeeds below.
                    var client = useTcp && OperatingSystem.IsAndroid()
                        ? MaaAgentClient.CreateTcp(tasker.Resource)
                        : useTcp
                            ? MaaAgentClient.CreateTcp(tasker)
                            : MaaAgentClient.Create(identifier, tasker);
                    _hasPreviousAgentClient = true;
                    return client;
                }
            }
            catch (ArgumentException) when (attempt < MaxAgentCreateAttempts
                                             && (retryOnFirstArgumentException || _hasPreviousAgentClient))
            {
                LoggerHelper.Warning("AgentClient 创建失败，等待旧实例清理后重试一次...");
                GC.Collect();
                GC.WaitForPendingFinalizers();
                Thread.Sleep(1000);
            }
        }

        throw new InvalidOperationException("AgentClient 创建重试流程异常结束。");
    }

    /// <summary>
    /// 启动所有配置的 Agent
    /// </summary>
    public static async Task<List<AgentContext>> StartAgentsAsync(
        MaaTasker tasker,
        List<MaaInterface.MaaInterfaceAgent> agentConfigs,
        InstanceConfiguration instanceConfig,
        MaaProcessor processor,
        CancellationToken token)
    {
        var contexts = new List<AgentContext>();
        var validConfigs = agentConfigs.Where(a => a.ChildExec != null).ToList();
        if (validConfigs.Count == 0)
            return contexts;

        processor.AddLogByKey(LangKeys.StartingAgent, (Avalonia.Media.IBrush?)null);

        foreach (var agentConfig in validConfigs)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var ctx = await StartSingleAgentAsync(tasker, agentConfig, instanceConfig, processor, token);
                if (ctx != null)
                    contexts.Add(ctx);
            }
            catch (OperationCanceledException)
            {
                // 取消时清理已启动的 agent
                KillAllAgents(contexts);
                throw;
            }
            catch (Exception ex)
            {
                KillAllAgents(contexts);
                throw new InvalidOperationException(
                    $"Agent '{agentConfig.ChildExec}' failed to start: {ex.Message}",
                    ex);
            }
        }

        return contexts;
    }

    /// <summary>
    /// 启动单个 Agent
    /// </summary>
    private static async Task<AgentContext?> StartSingleAgentAsync(
        MaaTasker tasker,
        MaaInterface.MaaInterfaceAgent agentConfig,
        InstanceConfiguration instanceConfig,
        MaaProcessor processor,
        CancellationToken token)
    {
        var ctx = new AgentContext
        {
            Config = agentConfig
        };

        var chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        var baseIdentifier = string.IsNullOrWhiteSpace(agentConfig.Identifier)
            ? new string(Enumerable.Repeat(chars, 8).Select(c => c[Random.Next(c.Length)]).ToArray())
            : agentConfig.Identifier;
        // Append instance ID to prevent socket name collision when multiple instances share the same config identifier.
        var identifier = string.IsNullOrWhiteSpace(agentConfig.Identifier)
            ? baseIdentifier
            : $"{baseIdentifier}_{processor.InstanceId}";
        LoggerHelper.Info($"Agent 标识符：{identifier}");

        ctx.Client = CreateAgentClient(tasker, instanceConfig, identifier, retryOnFirstArgumentException: false);

        var timeOut = agentConfig.Timeout ?? -1;
        if (timeOut > 0)
            ctx.Client.SetTimeout(TimeSpan.FromSeconds(timeOut));
        else if (OperatingSystem.IsAndroid())
            // This is only the observation interval for the p4a service's terminal
            // startup event, not the criterion for declaring startup failed.
            ctx.Client.SetTimeout(TimeSpan.FromSeconds(1));
        ctx.Client.Releasing += (_, _) =>
        {
            LoggerHelper.Info("Agent 进程已退出。");
            ctx.Client = null;
        };

        LoggerHelper.Info($"Agent 客户端哈希：{ctx.Client?.GetHashCode()}");

        if (!Directory.Exists(AppPaths.DataRoot))
            Directory.CreateDirectory(AppPaths.DataRoot);

        var program = MaaInterface.ReplacePlaceholder(agentConfig.ChildExec, AppPaths.DataRoot, true);
        if (IsPathLike(program))
            program = Path.GetFullPath(program, AppPaths.DataRoot);

        var rawArgs = agentConfig.ChildArgs ?? [];
        var platformArgs = MaaInterface.ReplacePlaceholder(rawArgs, AppPaths.DataRoot, true)
            .Select(arg =>
            {
                if (IsPathLike(arg))
                {
                    try { return Path.GetFullPath(arg, AppPaths.DataRoot); }
                    catch (Exception) { return arg; }
                }
                return arg;
            })
            .ToList();

        if (PlatformAgentFactory.Start != null)
        {
            return await StartPlatformAgentAsync(
                ctx,
                tasker,
                program,
                platformArgs,
                processor,
                token);
        }

        if (OperatingSystem.IsAndroid())
        {
            throw new PlatformNotSupportedException(
                "This Android APK does not contain a platform Agent runtime. "
                + "Build the resource APK with the python-for-android AAR enabled in workflows/install.yml.");
        }

        var replacedArgs = platformArgs.Select(ConvertPath).ToList();

        var executablePath = program;

        // Bare executable names (for example, "python") are resolved through PATH
        // by Process.Start. Validate only explicit filesystem paths here.
        if (string.IsNullOrWhiteSpace(executablePath)
            || (HasExplicitPath(executablePath) && !File.Exists(executablePath)))
        {
            var errorMsg = LangKeys.AgentExecutableNotFound.ToLocalizationFormatted(false, executablePath);
            throw new FileNotFoundException(errorMsg, executablePath);
        }

        var isPythonProcess = IsPythonProcess(program, executablePath, replacedArgs);
        var shouldAddUnbufferedFlag = isPythonProcess
            && replacedArgs.Any(arg => arg.EndsWith(".py", StringComparison.OrdinalIgnoreCase))
            && !replacedArgs.Any(arg => string.Equals(arg, "-u", StringComparison.OrdinalIgnoreCase));

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = AppPaths.DataRoot,
            Arguments = $"{(shouldAddUnbufferedFlag ? "-u " : "")}{string.Join(" ", replacedArgs)} {ctx.Client.Id}",
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            StandardErrorEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
            WindowStyle = ProcessWindowStyle.Hidden,
            CreateNoWindow = true
        };

        startInfo.Environment["MFA_INSTANCE_ID"] = processor.InstanceId;
        startInfo.Environment["MFA_INSTANCE_NAME"] =
            MaaProcessorManager.Instance.GetInstanceName(processor.InstanceId) ?? string.Empty;
        ApplyChildProcessEncodingEnvironmentVariables(startInfo, isPythonProcess);
        ApplyPiEnvironmentVariables(startInfo, processor);

        LoggerHelper.Info(
            $"Agent 启动命令：{program} {(shouldAddUnbufferedFlag ? "-u " : "")}{string.Join(" ", replacedArgs)} {ctx.Client.Id} "
            + $"socket_id={ctx.Client.Id} instance_id={startInfo.Environment["MFA_INSTANCE_ID"]} instance_name={startInfo.Environment["MFA_INSTANCE_NAME"]}");

        IMaaAgentClient.AgentServerStartupMethod method = (s, directory) =>
        {
            try
            {
                ctx.Process = Process.Start(startInfo);
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode is 2 or 3)
            {
                var errorMsg = LangKeys.AgentExecutableNotFound.ToLocalizationFormatted(false, executablePath);
                throw new FileNotFoundException(errorMsg, executablePath, ex);
            }
            if (ctx.Process == null)
                LoggerHelper.Error("Agent 启动失败。");
            else
            {
                ctx.Process.Exited += (_, _) =>
                {
                    LoggerHelper.Info("Agent 进程已退出。");
                    StopReadStreams(ctx);
                    ctx.Process = null;
                };

                BindProcessLifetime(ctx);

                var readToken = ResetReadCancellation(ctx).Token;
                TaskManager.RunTaskAsync(() => ReadProcessStreamAsync(ctx.Process.StandardOutput.BaseStream,
                    line => HandleOutputLine(line, processor, "Stdout"), readToken), token: readToken, noMessage: true);
                TaskManager.RunTaskAsync(() => ReadProcessStreamAsync(ctx.Process.StandardError.BaseStream,
                    line => HandleOutputLine(line, processor, "StdErr"), readToken), token: readToken, noMessage: true);

                TaskManager.RunTaskAsync(async () => await ctx.Process.WaitForExitAsync(token), token: token, name: "Agent程序启动");
            }
            return ctx.Process;
        };

        // 重连逻辑
        const int maxRetries = 3;
        bool linkStartSuccess = false;
        Exception? lastException = null;

        for (int retryCount = 0; retryCount < maxRetries && !linkStartSuccess && !token.IsCancellationRequested; retryCount++)
        {
            try
            {
                token.ThrowIfCancellationRequested();

                if (retryCount > 0)
                {
                    LoggerHelper.Info($"Agent LinkStart 重试：第 {retryCount + 1}/{maxRetries} 次。");
                    processor.AddLog(LangKeys.AgentConnectionRetry.ToLocalizationFormatted(false, $"{retryCount + 1}/{maxRetries}"),
                        Avalonia.Media.Brushes.Orange, changeColor: false);
                    await Task.Delay(1000 * retryCount, token);

                    if (ctx.Process != null && !ctx.Process.HasExited)
                    {
                        try
                        {
                            ctx.Process.Kill(true);
                            ctx.Process.WaitForExit(3000);
                        }
                        catch (Exception killEx)
                        {
                            LoggerHelper.Warning($"结束 Agent 进程失败：{killEx.Message}");
                        }
                        ctx.Process.Dispose();
                        ctx.Process = null;
                    }
                }

                linkStartSuccess = ctx.Client.LinkStart(method, token);
            }
            catch (OperationCanceledException)
            {
                LoggerHelper.Info("用户已取消 Agent LinkStart。");
                throw;
            }
            catch (SEHException sehEx)
            {
                lastException = sehEx;
                LoggerHelper.Warning($"Agent LinkStart 发生 SEHException（第 {retryCount + 1} 次）：{sehEx.Message}");

                if (retryCount < maxRetries - 1)
                {
                    if (token.IsCancellationRequested)
                    {
                        LoggerHelper.Info("用户已取消 Agent 重试。");
                        token.ThrowIfCancellationRequested();
                    }

                    KillSingleAgent(ctx);

                    try
                    {
                        ctx.Client = CreateAgentClient(tasker, instanceConfig, identifier, retryOnFirstArgumentException: true);
                        timeOut = agentConfig.Timeout ?? -1;
                        if (timeOut > 0)
                            ctx.Client.SetTimeout(TimeSpan.FromSeconds(timeOut));
                        ctx.Client.Releasing += (_, _) => LoggerHelper.Info("Agent 进程已退出。");
                    }
                    catch (Exception recreateEx)
                    {
                        LoggerHelper.Error($"重建 AgentClient 失败：{recreateEx.Message}");
                        throw;
                    }
                }
            }
            catch (Exception ex)
            {
                lastException = ex;
                LoggerHelper.Warning($"Agent LinkStart 发生异常（第 {retryCount + 1} 次）：{ex.Message}");
                break;
            }
        }

        if (token.IsCancellationRequested && !linkStartSuccess)
        {
            LoggerHelper.Info("Agent LinkStart 循环因取消而退出。");
            token.ThrowIfCancellationRequested();
        }

        if (!linkStartSuccess)
        {
            var errorMessage = lastException?.Message ?? "Failed to LinkStart agentClient!";
            var agentProcess = ctx.Process;
            if (agentProcess != null)
            {
                try
                {
                    var errorDetails = new StringBuilder();
                    errorDetails.AppendLine(errorMessage);

                    if (agentProcess.HasExited)
                    {
                        var exitCode = agentProcess.ExitCode;
                        var stderr = await agentProcess.StandardError.ReadToEndAsync(token);
                        var stdout = await agentProcess.StandardOutput.ReadToEndAsync(token);
                        errorDetails.AppendLine($"Agent 进程退出码：{exitCode}");
                        if (!string.IsNullOrWhiteSpace(stderr))
                        {
                            errorDetails.AppendLine($"标准错误输出：{stderr}");
                            using (PushAgentLogContext(processor, "StdErr"))
                                LoggerHelper.Error(stderr);
                            processor.AddLog($"Agent Error: {stderr}", Avalonia.Media.Brushes.OrangeRed, changeColor: false,
                                useMarkdown: true);
                        }
                        if (!string.IsNullOrWhiteSpace(stdout))
                        {
                            errorDetails.AppendLine($"标准输出：{stdout}");
                            using (PushAgentLogContext(processor, "Stdout"))
                                LoggerHelper.Info(stdout);
                        }
                        errorMessage = errorDetails.ToString();
                    }
                    else
                    {
                        if (agentProcess.WaitForExit(3000))
                        {
                            var exitCode = agentProcess.ExitCode;
                            var stderr = await agentProcess.StandardError.ReadToEndAsync(token);
                            var stdout = await agentProcess.StandardOutput.ReadToEndAsync(token);
                            errorDetails.AppendLine($"Agent 进程退出码：{exitCode}");
                            if (!string.IsNullOrWhiteSpace(stderr))
                            {
                                errorDetails.AppendLine($"标准错误输出：{stderr}");
                                using (PushAgentLogContext(processor, "StdErr"))
                                    LoggerHelper.Error(stderr);
                                processor.AddLog($"Agent Error: {stderr}", Avalonia.Media.Brushes.OrangeRed, changeColor: false,
                                    useMarkdown: true);
                                if (!string.IsNullOrWhiteSpace(stdout))
                                {
                                    errorDetails.AppendLine($"标准输出：{stdout}");
                                    using (PushAgentLogContext(processor, "Stdout"))
                                        LoggerHelper.Info(stdout);
                                    errorMessage = errorDetails.ToString();
                                }
                            }
                        }
                    }
                }
                catch (Exception readEx)
                {
                    LoggerHelper.Warning($"读取 Agent 进程输出失败：{readEx.Message}");
                }
            }
            throw lastException == null
                ? new InvalidOperationException(errorMessage)
                : new InvalidOperationException(errorMessage, lastException);
        }

        return ctx;
    }

    private static async Task<AgentContext?> StartPlatformAgentAsync(
        AgentContext ctx,
        MaaTasker tasker,
        string program,
        IReadOnlyList<string> arguments,
        MaaProcessor processor,
        CancellationToken token)
    {
        var client = ctx.Client
            ?? throw new InvalidOperationException("Agent client was not created before platform startup.");
        var launcher = PlatformAgentFactory.Start
            ?? throw new PlatformNotSupportedException("The platform Agent launcher is unavailable.");

        var request = new PlatformAgentStartRequest(
            client.Id,
            program,
            arguments,
            AppPaths.DataRoot,
            processor.InstanceId,
            MaaProcessorManager.Instance.GetInstanceName(processor.InstanceId) ?? string.Empty,
            line => HandleOutputLine(line, processor, "Stdout"));

        try
        {
            ctx.PlatformSession = launcher(request)
                ?? throw new InvalidOperationException("The platform Agent launcher did not create a session.");

            LoggerHelper.Info(
                $"Platform Agent started: program={program}, socket_id={client.Id}, instance_id={processor.InstanceId}");

            // A python-for-android service first unpacks its private Python bundle in another
            // Android process. Keep attempting the native Agent connection while that happens.
            const int maxAttempts = 30;
            Exception? lastException = null;
            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                token.ThrowIfCancellationRequested();
                ThrowIfPlatformAgentStartupFailed(ctx.PlatformSession);
                try
                {
                    var connected = PlatformAgentFactory.LinkStart?.Invoke(client)
                                    ?? client.LinkStart();
                    if (connected)
                    {
                        if (ctx.PlatformSession is IPlatformAgentStartupStatus startupStatus)
                            startupStatus.CompleteStartup();
                        // Complete the bindings only after StartUpResponse has been
                        // consumed. This preserves normal Agent event forwarding while
                        // preventing Android preview events from racing the handshake.
                        // On Android the Agent client is created from Resource and its
                        // custom action/recognition callbacks already receive the native
                        // MaaContext for reverse calls. Registering Tasker additionally
                        // installs synchronous Tasker/Context event sinks. Those sinks
                        // can block MaaFramework inside Node.Action.Starting and prevent
                        // the controller action from ever running. Desktop keeps its
                        // established event-forwarding behavior.
                        if (!OperatingSystem.IsAndroid())
                            client.Tasker = tasker;
                        // Android's virtual-display preview continuously captures frames.
                        // Registering the controller sink forwards two Agent messages for
                        // every preview frame and can starve the request/response channel.
                        // Tasker reverse requests can still expose the bound controller, so
                        // the Android Agent keeps full context/controller functionality
                        // without forwarding this high-frequency event stream.
                        if (!OperatingSystem.IsAndroid())
                            client.Controller = tasker.Controller;
                        processor.AddLog(
                            "python-for-android Agent 已连接",
                            (Avalonia.Media.IBrush?)null);
                        LoggerHelper.Info("python-for-android Agent connected successfully.");
                        return ctx;
                    }
                }
                catch (Exception ex)
                {
                    ThrowIfPlatformAgentStartupFailed(ctx.PlatformSession);
                    lastException = ex;
                    LoggerHelper.Warning(
                        $"Platform Agent connection attempt {attempt}/{maxAttempts} failed: {ex.Message}");
                }

                if (attempt < maxAttempts)
                    await Task.Delay(TimeSpan.FromSeconds(1), token);
            }

            throw new InvalidOperationException(
                lastException?.Message ?? "The platform Agent did not connect within 30 seconds.",
                lastException);
        }
        catch
        {
            KillSingleAgent(ctx);
            throw;
        }
    }

    private static void ThrowIfPlatformAgentStartupFailed(IPlatformAgentSession? session)
    {
        if (session is IPlatformAgentStartupStatus { StartupFailure: { } failure })
            throw new InvalidOperationException($"Android Python Agent startup failed: {failure}");
    }

    private static void ApplyPiEnvironmentVariables(ProcessStartInfo startInfo, MaaProcessor processor)
    {
        SetEnvironmentVariable(startInfo, "PI_INTERFACE_VERSION", PiInterfaceVersion);
        SetEnvironmentVariable(startInfo, "PI_CLIENT_NAME", PiClientName);
        SetEnvironmentVariable(startInfo, "PI_CLIENT_VERSION", RootViewModel.Version);
        SetEnvironmentVariable(startInfo, "PI_CLIENT_LANGUAGE", NormalizePiLanguageCode(LanguageHelper.CurrentLanguage));
        SetEnvironmentVariable(startInfo, "PI_CLIENT_MAAFW_VERSION", GetPiClientMaaFwVersion());
        SetEnvironmentVariable(startInfo, "PI_VERSION", MaaProcessor.Interface?.Version);

        var viewModel = processor.ViewModel;
        SetEnvironmentVariable(startInfo, "PI_CONTROLLER", BuildControllerSnapshotJson(viewModel));
        SetEnvironmentVariable(startInfo, "PI_RESOURCE", BuildResourceSnapshotJson(viewModel));
    }

    private static void SetEnvironmentVariable(ProcessStartInfo startInfo, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        startInfo.Environment[key] = value;
    }

    private static void ApplyChildProcessEncodingEnvironmentVariables(ProcessStartInfo startInfo, bool isPythonProcess)
    {
        if (!OperatingSystem.IsWindows())
        {
            SetEnvironmentVariable(startInfo, "LANG", "C.UTF-8");
            SetEnvironmentVariable(startInfo, "LC_ALL", "C.UTF-8");
        }

        if (!isPythonProcess)
            return;

        // Force Python and pip to emit UTF-8 regardless of the host system locale.
        SetEnvironmentVariable(startInfo, "PYTHONIOENCODING", "utf-8");
        SetEnvironmentVariable(startInfo, "PYTHONUTF8", "1");
    }

    private static bool IsPythonProcess(string? program, string executablePath, IReadOnlyCollection<string> args)
    {
        if (!string.IsNullOrWhiteSpace(program)
            && program.Contains("python", StringComparison.OrdinalIgnoreCase))
            return true;

        if (!string.IsNullOrWhiteSpace(executablePath)
            && Path.GetFileName(executablePath).Contains("python", StringComparison.OrdinalIgnoreCase))
            return true;

        return args.Any(arg => arg.EndsWith(".py", StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizePiLanguageCode(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
            return string.Empty;

        return language.Replace('-', '_').ToLowerInvariant();
    }

    private static string GetPiClientMaaFwVersion()
    {
        try
        {
            return NativeBindingContext.LibraryVersion ?? string.Empty;
        }
        catch (Exception ex)
        {
            LoggerHelper.Warning($"读取 MaaFramework 版本失败：{ex.Message}");
            return string.Empty;
        }
    }

    private static string? BuildControllerSnapshotJson(TaskQueueViewModel? viewModel)
    {
        var controller = viewModel?.SelectedController;
        if (controller == null)
            return null;

        var snapshot = JObject.FromObject(controller, JsonSerializer.CreateDefault(CompactJsonSettings));
        ApplyLocalizedUiFields(snapshot, controller.Label, controller.Name, controller.Description);
        return snapshot.ToString(Formatting.None);
    }

    private static string? BuildResourceSnapshotJson(TaskQueueViewModel? viewModel)
    {
        var resource = viewModel?.CurrentResources.FirstOrDefault(r =>
            string.Equals(r.Name, viewModel.CurrentResource, StringComparison.Ordinal));
        if (resource == null)
            return null;

        var snapshot = JObject.FromObject(resource, JsonSerializer.CreateDefault(CompactJsonSettings));
        ApplyLocalizedUiFields(snapshot, resource.Label, resource.Name, resource.Description);
        return snapshot.ToString(Formatting.None);
    }

    private static void ApplyLocalizedUiFields(JObject snapshot, string? label, string? fallbackName, string? description)
    {
        var finalLabel = LanguageHelper.GetLocalizedDisplayName(label, fallbackName ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(finalLabel))
            snapshot["label"] = finalLabel;

        var finalDescription = ResolveLocalizedDescription(description);
        if (!string.IsNullOrWhiteSpace(finalDescription))
            snapshot["description"] = finalDescription;
        else
            snapshot.Remove("description");
    }

    private static string ResolveLocalizedDescription(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
            return string.Empty;

        try
        {
            return LanguageHelper.GetLocalizedString(description.ResolveContentAsync().GetAwaiter().GetResult());
        }
        catch (Exception ex)
        {
            LoggerHelper.Warning($"解析 Agent 环境变量描述文本失败：{ex.Message}");
            return string.Empty;
        }
    }

    /// <summary>
    /// 终止所有 Agent 进程并释放资源
    /// </summary>
    public static bool KillAllAgents(List<AgentContext> contexts, MaaTasker? taskerToDispose = null)
    {
        var allProcessesExited = true;
        AgentContext[] contextsToStop;
        lock (contexts)
        {
            contextsToStop = contexts.ToArray();
            contexts.Clear();
        }

        foreach (var ctx in contextsToStop)
        {
            allProcessesExited &= KillSingleAgent(ctx, taskerToDispose);
        }
        _hasPreviousAgentClient = false;

        // 只在最后处理一次 tasker
        if (taskerToDispose != null)
        {
            DisposeMaaTasker(taskerToDispose);
        }

        return allProcessesExited;
    }

    /// <summary>
    /// 终止单个 Agent（不处理 MaaTasker）
    /// </summary>
    public static bool KillSingleAgent(AgentContext ctx, MaaTasker? taskerToDispose = null)
    {
        var agentClient = ctx.Client;
        var agentProcess = ctx.Process;
        var platformSession = ctx.PlatformSession;
        var processExited = true;

        StopReadStreams(ctx);

        ctx.Client = null;
        ctx.Process = null;
        ctx.PlatformSession = null;

        // 步骤 1: 停止 AgentClient
        if (agentClient != null)
        {
            LoggerHelper.Info("正在停止 AgentClient 连接。");
            try
            {
                bool shouldStop = false;
                try { shouldStop = !agentClient.IsStateless && !agentClient.IsInvalid; }
                catch (ObjectDisposedException) { }

                if (shouldStop)
                {
                    try
                    {
                        _ = PlatformAgentFactory.LinkStop?.Invoke(agentClient)
                            ?? agentClient.LinkStop();
                        LoggerHelper.Info("AgentClient LinkStop 成功。");
                    }
                    catch (Exception e)
                    {
                        LoggerHelper.Warning($"AgentClient LinkStop 失败：{e.Message}");
                    }
                }
            }
            catch (Exception e)
            {
                LoggerHelper.Warning($"检查 AgentClient LinkStop 状态失败：{e.Message}");
            }
        }

        // 步骤 2: 终止进程
        if (agentProcess != null)
        {
            LoggerHelper.Info("正在终止 Agent 进程。");
            try
            {
                var hasExited = true;
                try { hasExited = agentProcess.HasExited; }
                catch (InvalidOperationException) { }
                catch (Exception ex)
                {
                    LoggerHelper.Warning($"检查 Agent 进程是否退出失败：{ex.Message}");
                    hasExited = false;
                }

                if (!hasExited)
                {
                    try
                    {
                        LoggerHelper.Info($"正在结束 Agent 进程：{agentProcess.ProcessName}");
                        agentProcess.Kill(true);
                        processExited = agentProcess.WaitForExit(5000);
                        if (processExited)
                            LoggerHelper.Info("Agent 进程已成功结束。");
                        else
                            LoggerHelper.Error("结束 Agent 进程超时：等待 5 秒后进程仍未退出。");
                    }
                    catch (Exception ex)
                    {
                        LoggerHelper.Warning($"结束 Agent 进程失败：{ex.Message}");
                        processExited = false;
                    }
                }
                else
                {
                    LoggerHelper.Info("Agent 进程已提前退出。");
                }
            }
            catch (Exception e)
            {
                LoggerHelper.Error($"处理 Agent 进程时发生错误：{e.Message}");
                processExited = false;
            }
            finally
            {
                DisposeJob(ctx);
                if (!processExited)
                {
                    try
                    {
                        // Closing the Windows job object may terminate the process tree. Verify
                        // once more before reporting the update environment as safe.
                        processExited = agentProcess.WaitForExit(1000);
                    }
                    catch (Exception ex)
                    {
                        LoggerHelper.Warning($"再次确认 Agent 进程退出状态失败：{ex.Message}");
                    }
                }
                try { agentProcess.Dispose(); }
                catch (Exception e) { LoggerHelper.Warning($"释放 Agent 进程对象失败：{e.Message}"); }
            }
        }
        else
        {
            DisposeJob(ctx);
        }

        if (platformSession != null)
        {
            try
            {
                platformSession.Dispose();
                LoggerHelper.Info("Platform Agent service stopped.");
            }
            catch (Exception ex)
            {
                LoggerHelper.Warning($"Failed to stop the platform Agent service: {ex.Message}");
                processExited = false;
            }
        }

        return processExited;
    }

    /// <summary>
    /// 停止所有 Agent 的输出流读取
    /// </summary>
    public static void StopAllReadStreams(List<AgentContext> contexts)
    {
        foreach (var ctx in contexts)
            StopReadStreams(ctx);
    }

    /// <summary>
    /// 检查是否有任何 Agent 配置需要启动
    /// </summary>
    public static bool HasAgentConfigs(List<MaaInterface.MaaInterfaceAgent>? agents)
    {
        return agents is { Count: > 0 } && agents.Any(a => a.ChildExec != null);
    }

    #region 内部辅助方法

    private static CancellationTokenSource ResetReadCancellation(AgentContext ctx)
    {
        lock (ctx.ReadLock)
        {
            ctx.ReadCancellationTokenSource?.Cancel();
            ctx.ReadCancellationTokenSource?.Dispose();
            ctx.ReadCancellationTokenSource = new CancellationTokenSource();
            return ctx.ReadCancellationTokenSource;
        }
    }

    private static void StopReadStreams(AgentContext ctx)
    {
        lock (ctx.ReadLock)
        {
            if (ctx.ReadCancellationTokenSource == null) return;
            ctx.ReadCancellationTokenSource.Cancel();
            ctx.ReadCancellationTokenSource.Dispose();
            ctx.ReadCancellationTokenSource = null;
        }
    }

    private static void HandleOutputLine(string? line, MaaProcessor processor, string streamKind)
    {
        if (string.IsNullOrEmpty(line)) return;

        var outData = line;
        try { outData = Regex.Replace(outData, @"\x1B\[[0-9;]*[a-zA-Z]", ""); }
        catch (Exception) { }
        outData = DecodeUnicodeEscapes(outData);

        // Some Agent implementations prefix MFA's severity protocol with '*'
        // (for example "*info: message"). Accept that established form while
        // preserving the existing desktop-compatible "info: message" syntax.
        if (outData.StartsWith('*')
            && outData.Length > 1
            && MaaProcessor.CheckShouldLog(outData[1..]))
        {
            outData = outData[1..];
        }

        DispatcherHelper.PostOnMainThread(() =>
        {
            if (MaaProcessor.CheckShouldLog(outData))
                processor.AddLog(outData, (Avalonia.Media.IBrush?)null, useMarkdown: true);
            else
            {
                using var _ = PushAgentLogContext(processor, streamKind);
                LoggerHelper.Info(outData);
            }
        });
    }

    private static string ConvertPath(string path)
    {
        if (Path.Exists(path) && !path.Contains("\""))
            return $"\"{path}\"";
        return path;
    }

    private static bool IsPathLike(string? input)
    {
        if (string.IsNullOrEmpty(input)) return false;
        bool hasPathSeparator = input.Contains(Path.DirectorySeparatorChar) || input.Contains(Path.AltDirectorySeparatorChar);
        bool isAbsolutePath = Path.IsPathRooted(input);
        bool isRelativePath = input.StartsWith("./") || input.StartsWith("../") || (hasPathSeparator && !input.StartsWith("-"));
        bool hasFileExtension = Path.HasExtension(input) && !input.StartsWith("-");
        return hasPathSeparator || isAbsolutePath || isRelativePath || hasFileExtension;
    }

    private static bool HasExplicitPath(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return false;

        return Path.IsPathRooted(input)
               || input.Contains(Path.DirectorySeparatorChar)
               || input.Contains(Path.AltDirectorySeparatorChar)
               || input.StartsWith("./", StringComparison.Ordinal)
               || input.StartsWith("../", StringComparison.Ordinal);
    }

    private static void DisposeMaaTasker(MaaTasker maaTasker)
    {
        if (maaTasker.IsRunning && !maaTasker.IsStopping)
        {
            LoggerHelper.Info("释放前正在停止 MaaTasker。");
            try
            {
                var stopResult = maaTasker.Stop().Wait();
                LoggerHelper.Info($"MaaTasker 停止结果：{stopResult}");
            }
            catch (ObjectDisposedException) { LoggerHelper.Info("停止 MaaTasker 时对象已被释放。"); }
            catch (Exception e) { LoggerHelper.Warning($"停止 MaaTasker 失败：{e.Message}"); }
        }

        LoggerHelper.Info("正在释放 MaaTasker。");
        try
        {
            maaTasker.Dispose();
            LoggerHelper.Info("MaaTasker 已成功释放。");
        }
        catch (ObjectDisposedException) { LoggerHelper.Info("MaaTasker 已提前释放。"); }
        catch (Exception e) { LoggerHelper.Warning($"释放 MaaTasker 失败：{e.Message}"); }
    }

    #endregion

    #region 进程生命周期绑定 (Windows)

    private static void BindProcessLifetime(AgentContext ctx)
    {
        if (!OperatingSystem.IsWindows()) return;
        TryBindProcessToJob(ctx);
    }

    private static void DisposeJob(AgentContext ctx)
    {
        if (!OperatingSystem.IsWindows()) return;
        lock (ctx.JobLock)
        {
            if (ctx.JobHandle == null) return;
            ctx.JobHandle.Dispose();
            ctx.JobHandle = null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static void TryBindProcessToJob(AgentContext ctx)
    {
        if (ctx.Process == null) return;

        lock (ctx.JobLock)
        {
            if (ctx.JobHandle == null || ctx.JobHandle.IsInvalid)
            {
                ctx.JobHandle = CreateJobObject(IntPtr.Zero, null);
                if (ctx.JobHandle == null || ctx.JobHandle.IsInvalid)
                {
                    LoggerHelper.Warning($"CreateJobObject 失败：{Marshal.GetLastWin32Error()}");
                    ctx.JobHandle = null;
                    return;
                }

                var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
                {
                    BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                    {
                        LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                    }
                };

                if (!SetInformationJobObject(ctx.JobHandle, JOBOBJECTINFOCLASS.JobObjectExtendedLimitInformation,
                        ref info, (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
                {
                    LoggerHelper.Warning($"SetInformationJobObject 失败：{Marshal.GetLastWin32Error()}");
                    ctx.JobHandle.Dispose();
                    ctx.JobHandle = null;
                    return;
                }
            }

            if (!AssignProcessToJobObject(ctx.JobHandle, ctx.Process.Handle))
                LoggerHelper.Warning($"AssignProcessToJobObject 失败：{Marshal.GetLastWin32Error()}");
        }
    }

    #endregion

    #region 流读取

    private static readonly Encoding Utf8Strict = new UTF8Encoding(false, true);
    private static readonly Regex UnicodeSurrogatePairRegex = new(
        @"\\u([dD][89abAB][0-9a-fA-F]{2})\\u([dD][cdefCDEF][0-9a-fA-F]{2})",
        RegexOptions.Compiled);
    private static readonly Regex UnicodeEscapeRegex = new(
        @"\\u([0-9a-fA-F]{4})",
        RegexOptions.Compiled);
    private static readonly Lazy<Encoding> GbkEncoding = new(() =>
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(936);
    });
    private static readonly Lazy<Encoding> Gb18030Encoding = new(() =>
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(54936);
    });
    private static readonly Lazy<Encoding> Big5Encoding = new(() =>
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(950);
    });

    private static string DecodeProcessLine(byte[] buffer)
    {
        try { return Utf8Strict.GetString(buffer); }
        catch (DecoderFallbackException) { }
        try { return Gb18030Encoding.Value.GetString(buffer); }
        catch (DecoderFallbackException) { }
        try { return GbkEncoding.Value.GetString(buffer); }
        catch (DecoderFallbackException) { }
        return Big5Encoding.Value.GetString(buffer);
    }

    private static string DecodeUnicodeEscapes(string text)
    {
        if (string.IsNullOrEmpty(text) || text.IndexOf(@"\u", StringComparison.OrdinalIgnoreCase) < 0)
            return text;

        try
        {
            var decoded = UnicodeSurrogatePairRegex.Replace(text, match =>
            {
                var high = Convert.ToInt32(match.Groups[1].Value, 16);
                var low = Convert.ToInt32(match.Groups[2].Value, 16);
                return char.ConvertFromUtf32(char.ConvertToUtf32((char)high, (char)low));
            });

            return UnicodeEscapeRegex.Replace(decoded, match =>
            {
                var codePoint = Convert.ToInt32(match.Groups[1].Value, 16);
                return ((char)codePoint).ToString();
            });
        }
        catch (Exception ex)
        {
            LoggerHelper.Warning($"解码 Agent Unicode 转义失败：{ex.Message}");
            return text;
        }
    }

    internal static async Task ReadProcessStreamAsync(Stream stream, Action<string> onLine, CancellationToken token)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var readBuffer = new byte[4096];
        var lineBuffer = new List<byte>();

        while (!token.IsCancellationRequested)
        {
            int bytesRead;
            try { bytesRead = await stream.ReadAsync(readBuffer.AsMemory(0, readBuffer.Length), token); }
            catch (OperationCanceledException) { break; }
            if (bytesRead <= 0) break;

            for (int i = 0; i < bytesRead; i++)
            {
                var value = readBuffer[i];
                if (value == (byte)'\n')
                {
                    if (lineBuffer.Count > 0 && lineBuffer[^1] == (byte)'\r')
                        lineBuffer.RemoveAt(lineBuffer.Count - 1);
                    onLine(lineBuffer.Count > 0 ? DecodeProcessLine(lineBuffer.ToArray()) : string.Empty);
                    lineBuffer.Clear();
                }
                else
                {
                    lineBuffer.Add(value);
                }
            }
        }

        if (lineBuffer.Count > 0)
            onLine(DecodeProcessLine(lineBuffer.ToArray()));
    }

    #endregion

    #region Windows P/Invoke

    [SupportedOSPlatform("windows")]
    private enum JOBOBJECTINFOCLASS
    {
        JobObjectExtendedLimitInformation = 9
    }

    [SupportedOSPlatform("windows")]
    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [SupportedOSPlatform("windows")]
    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [SupportedOSPlatform("windows")]
    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [SupportedOSPlatform("windows")] private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    [SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern AgentContext.SafeJobHandle CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(AgentContext.SafeJobHandle hJob,
        JOBOBJECTINFOCLASS infoClass,
        ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info,
        uint cbJobObjectInfoLength);

    [SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(AgentContext.SafeJobHandle hJob, IntPtr hProcess);

    [SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool CloseHandle(IntPtr hObject);

    #endregion
}
