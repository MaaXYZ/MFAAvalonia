using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Notifications;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using MFAAvalonia.Configuration;
using MFAAvalonia.Extensions;
using MFAAvalonia.Extensions.MaaFW;
using MFAAvalonia.Helper;
using MFAAvalonia.ViewModels.Other;
using MFAAvalonia.ViewModels.Pages;
using MFAAvalonia.ViewModels.UsersControls;
using MFAAvalonia.ViewModels.UsersControls.Settings;
using MFAAvalonia.ViewModels.Windows;
using MFAAvalonia.Views.Mobile;
using MFAAvalonia.Views.Pages;
using MFAAvalonia.Views.UserControls;
using MFAAvalonia.Views.UserControls.Settings;
using MFAAvalonia.Views.Windows;
using Microsoft.Extensions.DependencyInjection;
using SukiUI.Dialogs;
using SukiUI.Toasts;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Net.Http;

namespace MFAAvalonia;

public partial class App : Application
{
    private static readonly string[] WindowsMaaNativeLibraryManifest =
    [
        "fastdeploy_ppocr_maa.dll",
        "MaaAdbControlUnit.dll",
        "MaaAgentClient.dll",
        "MaaAgentServer.dll",
        "MaaCustomControlUnit.dll",
        "MaaFramework.dll",
        "MaaGamepadControlUnit.dll",
        "MaaRecordControlUnit.dll",
        "MaaReplayControlUnit.dll",
        "MaaToolkit.dll",
        "MaaUtils.dll",
        "MaaWin32ControlUnit.dll",
        "onnxruntime_maa.dll",
        "opencv_world4_maa.dll",
        "MaaFramework.Binding.Native.dll",
    ];

    private static readonly string[] LinuxMaaNativeLibraryManifest =
    [
        "libMaaAdbControlUnit.so",
        "libMaaAgentClient.so",
        "libMaaAgentServer.so",
        "libMaaCustomControlUnit.so",
        "libMaaFramework.so",
        "libMaaKWinControlUnit.so",
        "libMaaRecordControlUnit.so",
        "libMaaReplayControlUnit.so",
        "libMaaToolkit.so",
        "libMaaUtils.so",
        "libMaaWlRootsControlUnit.so",
    ];

    private static readonly string[] MacOsMaaNativeLibraryManifest =
    [
        "libMaaAdbControlUnit.dylib",
        "libMaaAgentClient.dylib",
        "libMaaAgentServer.dylib",
        "libMaaCustomControlUnit.dylib",
        "libMaaFramework.dylib",
        "libMaaMacOSControlUnit.dylib",
        "libMaaPlayCoverControlUnit.dylib",
        "libMaaRecordControlUnit.dylib",
        "libMaaReplayControlUnit.dylib",
        "libMaaToolkit.dylib",
        "libMaaUtils.dylib",
    ];

