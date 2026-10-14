using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using MFAAvalonia;
using MFAAvalonia.Configuration;
using MFAAvalonia.Extensions;
using MFAAvalonia.Extensions.MaaFW;
using MFAAvalonia.Utilities.Attributes;
using MFAAvalonia.ViewModels.Other;
using MFAAvalonia.ViewModels.Pages;
using MFAAvalonia.ViewModels.UsersControls.Settings;
using MFAAvalonia.ViewModels.Windows;
using MFAAvalonia.Views.Pages;
using MFAAvalonia.Views.UserControls.Settings;
using MFAAvalonia.Views.Windows;
using System;
using Microsoft.Extensions.DependencyInjection;
using SukiUI.Dialogs;
using SukiUI.Toasts;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace MFAAvalonia.Helper;

#pragma warning disable CS0169 // The field is never used
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor

[LazyStatic]
public static partial class Instances
{
    #region Core Resolver

    private static readonly ConcurrentDictionary<Type, Lazy<object>> ServiceCache = new();

    /// <summary>
    /// 解析服务（自动缓存 + 循环依赖检测）
    /// </summary>
    private static T Resolve<T>() where T : class
    {
        var serviceType = typeof(T);

        var lazy = ServiceCache.GetOrAdd(serviceType, _ =>
            new Lazy<object>(
                () =>
                {
                    if (Design.IsDesignMode)
                    {
                        try
                        {
                            // 设计时核心逻辑：接口自动匹配实现类，普通类直接创建
                            object designInstance;

                            if (serviceType.IsInterface)
                            {
                                // 1. 接口类型：去掉"I"前缀，查找对应的实现类
                                designInstance = CreateInstanceFromInterface<T>(serviceType);
                            }
                            else
                            {
                                // 2. 普通类（非接口）：直接创建实例
                                designInstance = Activator.CreateInstance<T>()!;
                            }

                            LoggerHelper.Info($"设计时模式：成功创建 {serviceType.Name} 实例（实际类型：{designInstance.GetType().Name}）并缓存");
                            return designInstance;
                        }
                        catch (MissingMethodException ex)
                        {
                            throw new InvalidOperationException(
                                $"设计时模式下，{serviceType.Name}（或其实现类）缺少无参构造函数！请添加默认构造函数。", ex);
                        }
                        catch (TypeLoadException ex)
                        {
                            throw new InvalidOperationException(
                                $"设计时模式下，未找到 {serviceType.Name} 对应的实现类！请检查：1. 实现类命名是否为「去掉I前缀」（如 ISukiToastManager → SukiToastManager）；2. 实现类与接口在同一命名空间；3. 实现类已编译到项目中。", ex);
                        }
                        catch (Exception ex)
                        {
                            throw new InvalidOperationException(
                                $"设计时模式下创建 {serviceType.Name} 实例失败：{ex.Message}", ex);
                        }
                    }

                    // 运行时：走原有DI容器解析逻辑（不受影响）
                    try
                    {
                        if (App.Services == null)
                            throw new NullReferenceException("App.Services 未初始化（运行时必须先配置依赖注入）");

                        var runtimeInstance = App.Services.GetRequiredService<T>();
                        return runtimeInstance;
                    }
                    catch (InvalidOperationException ex)
                    {
                        throw new InvalidOperationException(
                            $"运行时解析 {serviceType.Name} 失败。可能原因：1. 服务未注册；2. 循环依赖；3. 初始化时线程竞争。", ex);
                    }
                    catch (NullReferenceException ex)
                    {
                        throw new InvalidOperationException(
                            $"运行时解析 {serviceType.Name} 失败：App.Services 为 null，请检查依赖注入初始化逻辑。", ex);
                    }
                },
                LazyThreadSafetyMode.ExecutionAndPublication
            ));

        return (T)lazy.Value;
    }

    public static bool IsResolved<T>() where T : class
    {
        var serviceType = typeof(T);
        return ServiceCache.TryGetValue(serviceType, out var lazy) && lazy.IsValueCreated;
    }

    public static bool TryGetResolved<T>(out T? instance) where T : class
    {
        var serviceType = typeof(T);
        if (ServiceCache.TryGetValue(serviceType, out var lazy) && lazy.IsValueCreated)
        {
            instance = (T)lazy.Value;
            return true;
        }

        instance = null;
        return false;
    }

