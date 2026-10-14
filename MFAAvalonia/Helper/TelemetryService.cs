using MFAAvalonia.Configuration;
using MFAAvalonia.Extensions.MaaFW;
using MFAAvalonia.Helper.ValueType;
using MFAAvalonia.ViewModels.Windows;
using Sentry;
using Sentry.Protocol;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using System.Management;
using System.IO;
using System.Text.RegularExpressions;

namespace MFAAvalonia.Helper;

public static class TelemetryService
{
    internal sealed record BootstrapTelemetryConfig(
        string Dsn,
        string ResourceName,
        string ResourceVersion,
        string? Environment = null,
        bool Tracing = true,
        double TracesSampleRate = 1.0,
        double FailureAttachmentsSampleRate = 1.0);

    private sealed class RunState
    {
        public required string RunId { get; init; }
        public required ITransactionTracer Transaction { get; init; }
        public Dictionary<MFATask, TaskTelemetryState> Tasks { get; } = new();
        public MFATask? ActiveTask { get; set; }
        public bool HasFailed { get; set; }
        public int TaskCount { get; init; }
        public int FailedNodeCount { get; set; }
        public FailureInfo? RootFailure { get; set; }
        public FailureInfo? TerminalFailure { get; set; }
        public TaskEvidenceSnapshot? Evidence { get; set; }
        public long EvidenceEpoch { get; set; }
        public Dictionary<long, (long NodeId, DateTimeOffset StartedAt)> LastPipelineSteps { get; } = new();
    }

    private sealed class TaskTelemetryState
    {
        public required ISpan Span { get; init; }
        public required DateTimeOffset StartedAt { get; init; }
        public long? TaskId { get; set; }
    }

    private sealed class PendingFailureWorker
    {
        private readonly object _sync = new();
        private bool _captured;
        public CancellationTokenSource Cancellation { get; } = new();
        public required SentryEvent Event { get; init; }
        public Task? Task { get; set; }
        public bool TryCapture(
            SentryHint hint,
            Action<SentryEvent> configure,
            Action? beforeCapture = null)
        {
            lock (_sync)
            {
                if (_captured || Cancellation.IsCancellationRequested || !IsActive) return false;
                configure(Event);
                beforeCapture?.Invoke();
                var eventId = SentrySdk.CaptureEvent(Event, hint, _ => { });
                _captured = true;
                LoggerHelper.Info($"[Telemetry] 失败事件已加入发送队列：event_id={eventId}");
                return true;
            }
        }

        public void Cancel()
        {
            lock (_sync) Cancellation.Cancel();
        }
    }

    private const int MaxFailedNodesPerRun = 32;
    private const int MaxFailureDetailLength = 2048;
    private const int MaxSerializedDiagnosticLogBytes = 6 * 1024;
    private const int MaxDiagnosticLogAttributeCharacters = 200;
    private static readonly TimeSpan ImageSettleTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ImageSettleInterval = TimeSpan.FromMilliseconds(100);
    private static readonly object SyncRoot = new();
    private static readonly Dictionary<string, RunState> Runs = new();
    private static IDisposable? _sdk;
    private static bool _isBootstrapSdk;
    private static readonly string AnonymousMachineId = CreateAnonymousMachineId();
    private static double FailureAttachmentSampleRate = 1.0;
    private static readonly List<PendingFailureWorker> PendingAttachmentWorkers = new();
    private static long EvidenceEpoch;

    public static bool IsActive
    {
        get
        {
            lock (SyncRoot)
                return _sdk != null;
        }
    }

    public static void InitializeBootstrapFromInterface(string dataRoot)
    {
        if (!IsBootstrapTelemetryEnabled(dataRoot))
            return;

        var bootstrapConfig = TryReadBootstrapTelemetryConfig(dataRoot);
        if (bootstrapConfig == null || IsBlockedBuild(bootstrapConfig.ResourceVersion))
            return;

        lock (SyncRoot)
        {
            if (_sdk != null)
                return;

            try
            {
                _sdk = SentrySdk.Init(options =>
                {
                    options.Dsn = bootstrapConfig.Dsn;
                    options.SendDefaultPii = false;
                    options.AttachStacktrace = false;
                    options.AutoSessionTracking = true;
                    options.IsGlobalModeEnabled = true;
                    options.EnableLogs = true;
                    options.ShutdownTimeout = TimeSpan.FromSeconds(1);
                    options.Release = $"MFA@{RootViewModel.Version}+{bootstrapConfig.ResourceName}@{bootstrapConfig.ResourceVersion}";
                    options.Environment = string.IsNullOrWhiteSpace(bootstrapConfig.Environment)
                        ? GetDefaultEnvironment(bootstrapConfig.ResourceVersion)
                        : bootstrapConfig.Environment;
                    options.TracesSampleRate = bootstrapConfig.Tracing
                        ? Math.Clamp(bootstrapConfig.TracesSampleRate, 0.0, 1.0)
                        : 0;
                });
                FailureAttachmentSampleRate = Math.Clamp(bootstrapConfig.FailureAttachmentsSampleRate, 0.0, 1.0);
                _isBootstrapSdk = true;
                ConfigureScope(bootstrapConfig.ResourceName, bootstrapConfig.ResourceVersion);
            }
            catch
            {
                _sdk?.Dispose();
                _sdk = null;
            }
        }
    }

    public static void InitializeFromInterface()
    {
        var config = MaaProcessor.Interface?.Telemetry?.Sentry;
        if (config == null || string.IsNullOrWhiteSpace(config.Dsn))
        {
            ShutdownBootstrapSdk();
            LoggerHelper.Info("[Telemetry] interface 未配置 Sentry DSN，跳过初始化");
            return;
        }

        if (!IsUserEnabled() || IsBlockedBuild(MaaProcessor.Interface?.Version))
        {
            ShutdownBootstrapSdk();
            LoggerHelper.Info("[Telemetry] 用户已关闭或当前为调试资源，跳过初始化");
            return;
        }

        lock (SyncRoot)
        {
            try
            {
                var resourceName = MaaProcessor.Interface?.Name ?? "unknown";
                var resourceVersion = MaaProcessor.Interface?.Version ?? "0.0.0";
                FailureAttachmentSampleRate = Math.Clamp(config.FailureAttachmentsSampleRate ?? 1.0, 0.0, 1.0);

                if (_sdk != null)
                {
                    _isBootstrapSdk = false;
                    ConfigureScope(resourceName, resourceVersion);
                    return;
                }

                var tracing = config.Tracing ?? true;
                var sampleRate = Math.Clamp(config.TracesSampleRate ?? 1.0, 0.0, 1.0);

                _sdk = SentrySdk.Init(options =>
                {
                    options.Dsn = config.Dsn;
                    options.SendDefaultPii = false;
                    options.AttachStacktrace = false;
                    options.AutoSessionTracking = true;
                    options.IsGlobalModeEnabled = true;
                    options.EnableLogs = true;
                    options.ShutdownTimeout = TimeSpan.FromSeconds(1);
                    options.Release = $"MFA@{RootViewModel.Version}+{resourceName}@{resourceVersion}";
                    options.Environment = string.IsNullOrWhiteSpace(config.Environment)
                        ? GetDefaultEnvironment(resourceVersion)
                        : config.Environment;
                    options.TracesSampleRate = tracing ? sampleRate : 0;
                });

                ConfigureScope(resourceName, resourceVersion);
                LoggerHelper.Info("[Telemetry] Sentry 已初始化");
            }
            catch (Exception ex)
            {
                _sdk?.Dispose();
                _sdk = null;
                LoggerHelper.Warning($"[Telemetry] Sentry 初始化失败：{ex.Message}");
            }
        }
    }