    private static readonly IReadOnlyDictionary<string, string[]> MaaNativeLibraryManifestByRuntimeIdentifier =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["win-x64"] = WindowsMaaNativeLibraryManifest,
            ["win-arm64"] = WindowsMaaNativeLibraryManifest,
            ["linux-x64"] = LinuxMaaNativeLibraryManifest,
            ["linux-arm64"] = LinuxMaaNativeLibraryManifest,
            ["osx-x64"] = MacOsMaaNativeLibraryManifest,
            ["osx-arm64"] = MacOsMaaNativeLibraryManifest,
        };

    private sealed class StartupDependencyDiagnosis
    {
        public bool IsMaaNativeLibraryMissing { get; init; }
        public bool IsVisualCppRuntimeMissing { get; init; }
        public string MissingLibrarySummary { get; init; } = string.Empty;
        public string ExpectedRuntimeDirectory { get; init; } = string.Empty;
        public IReadOnlyList<string> ExpectedMaaLibraries { get; init; } = Array.Empty<string>();
    }

    /// <summary>
    /// Gets services.
    /// </summary>
    public static IServiceProvider Services { get; private set; }

    /// <summary>
    /// 内存优化器实例（保存引用以便在退出时释放）
    /// </summary>
    private static AvaloniaMemoryCracker? _memoryCracker;

    /// <summary>
    /// 标记是否已经显示过启动错误对话框（确保只显示一次）
    /// 使用 int 类型以便使用 Interlocked 原子操作
    /// 0 = 未显示，1 = 已显示
    /// </summary>
    private static int _hasShownStartupError = 0;

    /// <summary>
    /// 是否处于运行库缺失模式（仅显示下载窗口）
    /// </summary>
    public static bool IsRuntimeMissingMode = false;

    /// <summary>
    /// 是否处于临时目录运行模式（显示警告窗口）
    /// </summary>
    public static bool IsTempDirMode = false;

    public override void Initialize()
    {
        try
        {
            TelemetryService.InitializeBootstrapFromInterface(AppContext.BaseDirectory);
            base.Initialize();
            AppPaths.Initialize();
            LoggerHelper.InitializeLogger();
            if (!IsRuntimeMissingMode && !IsTempDirMode)
            {
                MaaProcessor.StartInterfacePreload();
            }
            AppPaths.CleanupObsoleteExecutableBackups(
                message => LoggerHelper.Info(message),
                message => LoggerHelper.Warning(message));
            AvaloniaXamlLoader.Load(this);
            LanguageHelper.Initialize();
            ConfigurationManager.Initialize();
            SystemSleepHelper.ApplyPreventSleep();
            FontService.Initialize();

            // 保存引用以便在退出时正确释放
            _memoryCracker = new AvaloniaMemoryCracker();
            _memoryCracker.Cracker();

            GlobalHotkeyService.Initialize();
            TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException; //Task线程内未捕获异常处理事件
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException; //非UI线程内未捕获异常处理事件
            Dispatcher.UIThread.UnhandledException += OnDispatcherUnhandledException; //UI线程内未捕获异常处理事件
        }
        catch (Exception ex)
        {
            LoggerHelper.Error($"应用初始化失败：原因={ex.Message}", ex);
            ShowStartupErrorAndExit(ex, "应用初始化");
        }
    }


    public override void OnFrameworkInitializationCompleted()
    {
        try
        {
            if (IsRuntimeMissingMode)
            {
                if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop1)
                {
                    desktop1.MainWindow = new RuntimeMissingWindow();
                }
                base.OnFrameworkInitializationCompleted();
                return;
            }

            if (IsTempDirMode)
            {
                if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktopTemp)
                {
                    desktopTemp.MainWindow = new TempDirWarningWindow();
                }
                base.OnFrameworkInitializationCompleted();
                return;
            }

            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.ShutdownRequested += OnShutdownRequested;
                var services = new ServiceCollection();

                services.AddSingleton(desktop);

                ConfigureServices(services);

                var views = ConfigureViews(services);

                Services = services.BuildServiceProvider();

                MaaProcessorManager.Instance.LoadInstanceConfig();
                if (!OperatingSystem.IsAndroid())
                {
                    TelemetryService.InitializeFromInterface();

                    // 启动懒加载：先加载 ActiveTab，再加载有定时任务的，最后加载其余
                    _ = MaaProcessorManager.Instance.StartLazyLoadingAsync();
                }

                DataTemplates.Add(new ViewLocator(views));

                var window = views.CreateView<RootViewModel>(Services) as Window;

                desktop.MainWindow = window;

                TrayIconManager.InitializeTrayIcon(this, Instances.RootView, Instances.RootViewModel);

                if (GlobalConfiguration.HasFileAccessError)
                {
                    var reason = (GlobalConfiguration.LastFileAccessErrorMessage ?? string.Empty).Trim();
                    if (string.IsNullOrWhiteSpace(reason))
                    {
                        reason = "GlobalConfigFileReasonUnknown".ToLocalization();
                    }
                    var message = "GlobalConfigFileAccessError".ToLocalizationFormatted(false, GlobalConfiguration.ConfigPath, reason);
                    DispatcherHelper.PostOnMainThread(() =>
                        Instances.DialogManager.CreateDialog()
                            .OfType(NotificationType.Error)
                            .WithContent(message)
                            .WithActionButton(LangKeys.Ok.ToLocalization(), _ => { }, true)
                            .TryShow());
                }
            }
            else if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
            {
                var services = new ServiceCollection();

                services.AddSingleton(singleView);

                ConfigureServices(services);

                var views = ConfigureViews(services);

                Services = services.BuildServiceProvider();

                MaaProcessorManager.Instance.LoadInstanceConfig();
                if (!OperatingSystem.IsAndroid())
                {
                    TelemetryService.InitializeFromInterface();

                    // 启动懒加载
                    _ = MaaProcessorManager.Instance.StartLazyLoadingAsync();
                }

                DataTemplates.Add(new ViewLocator(views));

                var mainView = views.CreateView<RootViewModel>(Services);

                singleView.MainView = mainView;

                if (OperatingSystem.IsAndroid())
                {
                    MaaProcessorManager.Instance.Current.InitializeData();
                    TimerModel.Instance.RefreshInstanceList();
                }

                if (GlobalConfiguration.HasFileAccessError)
                {
                    var reason = (GlobalConfiguration.LastFileAccessErrorMessage ?? string.Empty).Trim();
                    if (string.IsNullOrWhiteSpace(reason))
                    {
                        reason = "GlobalConfigFileReasonUnknown".ToLocalization();
                    }
                    var message = "GlobalConfigFileAccessError".ToLocalizationFormatted(false, GlobalConfiguration.ConfigPath, reason);
                    DispatcherHelper.PostOnMainThread(() =>
                        Instances.DialogManager.CreateDialog()
                            .OfType(NotificationType.Error)
                            .WithContent(message)
                            .WithActionButton(LangKeys.Ok.ToLocalization(), _ => { }, true)
                            .TryShow());
                }
            }

            base.OnFrameworkInitializationCompleted();
        }
        catch (Exception ex)
        {
            LoggerHelper.Error($"框架初始化失败：原因={ex.Message}", ex);
            ShowStartupErrorAndExit(ex, "框架初始化");
        }
    }


    private void OnShutdownRequested(object sender, ShutdownRequestedEventArgs e)
    {
        TelemetryService.Shutdown();
        TrayIconManager.DisposeTrayIcon(this);

        Instances.PersistRuntimeState();

        foreach (var p in MaaProcessor.Processors)
        {
            p.SetTasker();
        }
        GlobalHotkeyService.Shutdown();

        // 强制清理所有应用资源（包括字体）
        ForceCleanupAllResources();

        // 释放内存优化器
        _memoryCracker?.Dispose();
        _memoryCracker = null;

        // 取消全局异常事件订阅，避免内存泄漏
        TaskScheduler.UnobservedTaskException -= TaskScheduler_UnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException -= CurrentDomain_UnhandledException;
        Dispatcher.UIThread.UnhandledException -= OnDispatcherUnhandledException;
    }

    /// <summary>
    /// 手动清理内存缓存（用于降低内存占用）
    /// 此方法会清除字体缓存等非必要的内存占用
    /// </summary>
    public static void ClearMemoryCaches()
    {
        try
        {
            // 清除字体缓存（保留当前使用的字体）
            FontService.Instance.ClearFontCache();

            LoggerHelper.Info("[内存管理] 已清除应用程序缓存。");
        }
        catch (Exception ex)
        {
            LoggerHelper.Warning($"[内存管理] 清除缓存失败：原因={ex.Message}");
        }
    }

    /// <summary>
    /// 强制清理所有资源（用于应用退出）
    /// </summary>
    private static void ForceCleanupAllResources()
    {
        try
        {
            // 强制清理所有字体资源
            FontService.Instance.ForceCleanupAllFontResources();

            LoggerHelper.Info("[内存管理] 已强制清理所有应用资源。");
        }
        catch (Exception ex)
        {
            LoggerHelper.Warning($"[内存管理] 强制清理资源失败：原因={ex.Message}");
        }
    }

    private static ViewsHelper ConfigureViews(ServiceCollection services)
    {

        var views = new ViewsHelper();

        if (OperatingSystem.IsAndroid())
        {
            views.AddView<RootViewMobile, RootViewModel>(services);
        }
        else
        {
            views.AddView<RootView, RootViewModel>(services);
        }

        return views

            // Add pages
            .AddView<InstanceContainerView, InstanceTabBarViewModel>(services)
            .AddView<TaskQueueView, TaskQueueViewModel>(services)
            .AddView<MonitorView, MonitorViewModel>(services)
            .AddView<ResourcesView, ResourcesViewModel>(services)
            .AddView<SettingsView, SettingsViewModel>(services)
            .AddView<ScreenshotView, ScreenshotViewModel>(services)

            // Add additional views
            .AddView<AddTaskDialogView, AddTaskDialogViewModel>(services)
            .AddView<TaskRemarkDialogView, TaskRemarkDialogViewModel>(services)
            .AddView<ExportLogDialogView, ExportLogDialogViewModel>(services)
            .AddView<AdbEditorDialogView, AdbEditorDialogViewModel>(services)
            .AddView<PlayCoverEditorDialog, PlayCoverEditorDialogViewModel>(services)
            .AddView<RenameInstanceDialog, RenameInstanceDialogViewModel>(services)
            .AddView<MultiInstanceEditorDialogView, MultiInstanceEditorDialogViewModel>(services)
            .AddView<CustomThemeDialogView, CustomThemeDialogViewModel>(services)
            .AddView<ConnectSettingsUserControl, ConnectSettingsUserControlModel>(services)
            .AddView<GameSettingsUserControl, GameSettingsUserControlModel>(services)
            .AddView<GuiSettingsUserControl, GuiSettingsUserControlModel>(services)
            .AddView<StartSettingsUserControl, StartSettingsUserControlModel>(services)
            .AddView<ExternalNotificationSettingsUserControl, ExternalNotificationSettingsUserControlModel>(services)
            .AddView<TimerSettingsUserControl, TimerSettingsUserControlModel>(services)
            .AddView<PerformanceUserControl, PerformanceUserControlModel>(services)
            .AddView<VersionUpdateSettingsUserControl, VersionUpdateSettingsUserControlModel>(services)
            .AddOnlyView<AboutUserControl, SettingsViewModel>(services)
            .AddOnlyView<HotKeySettingsUserControl, SettingsViewModel>(services)
            .AddOnlyView<ConfigurationMgrUserControl, SettingsViewModel>(services);
    }

    private static void ConfigureServices(ServiceCollection services)
    {
        services.AddSingleton<ISukiToastManager, SukiToastManager>();
        services.AddSingleton<ISukiDialogManager, SukiDialogManager>();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            // 如果已经显示过启动错误，不再显示其他错误对话框
            if (System.Threading.Interlocked.CompareExchange(ref _hasShownStartupError, 0, 0) == 1)
            {
                LoggerHelper.Error($"启动失败后又发生 UI 线程异常，已忽略弹窗：原因={e.Exception.Message}", e.Exception);
                e.Handled = true;
                return;
            }

            if (TryIgnoreException(e.Exception, out string errorMessage))
            {
                LoggerHelper.Warning(errorMessage);
                LoggerHelper.Error($"已忽略 UI 线程异常：原因={e.Exception.Message}", e.Exception);
                e.Handled = true;
                return;
            }

            e.Handled = true;
            LoggerHelper.Error(e.Exception);
            TelemetryService.CaptureException(e.Exception, "ui-thread");
            ErrorView.ShowException(e.Exception);
        }
        catch (Exception ex)
        {
            LoggerHelper.Error($"处理 UI 线程异常时发生错误：原因={ex.Message}", ex);
            if (System.Threading.Interlocked.CompareExchange(ref _hasShownStartupError, 0, 0) == 0)
            {
                ErrorView.ShowException(ex, true);
            }
        }
    }

    void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        try
        {
            // 如果已经显示过启动错误，不再显示其他错误对话框
            if (System.Threading.Interlocked.CompareExchange(ref _hasShownStartupError, 0, 0) == 1)
            {
                LoggerHelper.Error($"启动失败后又发生非 UI 线程异常，已忽略弹窗：对象={e.ExceptionObject}");
                return;
            }

            if (e.ExceptionObject is Exception ex && TryIgnoreException(ex, out string errorMessage))
            {
                LoggerHelper.Warning(errorMessage);
                LoggerHelper.Error($"已忽略非 UI 线程异常：原因={ex.Message}", ex);
                return;
            }

            var sbEx = new StringBuilder();
            if (e.IsTerminating)
                sbEx.Append("非UI线程发生致命错误");
            else
                sbEx.Append("非UI线程异常：");

            if (e.ExceptionObject is Exception ex2)
            {
                TelemetryService.CaptureException(ex2, "app-domain");
                ErrorView.ShowException(ex2);
                sbEx.Append(ex2);
            }
            else
            {
                sbEx.Append(e.ExceptionObject);
            }
            LoggerHelper.Error($"捕获到非 UI 线程未处理异常：详情={sbEx}", e.ExceptionObject as Exception ?? new Exception(sbEx.ToString()));
        }
        catch (Exception ex)
        {
            LoggerHelper.Error($"处理非 UI 线程异常时发生错误：原因={ex.Message}", ex);
        }
    }

    void TaskScheduler_UnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs e)
    {
        try
        {
            // 如果已经显示过启动错误，不再显示其他错误对话框
            if (System.Threading.Interlocked.CompareExchange(ref _hasShownStartupError, 0, 0) == 1)
            {
                LoggerHelper.Error($"启动失败后又发生未观察任务异常，已忽略弹窗：原因={e.Exception.Message}", e.Exception);
                e.SetObserved();
                return;
            }

            if (TryIgnoreException(e.Exception, out string errorMessage))
            {
                LoggerHelper.Warning(errorMessage);
                LoggerHelper.Info($"已忽略未观察任务异常：原因={e.Exception.Message}");
            }
            else
            {
                LoggerHelper.Error(e.Exception);
                TelemetryService.CaptureException(e.Exception, "unobserved-task");
                ErrorView.ShowException(e.Exception);

                foreach (var item in e.Exception.InnerExceptions ?? Enumerable.Empty<Exception>())
                {
                    LoggerHelper.Error(string.Format("异常类型：{0}{1}来自：{2}{3}异常内容：{4}",
                        item.GetType(), Environment.NewLine, item.Source,
                        Environment.NewLine, item.Message));
                }
            }

            e.SetObserved();
        }
        catch (Exception ex)
        {
            LoggerHelper.Error($"处理未观察任务异常时发生错误：原因={ex.Message}", ex);
            e.SetObserved();
        }
    }

