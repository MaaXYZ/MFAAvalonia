using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using MaaFramework.Binding;
using MFAAvalonia;
using MFAAvalonia.Helper;
using MFAAvalonia.ViewModels.Windows;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MFAAvalonia.Desktop;

sealed class Program
{
    /// <summary>
    /// 主窗口关闭后的强制退出超时时间（毫秒）
    /// </summary>
    private const int ForceExitTimeoutMs = 5000;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int MessageBox(IntPtr hWnd, string lpText, string lpCaption, uint uType);

    [STAThread]
    public static void Main(string[] args)
    {
        // 快速识别参数：用于更新时判断该可执行文件是否为 MFAAvalonia
        if (args.Length > 0 && args[0] == "--identify")
        {
            Console.Write("MFAAvalonia");
            return;
        }

        if (AppRuntime.IsHelpRequested(args))
        {
            Console.Write(AppRuntime.GetHelpText(Environment.ProcessPath));
            return;
        }

        try
        {
            var executablePath = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "MFAAvalonia.exe");
            var instanceKey = AppRuntime.CreateInstanceKey(executablePath);
            AppRuntime.Initialize(args, instanceKey);
            TelemetryService.InitializeBootstrapFromInterface(AppContext.BaseDirectory);

            if (!AppRuntime.IsNewInstance && AppRuntime.TryForwardLaunchCommand())
            {
                AppRuntime.ReleaseMutex();
                return;
            }

            if (!AppRuntime.IsNewInstance)
                _ = AppRuntime.TryRecoverUnresponsiveInstance();

            CheckSkiaAvailability();

            if (IsRunningInTemp())
            {
                App.IsTempDirMode = true;
            }

            LoggerHelper.InitializeLogger();
            Directory.SetCurrentDirectory(AppContext.BaseDirectory);
            PendingUpdateDeletionHelper.ProcessPendingDirectories();
#if DEBUG
            // Debug must load freshly built managed assemblies from the output root.
            // Falling back to libs can silently run stale Markdown/UI binaries.
            PrivatePathHelper.SetupNativeLibraryResolver(enableManagedLibraryResolver: false);
#else
            PrivatePathHelper.CleanupDuplicateLibraries(AppContext.BaseDirectory, AppContext.GetData("SubdirectoriesToProbe") as string);
            PrivatePathHelper.SetupNativeLibraryResolver();
#endif

            List<string> resultDirectories = new();

            string baseDirectory = AppContext.BaseDirectory;

            string runtimesPath = Path.Combine(baseDirectory, "runtimes");

            if (!Directory.Exists(runtimesPath))
            {
                try
                {
                    LoggerHelper.Warning("runtimes文件夹不存在");
                }
                catch
                {
                }
            }
            else
            {
                var maaFiles = Directory.EnumerateFiles(
                    runtimesPath,
                    "*MaaFramework*",
                    SearchOption.AllDirectories
                );

                foreach (var filePath in maaFiles)
                {
                    var fileDirectory = Path.GetDirectoryName(filePath) ?? string.Empty;
                    if (!resultDirectories.Contains(fileDirectory) && fileDirectory.Contains(VersionChecker.GetNormalizedArchitecture()))
                    {
                        resultDirectories.Add(fileDirectory);
                    }
                }
                try
                {
                    LoggerHelper.Info($"检测到 MaaFramework 运行时目录：数量={resultDirectories.Count}，路径=[{string.Join(", ", resultDirectories)}]");
                }
                catch
                {
                }
                NativeBindingContext.AppendNativeLibrarySearchPaths(resultDirectories);
            }

            try
            {
                LoggerHelper.Info($"启动参数：数量={AppRuntime.Args.Count}，内容=[{string.Join(", ", AppRuntime.Args)}]");
                LoggerHelper.Info($"程序版本：{RootViewModel.Version}");
                LoggerHelper.Info($".NET 运行时：{RuntimeInformation.FrameworkDescription}");
            }
            catch
            {
            }

            // 启动强制退出监控线程
            // 当主窗口关闭后，如果进程在指定时间内没有正常退出，则强制终止
            StartForceExitWatchdog();

            BuildAvaloniaApp()
                .StartWithClassicDesktopLifetime(args, ShutdownMode.OnMainWindowClose);

            // 主窗口已关闭，通知监控线程开始计时
            SignalMainWindowClosed();
        }
        catch (Exception e)
        {
            try
            {
                Console.Error.WriteLine($"启动失败，总异常捕获：{e}");
            }
            catch
            {
            }

            // 使用 App类的统一错误处理方法（确保只显示一次）
            App.ShowStartupErrorAndExit(e, "程序启动");
        }
    }

    /// <summary>
    /// 用于通知监控线程主窗口已关闭的事件
    /// </summary>
    private static readonly ManualResetEventSlim MainWindowClosedEvent = new(false);

    /// <summary>
    /// 启动强制退出监控线程
    /// </summary>
    private static void StartForceExitWatchdog()
    {
        var watchdogThread = new Thread(() =>
        {
            try
            {
                // 等待主窗口关闭信号
                MainWindowClosedEvent.Wait();

                // 主窗口已关闭，开始计时
                // 等待指定时间，如果进程还没退出则强制终止
                Thread.Sleep(ForceExitTimeoutMs);

                // 如果代码执行到这里，说明进程在超时时间内没有正常退出
                try
                {
                    LoggerHelper.Warning($"进程在主窗口关闭后 {ForceExitTimeoutMs}ms 内未能正常退出，强制终止进程");
                }
                catch
                {
                    // 忽略日志错误
                }

                // 强制终止当前进程
                Environment.Exit(0);
            }
            catch
            {
                // 忽略监控线程中的任何异常
            }
        })
        {
            Name = "ForceExitWatchdog",
            IsBackground = true, // 设置为后台线程，这样如果主线程正常退出，此线程也会自动终止
            Priority = ThreadPriority.BelowNormal
        };

        watchdogThread.Start();
    }

    /// <summary>
    /// 通知监控线程主窗口已关闭
    /// </summary>
    private static void SignalMainWindowClosed()
    {
        MainWindowClosedEvent.Set();
    }

    private static void CheckSkiaAvailability()
    {
        try
        {
            // 尝试访问 SkiaSharp 类型以触发潜在的 DllNotFoundException
            // Accessing static property might trigger type initializer
            var test = SkiaSharp.SKImageInfo.Empty;
        }
        catch (Exception e)
        {
            try
            {
                LoggerHelper.Error($"SkiaSharp 依赖检测失败：{e.Message}", e);
                if (OperatingSystem.IsWindows())
                {
                    MessageBox(IntPtr.Zero,
                        $"启动检测失败：无法加载 SkiaSharp 图形库组件。\n通常是因为缺少 Visual C++ 运行库或文件缺失。\n\n详细错误: {e.Message}\n{e.InnerException?.Message}",
                        "MFA - 严重错误",
                        0x10); // MB_ICONERROR
                }
                else
                {
                    Console.Error.WriteLine("Critical Error: SkiaSharp dependency missing.");
                    Console.Error.WriteLine(e);
                }
            }
            catch
            {
                // Ignore errors during error reporting
            }
            Environment.Exit(1);
        }
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
    }

    private static bool IsRunningInTemp()
    {
        try
        {
            var tempPath = Path.GetTempPath();
            var currentPath = AppContext.BaseDirectory;

            if (string.IsNullOrEmpty(tempPath) || string.IsNullOrEmpty(currentPath))
            {
                return false;
            }

            // Normalize paths
            tempPath = Path.GetFullPath(tempPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            currentPath = Path.GetFullPath(currentPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            return currentPath.StartsWith(tempPath, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