    public static void SetEnabled(bool enabled)
    {
        GlobalConfiguration.SetValue(ConfigurationKeys.HelpImproveSoftware, enabled.ToString());
        if (enabled)
            InitializeFromInterface();
        else
            Shutdown(captureFallback: false);
    }

    private static void ShutdownBootstrapSdk()
    {
        lock (SyncRoot)
        {
            if (!_isBootstrapSdk)
                return;

            SentrySdk.EndSession(SessionEndStatus.Exited);
            _sdk?.Dispose();
            _sdk = null;
            _isBootstrapSdk = false;
        }
    }

    public static void CaptureException(Exception exception, string source)
    {
        CaptureException(exception, source, useRootCause: false);
    }

    public static void CaptureStartupException(Exception exception)
    {
        CaptureException(exception, "startup", useRootCause: true);
    }

    private static void CaptureException(Exception exception, string source, bool useRootCause)
    {
        if (!IsActive)
            return;

        try
        {
            var capturedException = useRootCause ? GetRootCause(exception) : exception;
            using (SentrySdk.PushScope())
            {
                SentrySdk.ConfigureScope(scope =>
                {
                    scope.SetTag("exception.source", source);
                    var (code, family) = ClassifyException(capturedException);
                    scope.SetTag("error.code", code);
                    scope.SetTag("error.family", family);
                    scope.SetTag("exception.root_type", capturedException.GetType().FullName ?? capturedException.GetType().Name);
                    if (!ReferenceEquals(capturedException, exception))
                    {
                        scope.SetExtra("exception.wrapper_type", exception.GetType().FullName ?? exception.GetType().Name);
                        scope.SetExtra("exception.wrapper_message", exception.Message);
                    }
                });

                var @event = new SentryEvent(capturedException);
                if (useRootCause)
                {
                    capturedException.SetSentryMechanism(
                        "startup",
                        "Fatal exception during application startup",
                        handled: false,
                        terminal: true);
                    @event.Message = capturedException.Message;
                    @event.TransactionName = "mfa.startup.failure";
                }

                var logs = TaskDiagnostics.BuildRecentLogs();
                CaptureDiagnosticLogs(logs, @event, "exception", diagnosticSource: source);
                SetLogEvidenceData(@event, logs);
                @event.SetExtra("attachment.status", "not_applicable");

                var eventId = SentrySdk.CaptureEvent(@event, new SentryHint(), _ => { });
                LoggerHelper.Info($"[Telemetry] 异常事件已加入发送队列：source={source}, event_id={eventId}, attachment={@event.Extra?.GetValueOrDefault("attachment.status")}");
            }
        }
        catch
        {
            // Diagnostics must never replace the original exception.
        }
    }

    private static Exception GetRootCause(Exception exception)
    {
        var current = exception;
        while (true)
        {
            if (current is AggregateException aggregate)
            {
                var flattened = aggregate.Flatten().InnerExceptions;
                if (flattened.Count == 1)
                {
                    current = flattened[0];
                    continue;
                }
            }

            if (current.InnerException == null)
                return current;

            current = current.InnerException;
        }
    }

    private static void ConfigureScope(string resourceName, string resourceVersion)
    {
        SentrySdk.ConfigureScope(scope =>
        {
            scope.SetTag("app.name", resourceName);
            scope.SetTag("app.version", resourceVersion);
            scope.SetTag("mfa.version", RootViewModel.Version);
            scope.SetTag("resource.name", resourceName);
            scope.SetTag("resource.version", resourceVersion);
            scope.User = new SentryUser { Id = AnonymousMachineId };
            scope.Contexts["hardware"] = CollectHardwareContext();
            scope.Contexts["client"] = new Dictionary<string, object>
            {
                ["name"] = "MFAAvalonia",
                ["version"] = RootViewModel.Version,
                ["runtime"] = RuntimeInformation.FrameworkDescription
            };
        });
    }

    internal static string? TryReadBootstrapDsn(string dataRoot) =>
        TryReadBootstrapTelemetryConfig(dataRoot)?.Dsn;

    internal static BootstrapTelemetryConfig? TryReadBootstrapTelemetryConfig(string dataRoot)
    {
        foreach (var fileName in new[] { "interface.jsonc", "interface.json" })
        {
            var path = Path.Combine(dataRoot, fileName);
            if (!File.Exists(path))
                continue;

            try
            {
                var root = JObject.Parse(File.ReadAllText(path), new JsonLoadSettings { CommentHandling = CommentHandling.Ignore });
                var dsn = root["telemetry"]?["sentry"]?["dsn"]?.Value<string>();
                if (IsSentryDsn(dsn))
                    return new BootstrapTelemetryConfig(
                        dsn,
                        root["name"]?.Value<string>() ?? "unknown",
                        root["version"]?.Value<string>() ?? "0.0.0",
                        root["telemetry"]?["sentry"]?["environment"]?.Value<string>(),
                        root["telemetry"]?["sentry"]?["tracing"]?.Value<bool?>() ?? true,
                        root["telemetry"]?["sentry"]?["traces_sample_rate"]?.Value<double?>() ?? 1.0,
                        root["telemetry"]?["sentry"]?["failure_attachments_sample_rate"]?.Value<double?>() ?? 1.0);
            }
            catch
            {
                try
                {
                    var content = File.ReadAllText(path);
                    var match = Regex.Match(content, "\\\"telemetry\\\"\\s*:\\s*\\{.*?\\\"sentry\\\"\\s*:\\s*\\{.*?\\\"dsn\\\"\\s*:\\s*\\\"(?<dsn>[^\\\"]+)\\\"", RegexOptions.Singleline);
                    var dsn = match.Groups["dsn"].Value;
                    if (IsSentryDsn(dsn))
                        return new BootstrapTelemetryConfig(
                            dsn,
                            TryReadBootstrapValue(content, "name") ?? "unknown",
                            TryReadBootstrapValue(content, "version") ?? "0.0.0",
                            TryReadBootstrapValue(content, "environment"));
                }
                catch
                {
                    // Bootstrap telemetry must never change startup behavior.
                }
            }
        }

        return null;
    }

