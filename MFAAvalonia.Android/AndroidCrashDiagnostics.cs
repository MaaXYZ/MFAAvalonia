using Android.Content;
using Android.OS;
using Android.Runtime;
using System;
using System.IO;
using System.Text;
using System.Threading;

namespace MFAAvalonia.Android;

/// <summary>
/// Records the first Android-side managed exception before MFA's normal logger is available.
/// This is intentionally Android-only and never marks an exception as handled.
/// </summary>
internal static class AndroidCrashDiagnostics
{
    private const string Tag = "MFAAndroidCrash";
    private const string LogFileName = "android-crash.log";
    private static readonly Lock SyncRoot = new();
    private static string? _logPath;
    private static string _phase = "process-start";
    private static int _installed;

    public static void Install(Context context)
    {
        if (Interlocked.Exchange(ref _installed, 1) != 0)
            return;

        try
        {
            var root = AppContext.BaseDirectory.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            var logDirectory = Path.Combine(root, "logs");
            Directory.CreateDirectory(logDirectory);
            _logPath = Path.Combine(logDirectory, LogFileName);
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn(Tag, $"Unable to prepare the persistent crash log: {ex}");
        }

        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        AndroidEnvironment.UnhandledExceptionRaiser += OnAndroidUnhandledException;
        Write(
            "diagnostics-installed",
            $"package={context.PackageName}; sdk={Build.VERSION.SdkInt}; " +
            $"device={Build.Manufacturer}/{Build.Model}; abi={Build.SupportedAbis?[0] ?? "unknown"}",
            null);
    }

    public static void SetPhase(string phase)
    {
        _phase = phase;
        global::Android.Util.Log.Debug(Tag, $"phase={phase}");
    }

    public static void Record(string source, Exception exception) =>
        Write(source, $"phase={_phase}", exception);

    private static void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs args)
    {
        var exception = args.ExceptionObject as Exception
                        ?? new Exception(args.ExceptionObject?.ToString() ?? "Unknown unhandled exception object.");
        Write("app-domain", $"phase={_phase}; terminating={args.IsTerminating}", exception);
    }

    private static void OnAndroidUnhandledException(object? sender, RaiseThrowableEventArgs args)
    {
        // Do not set Handled: this hook is diagnostic only and must preserve Android's crash semantics.
        Write("android-runtime", $"phase={_phase}", args.Exception);
    }

    private static void Write(string source, string details, Exception? exception)
    {
        try
        {
            var message = new StringBuilder()
                .Append('[').Append(DateTimeOffset.Now.ToString("O")).Append("] ")
                .Append("source=").Append(source).Append("; ")
                .Append(details);
            if (exception != null)
                message.AppendLine().Append(exception);

            var text = message.ToString();
            global::Android.Util.Log.Error(Tag, text);
            if (string.IsNullOrWhiteSpace(_logPath))
                return;

            lock (SyncRoot)
            {
                File.AppendAllText(
                    _logPath,
                    text + System.Environment.NewLine + System.Environment.NewLine,
                    Encoding.UTF8);
            }
        }
        catch (Exception loggingException)
        {
            // Crash diagnostics must never replace the original exception.
            global::Android.Util.Log.Warn(Tag, $"Unable to persist crash diagnostics: {loggingException}");
        }
    }
}
