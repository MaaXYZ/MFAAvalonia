using System.Collections.Generic;

namespace MFAAvalonia.Configuration;

public static class ConfigurationKeys
{
    #region 全局设置

    public const string DefaultConfig = "DefaultConfig";
    public const string ShowGui = "ShowGui";
    public const string LinkStart = "LinkStart";
    public const string DoNotShowAnnouncementAgain = "AnnouncementInfo.DoNotShowAgain";
    public const string WelcomeAnnouncementSnapshot = "AnnouncementInfo.WelcomeSnapshot";
    public const string DoNotShowChangelogAgain = "Changelog.DoNotShowAgain";
    public const string ForceScheduledStart = "ForceScheduledStart";
    public const string CustomConfig = "CustomConfig";
    public const string NoAutoStart = "NoAutoStart";
    public const string HelpImproveSoftware = "HelpImproveSoftware";

    #endregion

    #region 主页设置

    public const string EnableEdit = "EnableEdit";
    public const string TaskItems = "TaskItems";
    public const string ResourceOptionItems = "ResourceOptionItems";
    public const string GlobalOptionItems = "GlobalOptionItems";
    public const string ControllerOptionItems = "ControllerOptionItems";
    public const string InstancePresetKey = "InstancePresetKey";

    #endregion

    #region 启动设置

    public const string BeforeTask = "BeforeTask";
    public const string AfterTask = "AfterTask";
    public const string AutoMinimize = "AutoMinimize";
    public const string AutoHide = "AutoHide";
    public const string SoftwarePath = "SoftwarePath";
    public const string WaitSoftwareTime = "WaitSoftwareTime";
    public const string EmulatorConfig = "EmulatorConfig";
    public const string IncludeInGlobalStart = "IncludeInGlobalStart";

    #endregion

    #region 全局启动设置

    public const string GlobalStartEnabled = "GlobalStartEnabled";
    public const string GlobalExtraLaunchCount = "GlobalExtraLaunchCount";
    public const string GlobalExtraLaunchNameKeyFormat = "GlobalExtraLaunch_{0}_Name";
    public const string GlobalExtraLaunchPathKeyFormat = "GlobalExtraLaunch_{0}_Path";
    public const string GlobalExtraLaunchArgsKeyFormat = "GlobalExtraLaunch_{0}_Args";
    public const string GlobalExtraLaunchWaitKeyFormat = "GlobalExtraLaunch_{0}_Wait";
    public const string GlobalExtraLaunchEnabledKeyFormat = "GlobalExtraLaunch_{0}_Enabled";

    #endregion

    #region 性能设置

    public const string UseDirectML = "UseDirectML";
    public const string GPUOption = "GPUOption";
    public const string GPUs = "GPUs";
    public const string PreventSleep = "PreventSleep";

    #endregion

    #region 连接设置

    public const string RememberAdb = "RememberAdb";
    public const string UseFingerprintMatching = "UseFingerprintMatching";
    public const string AdbControlScreenCapType = "AdbControlScreenCapType";
    public const string AdbControlInputType = "AdbControlInputType";
    public const string Win32ControlScreenCapType = "Win32ControlScreenCapType";
    public const string Win32ControlMouseType = "Win32ControlMouseType";
    public const string Win32ControlKeyboardType = "Win32ControlKeyboardType";
    public const string MacOSControlScreenCapType = "MacOSControlScreenCapType";
    public const string MacOSControlInputType = "MacOSControlInputType";
    public const string GamepadControlScreenCapType = "GamepadControlScreenCapType";
    public const string GamepadType = "GamepadType";
    public const string AllowAdbRestart = "AllowAdbRestart";
    public const string AllowAdbHardRestart = "AllowAdbHardRestart";
    public const string RetryOnDisconnected = "RetryOnDisconnected";
    public const string RetryOnDisconnectedWin32 = "RetryOnDisconnectedWin32";
    public const string AutoDetectOnConnectionFailed = "AutoDetectOnConnectionFailed";
    public const string AutoConnectAfterRefresh = "AutoConnectAfterRefresh";
    public const string AgentTcpMode = "AgentTcpMode";
    public const string AdbDevice = "AdbDevice";
    public const string DesktopWindowClassName = "DesktopWindowClassName";
    public const string DesktopWindowName = "DesktopWindowName";
    public const string PlayCoverConfig = "PlayCoverConfig";
    public const string WlRootsSocketPath = "WlRootsSocketPath";
    public const string CurrentController = "CurrentController";
    public const string CurrentControllerName = "CurrentControllerName";
    public const string AndroidRunMode = "AndroidRunMode";
    public const string AndroidResolution = "AndroidResolution";

    #endregion

    #region 游戏设置