    private static string? TryReadBootstrapValue(string content, string propertyName)
    {
        var match = Regex.Match(content, $"\\\"{Regex.Escape(propertyName)}\\\"\\s*:\\s*\\\"(?<value>[^\\\"]+)\\\"", RegexOptions.Singleline);
        return match.Success ? match.Groups["value"].Value : null;
    }

    private static bool IsSentryDsn(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.Host.Contains(".ingest.", StringComparison.OrdinalIgnoreCase);

    private static bool IsBootstrapTelemetryEnabled(string dataRoot)
    {
        try
        {
            var path = Path.Combine(dataRoot, "appsettings.json");
            if (!File.Exists(path))
                return true;

            var root = JObject.Parse(File.ReadAllText(path));
            var value = root[ConfigurationKeys.HelpImproveSoftware]?.Value<string>();
            return !bool.TryParse(value, out var enabled) || enabled;
        }
        catch
        {
            // Match GlobalConfiguration's default when its file cannot be read.
            return true;
        }
    }

    private static (string Code, string Family) ClassifyException(Exception exception)
    {
        var all = EnumerateExceptions(exception).ToList();
        var text = string.Join("\n", all.Select(item => item.ToString()));
        if (text.Contains("GetProcAddress failed", StringComparison.OrdinalIgnoreCase) || all.Any(item => item is EntryPointNotFoundException))
            return ("native_symbol_missing", "native_library");
        if (text.Contains("MaaFramework library not loaded", StringComparison.OrdinalIgnoreCase))
            return ("maafw_library_not_loaded", "native_library");
        if (text.Contains("native library directory", StringComparison.OrdinalIgnoreCase)
            && text.Contains("resolver", StringComparison.OrdinalIgnoreCase))
            return ("native_library_resolver_failed", "native_library");
        if (text.Contains("appindicator", StringComparison.OrdinalIgnoreCase) || text.Contains("ayatana", StringComparison.OrdinalIgnoreCase))
            return ("linux_tray_library_missing", "desktop_integration");
        if (all.Any(item => item is OutOfMemoryException) || text.Contains("failed to spawn thread", StringComparison.OrdinalIgnoreCase))
            return ("system_resource_exhausted", "runtime");
        if (all.Any(item => item is DllNotFoundException or BadImageFormatException)
            || text.Contains("Unable to load shared library", StringComparison.OrdinalIgnoreCase)
            || text.Contains("cannot open shared object file", StringComparison.OrdinalIgnoreCase)
            || text.Contains("specified module could not be found", StringComparison.OrdinalIgnoreCase))
            return ("native_library_load_failed", "native_library");
        if (all.Any(item => item is FileNotFoundException))
            return ("file_not_found", "file_system");
        return ("unhandled_exception", "runtime");
    }

    private static IEnumerable<Exception> EnumerateExceptions(Exception exception)
    {
        yield return exception;
        if (exception is AggregateException aggregate)
        {
            foreach (var inner in aggregate.InnerExceptions.SelectMany(EnumerateExceptions))
                yield return inner;
        }
        else if (exception.InnerException != null)
        {
            foreach (var inner in EnumerateExceptions(exception.InnerException))
                yield return inner;
        }
    }

    public static void StartRun(
        string instanceId,
        IReadOnlyCollection<string> taskNames,
        string? controllerName,
        string? controllerType)
    {
        if (!IsActive)
            return;

        lock (SyncRoot)
        {
            Interlocked.Increment(ref EvidenceEpoch);
            FinishRunLocked(instanceId, SpanStatus.Cancelled);
            var runId = Guid.NewGuid().ToString("N");
            var transaction = SentrySdk.StartTransaction("mfa.task_run", "mfa.run");
            transaction.SetData("run.id", runId);
            transaction.SetTag("run.id", runId);
            transaction.SetData("task_count", taskNames.Count);
            if (taskNames.Count > 0)
                transaction.SetData("tasks", string.Join(",", taskNames));
            if (!string.IsNullOrWhiteSpace(controllerName))
            {
                transaction.SetData("controller.name", controllerName);
                transaction.SetTag("controller.name", controllerName);
            }
            if (!string.IsNullOrWhiteSpace(controllerType))
            {
                transaction.SetData("controller.type", controllerType);
                transaction.SetTag("controller.type", controllerType);
            }
            Runs[instanceId] = new RunState
            {
                RunId = runId,
                Transaction = transaction,
                TaskCount = taskNames.Count
            };
        }
    }

    public static void StartTask(string instanceId, MFATask task)
    {
        lock (SyncRoot)
        {
            if (!Runs.TryGetValue(instanceId, out var run))
                return;

            var name = task.SourceItem?.InterfaceItem?.Name ?? task.Name ?? "unknown";
            var span = run.Transaction.StartChild("mfa.task", name);
            span.SetData("task", name);
            span.SetData("repeat.count", task.Count);
            foreach (var option in BuildOptionSummary(task))
            {
                span.SetData($"option.{option.Key}", option.Value);
            }
            run.Tasks[task] = new TaskTelemetryState
            {
                Span = span,
                StartedAt = DateTimeOffset.UtcNow
            };
            run.ActiveTask = task;
            run.FailedNodeCount = 0;
            run.RootFailure = null;
            run.TerminalFailure = null;
            run.LastPipelineSteps.Clear();
            run.EvidenceEpoch = Interlocked.Increment(ref EvidenceEpoch);
            // Log collection is independent from screenshot attachment sampling.
            try
            {
                run.Evidence = run.Tasks.Count == 1
                    ? TaskDiagnostics.CaptureStart(Runs.Count == 1)
                    : null;
            }
            catch (Exception ex)
            {
                run.Evidence = null;
                LoggerHelper.Warning($"[Telemetry] 无法建立任务取证边界：{ex.GetType().Name}");
            }
        }
    }

    public static void FinishTask(string instanceId, MFATask task, MFATask.MFATaskStatus status, bool hadFailure = false)
    {
        lock (SyncRoot)
        {
            if (!Runs.TryGetValue(instanceId, out var run) || !run.Tasks.Remove(task, out var taskState))
                return;

            var span = taskState.Span;
            var spanStatus = hadFailure ? SpanStatus.InternalError : ToSpanStatus(status);
            span.Status = spanStatus;
            span.SetData("result", hadFailure ? "failure" : ToResult(status));
            span.Finish();
            if (status == MFATask.MFATaskStatus.FAILED || hadFailure)
            {
                run.HasFailed = true;
                try
                {
                    CaptureFailureEvent(instanceId, task, taskState, run);
                }
                catch (Exception ex)
                {
                    LoggerHelper.Warning($"[Telemetry] 无法记录任务失败事件：{ex.GetType().Name}");
                }
            }
            if (ReferenceEquals(run.ActiveTask, task))
                run.ActiveTask = null;
        }
    }

    public static void SetActiveTaskId(string instanceId, long taskId)
    {
        lock (SyncRoot)
        {
            if (!Runs.TryGetValue(instanceId, out var run)
                || run.ActiveTask == null
                || !run.Tasks.TryGetValue(run.ActiveTask, out var taskState))
                return;

            taskState.Span.SetData("task_id", taskId);
            taskState.TaskId = taskId;
        }
    }

    public static void RecordFailedNode(string instanceId, string? nodeName, string? stage = null, long? nodeId = null, long? taskId = null, long? durationMs = null)
    {
        lock (SyncRoot)
        {
            if (!Runs.TryGetValue(instanceId, out var run)
                || run.ActiveTask == null
                || !run.Tasks.TryGetValue(run.ActiveTask, out var taskState)
                || run.FailedNodeCount++ >= MaxFailedNodesPerRun)
                return;

            var taskSpan = taskState.Span;
            var taskName = run.ActiveTask.SourceItem?.InterfaceItem?.Name ?? run.ActiveTask.Name ?? "unknown";
            var span = taskSpan.StartChild("mfa.node", string.IsNullOrWhiteSpace(nodeName) ? "unknown" : nodeName);
            span.SetData("stage", stage ?? "unknown");
            span.SetData("task", taskName);
            if (nodeId.HasValue) span.SetData("node_id", nodeId.Value);
            if (taskId.HasValue) span.SetData("task_id", taskId.Value);
            if (durationMs.HasValue) span.SetData("duration_ms", durationMs.Value);
            span.Status = SpanStatus.InternalError;
            span.Finish();
            var failure = new FailureInfo(nodeName ?? "unknown", stage ?? "unknown", nodeId, taskId, durationMs);
            run.RootFailure ??= failure;
            run.TerminalFailure = failure;
        }
    }

    public static void RecordTaskFailure(string instanceId, string code, string stage, string? detail = null)
    {
        RecordTaskFailure(instanceId, code, stage, detail, null);
    }

    public static void RecordTaskFailure(string instanceId, string code, string stage, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        RecordTaskFailure(instanceId, code, stage, BuildExceptionDetail(exception), exception);
    }

    private static void RecordTaskFailure(
        string instanceId,
        string code,
        string stage,
        string? detail,
        Exception? exception)
    {
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(stage))
            return;

        lock (SyncRoot)
        {
            if (!Runs.TryGetValue(instanceId, out var run)
                || run.ActiveTask == null
                || !run.Tasks.TryGetValue(run.ActiveTask, out var taskState))
                return;

            var taskSpan = taskState.Span;
            var failure = new FailureInfo(code, stage, null, null, null, detail, exception);
            run.RootFailure ??= failure;
            run.TerminalFailure = failure;
            var taskName = run.ActiveTask.SourceItem?.InterfaceItem?.Name ?? run.ActiveTask.Name ?? "unknown";
            var span = taskSpan.StartChild("mfa.error", code);
            span.SetData("stage", stage);
            span.SetData("task", taskName);
            if (!string.IsNullOrWhiteSpace(detail))
                span.SetData("detail", detail);
            span.Status = SpanStatus.InternalError;
            span.Finish();
        }
    }