    /// <summary>
    /// 从接口类型创建实现类实例（设计时专用）
    /// 规则：去掉接口的"I"前缀，查找同一命名空间下的实现类
    /// </summary>
    private static T CreateInstanceFromInterface<T>(Type interfaceType) where T : class
    {
        // 校验接口命名（必须以"I"开头，且长度>1）
        if (!interfaceType.Name.StartsWith("I", StringComparison.Ordinal) || interfaceType.Name.Length <= 1)
        {
            throw new InvalidOperationException($"接口 {interfaceType.Name} 命名不规范，无法自动匹配实现类（需以'I'开头，如 ISukiToastManager）");
        }

        // 生成实现类名：去掉"I"前缀（如 ISukiToastManager → SukiToastManager）
        string implementationClassName = interfaceType.Name.Substring(1);

        // 查找实现类：在接口所在的程序集中，查找同名（去掉I）的非接口类
        Type? implementationType = interfaceType.Assembly.GetTypes()
            .FirstOrDefault(t =>
                t.Name == implementationClassName
                && // 类名匹配
                !t.IsInterface
                && // 不是接口
                !t.IsAbstract
                && // 不是抽象类
                interfaceType.IsAssignableFrom(t)); // 实现了当前接口

        if (implementationType == null)
        {
            // 尝试容错：忽略大小写匹配（比如 ISukiToastManager → sukiToastManager，可选）
            implementationType = interfaceType.Assembly.GetTypes()
                .FirstOrDefault(t =>
                    string.Equals(t.Name, implementationClassName, StringComparison.OrdinalIgnoreCase) && !t.IsInterface && !t.IsAbstract && interfaceType.IsAssignableFrom(t));
        }

        if (implementationType == null)
        {
            throw new TypeLoadException($"未找到 {interfaceType.Name} 的实现类（期望类名：{implementationClassName}）");
        }

        // 创建实现类实例（要求实现类有无参构造函数）
        var instance = Activator.CreateInstance(implementationType);
        if (instance == null)
        {
            throw new InvalidOperationException($"实现类 {implementationType.Name} 无法创建实例（可能是抽象类或无无参构造函数）");
        }

        return (T)instance;
    }

    #endregion

    /// <summary>
    /// 关闭当前应用程序
    /// </summary>
    private static class ShutdownState
    {
        internal static int Requested;
        internal static int WatchdogStarted;
    }

    public static void EnsureShutdownWatchdogStarted()
    {
        if (Interlocked.Exchange(ref ShutdownState.WatchdogStarted, 1) != 0) return;

        new Thread(() =>
        {
            Thread.Sleep(5000);
            Environment.Exit(0);
        })
        {
            Name = "MFAAvalonia-ShutdownWatchdog",
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal
        }.Start();
    }

    public static void ShutdownApplication()
    {
        ShutdownApplication(false);
    }

    public static void ShutdownApplication(bool forceStop)
    {
        var isFirstRequest = Interlocked.Exchange(ref ShutdownState.Requested, 1) == 0;
        if (isFirstRequest)
            PersistRuntimeState();

        if (forceStop)
        {
            AppRuntime.ReleaseMutex();
            // 强制退出时，只做最基本的清理，避免卡住
            TryBeforeClosed(true, false);
            Environment.Exit(0);
            return;
        }

        // 关闭可能先被运行中任务的确认框取消；确认后必须允许再次投递关闭请求。
        // 看门狗由 RootView.OnClosing 在窗口真正开始关闭时启动。
        DispatcherHelper.PostOnMainThread(() => ApplicationLifetime.Shutdown());
    }