    public const string Resource = "Resource";
    public const string Recording = "recording";
    public const string SaveDraw = "save_draw";
    public const string ShowHitDraw = "show_hit_box";
    public const string SaveOnError = "save_on_error";
    public const string Prescript = "Prescript";
    public const string Postscript = "Post-script";
    public const string ContinueRunningWhenError = "ContinueRunningWhenError";
    public const string UseSeparateScreenshotTasker = "UseSeparateScreenshotTasker";

    #endregion

    #region 界面设置

    public const string LangIndex = "LangIndex";
    public const string CurrentLanguage = "CurrentLanguage";
    public const string ThemeIndex = "ThemeIndex";
    public const string ShouldMinimizeToTray = "ShouldMinimizeToTray";
    public const string EnableShowIcon = "EnableShowIcon";
    public const string EnableToastNotification = "EnableToastNotification";

    public const string OtherColorTheme = "OtherColorTheme";
    public const string BackgroundStyle = "BackgroundStyle";
    public const string BaseTheme = "BaseTheme";
    public const string BackgroundAnimations = "BackgroundAnimations";
    public const string BackgroundTransitions = "BackgroundTransitions";
    public const string ColorTheme = "ColorTheme";
    public const string BackgroundImagePath = "BackgroundImagePath";
    public const string BackgroundImageOpacity = "BackgroundImageOpacity";
    public const string FontScale = "FontScale";
    public const string FontFamily = "FontFamily";

    #endregion

    #region 外部通知

    public const string ExternalNotificationEnabled = "ExternalNotificationEnabled";
    public const string ExternalNotificationDingTalkToken = "ExternalNotificationDingTalkToken";
    public const string ExternalNotificationDingTalkSecret = "ExternalNotificationDingTalkSecret";
    public const string ExternalNotificationEmailAccount = "ExternalNotificationEmailAccount";
    public const string ExternalNotificationEmailSecret = "ExternalNotificationEmailSecret";
    public const string ExternalNotificationLarkWebhookUrl = "ExternalNotificationLarkWebhookUrl";
    public const string ExternalNotificationLarkID = "ExternalNotificationLarkID";
    public const string ExternalNotificationLarkToken = "ExternalNotificationLarkToken";
    public const string ExternalNotificationWxPusherToken = "ExternalNotificationWxPusherToken";
    public const string ExternalNotificationWxPusherUID = "ExternalNotificationWxPusherUID";
    public const string ExternalNotificationTelegramBotToken = "ExternalNotificationTelegramBotToken";
    public const string ExternalNotificationTelegramChatId = "ExternalNotificationTelegramChatId";
    public const string ExternalNotificationDiscordBotToken = "ExternalNotificationDiscordBotToken";
    public const string ExternalNotificationDiscordChannelId = "ExternalNotificationDiscordChannelId";
    public const string ExternalNotificationDiscordWebhookUrl = "ExternalNotificationDiscordWebhookUrl";
    public const string ExternalNotificationDiscordWebhookName = "ExternalNotificationDiscordWebhookName";
    public const string ExternalNotificationSmtpServer = "ExternalNotificationSmtpServer";
    public const string ExternalNotificationSmtpPort = "ExternalNotificationSmtpPort";
    public const string ExternalNotificationSmtpUser = "ExternalNotificationSmtpUser";
    public const string ExternalNotificationSmtpPassword = "ExternalNotificationSmtpPassword";
    public const string ExternalNotificationSmtpFrom = "ExternalNotificationSmtpFrom";
    public const string ExternalNotificationSmtpTo = "ExternalNotificationSmtpTo";
    public const string ExternalNotificationSmtpUseSsl = "ExternalNotificationSmtpUseSsl";
    public const string ExternalNotificationSmtpRequiresAuthentication = "ExternalNotificationSmtpRequiresAuthentication";
    public const string ExternalNotificationQmsgServer = "ExternalNotificationQmsgServer";
    public const string ExternalNotificationQmsgKey = "ExternalNotificationQmsgKey";
    public const string ExternalNotificationQmsgUser = "ExternalNotificationQmsgUser";
    public const string ExternalNotificationQmsgBot = "ExternalNotificationQmsgBot";
    public const string ExternalNotificationOneBotServer = "ExternalNotificationOneBotServer";
    public const string ExternalNotificationOneBotKey = "ExternalNotificationOneBotKey";
    public const string ExternalNotificationOneBotUser = "ExternalNotificationOneBotUser";
    public const string ExternalNotificationServerChanKey = "ExternalNotificationServerChanKey";
    public const string ExternalNotificationCustomWebhookUrl = "ExternalNotificationCustomWebhookUrl";
    public const string ExternalNotificationCustomWebhookContentType = "ExternalNotificationCustomWebhookContentType";
    public const string ExternalNotificationCustomWebhookPayloadTemplate = "ExternalNotificationCustomWebhookPayloadTemplate";
    public const string ExternalNotificationEnableCustomMessage = "ExternalNotificationEnableCustomMessage";
    public const string ExternalNotificationCustomSuccessText = "ExternalNotificationCustomSuccessText";
    public const string ExternalNotificationCustomFailureText = "ExternalNotificationCustomFailureText";