    public static void RecordNodeEvent(string instanceId, string message, string details, bool shouldTrace = true)
    {
        if (!message.StartsWith("Node.", StringComparison.Ordinal))
            return;

        JObject detail;
        try { detail = JObject.Parse(details); }
        catch { return; }
        var taskId = detail["task_id"]?.Value<long>();
        var nodeId = detail["node_id"]?.Value<long>();
        if (!taskId.HasValue) return;

        lock (SyncRoot)
        {
            if (!Runs.TryGetValue(instanceId, out var run)) return;
            if (message.Equals("Node.PipelineNode.Starting", StringComparison.Ordinal))
            {
                if (nodeId.HasValue) run.LastPipelineSteps[taskId.Value] = (nodeId.Value, DateTimeOffset.UtcNow);
                if (!shouldTrace) return;
            }

            var searchNode = detail["name"]?.Value<string>();
            var hitNode = message.StartsWith("Node.PipelineNode.", StringComparison.Ordinal)
                ? detail["node_details"]?["name"]?.Value<string>()
                : null;
            var nodeName = string.IsNullOrWhiteSpace(hitNode) ? searchNode : hitNode;
            if (string.IsNullOrWhiteSpace(nodeName) || run.ActiveTask == null
                || !run.Tasks.TryGetValue(run.ActiveTask, out var taskState)) return;
            var taskSpan = taskState.Span;
            string? stage = message.Equals("Node.PipelineNode.Failed", StringComparison.Ordinal)
                ? string.IsNullOrWhiteSpace(hitNode) ? "recognition" : "action"
                : null;
            long? duration = null;
            if ((message.Equals("Node.PipelineNode.Succeeded", StringComparison.Ordinal)
                    || message.Equals("Node.PipelineNode.Failed", StringComparison.Ordinal))
                && run.LastPipelineSteps.Remove(taskId.Value, out var step)
                && nodeId.HasValue
                && step.NodeId == nodeId.Value)
            {
                duration = (long)(DateTimeOffset.UtcNow - step.StartedAt).TotalMilliseconds;
            }
            var isFailure = message.EndsWith(".Failed", StringComparison.Ordinal);
            if (isFailure)
            {
                var failure = new FailureInfo(nodeName!, stage, nodeId, taskId, duration, Message: message);
                run.RootFailure ??= failure;
                run.TerminalFailure = failure;
                if (++run.FailedNodeCount > MaxFailedNodesPerRun) return;
            }
            var taskName = run.ActiveTask.SourceItem?.InterfaceItem?.Name ?? run.ActiveTask.Name ?? "unknown";
            var span = taskSpan.StartChild("mfa.node", nodeName);
            span.SetData("message", message);
            if (!string.IsNullOrWhiteSpace(hitNode) && !string.IsNullOrWhiteSpace(searchNode))
                span.SetData("search_node", searchNode);
            if (!string.IsNullOrWhiteSpace(stage))
                span.SetData("stage", stage);
            span.SetData("task", taskName);
            span.SetData("task_id", taskId.Value);
            if (nodeId.HasValue) span.SetData("node_id", nodeId.Value);
            if (duration.HasValue) span.SetData("duration_ms", duration.Value);
            span.Status = isFailure ? SpanStatus.InternalError : SpanStatus.Ok;
            span.Finish();
        }
    }

    public static void FinishRun(string instanceId, MFATask.MFATaskStatus status)
    {
        lock (SyncRoot)
            FinishRunLocked(instanceId, ToSpanStatus(status));
    }