// 统一的异常过滤方法，返回是否应该忽略以及对应的错误消息
    private bool TryIgnoreException(Exception ex, out string errorMessage)
    {
        errorMessage = string.Empty;

        // 递归检查InnerException
        if (ex.InnerException != null && TryIgnoreException(ex.InnerException, out errorMessage))
            return true;

        // 检查AggregateException的所有InnerExceptions
        if (ex is AggregateException aggregateEx)
        {
            foreach (var innerEx in aggregateEx.InnerExceptions)
            {
                if (TryIgnoreException(innerEx, out errorMessage))
                    return true;
            }
        }
        if (ex is IOException exception && exception.Message.Contains("EOF"))
        {
            errorMessage = "SSL验证证书错误";
            LoggerHelper.Warning($"已忽略 EOF 类型 IO 异常：原因={exception.Message}");
            return true;
        }

        // 检查特定类型的异常并设置对应的错误消息
        if (ex is OperationCanceledException)
        {
            errorMessage = "已忽略任务取消异常";
            return true;
        }

        if (ex is InvalidOperationException && ex.Message.Contains("Stop"))
        {
            errorMessage = "已忽略与 Stop 相关的异常：" + ex.Message;
            return true;
        }

        if (ex.GetType().FullName == "SharpHook.HookException")
        {
            errorMessage = "macOS中的全局快捷键Hook异常，可能是由于权限不足或系统限制导致的";
            return true;
        }

        if (ex is AuthenticationException)
        {
            errorMessage = "SSL验证证书错误";
            return true;
        }

        if (ex is SocketException)
        {
            errorMessage = "代理设置的SSL验证错误";
            return true;
        }

        // 检查 DBus 异常（仅在 Linux 上可用）
        if (TryHandleDBusException(ex, out errorMessage))
        {
            return true;
        }

//忽略 SEHException，这通常是由于外部组件（如 MaaFramework）的问题导致的
// 这些异常已经在业务逻辑中处理了（如显示连接失败消息），不应该再次显示给用户
        if (ex is SEHException)
        {
            errorMessage = "已忽略外部组件异常（SEHException）：" + ex.Message;
            return true;
        }

        return false;
    }

    /// <summary>
    /// 尝试处理 DBus 异常（仅在 Linux 上可用）
    /// 使用反射来避免在 Windows 上加载 Tmds.DBus.Protocol 程序集
    /// </summary>
    private static bool TryHandleDBusException(Exception ex, out string errorMessage)
    {
        errorMessage = string.Empty;
        try
        {
            // 检查异常类型名称，避免直接引用 Tmds.DBus.Protocol 类型
            var exType = ex.GetType();
            if (exType.FullName == "Tmds.DBus.Protocol.DBusException")
            {
                // 使用反射获取 ErrorName 和 Message 属性
                var errorNameProp = exType.GetProperty("ErrorName");
                var errorName = errorNameProp?.GetValue(ex) as string;

                if (errorName == "org.freedesktop.DBus.Error.ServiceUnknown" && ex.Message.Contains("com.canonical.AppMenu.Registrar"))
                {
                    errorMessage = "检测到DBus服务(com.canonical.AppMenu.Registrar)不可用，这在非Unity桌面环境中是正常现象";
                    return true;
                }
            }
        }
        catch
        {
            // 如果反射失败，忽略错误
        }

        return false;
    }

    /// <summary>
    /// 显示启动错误并退出应用（确保只显示一次）
    /// 使用 Interlocked 原子操作确保线程安全
    /// </summary>
    public static void ShowStartupErrorAndExit(Exception exception, string stage = "启动")
    {
        // 使用原子操作检查并设置标志，确保只有一个线程能进入
        if (System.Threading.Interlocked.CompareExchange(ref _hasShownStartupError, 1, 0) != 0)
        {
            // 已经有其他线程在显示错误对话框，直接退出
            System.Threading.Thread.Sleep(100); // 等待一下让第一个线程显示对话框
            Environment.Exit(1);
            return;
        }

        TelemetryService.CaptureStartupException(exception);
        try
        {
            Sentry.SentrySdk.FlushAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
        }
        catch
        {
            // Startup diagnostics must not delay or replace the original failure.
        }

        try
        {
            // 尝试获取本地化标题和消息格式
            var title = LanguageHelper.GetLocalizedString("StartupErrorTitle");
            if (string.IsNullOrEmpty(title) || title == "StartupErrorTitle")
                title = "启动失败";

            var format = LanguageHelper.GetLocalizedString("StartupErrorMessage");
            if (string.IsNullOrEmpty(format) || format == "StartupErrorMessage")
                format = "{0}失败\n\n错误信息：\n{1}\n\n详细信息：\n{2}";

            var message = string.Format(format, stage, exception.Message, exception);

            string? downloadUrl = null;

            // Windows: 检测 MSVC 2022 缺失问题
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var diagnosis = DiagnoseWindowsStartupDependencyIssue(exception);
                var isDllNotFound = diagnosis.IsMaaNativeLibraryMissing || diagnosis.IsVisualCppRuntimeMissing;

                if (isDllNotFound)
                {
                    if (diagnosis.IsMaaNativeLibraryMissing)
                    {
                        var missingMaaMsg = LanguageHelper.GetLocalizedString("MaaNativeLibraryMissing");
                        if (string.IsNullOrEmpty(missingMaaMsg) || missingMaaMsg == "MaaNativeLibraryMissing")
                            missingMaaMsg = "检测到缺少 MAA 依赖文件，请重新完整解压或重新安装程序。";

                        var missingMaaDetailFormat = LanguageHelper.GetLocalizedString("MaaNativeLibraryMissingDetail");
                        if (string.IsNullOrEmpty(missingMaaDetailFormat) || missingMaaDetailFormat == "MaaNativeLibraryMissingDetail")
                            missingMaaDetailFormat = "缺失文件：{0}\n建议检查目录：\n{1}\n\n请不要只单独复制主程序文件。";

                        var missingLibrarySummary = string.IsNullOrWhiteSpace(diagnosis.MissingLibrarySummary)
                            ? "MaaFramework.dll"
                            : diagnosis.MissingLibrarySummary;

                        var expectedRuntimeDirectory = string.IsNullOrWhiteSpace(diagnosis.ExpectedRuntimeDirectory)
                            ? Path.Combine(AppContext.BaseDirectory, "runtimes")
                            : diagnosis.ExpectedRuntimeDirectory;

                        message = $"{missingMaaMsg}\n\n{string.Format(missingMaaDetailFormat, missingLibrarySummary, expectedRuntimeDirectory)}";
                    }

                    if (diagnosis.IsVisualCppRuntimeMissing)
                    {
                        try
                        {
                            // 尝试显示自定义下载窗口
                            void ShowRuntimeWindow()
                            {
                                var win = new RuntimeMissingWindow();
                                win.Show();

                                // 启动消息循环等待窗口关闭（窗口内部处理下载和退出）
                                var cts = new System.Threading.CancellationTokenSource();
                                win.Closed += (s, e) => cts.Cancel();
                                Dispatcher.UIThread.MainLoop(cts.Token);
                            }

                            if (Dispatcher.UIThread.CheckAccess())
                            {
                                ShowRuntimeWindow();
                                Environment.Exit(1);
                                return;
                            }
                            else
                            {
                                var tcs = new TaskCompletionSource<bool>();
                                Dispatcher.UIThread.Post(() =>
                                {
                                    try
                                    {
                                        ShowRuntimeWindow();
                                        tcs.TrySetResult(true);
                                    }
                                    catch (Exception ex)
                                    {
                                        tcs.TrySetException(ex);
                                    }
                                });

                                if (tcs.Task.Wait(15000)) // 等待15秒防止死锁
                                {
                                    Environment.Exit(1);
                                    return;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            LoggerHelper.Error($"无法显示 RuntimeMissingWindow: {ex}");
                            // 尝试显示自定义下载窗口
                            try
                            {
                                // 如果我们在UI线程且App正常，直接显示
                                if (Application.Current != null && Dispatcher.UIThread.CheckAccess())
                                {
                                    var win = new RuntimeMissingWindow();
                                    win.Closed += (s, e) => Environment.Exit(1);
                                    win.Show();
                                    // 启动嵌套循环以阻塞退出
                                    Dispatcher.UIThread.MainLoop(System.Threading.CancellationToken.None);
                                    return;
                                }

                                // 当 SkiaSharp 等底层渲染库缺失时，Avalonia 无法启动 UI，因此 RuntimeMissingWindow 也无法显示。
                                // 这里不再尝试无效的重启，而是直接回退到原生消息框，并确保消息框内容友好。
                            }
                            catch
                            {
                                // 降级处理：如果在尝试显示窗口时失败，回退到MessageBox
                            }

                            downloadUrl = "https://aka.ms/vs/17/release/vc_redist.x64.exe";

                            // 优化错误信息：如果是缺运行库，只显示友好提示，不显示吓人的堆栈信息
                            // 这解决了“截图内容”问题
                            var msvcMsg = LanguageHelper.GetLocalizedString("MSVC2022Missing");
                            if (string.IsNullOrEmpty(msvcMsg) || msvcMsg == "MSVC2022Missing")
                                msvcMsg = "检测到缺少运行库 Runtime，请尝试下载并安装最新 Microsoft Visual C++ Redistributable (x64) 运行库。";

                            var link = LanguageHelper.GetLocalizedString("MSVC2022DownloadLink");
                            if (string.IsNullOrEmpty(link) || link == "MSVC2022DownloadLink")
                                link = "下载链接：" + downloadUrl;

                            var confirmStr = LanguageHelper.GetLocalizedString("DownloadNowConfirmation");
                            if (string.IsNullOrEmpty(confirmStr) || confirmStr == "DownloadNowConfirmation")
                                confirmStr = "是否立即下载？";

                            // 重写 message，仅包含友好提示
                            message = $"{msvcMsg}\n{link}\n\n{confirmStr}";
                        }
                    }

                    // 输出到控制台（作为备份）
                    Console.Error.WriteLine($"=== MFAAvalonia {title} ===");
                    Console.Error.WriteLine(message);

                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    {
                        // Windows: 使用 MessageBox
                        var shortMessage = message.Length > 2048
                            ? message.Substring(0, 2048) + "...\n\n(Log truncated)"
                            : message;

                        if (downloadUrl != null)
                        {
                            // MB_YESNO | MB_ICONERROR = 0x04 | 0x10 = 0x14
                            if (MessageBox(IntPtr.Zero, shortMessage, $"MFAAvalonia {title}", 0x14) == 6) // IDYES = 6
                            {
                                Process.Start(new ProcessStartInfo(downloadUrl)
                                {
                                    UseShellExecute = true
                                });
                            }
                        }
                        else
                        {
                            MessageBox(IntPtr.Zero, shortMessage, $"MFAAvalonia {title}", 0x10); // MB_ICONERROR
                        }
                    }
                }
                else
                {
                    var shortMessage = message.Length > 2048
                        ? message.Substring(0, 2048) + "...\n\n(Log truncated)"
                        : message;

                    MessageBox(IntPtr.Zero, shortMessage, $"MFAAvalonia {title}", 0x10); // MB_ICONERROR
                }
            }
            else
            {
                // Linux/macOS: 尝试显示原生对话框
                try
                {
                    if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                    {
                        var escapedMessage = message.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\r");
                        var escapedTitle = title.Replace("\\", "\\\\").Replace("\"", "\\\"");
                        var psi = new ProcessStartInfo
                        {
                            FileName = "osascript",
                            Arguments = $"-e \"display alert \\\"{escapedTitle}\\\" message \\\"{escapedMessage}\\\" as critical\"",
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };
                        Process.Start(psi)?.WaitForExit();
                    }
                    else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                    {
                        // 尝试 zenity
                        try
                        {
                            var psi = new ProcessStartInfo
                            {
                                FileName = "zenity",
                                Arguments = $"--error --text=\"{message.Replace("\"", "\\\"")}\" --title=\"{title}\"",
                                UseShellExecute = true
                            };
                            Process.Start(psi)?.WaitForExit();
                        }
                        catch
                        {
                            // 尝试 kdialog
                            var psi = new ProcessStartInfo
                            {
                                FileName = "kdialog",
                                Arguments = $"--error \"{message.Replace("\"", "\\\"")}\" --title \"{title}\"",
                                UseShellExecute = true
                            };
                            Process.Start(psi)?.WaitForExit();
                        }
                    }
                }
                catch
                {
                    // 忽略跨平台对话框启动失败，已在上方输出到 Console
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("=== MFAAvalonia Startup Failed (Fatal) ===");
            Console.Error.WriteLine(ex.ToString());
            Console.Error.WriteLine("Original Exception:");
            Console.Error.WriteLine(exception.ToString());
        }

        Environment.Exit(1);
    }

    // Windows MessageBox P/Invoke
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);

    private static StartupDependencyDiagnosis DiagnoseWindowsStartupDependencyIssue(Exception exception)
    {
        var expectedRuntimeDirectory = GetExpectedMaaNativeRuntimeDirectory();
        var expectedMaaLibraries = GetExpectedMaaNativeLibraries();
        var primaryMaaLibrary = GetPrimaryMaaNativeLibraryName();

        var allExceptions = EnumerateExceptions(exception).ToList();
        var joinedText = string.Join(Environment.NewLine, allExceptions.Select(ex => ex.ToString()));
        var isLikelyMaaLoadFailure = allExceptions.Any(IsLikelyMaaLoadFailure);

        var missingLibraryNames = allExceptions
            .SelectMany(ExtractReferencedLibraryNames)
            .Where(name => IsMaaRelatedLibraryName(name, expectedMaaLibraries))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (isLikelyMaaLoadFailure)
        {
            missingLibraryNames.AddRange(expectedMaaLibraries
                .Where(name => !LibraryExistsInKnownSearchPaths(name, expectedRuntimeDirectory)));
        }

        var actuallyMissingLibraries = missingLibraryNames
            .Where(name => !LibraryExistsInKnownSearchPaths(name, expectedRuntimeDirectory))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var isVisualCppRuntimeMissing =
            isLikelyMaaLoadFailure &&
            actuallyMissingLibraries.Count == 0 &&
            !string.IsNullOrWhiteSpace(primaryMaaLibrary) &&
            LibraryExistsInKnownSearchPaths(primaryMaaLibrary, expectedRuntimeDirectory) &&
            allExceptions.Any(ex => ex is DllNotFoundException or BadImageFormatException or TypeInitializationException || ex.ToString().Contains("DllNotFoundException", StringComparison.OrdinalIgnoreCase));

        if (isLikelyMaaLoadFailure)
        {
            LoggerHelper.Warning(
                $"启动依赖诊断：Maa加载异常={isLikelyMaaLoadFailure}, 预期MAA文件数量={expectedMaaLibraries.Count}, 缺失MAA文件数量={actuallyMissingLibraries.Count}, 判定缺VC运行库={isVisualCppRuntimeMissing}, 预期目录={expectedRuntimeDirectory}, 异常摘要={joinedText}");
        }

        return new StartupDependencyDiagnosis
        {
            IsMaaNativeLibraryMissing = actuallyMissingLibraries.Count > 0,
            IsVisualCppRuntimeMissing = isVisualCppRuntimeMissing,
            MissingLibrarySummary = actuallyMissingLibraries.Count > 0
                ? string.Join(", ", actuallyMissingLibraries)
                : string.Join(", ", missingLibraryNames),
            ExpectedRuntimeDirectory = expectedRuntimeDirectory,
            ExpectedMaaLibraries = expectedMaaLibraries,
        };
    }

    private static IEnumerable<Exception> EnumerateExceptions(Exception exception)
    {
        var visited = new HashSet<Exception>();
        var stack = new Stack<Exception>();
        stack.Push(exception);

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (!visited.Add(current))
            {
                continue;
            }

            yield return current;

            if (current is AggregateException aggregateException)
            {
                foreach (var inner in aggregateException.InnerExceptions)
                {
                    stack.Push(inner);
                }
            }

            if (current.InnerException != null)
            {
                stack.Push(current.InnerException);
            }
        }
    }

    private static bool IsLikelyMaaLoadFailure(Exception exception)
    {
        var text = exception.ToString();
        var isLoadException = exception is DllNotFoundException or FileNotFoundException or BadImageFormatException or TypeInitializationException ||
                              text.Contains("DllNotFoundException", StringComparison.OrdinalIgnoreCase) ||
                              text.Contains("FileNotFoundException", StringComparison.OrdinalIgnoreCase);

        return isLoadException &&
               (text.Contains("MaaFramework", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("MaaCore", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("MaaToolkit", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("MaaUtils", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("MaaAgent", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("Maa", StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> ExtractReferencedLibraryNames(Exception exception)
    {
        if (exception is FileNotFoundException fileNotFoundException && !string.IsNullOrWhiteSpace(fileNotFoundException.FileName))
        {
            yield return Path.GetFileName(fileNotFoundException.FileName);
        }

        foreach (Match match in Regex.Matches(exception.ToString(), @"(?i)\b[\w\.-]*Maa[\w\.-]*\.dll\b"))
        {
            if (!string.IsNullOrWhiteSpace(match.Value))
            {
                yield return Path.GetFileName(match.Value);
            }
        }
    }

    private static bool IsMaaRelatedLibraryName(string? libraryName, IReadOnlyCollection<string> expectedMaaLibraries)
    {
        if (string.IsNullOrWhiteSpace(libraryName))
        {
            return false;
        }

        return expectedMaaLibraries.Contains(libraryName, StringComparer.OrdinalIgnoreCase) ||
               libraryName.Contains("Maa", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LibraryExistsInKnownSearchPaths(string libraryName, string expectedRuntimeDirectory)
    {
        var searchDirectories = new[]
        {
            AppContext.BaseDirectory,
            Path.Combine(AppContext.BaseDirectory, "libs"),
            expectedRuntimeDirectory,
        };

        return searchDirectories.Any(directory =>
        {
            try
            {
                return Directory.Exists(directory) &&
                       File.Exists(Path.Combine(directory, libraryName));
            }
            catch
            {
                return false;
            }
        });
    }

    private static string GetExpectedMaaNativeRuntimeDirectory()
    {
        return Path.Combine(AppContext.BaseDirectory, "runtimes", GetCurrentRuntimeIdentifier(), "native");
    }

    private static string GetCurrentRuntimeIdentifier() =>
        $"{GetCurrentRuntimeOsName()}-{VersionChecker.GetNormalizedArchitecture()}";

    private static string GetCurrentRuntimeOsName()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return "win";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return "linux";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return "osx";
        }

        return "unknown";
    }

    private static string GetPrimaryMaaNativeLibraryName() => GetCurrentRuntimeOsName() switch
    {
        "win" => "MaaFramework.dll",
        "linux" => "libMaaFramework.so",
        "osx" => "libMaaFramework.dylib",
        _ => string.Empty,
    };

    private static IReadOnlyList<string> GetExpectedMaaNativeLibraries()
    {
        if (MaaNativeLibraryManifestByRuntimeIdentifier.TryGetValue(GetCurrentRuntimeIdentifier(), out var libraries))
        {
            return libraries;
        }

        return Array.Empty<string>();
    }
}
