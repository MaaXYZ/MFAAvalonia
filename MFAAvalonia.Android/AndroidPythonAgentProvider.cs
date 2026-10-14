using Android.App;
using Android.Content;
using Android.OS;
using MaaFramework.Binding;
using MFAAvalonia.Extensions.MaaFW;
using System;
using System.Reflection;
using System.Text.Json;
using System.Threading;

namespace MFAAvalonia.Android;

internal sealed class AndroidPythonAgentProvider
{
    private const string ServiceClassMetadataKey = "MFA.Android.PythonServiceClass";
    private const string EntryPointMetadataKey = "MFA.Android.PythonAgentEntryPoint";
    private const string OutputLineExtra = "line";
    private const string FatalOutputPrefix = "__MFA_ANDROID_AGENT_FATAL__:";
    private const string ExitOutputPrefix = "__MFA_ANDROID_AGENT_EXIT__:";

    private readonly Activity _activity;
    private readonly Lock _sessionLock = new();
    private readonly string? _serviceClass;
    private readonly string? _entryPoint;
    private bool _sessionActive;

    public AndroidPythonAgentProvider(Activity activity)
    {
        _activity = activity;
        foreach (var attribute in typeof(AndroidPythonAgentProvider).Assembly
                     .GetCustomAttributes<AssemblyMetadataAttribute>())
        {
            if (string.Equals(attribute.Key, ServiceClassMetadataKey, StringComparison.Ordinal))
                _serviceClass = attribute.Value;
            else if (string.Equals(attribute.Key, EntryPointMetadataKey, StringComparison.Ordinal))
                _entryPoint = attribute.Value;
        }
    }

    public bool IsAvailable => !string.IsNullOrWhiteSpace(_serviceClass);

    public bool LinkStart(MaaAgentClient client)
    {
        var result = NativeBridgeInterop.SafeAgentClientConnect(client.Handle);
        if (result < 0)
        {
            global::Android.Util.Log.Warn(
                "MFAAgent",
                $"MaaAgentClientConnect was interrupted or threw across its native ABI; result={result}. Retrying.");
        }
        return result > 0;
    }

    public bool LinkStop(MaaAgentClient client)
    {
        var result = NativeBridgeInterop.SafeAgentClientDisconnect(client.Handle);
        if (result < 0)
        {
            global::Android.Util.Log.Warn(
                "MFAAgent",
                $"MaaAgentClientDisconnect was interrupted or threw across its native ABI; result={result}.");
        }
        return result > 0;
    }

    public IPlatformAgentSession? Start(PlatformAgentStartRequest request)
    {
        if (!IsAvailable)
            return null;

        lock (_sessionLock)
        {
            if (_sessionActive)
                throw new InvalidOperationException(
                    "MFA Android supports one python-for-android Agent service at a time.");
            _sessionActive = true;
        }

        try
        {
            var outputAction = $"{_activity.PackageName}.PYTHON_AGENT_OUTPUT";
            var startupState = new AgentStartupState();
            var outputReceiver = new AgentOutputReceiver(request.Output, startupState);
            var outputFilter = new IntentFilter(outputAction);
            if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu)
                _activity.RegisterReceiver(outputReceiver, outputFilter, ReceiverFlags.NotExported);
            else
                _activity.RegisterReceiver(outputReceiver, outputFilter);

            var serviceArgument = JsonSerializer.Serialize(new
            {
                client_id = request.Identifier,
                child_exec = request.Program,
                child_args = request.Arguments,
                data_root = request.DataRoot,
                entrypoint = _entryPoint,
                instance_id = request.InstanceId,
                instance_name = request.InstanceName,
                native_library_dir = _activity.ApplicationInfo?.NativeLibraryDir ?? string.Empty,
                output_action = outputAction,
                output_package = _activity.PackageName ?? string.Empty,
            });

            var serviceClass = _serviceClass!;
            try
            {
                AndroidPythonServiceInterop.Prepare(_activity, serviceClass);
                AndroidPythonServiceInterop.Start(_activity, serviceClass, serviceArgument);
                return new Session(this, serviceClass, outputReceiver, startupState);
            }
            catch
            {
                _activity.UnregisterReceiver(outputReceiver);
                outputReceiver.Dispose();
                throw;
            }
        }
        catch
        {
            ReleaseSession();
            throw;
        }
    }

    private void ReleaseSession()
    {
        lock (_sessionLock)
            _sessionActive = false;
    }

    private sealed class Session : IPlatformAgentSession, IPlatformAgentStartupStatus
    {
        private readonly AndroidPythonAgentProvider _owner;
        private readonly string _serviceClass;
        private readonly AgentOutputReceiver _outputReceiver;
        private readonly AgentStartupState _startupState;
        private bool _disposed;

        public Session(
            AndroidPythonAgentProvider owner,
            string serviceClass,
            AgentOutputReceiver outputReceiver,
            AgentStartupState startupState)
        {
            _owner = owner;
            _serviceClass = serviceClass;
            _outputReceiver = outputReceiver;
            _startupState = startupState;
        }

        public string? StartupFailure => _startupState.Failure;

        public void CompleteStartup() => _startupState.Complete();

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            try
            {
                AndroidPythonServiceInterop.Stop(_owner._activity, _serviceClass);
            }
            finally
            {
                try
                {
                    _owner._activity.UnregisterReceiver(_outputReceiver);
                }
                catch (ArgumentException)
                {
                    // The Activity may already have torn down its receiver table.
                }
                _outputReceiver.Dispose();
                _owner.ReleaseSession();
            }
        }
    }

    private sealed class AgentStartupState
    {
        private string? _failure;
        private int _completed;

        public string? Failure => Volatile.Read(ref _failure);

        public void Complete() => Interlocked.Exchange(ref _completed, 1);

        public void Fail(string message)
        {
            if (Volatile.Read(ref _completed) == 0)
                Interlocked.CompareExchange(ref _failure, message, null);
        }
    }

    private sealed class AgentOutputReceiver(
        Action<string> output,
        AgentStartupState startupState) : BroadcastReceiver
    {
        public override void OnReceive(Context? context, Intent? intent)
        {
            var line = intent?.GetStringExtra(OutputLineExtra);
            if (string.IsNullOrEmpty(line))
                return;

            if (line.StartsWith(FatalOutputPrefix, StringComparison.Ordinal)
                || line.StartsWith(ExitOutputPrefix, StringComparison.Ordinal))
            {
                var prefixLength = line.StartsWith(FatalOutputPrefix, StringComparison.Ordinal)
                    ? FatalOutputPrefix.Length
                    : ExitOutputPrefix.Length;
                var detail = line[prefixLength..].Trim();
                startupState.Fail(string.IsNullOrEmpty(detail)
                    ? "The Android Python Agent process exited during startup."
                    : detail);
                output($"error: Android Python Agent startup failed: {detail}");
                return;
            }

            output(line);
        }
    }
}