    #endregion

    #region 更新

    public const string UIUpdateChannelIndex = "UIUpdateChannelIndex";
    public const string ResourceUpdateChannelIndex = "ResourceUpdateChannelIndex";
    public const string DownloadSourceIndex = "DownloadSourceIndex";
    public const string EnableAutoUpdateResource = "EnableAutoUpdateResource";
    public const string EnableAutoUpdateMFA = "EnableAutoUpdateMFA";
    public const string EnableCheckVersion = "EnableCheckVersion";
    public const string DownloadCDK = "DownloadCDK";
    public const string GitHubToken = "GitHubToken";
    public const string ProxyAddress = "ProxyAddress";
    public const string ProxyType = "ProxyType";
    public const string CurrentTasks = "CurrentTasks";
    public const string ResourceUpdateChannelInitialized = "ResourceUpdateChannelInitialized";

    #endregion

    #region UI设置

    public const string TaskQueueColumn1Width = "UI.TaskQueue.Column1Width";
    public const string TaskQueueColumn2Width = "UI.TaskQueue.Column2Width";
    public const string TaskQueueColumn3Width = "UI.TaskQueue.Column3Width";
    public const string TaskQueueLeftPanelCollapsed = "UI.TaskQueue.LeftPanelCollapsed";
    public const string TaskQueueRightPanelCollapsed = "UI.TaskQueue.RightPanelCollapsed";
    public const string TaskQueueDashboardLayout = "UI.TaskQueue.DashboardLayout";
    public const string DashboardCardGridLayout = "UI.DashboardCardGrid.Layout";
    public const string DashboardCardGridResourceLayoutHash = "UI.DashboardCardGrid.ResourceLayoutHash";
    public const string EnableLiveView = "UI.LiveView.EnableLiveView";
    public const string LiveViewRefreshRate = "UI.LiveView.RefreshRate";
    public const string MainWindowWidth = "UI.MainWindow.Width";
    public const string MainWindowHeight = "UI.MainWindow.Height";
    public const string MainWindowPositionX = "UI.MainWindow.PositionX";
    public const string MainWindowPositionY = "UI.MainWindow.PositionY";
    public const string MainWindowMaximized = "UI.MainWindow.Maximized";
    public const string HasCompletedFirstUseTutorial = "UI.HasCompletedFirstUseTutorial";

    #endregion

    #region 实例设置

    public static readonly HashSet<string> InstanceScopedKeys = new()
    {
        TaskItems,
        CurrentTasks,
        InstancePresetKey,
        ResourceOptionItems,
        ControllerOptionItems,
        BeforeTask,
        AfterTask,
        SoftwarePath,
        WaitSoftwareTime,
        EmulatorConfig,
        RememberAdb,
        UseFingerprintMatching,
        AdbControlScreenCapType,
        AdbControlInputType,
        Win32ControlScreenCapType,
        Win32ControlMouseType,
        Win32ControlKeyboardType,
        MacOSControlScreenCapType,
        MacOSControlInputType,
        GamepadControlScreenCapType,
        GamepadType,
        AllowAdbRestart,
        AllowAdbHardRestart,
        RetryOnDisconnected,
        RetryOnDisconnectedWin32,
        AutoDetectOnConnectionFailed,
        AutoConnectAfterRefresh,
        AdbDevice,
        DesktopWindowClassName,
        DesktopWindowName,
        PlayCoverConfig,
        WlRootsSocketPath,
        CurrentController,
        CurrentControllerName,
        Resource,
        EnableLiveView,
        LiveViewRefreshRate,
        Prescript,
        Postscript,
        ContinueRunningWhenError,
        UseSeparateScreenshotTasker,
        AgentTcpMode
    };

    public static bool IsInstanceScoped(string key) => InstanceScopedKeys.Contains(key);

    #endregion

    #region 多实例管理

    /// <summary>实例ID列表（逗号分隔）</summary>
    public const string InstanceList = "Instances.List";

    /// <summary>实例显示顺序（逗号分隔的ID）</summary>
    public const string InstanceOrder = "Instances.Order";

    /// <summary>最后激活的实例ID</summary>
    public const string LastActiveInstance = "Instances.LastActive";

    /// <summary>最后激活的实例名称（当实例ID失效时用于回退匹配）</summary>
    public const string LastActiveInstanceName = "Instances.LastActiveName";

    /// <summary>实例名称（存储在各实例独立 JSON 中）</summary>
    public const string InstanceName = "InstanceName";

    /// <summary>实例名称模板（旧格式，用于从全局配置迁移）：Instance.{id}.Name</summary>
    public const string InstanceNameTemplate = "Instance.{0}.Name";

    #endregion
}