    /// <summary>
    /// 重启当前应用程序
    /// </summary>
    public static void RestartApplication(bool noAutoStart = false, bool forgeStop = false)
    {
        PersistRuntimeState();
        AppRuntime.ReleaseMutex();
        if (noAutoStart)
            GlobalConfiguration.SetValue(ConfigurationKeys.NoAutoStart, bool.TrueString);
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = GetExecutablePath(),
                UseShellExecute = true
            }
        };

        try
        {
            process.Start();
            if (forgeStop)
                Environment.Exit(0);
            else
                ShutdownApplication();
        }
        catch (Exception ex)
        {
            LoggerHelper.Error($"重启失败: {ex.Message}");
        }
    }

    public static void PersistRuntimeState()
    {
        if (OperatingSystem.IsAndroid())
            return;

        try
        {
            MaaProcessorManager.Instance.PersistCurrentSelection();

            foreach (var processor in MaaProcessor.Processors.ToList())
            {
                processor.ViewModel?.PersistConfigurationState();
            }
        }
        catch (Exception ex)
        {
            LoggerHelper.Error($"退出前保存运行时状态失败：原因={ex.Message}", ex);
        }
    }

    /// <summary>
    /// 关闭操作系统（需要管理员权限）
    /// </summary>
    public static void ShutdownSystem()
    {
        PersistRuntimeState();
        TryBeforeClosed();
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Process.Start("shutdown", "/s /t 0");
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                Process.Start("shutdown", "-h now");
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                Process.Start("sudo", "shutdown -h now");
            }
        }
        catch (Exception ex)
        {
            LoggerHelper.Error($"关机失败: {ex.Message}");
        }
        ShutdownApplication();
    }
    /// <summary>
    /// 跨平台重启操作系统（需要管理员/root权限）
    /// </summary>
    public static void RestartSystem()
    {
        PersistRuntimeState();
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Windows重启命令[8,3](@ref)
                using var process = new Process();
                process.StartInfo.FileName = "shutdown.exe";
                process.StartInfo.Arguments = "/r /t 0 /f"; // /f 强制关闭所有程序
                process.StartInfo.UseShellExecute = true;
                process.StartInfo.Verb = "runas"; // 请求管理员权限
                process.Start();
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                // Linux重启命令[7,3](@ref)
                using var process = new Process();
                process.StartInfo.FileName = "/bin/bash";
                process.StartInfo.Arguments = "-c \"sudo shutdown -r now\"";
                process.StartInfo.RedirectStandardInput = true;
                process.Start();
                process.StandardInput.WriteLine("password"); // 需替换实际密码或配置免密sudo
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                // macOS重启命令[3,7](@ref)
                using var process = new Process();
                process.StartInfo.FileName = "/usr/bin/sudo";
                process.StartInfo.Arguments = "shutdown -r now";
                process.StartInfo.UseShellExecute = true;
                process.Start();
            }
        }
        catch (Exception ex)
        {
            LoggerHelper.Error($"重启失败: {ex.Message}");
            // 备用方案：尝试通用POSIX命令
            TryFallbackReboot();
        }
    }

    /// <summary>
    /// 备用重启方案（兼容非标准环境）
    /// </summary>
    private static void TryFallbackReboot()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = MFAExtensions.GetFallbackCommand(),
                UseShellExecute = RuntimeInformation.IsOSPlatform(OSPlatform.Windows),
                CreateNoWindow = true
            };

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                psi.Arguments = "/c shutdown /r /t 0";
            }
            else
            {
                psi.Arguments = "-c \"sudo reboot\"";
            }

            Process.Start(psi);
        }
        catch (Exception ex)
        {
            LoggerHelper.Error($"备用重启方案失败: {ex.Message}");
        }
    }


    public static string GetExecutablePath()
    {
        // 兼容.NET 5+环境
        return Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? AppPaths.InstallRoot;
    }

    public static void TryBeforeClosed() => TryBeforeClosed(false, true);

    public static void TryBeforeClosed(bool noLog, bool stopTask)
    {
        if (OperatingSystem.IsAndroid())
            return;

        if (IsResolved<RootView>())
        {
            RootView.BeforeClosed(noLog, stopTask);
        }
    }

    private static IClassicDesktopStyleApplicationLifetime _applicationLifetime;
    private static ISukiToastManager _toastManager;
    private static ISukiDialogManager _dialogManager;

    private static RootView _rootView;
    private static RootViewModel _rootViewModel;

    public static TopLevel? TopLevel =>
        Application.Current?.ApplicationLifetime switch
        {
            IClassicDesktopStyleApplicationLifetime desktop => desktop.MainWindow,
            ISingleViewApplicationLifetime single when single.MainView is Control control => TopLevel.GetTopLevel(control),
            _ => null
        };

    public static IStorageProvider? StorageProvider => TopLevel?.StorageProvider;

    public static IClipboard? Clipboard => TopLevel?.Clipboard;

    private static InstanceContainerView _instanceContainerView;
    private static InstanceTabBarViewModel _instanceTabBarViewModel;
    private static SettingsView _settingsView;
    private static SettingsViewModel _settingsViewModel;
    private static ResourcesView _resourcesView;
    private static ResourcesViewModel _resourcesViewModel;
    private static MonitorView _monitorView;
    private static MonitorViewModel _monitorViewModel;
    private static ScreenshotView _screenshotView;
    private static ScreenshotViewModel _screenshotViewModel;
    
    private static ConnectSettingsUserControl _connectSettingsUserControl;
    private static ConnectSettingsUserControlModel _connectSettingsUserControlModel;
    private static GuiSettingsUserControl _guiSettingsUser;
    private static GuiSettingsUserControlModel _guiSettingsUserControlModel;
    private static ConfigurationMgrUserControl _configurationMgrUserControl;
    private static ExternalNotificationSettingsUserControl _externalNotificationSettingsUserControl;
    private static ExternalNotificationSettingsUserControlModel _externalNotificationSettingsUserControlModel;
    private static TimerSettingsUserControl _timerSettingsUserControl;
    private static TimerSettingsUserControlModel _timerSettingsUserControlModel;
    private static PerformanceUserControl _performanceUserControl;
    private static PerformanceUserControlModel _performanceUserControlModel;
    private static GameSettingsUserControl _gameSettingsUserControl;
    private static GameSettingsUserControlModel _gameSettingsUserControlModel;
    private static VersionUpdateSettingsUserControl _versionUpdateSettingsUserControl;
    private static VersionUpdateSettingsUserControlModel _versionUpdateSettingsUserControlModel;
    private static StartSettingsUserControl _startSettingsUserControl;
    private static StartSettingsUserControlModel _startSettingsUserControlModel;
    private static AboutUserControl _aboutUserControl;
    private static HotKeySettingsUserControl _hotKeySettingsUserControl;

    public static void ReloadConfigurationForSwitch(bool refreshTask = true)
    {
        var task = ReloadConfigurationForSwitchAsync(refreshTask);
        task.ContinueWith(t =>
        {
            LoggerHelper.Error($"刷新配置失败：原因={t.Exception?.GetBaseException().Message}", t.Exception);
        }, TaskContinuationOptions.OnlyOnFaulted);
    }

    public static async Task ReloadConfigurationForSwitchAsync(bool refreshTask = true)
    {
        static Task UpdateProgressAsync(double value) =>
            DispatcherHelper.RunOnMainThreadAsync(() =>
            {
                if (TryGetResolved<RootViewModel>(out var rootViewModel))
                {
                    rootViewModel.SetConfigSwitchProgress(value);
                }
            });

        await UpdateProgressAsync(30);

        // 仅在配置档案切换时刷新 SettingsViewModel 和全局设置
        if (refreshTask)
        {
            await DispatcherHelper.RunOnMainThreadAsync(() =>
            {
                if (IsResolved<SettingsViewModel>())
                {
                    SettingsViewModel.RefreshCurrentConfiguration();
                }
            });
            await UpdateProgressAsync(35);

            await DispatcherHelper.RunOnMainThreadAsync(() =>
            {
                if (IsResolved<GuiSettingsUserControlModel>())
                {
                    var gui = GuiSettingsUserControlModel;
                    var theme = SukiUI.SukiTheme.GetInstance();

                    gui.BackgroundAnimations = ConfigurationManager.Current.GetValue(ConfigurationKeys.BackgroundAnimations, false);
                    gui.BackgroundTransitions = ConfigurationManager.Current.GetValue(ConfigurationKeys.BackgroundTransitions, false);
                    gui.BackgroundStyle = ConfigurationManager.Current.GetValue(ConfigurationKeys.BackgroundStyle, SukiUI.Enums.SukiBackgroundStyle.GradientSoft, SukiUI.Enums.SukiBackgroundStyle.GradientSoft,
                        new Converters.UniversalEnumConverter<SukiUI.Enums.SukiBackgroundStyle>());
                    gui.ShouldMinimizeToTray = ConfigurationManager.Current.GetValue(ConfigurationKeys.ShouldMinimizeToTray, false);
                    gui.EnableToastNotification = ConfigurationManager.Current.GetValue(ConfigurationKeys.EnableToastNotification, true);
                    gui.BackgroundImagePath = ConfigurationManager.Current.GetValue(ConfigurationKeys.BackgroundImagePath, string.Empty);
                    gui.BackgroundImageOpacity = ConfigurationManager.Current.GetValue(ConfigurationKeys.BackgroundImageOpacity, 0.2);
                    gui.FontScale = ConfigurationManager.Current.GetValue(ConfigurationKeys.FontScale, FontService.DefaultScale);

                    gui.CurrentColorTheme = GuiSettingsUserControlModel.FindColorThemeFromConfig();
                    gui.BaseTheme = ConfigurationManager.Current.GetValue(ConfigurationKeys.BaseTheme, Avalonia.Styling.ThemeVariant.Light, new System.Collections.Generic.Dictionary<object, Avalonia.Styling.ThemeVariant>
                    {
                        ["Dark"] = Avalonia.Styling.ThemeVariant.Dark,
                        ["Light"] = Avalonia.Styling.ThemeVariant.Light
                    });

                    // 实际应用主题到UI
                    theme.ChangeBaseTheme(gui.BaseTheme);
                    theme.ChangeColorTheme(gui.CurrentColorTheme);

                    var language = LanguageHelper.CurrentLanguage;
                    gui.CurrentLanguage = language;
                    LanguageHelper.ChangeLanguage(language);
                }
            });
            await UpdateProgressAsync(45);
        }

        // 实例级配置：ConnectSettings、StartSettings、GameSettings 在实例切换和配置切换时都需要刷新
        await DispatcherHelper.RunOnMainThreadAsync(() =>
        {
            if (IsResolved<ConnectSettingsUserControlModel>())
            {
                var connect = ConnectSettingsUserControlModel;
                connect.IsSyncing = true;
                try
                {
                    connect.CurrentControllerType = ConfigurationManager.CurrentInstance.GetValue(ConfigurationKeys.CurrentController,
                        MaaControllerTypes.Adb, MaaControllerTypes.None,
                        new Converters.UniversalEnumConverter<MaaControllerTypes>());
                    connect.RememberAdb = ConfigurationManager.CurrentInstance.GetValue(ConfigurationKeys.RememberAdb, true);
                    connect.UseFingerprintMatching = ConfigurationManager.CurrentInstance.GetValue(ConfigurationKeys.UseFingerprintMatching, true);
                    connect.AdbControlScreenCapType = ConfigurationManager.CurrentInstance.GetValue(ConfigurationKeys.AdbControlScreenCapType, MaaFramework.Binding.AdbScreencapMethods.None,
                        new System.Collections.Generic.List<MaaFramework.Binding.AdbScreencapMethods>
                        {
                            MaaFramework.Binding.AdbScreencapMethods.All,
                            MaaFramework.Binding.AdbScreencapMethods.Default
                        }, new Converters.UniversalEnumConverter<MaaFramework.Binding.AdbScreencapMethods>());
                    connect.AdbControlInputType = ConfigurationManager.CurrentInstance.GetValue(ConfigurationKeys.AdbControlInputType, MaaFramework.Binding.AdbInputMethods.None, new System.Collections.Generic.List<MaaFramework.Binding.AdbInputMethods>
                    {
                        MaaFramework.Binding.AdbInputMethods.All,
                        MaaFramework.Binding.AdbInputMethods.Default
                    }, new Converters.UniversalEnumConverter<MaaFramework.Binding.AdbInputMethods>());
                    connect.Win32ControlScreenCapType = ConfigurationManager.CurrentInstance.GetValue(ConfigurationKeys.Win32ControlScreenCapType, MaaFramework.Binding.Win32ScreencapMethods.FramePool, MaaFramework.Binding.Win32ScreencapMethods.None,
                        new Converters.UniversalEnumConverter<MaaFramework.Binding.Win32ScreencapMethods>());
                    connect.Win32ControlMouseType = ConfigurationManager.CurrentInstance.GetValue(ConfigurationKeys.Win32ControlMouseType, MaaFramework.Binding.Win32InputMethod.SendMessage, MaaFramework.Binding.Win32InputMethod.None,
                        new Converters.UniversalEnumConverter<MaaFramework.Binding.Win32InputMethod>());
                    connect.Win32ControlKeyboardType = ConfigurationManager.CurrentInstance.GetValue(ConfigurationKeys.Win32ControlKeyboardType, MaaFramework.Binding.Win32InputMethod.SendMessage, MaaFramework.Binding.Win32InputMethod.None,
                        new Converters.UniversalEnumConverter<MaaFramework.Binding.Win32InputMethod>());
                    connect.MacOSControlScreenCapType = ConfigurationManager.CurrentInstance.GetValue(ConfigurationKeys.MacOSControlScreenCapType, MaaFramework.Binding.MacOSScreencapMethod.ScreenCaptureKit,
                        default(MaaFramework.Binding.MacOSScreencapMethod), new Converters.UniversalEnumConverter<MaaFramework.Binding.MacOSScreencapMethod>());
                    connect.MacOSControlInputType = ConfigurationManager.CurrentInstance.GetValue(ConfigurationKeys.MacOSControlInputType, MaaFramework.Binding.MacOSInputMethod.GlobalEvent,
                        default(MaaFramework.Binding.MacOSInputMethod), new Converters.UniversalEnumConverter<MaaFramework.Binding.MacOSInputMethod>());
                    connect.GamepadControlScreenCapType = ConfigurationManager.CurrentInstance.GetValue(ConfigurationKeys.GamepadControlScreenCapType, MaaFramework.Binding.Win32ScreencapMethods.FramePool,
                        MaaFramework.Binding.Win32ScreencapMethods.None, new Converters.UniversalEnumConverter<MaaFramework.Binding.Win32ScreencapMethods>());
                    connect.GamepadType = ConfigurationManager.CurrentInstance.GetValue(ConfigurationKeys.GamepadType, MaaFramework.Binding.GamepadType.Xbox360,
                        default(MaaFramework.Binding.GamepadType), new Converters.UniversalEnumConverter<MaaFramework.Binding.GamepadType>());
                    connect.RetryOnDisconnected = ConfigurationManager.CurrentInstance.GetValue(ConfigurationKeys.RetryOnDisconnected, false);
                    connect.RetryOnDisconnectedWin32 = ConfigurationManager.CurrentInstance.GetValue(ConfigurationKeys.RetryOnDisconnectedWin32, false);
                    connect.AllowAdbRestart = ConfigurationManager.CurrentInstance.GetValue(ConfigurationKeys.AllowAdbRestart, true);
                    connect.AllowAdbHardRestart = ConfigurationManager.CurrentInstance.GetValue(ConfigurationKeys.AllowAdbHardRestart, true);
                    connect.AutoDetectOnConnectionFailed = ConfigurationManager.CurrentInstance.GetValue(ConfigurationKeys.AutoDetectOnConnectionFailed, true);
                    connect.AutoConnectAfterRefresh = ConfigurationManager.CurrentInstance.GetValue(ConfigurationKeys.AutoConnectAfterRefresh, true);
                }
                finally
                {
                    connect.IsSyncing = false;
                }
            }

            if (IsResolved<StartSettingsUserControlModel>())
            {
                var start = StartSettingsUserControlModel;
                start.AutoMinimize = ConfigurationManager.Current.GetValue(ConfigurationKeys.AutoMinimize, false);
                start.AutoHide = ConfigurationManager.Current.GetValue(ConfigurationKeys.AutoHide, false);
                start.SoftwarePath = ConfigurationManager.CurrentInstance.GetValue(ConfigurationKeys.SoftwarePath, string.Empty);
                start.EmulatorConfig = ConfigurationManager.CurrentInstance.GetValue(ConfigurationKeys.EmulatorConfig, string.Empty);
                start.WaitSoftwareTime = ConfigurationManager.CurrentInstance.GetValue(ConfigurationKeys.WaitSoftwareTime, 60.0);
                start.BeforeTask = ConfigurationManager.CurrentInstance.GetValue(ConfigurationKeys.BeforeTask, "None");
                start.AfterTask = ConfigurationManager.CurrentInstance.GetValue(ConfigurationKeys.AfterTask, "None");
                start.GlobalStartEnabled = GlobalConfiguration.GetValue(
                    ConfigurationKeys.GlobalStartEnabled,
                    bool.FalseString) == bool.TrueString;
                start.RefreshGlobalStartInstancesCommand.Execute(null);
            }

            if (IsResolved<GameSettingsUserControlModel>())
            {
                var game = GameSettingsUserControlModel;
                game.Prescript = ConfigurationManager.CurrentInstance.GetValue(ConfigurationKeys.Prescript, string.Empty);
                game.PostScript = ConfigurationManager.CurrentInstance.GetValue(ConfigurationKeys.Postscript, string.Empty);
                game.ContinueRunningWhenError = ConfigurationManager.CurrentInstance.GetValue(ConfigurationKeys.ContinueRunningWhenError, true);
            }
        });
        await UpdateProgressAsync(60);

        // 全局配置：仅在配置档案切换时刷新（ExternalNotification、Performance、VersionUpdate 是全局的，实例切换时不变）
        if (refreshTask)
        {
            await DispatcherHelper.RunOnMainThreadAsync(() =>
            {
                if (IsResolved<PerformanceUserControlModel>())
                {
                    var performance = PerformanceUserControlModel;
                    performance.UseDirectML = ConfigurationManager.Current.GetValue(ConfigurationKeys.UseDirectML, false);
                    performance.GpuIndex = ConfigurationManager.Current.GetValue(ConfigurationKeys.GPUOption, 0);
                }

                if (IsResolved<ExternalNotificationSettingsUserControlModel>())
                {
                    var external = ExternalNotificationSettingsUserControlModel;
                    ExternalNotificationSettingsUserControlModel.EnabledExternalNotificationProviderList.Clear();
                    var enabled = ConfigurationManager.Current.GetValue(ConfigurationKeys.ExternalNotificationEnabled, string.Empty)
                        .Split(',', StringSplitOptions.RemoveEmptyEntries);
                    ExternalNotificationSettingsUserControlModel.EnabledExternalNotificationProviderList.AddRange(enabled);
                    external.UpdateExternalNotificationProvider();
                    external.EnabledExternalNotificationProviderCount = ExternalNotificationSettingsUserControlModel.EnabledExternalNotificationProviderList.Count;

                    external.DingTalkToken = ConfigurationManager.Current.GetDecrypt(ConfigurationKeys.ExternalNotificationDingTalkToken, string.Empty);
                    external.DingTalkSecret = ConfigurationManager.Current.GetDecrypt(ConfigurationKeys.ExternalNotificationDingTalkSecret, string.Empty);
                    external.EmailAccount = ConfigurationManager.Current.GetDecrypt(ConfigurationKeys.ExternalNotificationEmailAccount, string.Empty);
                    external.EmailSecret = ConfigurationManager.Current.GetDecrypt(ConfigurationKeys.ExternalNotificationEmailSecret, string.Empty);
                    external.LarkWebhookUrl = ConfigurationManager.Current.GetDecrypt(ConfigurationKeys.ExternalNotificationLarkWebhookUrl, string.Empty);
                    external.LarkId = ConfigurationManager.Current.GetDecrypt(ConfigurationKeys.ExternalNotificationLarkID, string.Empty);
                    external.LarkToken = ConfigurationManager.Current.GetDecrypt(ConfigurationKeys.ExternalNotificationLarkToken, string.Empty);
                    external.WxPusherToken = ConfigurationManager.Current.GetDecrypt(ConfigurationKeys.ExternalNotificationWxPusherToken, string.Empty);
                    external.WxPusherUid = ConfigurationManager.Current.GetDecrypt(ConfigurationKeys.ExternalNotificationWxPusherUID, string.Empty);
                    external.TelegramBotToken = ConfigurationManager.Current.GetDecrypt(ConfigurationKeys.ExternalNotificationTelegramBotToken, string.Empty);
                    external.TelegramChatId = ConfigurationManager.Current.GetDecrypt(ConfigurationKeys.ExternalNotificationTelegramChatId, string.Empty);
                    external.DiscordBotToken = ConfigurationManager.Current.GetDecrypt(ConfigurationKeys.ExternalNotificationDiscordBotToken, string.Empty);
                    external.DiscordChannelId = ConfigurationManager.Current.GetDecrypt(ConfigurationKeys.ExternalNotificationDiscordChannelId, string.Empty);
                    external.DiscordWebhookUrl = ConfigurationManager.Current.GetDecrypt(ConfigurationKeys.ExternalNotificationDiscordWebhookUrl, string.Empty);
                    external.DiscordWebhookName = ConfigurationManager.Current.GetDecrypt(ConfigurationKeys.ExternalNotificationDiscordWebhookName, string.Empty);
                    external.SmtpServer = ConfigurationManager.Current.GetDecrypt(ConfigurationKeys.ExternalNotificationSmtpServer, string.Empty);
                    external.SmtpPort = ConfigurationManager.Current.GetDecrypt(ConfigurationKeys.ExternalNotificationSmtpPort, string.Empty);
                    external.SmtpUser = ConfigurationManager.Current.GetDecrypt(ConfigurationKeys.ExternalNotificationSmtpUser, string.Empty);
                    external.SmtpPassword = ConfigurationManager.Current.GetDecrypt(ConfigurationKeys.ExternalNotificationSmtpPassword, string.Empty);
                    external.SmtpFrom = ConfigurationManager.Current.GetDecrypt(ConfigurationKeys.ExternalNotificationSmtpFrom, string.Empty);
                    external.SmtpTo = ConfigurationManager.Current.GetDecrypt(ConfigurationKeys.ExternalNotificationSmtpTo, string.Empty);
                    external.SmtpUseSsl = ConfigurationManager.Current.GetValue(ConfigurationKeys.ExternalNotificationSmtpUseSsl, false);
                    external.SmtpRequireAuthentication = ConfigurationManager.Current.GetValue(ConfigurationKeys.ExternalNotificationSmtpRequiresAuthentication, false);
                    external.QmsgServer = ConfigurationManager.Current.GetDecrypt(ConfigurationKeys.ExternalNotificationQmsgServer, string.Empty);
                    external.QmsgKey = ConfigurationManager.Current.GetDecrypt(ConfigurationKeys.ExternalNotificationQmsgKey, string.Empty);
                    external.QmsgUser = ConfigurationManager.Current.GetDecrypt(ConfigurationKeys.ExternalNotificationQmsgUser, string.Empty);
                    external.QmsgBot = ConfigurationManager.Current.GetDecrypt(ConfigurationKeys.ExternalNotificationQmsgBot, string.Empty);
                    external.OnebotServer = ConfigurationManager.Current.GetDecrypt(ConfigurationKeys.ExternalNotificationOneBotServer, string.Empty);
                    external.OnebotKey = ConfigurationManager.Current.GetDecrypt(ConfigurationKeys.ExternalNotificationOneBotKey, string.Empty);
                    external.OnebotUser = ConfigurationManager.Current.GetDecrypt(ConfigurationKeys.ExternalNotificationOneBotUser, string.Empty);
                    external.ServerChanSendKey = ConfigurationManager.Current.GetDecrypt(ConfigurationKeys.ExternalNotificationServerChanKey, string.Empty);
                    external.CustomWebhookUrl = ConfigurationManager.Current.GetDecrypt(ConfigurationKeys.ExternalNotificationCustomWebhookUrl, string.Empty);
                    external.CustomWebhookContentType = ConfigurationManager.Current.GetDecrypt(ConfigurationKeys.ExternalNotificationCustomWebhookContentType, "application/json");
                    external.CustomWebhookPayloadTemplate = ConfigurationManager.Current.GetDecrypt(ConfigurationKeys.ExternalNotificationCustomWebhookPayloadTemplate, "{\"message\": \"{message}\"}");
                    external.EnabledCustom = ConfigurationManager.Current.GetValue(ConfigurationKeys.ExternalNotificationEnableCustomMessage, false);
                    external.CustomSuccessText = ConfigurationManager.Current.GetValue(ConfigurationKeys.ExternalNotificationCustomSuccessText, string.Empty);
                    external.CustomFailureText = ConfigurationManager.Current.GetValue(ConfigurationKeys.ExternalNotificationCustomFailureText, string.Empty);
                }

                if (IsResolved<VersionUpdateSettingsUserControlModel>())
                {
                    var version = VersionUpdateSettingsUserControlModel;
                    version.DownloadSourceIndex = ConfigurationManager.Current.GetValue(ConfigurationKeys.DownloadSourceIndex, 1);
                    version.UIUpdateChannelIndex = ConfigurationManager.Current.GetValue(ConfigurationKeys.UIUpdateChannelIndex, 2);
                    version.ResourceUpdateChannelIndex = ConfigurationManager.Current.GetValue(ConfigurationKeys.ResourceUpdateChannelIndex, 2);
                    version.GitHubToken = SimpleEncryptionHelper.Decrypt(ConfigurationManager.Current.GetValue(ConfigurationKeys.GitHubToken, string.Empty));
                    version.CdkPassword = SimpleEncryptionHelper.Decrypt(ConfigurationManager.Current.GetValue(ConfigurationKeys.DownloadCDK, string.Empty));
                    version.EnableCheckVersion = ConfigurationManager.Current.GetValue(ConfigurationKeys.EnableCheckVersion, true);
                    version.EnableAutoUpdateResource = ConfigurationManager.Current.GetValue(ConfigurationKeys.EnableAutoUpdateResource, false);
                    version.EnableAutoUpdateMFA = ConfigurationManager.Current.GetValue(ConfigurationKeys.EnableAutoUpdateMFA, false);
                    version.ProxyAddress = ConfigurationManager.Current.GetValue(ConfigurationKeys.ProxyAddress, string.Empty);
                    version.ProxyType = ConfigurationManager.Current.GetValue(ConfigurationKeys.ProxyType, VersionUpdateSettingsUserControlModel.UpdateProxyType.Http, VersionUpdateSettingsUserControlModel.UpdateProxyType.Http,
                        new Converters.UniversalEnumConverter<VersionUpdateSettingsUserControlModel.UpdateProxyType>());
                    if (ConfigurationManager.Current.TryGetValue<bool>("Debug", out var debugValue))
                    {
                        version.ShowLocalPackageUpdate = debugValue;
                    }
                    else
                    {
                        var rawDebug = ConfigurationManager.Current.GetValue("Debug", string.Empty);
                        version.ShowLocalPackageUpdate = bool.TryParse(rawDebug, out var parsedDebug) && parsedDebug;
                    }
                }
            });
        }

        // TimerSettings 需要在两种场景下都刷新（实例列表可能变化）
        await DispatcherHelper.RunOnMainThreadAsync(() =>
        {
            if (IsResolved<TimerSettingsUserControlModel>())
            {
                TimerSettingsUserControlModel.RefreshInstances();
            }
        });
        await UpdateProgressAsync(72);

        if (refreshTask)
        {
            await DispatcherHelper.RunOnMainThreadAsync(() =>
            {
                var task = InstanceTabBarViewModel.ActiveTab?.TaskQueueViewModel;
                if (task == null) return;

                task.CurrentConfiguration = ConfigurationManager.GetCurrentConfiguration();
                task.TaskItemViewModels = new();
                task.CurrentController = ConfigurationManager.CurrentInstance.GetValue(ConfigurationKeys.CurrentController, MaaControllerTypes.Adb, MaaControllerTypes.None,
                    new Converters.UniversalEnumConverter<MaaControllerTypes>());
                task.EnableLiveView = ConfigurationManager.CurrentInstance.GetValue(ConfigurationKeys.EnableLiveView, true);
                task.LiveViewRefreshRate = ConfigurationManager.CurrentInstance.GetValue(ConfigurationKeys.LiveViewRefreshRate, 30.0);
            });
            await UpdateProgressAsync(80);

            await UpdateProgressAsync(85);

            await DispatcherHelper.RunOnMainThreadAsync(() =>
            {
                var task = InstanceTabBarViewModel.ActiveTab?.TaskQueueViewModel;
                if (task == null) return;

                task.Processor.InitializeData();

                task.InitializeControllerOptions();
                // 先从配置中读取目标资源，然后传入 UpdateResourcesForController
                // 这样可以确保在配置切换时正确恢复资源，而不是总是选择第一个
                var targetResource = ConfigurationManager.CurrentInstance.GetValue(ConfigurationKeys.Resource, string.Empty);
                task.UpdateResourcesForController(targetResource);
                task.TryReadAdbDeviceFromConfig(); // 使用默认参数 (true, false, true) 以启用指纹匹配和自动检测，避免直接读取配置失败导致列表为空

                if (IsResolved<RootViewModel>())
                {
                    RootViewModel.IsDebugMode = ConfigurationManager.Maa.GetValue(ConfigurationKeys.Recording, false)
                        || ConfigurationManager.Maa.GetValue(ConfigurationKeys.SaveDraw, false)
                        || ConfigurationManager.Maa.GetValue(ConfigurationKeys.ShowHitDraw, false);
                }

                var selected = task.TaskItemViewModels.FirstOrDefault(i => i.IsResourceOptionItem)
                    ?? task.TaskItemViewModels.FirstOrDefault(i => i.InterfaceItem?.Advanced is { Count: > 0 }
                        || i.InterfaceItem?.Option is { Count: > 0 }
                        || !string.IsNullOrWhiteSpace(i.InterfaceItem?.Description)
                        || i.InterfaceItem?.Document != null
                        || i.InterfaceItem?.Repeatable == true);
                if (selected != null)
                {
                    selected.EnableSetting = true;
                }

                task.Processor.SetTasker();
            });
        }
        await UpdateProgressAsync(95);
    }
}