    public static void Shutdown(bool captureFallback = true)
    {
        PendingFailureWorker[] pending;
        lock (SyncRoot)
        {
            foreach (var instanceId in new List<string>(Runs.Keys))
                FinishRunLocked(instanceId, SpanStatus.Cancelled);
            pending = PendingAttachmentWorkers.ToArray();
        }

        if (!captureFallback)
        {
            foreach (var worker in pending) worker.Cancel();
        }

        try
        {
            if (captureFallback)
                Task.WaitAll(pending.Select(worker => worker.Task).Where(task => task != null).Cast<Task>().ToArray(), TimeSpan.FromSeconds(1));
        }
        catch (AggregateException) { }
        finally
        {
            if (captureFallback)
            {
                foreach (var worker in pending.Where(worker => worker.Task is { IsCompleted: false }))
                {
                    worker.TryCapture(new SentryHint(), @event =>
                    {
                        @event.SetExtra("logs.status", "not_available");
                        @event.SetExtra("attachment.status", "shutdown_timeout");
                        @event.SetExtra("attachment.detail", "evidence worker did not finish before telemetry shutdown");
                    });
                }
            }
            foreach (var worker in pending) worker.Cancel();
            lock (SyncRoot)
            {
                if (_sdk != null)
                    SentrySdk.EndSession(SessionEndStatus.Exited);
                _sdk?.Dispose();
                _sdk = null;
                _isBootstrapSdk = false;
                PendingAttachmentWorkers.Clear();
            }
        }
    }

    private static void FinishRunLocked(string instanceId, SpanStatus status)
    {
        if (!Runs.Remove(instanceId, out var run))
            return;

        foreach (var taskState in run.Tasks.Values)
        {
            var span = taskState.Span;
            span.Status = SpanStatus.Cancelled;
            span.SetData("result", "cancelled");
            span.Finish();
        }

        if (status == SpanStatus.Ok && run.HasFailed)
            status = SpanStatus.InternalError;
        run.Transaction.Status = status;
        run.Transaction.SetData("result", status == SpanStatus.Ok ? "success" : status == SpanStatus.Cancelled ? "cancelled" : "failure");
        run.Transaction.Finish();
    }

    private static bool IsUserEnabled() =>
        bool.TryParse(GlobalConfiguration.GetValue(ConfigurationKeys.HelpImproveSoftware, bool.TrueString), out var enabled) && enabled;

    private static bool IsBlockedBuild(string? resourceVersion)
    {
        if (Environment.GetEnvironmentVariable("MFA_TELEMETRY_FORCE") == "1")
            return false;
#if DEBUG
        return true;
#else
        return IsDebugResourceVersion(resourceVersion);
#endif
    }

    internal static bool IsDebugResourceVersion(string? resourceVersion)
    {
        var version = resourceVersion?.Trim() ?? string.Empty;
        if (version.Length == 0)
            return false;
        if (version.Equals("DEBUG_VERSION", StringComparison.Ordinal))
            return true;

        var normalized = version.TrimStart('v', 'V');
        var match = Regex.Match(normalized, @"^(?<major>\d+)(?:\.(?<minor>\d+))?(?:\.(?<patch>\d+))?(?:-(?<pre>[^+]+))?");
        if (!match.Success)
            return false;

        var major = int.Parse(match.Groups["major"].Value);
        var minor = match.Groups["minor"].Success ? int.Parse(match.Groups["minor"].Value) : 0;
        var patch = match.Groups["patch"].Success ? int.Parse(match.Groups["patch"].Value) : 0;
        if ((major, minor, patch).CompareTo((1, 0, 0)) < 0)
            return true;

        if (!match.Groups["pre"].Success)
            return false;

        var prerelease = match.Groups["pre"].Value.Split('.', StringSplitOptions.RemoveEmptyEntries);
        return !prerelease.Any(tag => tag is "beta" or "rc");
    }

    private static string GetDefaultEnvironment(string version) =>
        version.Contains("beta", StringComparison.OrdinalIgnoreCase) ? "beta"
        : version.Contains("rc", StringComparison.OrdinalIgnoreCase) ? "rc"
        : "production";

    private static SpanStatus ToSpanStatus(MFATask.MFATaskStatus status) => status switch
    {
        MFATask.MFATaskStatus.SUCCEEDED => SpanStatus.Ok,
        MFATask.MFATaskStatus.STOPPED or MFATask.MFATaskStatus.STOPPING => SpanStatus.Cancelled,
        _ => SpanStatus.InternalError
    };

    private static string ToResult(MFATask.MFATaskStatus status) => status switch
    {
        MFATask.MFATaskStatus.SUCCEEDED => "success",
        MFATask.MFATaskStatus.STOPPED or MFATask.MFATaskStatus.STOPPING => "cancelled",
        _ => "failure"
    };

    private sealed record FailureInfo(
        string Node,
        string? Stage,
        long? NodeId,
        long? TaskId,
        long? DurationMs,
        string? Detail = null,
        Exception? Exception = null,
        string? Message = null);

    private static void CaptureFailureEvent(
        string instanceId,
        MFATask task,
        TaskTelemetryState taskState,
        RunState run)
    {
        var taskSpan = taskState.Span;
        var taskName = task.SourceItem?.InterfaceItem?.Name ?? task.Name ?? "unknown-task";
        var rootFailure = run.RootFailure;
        var terminalFailure = run.TerminalFailure;
        var runId = run.RunId;
        var taskId = taskState.TaskId;
        var evidenceEpoch = run.EvidenceEpoch;
        var failureName = rootFailure == null
            ? taskName
            : string.IsNullOrWhiteSpace(rootFailure.Stage)
                ? $"{taskName} ({rootFailure.Node})"
                : $"{taskName} ({rootFailure.Node}/{rootFailure.Stage})";
        var rootException = rootFailure?.Exception ?? terminalFailure?.Exception;
        var @event = rootException == null ? new SentryEvent() : new SentryEvent(rootException);
        @event.Message = $"Maa task failed: {failureName}";
        @event.TransactionName = "mfa.task.failure";
        var fingerprint = new List<string>
        {
            "mfa-task-failure",
            MaaProcessor.Interface?.Name ?? "unknown",
            taskName,
            rootFailure?.Node ?? "terminal_failure"
        };
        if (!string.IsNullOrWhiteSpace(rootFailure?.Stage))
            fingerprint.Add(rootFailure.Stage);
        else if (!string.IsNullOrWhiteSpace(rootFailure?.Message))
            fingerprint.Add(rootFailure.Message);
        @event.Fingerprint = fingerprint.ToArray();
        @event.SetTag("task.name", taskName);
        @event.SetTag("run.id", runId);
        if (taskId.HasValue) @event.SetTag("task.id", taskId.Value.ToString());
        @event.SetTag("result", "failure");
        if (rootException != null)
        {
            var (errorCode, errorFamily) = ClassifyException(rootException);
            @event.SetTag("error.code", errorCode);
            @event.SetTag("error.family", errorFamily);
        }
        var traceHeader = taskSpan.GetTraceHeader();
        @event.Contexts.Trace.TraceId = traceHeader.TraceId;
        @event.Contexts.Trace.SpanId = traceHeader.SpanId;
        @event.Contexts.Trace.Operation = taskSpan.Operation;
        @event.Contexts.Trace.Description = taskSpan.Description;
        @event.Contexts.Trace.Status = taskSpan.Status;
        if (rootFailure != null)
        {
            SetFailureData(@event, "failure", rootFailure, asTags: true);
            if (terminalFailure != null && terminalFailure != rootFailure)
                SetFailureData(@event, "terminal_failure", terminalFailure, asTags: false);
        }
        foreach (var option in BuildOptionSummary(task))
            @event.SetExtra($"option.{option.Key}", option.Value);
        @event.SetExtra("instance.task_count", run.TaskCount);
        @event.SetExtra("run.id", runId);
        if (taskId.HasValue) @event.SetExtra("task.id", taskId.Value);
        @event.SetExtra("task.duration_ms", Math.Max(0,
            (DateTimeOffset.UtcNow - taskState.StartedAt).TotalMilliseconds));
        if (run.Evidence != null)
        {
            @event.SetExtra("attachment.status", "pending");
            var evidence = run.Evidence;
            TaskEvidenceSelection selection;
            try
            {
                // FinishTask holds SyncRoot here, so the next task cannot advance the
                // evidence epoch until the terminal file generations are frozen.
                selection = TaskDiagnostics.CaptureEnd(evidence,
                    Runs.Count == 1 && Interlocked.Read(ref EvidenceEpoch) == evidenceEpoch);
            }
            catch (Exception ex)
            {
                @event.SetExtra("logs.status", "build_failed");
                @event.SetExtra("attachment.status", "build_failed");
                @event.SetExtra("attachment.detail", ex.GetType().Name);
                var failedEventId = SentrySdk.CaptureEvent(@event, new SentryHint(), _ => { });
                LoggerHelper.Info($"[Telemetry] 失败事件已加入发送队列：event_id={failedEventId}");
                return;
            }

            var includeImages = ShouldSampleAttachment(runId, taskId?.ToString() ?? taskName,
                FailureAttachmentSampleRate);
            var pendingWorker = new PendingFailureWorker { Event = @event };
            var workerTask = Task.Run(async () =>
            {
                using (selection)
                {
                    try
                    {
                        if (includeImages)
                        {
                            await SettleTaskImagesAsync(selection, evidenceEpoch,
                                pendingWorker.Cancellation.Token).ConfigureAwait(false);
                        }
                        var result = TaskDiagnostics.Build(selection, includeImages,
                            pendingWorker.Cancellation.Token);
                        pendingWorker.Cancellation.Token.ThrowIfCancellationRequested();
                        var hint = new SentryHint();
                        if (includeImages && result.Images.Status == TaskEvidenceBuildStatus.Success && result.Images.Data != null)
                        {
                            hint.AddAttachment(result.Images.Data,
                                $"mfa-task-failure-{runId}-{taskId?.ToString() ?? SanitizeFileName(taskName)}-screenshots.zip",
                                AttachmentType.Default, "application/zip");
                        }
                        pendingWorker.TryCapture(hint, item =>
                        {
                            SetLogEvidenceData(item, result.Logs);
                            item.SetExtra("attachment.status", !includeImages
                                ? "not_selected"
                                : result.Images.Status == TaskEvidenceBuildStatus.Success && result.Images.Data != null
                                    ? "attached"
                                    : ToAttachmentStatus(result.Images.Status));
                            item.SetExtra("attachment.image_count", result.Images.ImageCount);
                            item.SetExtra("attachment.selected_raw_bytes", result.Images.RawBytes);
                            item.SetExtra("attachment.bundle_bytes", result.Images.BundleBytes);
                            if (result.Images.Warnings.Count > 0)
                                item.SetExtra("attachment.warnings", string.Join(",", result.Images.Warnings));
                        }, () => CaptureDiagnosticLogs(result.Logs, @event, "task_failure",
                            instanceId, runId, taskId, taskName, rootFailure?.Node, rootFailure?.Stage,
                            traceHeader.TraceId, traceHeader.SpanId));
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex)
                    {
                        pendingWorker.TryCapture(new SentryHint(), item =>
                        {
                            item.SetExtra("logs.status", "build_failed");
                            item.SetExtra("attachment.status", "build_failed");
                            item.SetExtra("attachment.detail", ex.GetType().Name);
                        });
                    }
                }
            });
            pendingWorker.Task = workerTask;
            lock (SyncRoot) PendingAttachmentWorkers.Add(pendingWorker);
            _ = workerTask.ContinueWith(_ =>
            {
                lock (SyncRoot) PendingAttachmentWorkers.Remove(pendingWorker);
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return;
        }

        @event.SetExtra("logs.status", "not_available");
        @event.SetExtra("attachment.status", "concurrent_instance");
        var eventId = SentrySdk.CaptureEvent(@event, new SentryHint(), _ => { });
        LoggerHelper.Info($"[Telemetry] 任务失败事件已加入发送队列：event_id={eventId}, task={taskName}, attachment={@event.Extra?.GetValueOrDefault("attachment.status")}");
    }

    private static async Task SettleTaskImagesAsync(
        TaskEvidenceSelection selection,
        long expectedEpoch,
        CancellationToken cancellationToken)
    {
        if (!selection.Isolated) return;
        var deadline = DateTimeOffset.UtcNow + ImageSettleTimeout;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Read(ref EvidenceEpoch) != expectedEpoch) return;
            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero) return;
            await Task.Delay(remaining < ImageSettleInterval ? remaining : ImageSettleInterval,
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Read(ref EvidenceEpoch) != expectedEpoch) return;

            try
            {
                using var refresh = TaskDiagnostics.CaptureImageRefresh(selection, cancellationToken);
                // A new task may have started during discovery. Never publish that scan.
                if (Interlocked.Read(ref EvidenceEpoch) != expectedEpoch) return;
                TaskDiagnostics.ApplyImageRefresh(selection, refresh);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                TaskDiagnostics.AddWarning(selection.Warnings,
                    $"refresh_images_failed:{ex.GetType().Name}");
                return;
            }
        }
    }

    private static void SetFailureData(SentryEvent @event, string prefix, FailureInfo failure, bool asTags)
    {
        if (asTags)
        {
            @event.SetTag($"{prefix}.node", failure.Node);
            if (!string.IsNullOrWhiteSpace(failure.Stage))
                @event.SetTag($"{prefix}.stage", failure.Stage);
        }
        else
        {
            @event.SetExtra($"{prefix}.node", failure.Node);
            if (!string.IsNullOrWhiteSpace(failure.Stage))
                @event.SetExtra($"{prefix}.stage", failure.Stage);
        }
        if (!string.IsNullOrWhiteSpace(failure.Message)) @event.SetExtra($"{prefix}.message", failure.Message);
        if (failure.NodeId.HasValue) @event.SetExtra($"{prefix}.node_id", failure.NodeId.Value);
        if (failure.TaskId.HasValue) @event.SetExtra($"{prefix}.task_id", failure.TaskId.Value);
        if (failure.DurationMs.HasValue) @event.SetExtra($"{prefix}.duration_ms", failure.DurationMs.Value);
        if (!string.IsNullOrWhiteSpace(failure.Detail)) @event.SetExtra($"{prefix}.detail", failure.Detail);
    }

    private static string BuildExceptionDetail(Exception exception)
    {
        var detail = string.Join(" -> ", EnumerateExceptions(exception)
            .Select(item => string.IsNullOrWhiteSpace(item.Message)
                ? item.GetType().Name
                : $"{item.GetType().Name}: {item.Message}"));
        return detail.Length <= MaxFailureDetailLength
            ? detail
            : detail[..MaxFailureDetailLength];
    }

    private static string ToAttachmentStatus(TaskEvidenceBuildStatus status) => status switch
    {
        TaskEvidenceBuildStatus.ConcurrentInstance => "concurrent_instance",
        TaskEvidenceBuildStatus.NoEvidence => "no_evidence",
        TaskEvidenceBuildStatus.RawTooLarge => "raw_too_large",
        TaskEvidenceBuildStatus.BundleTooLarge => "bundle_too_large",
        _ => "build_failed"
    };

    private static void SetLogEvidenceData(SentryEvent @event, DiagnosticLogBuildResult logs)
    {
        @event.SetExtra("logs.status", logs.Status == TaskEvidenceBuildStatus.Success
            ? "captured"
            : ToAttachmentStatus(logs.Status));
        @event.SetExtra("logs.count", logs.Logs.Count);
        @event.SetExtra("logs.selected_raw_bytes", logs.RawBytes);
        @event.SetExtra("logs.truncated", logs.Truncated);
        if (logs.Warnings.Count > 0)
            @event.SetExtra("logs.warnings", string.Join(",", logs.Warnings));
    }

    private static void CaptureDiagnosticLogs(
        DiagnosticLogBuildResult result,
        SentryEvent @event,
        string reason,
        string? instanceId = null,
        string? runId = null,
        long? taskId = null,
        string? taskName = null,
        string? failureNode = null,
        string? failureStage = null,
        SentryId? traceId = null,
        SpanId? spanId = null,
        string? diagnosticSource = null)
    {
        if (result.Status != TaskEvidenceBuildStatus.Success)
            return;

        foreach (var source in result.Logs)
        {
            var attributes = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["diagnostic.reason"] = BoundedLogAttribute(reason),
                ["diagnostic.source"] = BoundedLogAttribute(source.Source),
                ["diagnostic.kind"] = BoundedLogAttribute(source.Kind),
                ["sentry.event_id"] = @event.EventId.ToString(),
                ["log.raw_bytes"] = source.RawBytes
            };
            AddLogAttribute(attributes, "exception.source", diagnosticSource);
            AddLogAttribute(attributes, "instance.id", instanceId);
            AddLogAttribute(attributes, "run.id", runId);
            if (taskId.HasValue) attributes["task.id"] = taskId.Value;
            AddLogAttribute(attributes, "task.name", taskName);
            AddLogAttribute(attributes, "failure.node", failureNode);
            AddLogAttribute(attributes, "failure.stage", failureStage);
            if (traceId.HasValue) attributes["trace.id"] = traceId.Value.ToString();
            if (spanId.HasValue) attributes["span.id"] = spanId.Value.ToString();

            var chunks = BuildDiagnosticLogChunks(source.Content, attributes);
            for (var index = 0; index < chunks.Count; index++)
            {
                var chunk = chunks[index];
                void ConfigureLog(SentryLog log)
                {
                    foreach (var attribute in attributes)
                        log.SetAttribute(attribute.Key, attribute.Value);
                    log.SetAttribute("log.chunk_index", index);
                    log.SetAttribute("log.chunk_count", chunks.Count);
                }
                CaptureStructuredLog(GetLogLevel(chunk), ConfigureLog, chunk);
            }
        }
    }

    private static void AddLogAttribute(Dictionary<string, object> attributes, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) attributes[key] = BoundedLogAttribute(value);
    }

    private static string BoundedLogAttribute(string value)
    {
        var result = new StringBuilder();
        foreach (var rune in value.EnumerateRunes().Take(MaxDiagnosticLogAttributeCharacters))
            result.Append((Rune.IsControl(rune) ? new Rune('\uFFFD') : rune).ToString());
        return result.ToString();
    }

    private static IReadOnlyList<string> BuildDiagnosticLogChunks(
        string content,
        IReadOnlyDictionary<string, object> attributes)
    {
        if (content.Length == 0) return Array.Empty<string>();
        var boundaries = new List<int> { 0 };
        var offset = 0;
        foreach (var rune in content.EnumerateRunes())
        {
            offset += rune.Utf16SequenceLength;
            boundaries.Add(offset);
        }

        var chunks = new List<string>();
        var startBoundary = 0;
        while (startBoundary < boundaries.Count - 1)
        {
            var highBoundary = Math.Min(boundaries.Count - 1,
                startBoundary + MaxSerializedDiagnosticLogBytes);
            var low = startBoundary + 1;
            var high = highBoundary;
            var best = -1;
            while (low <= high)
            {
                var middle = low + (high - low) / 2;
                var candidate = content[boundaries[startBoundary]..boundaries[middle]];
                if (SerializedDiagnosticLogFits(candidate, attributes))
                {
                    best = middle;
                    low = middle + 1;
                }
                else
                {
                    high = middle - 1;
                }
            }
            if (best < 0)
                throw new InvalidOperationException("Bounded diagnostic attributes leave no room for one Unicode rune.");
            chunks.Add(content[boundaries[startBoundary]..boundaries[best]]);
            startBoundary = best;
        }
        return chunks;
    }

    private static bool SerializedDiagnosticLogFits(
        string body,
        IReadOnlyDictionary<string, object> attributes)
    {
        var modeledAttributes = attributes.ToDictionary(
            item => item.Key,
            item => (object)new
            {
                value = item.Value,
                type = item.Value is string ? "string" : "integer"
            },
            StringComparer.Ordinal);
        modeledAttributes["log.chunk_index"] = new { value = long.MaxValue, type = "integer" };
        modeledAttributes["log.chunk_count"] = new { value = long.MaxValue, type = "integer" };
        modeledAttributes["sentry.sdk.name"] = new { value = new string('s', 64), type = "string" };
        modeledAttributes["sentry.sdk.version"] = new { value = new string('v', 32), type = "string" };
        modeledAttributes["sentry.release"] = new { value = new string('r', 200), type = "string" };
        modeledAttributes["sentry.environment"] = new { value = new string('e', 64), type = "string" };
        modeledAttributes["user.id"] = new { value = new string('u', 64), type = "string" };
        var model = new
        {
            timestamp = 9_999_999_999.999,
            level = "fatal",
            body,
            trace_id = new string('f', 32),
            span_id = new string('f', 16),
            attributes = modeledAttributes
        };
        return JsonSerializer.SerializeToUtf8Bytes(model).Length <= MaxSerializedDiagnosticLogBytes;
    }

    private static void CaptureStructuredLog(SentryLogLevel level, Action<SentryLog> configure, string content)
    {
        switch (level)
        {
            case SentryLogLevel.Fatal:
                SentrySdk.Logger.LogFatal(configure, content);
                break;
            case SentryLogLevel.Error:
                SentrySdk.Logger.LogError(configure, content);
                break;
            case SentryLogLevel.Warning:
                SentrySdk.Logger.LogWarning(configure, content);
                break;
            case SentryLogLevel.Debug:
                SentrySdk.Logger.LogDebug(configure, content);
                break;
            default:
                SentrySdk.Logger.LogInfo(configure, content);
                break;
        }
    }

    private static SentryLogLevel GetLogLevel(string content)
    {
        if (content.Contains("[FTL]", StringComparison.OrdinalIgnoreCase)
            || content.Contains("[FATAL]", StringComparison.OrdinalIgnoreCase))
            return SentryLogLevel.Fatal;
        if (content.Contains("[ERR]", StringComparison.OrdinalIgnoreCase)
            || content.Contains("[ERROR]", StringComparison.OrdinalIgnoreCase))
            return SentryLogLevel.Error;
        if (content.Contains("[WRN]", StringComparison.OrdinalIgnoreCase)
            || content.Contains("[WARN]", StringComparison.OrdinalIgnoreCase))
            return SentryLogLevel.Warning;
        if (content.Contains("[DBG]", StringComparison.OrdinalIgnoreCase)
            || content.Contains("[DEBUG]", StringComparison.OrdinalIgnoreCase))
            return SentryLogLevel.Debug;
        return SentryLogLevel.Info;
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var sanitized = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "unknown-task" : sanitized;
    }

    private static IReadOnlyDictionary<string, string> BuildOptionSummary(MFATask task)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var item = task.SourceItem?.InterfaceItem;
        if (item == null) return result;
        foreach (var option in item.Option ?? [])
            AddOptionSummary(result, option);
        foreach (var advanced in item.Advanced ?? [])
        {
            foreach (var data in advanced.Data)
                result[$"advanced.{advanced.Name}.{data.Key}"] = string.IsNullOrEmpty(data.Value) ? "empty" : "filled";
        }
        return result;
    }

    private static void AddOptionSummary(Dictionary<string, string> result, MaaInterface.MaaInterfaceSelectOption option)
    {
        if (string.IsNullOrWhiteSpace(option.Name)) return;
        MaaInterface.MaaInterfaceOption? definition = null;
        MaaProcessor.Interface?.Option?.TryGetValue(option.Name, out definition);
        if (option.Index.HasValue)
        {
            var selectedCase = definition?.Cases?.ElementAtOrDefault(option.Index.Value)?.Name;
            result[option.Name] = selectedCase ?? option.Index.Value.ToString();
        }
        if (option.SelectedCases is { Count: > 0 }) result[$"{option.Name}.cases"] = string.Join(",", option.SelectedCases);
        if (option.Data != null && definition != null)
        {
            foreach (var input in definition.Inputs ?? [])
            {
                var value = option.Data.GetValueOrDefault(input.Name ?? string.Empty);
                var type = input.PipelineType?.ToLowerInvariant();
                result[$"{option.Name}.{input.Name}"] = type is "int" or "float" or "bool" ? value ?? string.Empty : string.IsNullOrEmpty(value) ? "empty" : "filled";
            }
        }
        foreach (var child in option.SubOptions ?? []) AddOptionSummary(result, child);
    }

    private static string CreateAnonymousMachineId()
    {
        var raw = SimpleEncryptionHelper.GetPlatformSpecificId();
        if (string.IsNullOrWhiteSpace(raw)) raw = Environment.MachineName;
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes($"mfa-telemetry-v1:{raw}"))).ToLowerInvariant();
    }

    private static Dictionary<string, object> CollectHardwareContext()
    {
        var result = new Dictionary<string, object>
        {
            ["os"] = RuntimeInformation.OSDescription,
            ["architecture"] = RuntimeInformation.OSArchitecture.ToString(),
            ["cpu"] = "unknown",
            ["cpu_cores"] = Environment.ProcessorCount,
            ["memory_total_mb"] = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024 / 1024,
            ["gpu"] = "unknown"
        };
        if (!System.OperatingSystem.IsWindows()) return result;

        try
        {
#pragma warning disable CA1416
            using (var cpuSearcher = new ManagementObjectSearcher("SELECT Name FROM Win32_Processor"))
            {
                var cpu = cpuSearcher.Get().Cast<ManagementObject>()
                    .Select(item => item["Name"]?.ToString()?.Trim())
                    .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));
                if (cpu != null) result["cpu"] = cpu;
            }
            using (var memorySearcher = new ManagementObjectSearcher("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem"))
            {
                var memory = memorySearcher.Get().Cast<ManagementObject>()
                    .Select(item => item["TotalPhysicalMemory"])
                    .FirstOrDefault(value => value != null);
                if (memory != null && ulong.TryParse(memory.ToString(), out var bytes))
                    result["memory_total_mb"] = bytes / 1024 / 1024;
            }
            using var gpuSearcher = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController");
            var names = gpuSearcher.Get().Cast<ManagementObject>()
                .Select(item => item["Name"]?.ToString()?.Trim())
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(4);
            var gpu = string.Join(", ", names);
            if (!string.IsNullOrWhiteSpace(gpu)) result["gpu"] = gpu;
#pragma warning restore CA1416
        }
        catch (Exception ex)
        {
            LoggerHelper.Debug($"[Telemetry] 硬件信息采集失败：{ex.GetType().Name}");
        }
        return result;
    }

    private static bool ShouldSampleAttachment(string runId, string taskKey, double rate)
    {
        if (rate <= 0) return false;
        if (rate >= 1) return true;
        using var sha = SHA256.Create();
        var input = Encoding.UTF8.GetBytes($"mfa-failure-attachment-v1:{runId}:{taskKey}");
        var digest = sha.ComputeHash(input);
        var bucket = BitConverter.ToUInt64(digest, 0) / (double)ulong.MaxValue;
        return bucket < rate;
    }
}
