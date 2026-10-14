using Avalonia;
using Avalonia.Platform;
using MFAAvalonia.Configuration;
using MFAAvalonia.Views.Windows;
using SoundFlow.Abstracts.Devices;
using SoundFlow.Backends.MiniAudio;
using SoundFlow.Components;
using SoundFlow.Enums;
using SoundFlow.Providers;
using SoundFlow.Structs;
using SukiUI.Extensions;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MFAAvalonia.Helper;

public class ToastNotification
{
    // Android notifications must not depend on the Avalonia window being foreground.
    public static Action<string, string>? PlatformShowNotification { get; set; }
    // 单例实例
    public static ToastNotification Instance { get; } = new();

    // 存储当前显示的Toast（按显示顺序排列，第一个在最下方）
    private readonly List<NotificationView> _toastQueue = [];

    // 配置参数（可根据需求调整）
    public const int MarginBottom = 2; // 最底部Toast距离屏幕底部的间距
    public const int ToastSpacing = 16; // 两个Toast之间的间距
    public const int MarginRight = 2; // 最底部Toast距离屏幕底部的间距

    // 音频引擎单例（避免重复初始化 DllImportResolver）
    private static MiniAudioEngine? _sharedEngine;
    private static readonly Lock _engineLock = new();
    private static bool _engineInitFailed;

    private ToastNotification()
    {
        if (OperatingSystem.IsAndroid())
            return;

        try
        {
            // 订阅原生事件：任何屏幕变化（任务栏隐藏/显示、分辨率/缩放变化）都会触发
            Instances.RootView.Screens.Changed += (s, e) =>
            {DispatcherHelper.PostOnMainThread(() =>
                {
                    UpdateAllToastPositions();
                });
            };
        }
        catch (Exception ex)
        {
            LoggerHelper.Warning($"{ex.Message}");
        }
    }
    
    public static void Show(object? title, object? content = null, int duration = 4000, bool sound = true)
    {
        // 检查是否启用了 Toast 通知
        if (!ConfigurationManager.Current.GetValue(ConfigurationKeys.EnableToastNotification, true))
        {
            return;
        }

        if (OperatingSystem.IsAndroid())
        {
            var notificationTitle = title?.ToString() ?? string.Empty;
            var notificationContent = content?.ToString() ?? string.Empty;
            PlatformShowNotification?.Invoke(notificationTitle, notificationContent);
            ToastHelper.Info(
                notificationTitle,
                content,
                Math.Max(1, (int)Math.Ceiling(duration / 1000d)));
            return;
        }
        
        DispatcherHelper.PostOnMainThread(() =>
        {
            Instance.AddToast(new NotificationView(duration)
            {
                TitleText = title,
                MessageText = content
            });
        });
        PlayNotificationSound(sound);
    }

    /// <summary>
    /// 添加新Toast到队列并显示
    /// </summary>
    public void AddToast(NotificationView toast)
    {
        // 注册Toast关闭事件（关闭时从队列移除并重新排列）
        toast.Closed += (s, e) => RemoveToast(toast);

        // 添加到队列尾部（新Toast在最下方）
        _toastQueue.Add(toast);
        UpdateAllToastPositions(toast);
        // 显示Toast
        toast.Show();
    }

    /// <summary>
    /// 从队列移除Toast并重新排列
    /// </summary>
    public void RemoveToast(NotificationView toast)
    {
        if (_toastQueue.Remove(toast))
        {
            // 重新计算所有Toast的位置（带动画）
            UpdateAllToastPositions();
        }
    }
    private readonly Lock _positionLock = new();
    /// <summary>
    /// 重新计算并更新所有Toast的位置（核心逻辑）
    /// </summary>
    public void UpdateAllToastPositions(NotificationView? newToast = null)
    {
        DispatcherHelper.PostOnMainThread(() =>
        {
            lock (_positionLock)
            {
                // 使用第一个Toast的屏幕作为参考，确保一致性
                var referenceToast = _toastQueue.Count > 0 ? _toastQueue[0] : newToast;
                if (referenceToast == null) return;

                var screen = referenceToast.GetHostScreen();
                if (screen == null) return;

                // 从屏幕工作区底部开始计算
                double currentY = referenceToast.GetLatestWorkArea(screen).Bottom - MarginBottom * screen.Scaling;

                // 倒序遍历：最新的Toast在最下方，旧的依次往上排
                for (int i = _toastQueue.Count - 1; i >= 0; i--)
                {
                    var toast = _toastQueue[i];
                    if (toast.IsClosed || toast.IsClosing) continue;

                    // 确保使用正确的屏幕坐标
                    var toastScreen = toast.GetHostScreen() ?? screen;
                    double toastScaling = toastScreen.Scaling;
                    // 使用实际高度或Bounds高度
                    double toastHeight = toast.ActualToastHeight > 0 ? toast.ActualToastHeight : toast.Bounds.Height * toastScaling;

                    if (toastHeight <= 0)
                    {
                        // 如果高度仍无效，使用默认值
                        toastHeight = 100; // 默认高度
                    }

                    // 先减去当前Toast的高度
                    currentY -= toastHeight;

                    // 计算目标位置（确保在同一屏幕上计算）
                    var targetPosition = new PixelPoint(
                        (int)(referenceToast.GetLatestWorkArea(toastScreen).Right - toast.Bounds.Width * toastScaling - MarginRight * toastScaling),
                        (int)currentY
                    );

                    // 新Toast直接定位，其他使用动画
                    if (toast == newToast)
                    {
                        toast.Position = targetPosition;
                    }
                    else
                    {
                        toast.MoveTo(targetPosition, TimeSpan.FromMilliseconds(300));
                    }

                    // 预留间距
                    currentY -= ToastSpacing;
                }
            }
        });
    }

    /// <summary>
    /// 获取或创建共享的音频引擎实例
    /// </summary>
    private static MiniAudioEngine? GetOrCreateEngine()
    {
        if (_engineInitFailed) return null;
        
        lock (_engineLock)
        {
            if (_engineInitFailed) return null;
            
            if (_sharedEngine != null)
            {
                return _sharedEngine;
            }

            try
            {
                _sharedEngine = new MiniAudioEngine();
                return _sharedEngine;
            }
            catch (TypeInitializationException ex)
            {
                _engineInitFailed = true;
                LoggerHelper.Error($"音频引擎初始化失败（DllImportResolver 冲突）：{ex.InnerException?.Message ?? ex.Message}", ex);
                return null;
            }
            catch (Exception ex)
            {
                _engineInitFailed = true;
                LoggerHelper.Error($"音频引擎初始化失败：{ex.Message}", ex);
                return null;
            }
        }
    }

    public static void PlayNotificationSound(bool enable = true)
    {
        if (!enable) return;

        TaskManager.RunTask(async () =>
        {
            var uriString = "avares://MFAAvalonia.Core/Assets/Sound/SystemNotification.wav";
            var uri = new Uri(uriString);

            // 步骤1：检查嵌入资源
            if (!AssetLoader.Exists(uri))
            {
                LoggerHelper.Error($"未找到嵌入资源：{uriString}");
                return;
            }

            // 步骤2：获取资源流（确保可读取、可定位）
            await using var stream = AssetLoader.Open(uri);
            if (stream == null || stream.Length == 0 || !stream.CanSeek)
            {
                LoggerHelper.Error($"音频流无效：{(stream == null ? "流为空" : stream.CanSeek ? "长度为0" : "不可定位")}");
                return;
            }

            // 步骤3：获取共享引擎实例（避免重复初始化 DllImportResolver）
            var engine = GetOrCreateEngine();
            if (engine == null)
            {
                LoggerHelper.Warning("音频引擎不可用，跳过播放提示音");
                return;
            }

            AudioPlaybackDevice? playbackDevice = null;
            SoundPlayer? player = null;
            StreamDataProvider? dataProvider = null;

            try
            {
                // 步骤4：解析音频格式（优先自动解析，失败则用预设）
                stream.Seek(0, SeekOrigin.Begin);
                AudioFormat audioFormat = AudioFormat.Dvd; // 默认兜底
                var parsedFormat = AudioFormat.GetFormatFromStream(stream);
                if (parsedFormat.HasValue)
                {
                    audioFormat = parsedFormat.Value;
                }
                stream.Seek(0, SeekOrigin.Begin); // 解析后重置流位置

                // 步骤5：获取默认播放设备（核心！之前遗漏：必须绑定设备才能输出声音）
                engine.UpdateAudioDevicesInfo(); // 刷新设备列表
                var defaultDevice = engine.PlaybackDevices.FirstOrDefault(d => d.IsDefault);
                if (defaultDevice.Id == IntPtr.Zero)
                {
                    LoggerHelper.Warning("未找到默认音频播放设备，跳过播放");
                    return;
                }

                // 步骤6：创建播放设备实例（原生逻辑：绑定引擎、设备、格式）
                playbackDevice = engine.InitializePlaybackDevice(
                    deviceInfo: defaultDevice,
                    format: audioFormat,
                    config: null // 使用默认设备配置
                );

                // 步骤7：创建流数据提供器（实现 ISoundDataProvider，原生要求）
                dataProvider = new StreamDataProvider(engine, stream);

                // 步骤8：创建 SoundPlayer（绑定引擎、格式、数据提供器）
                player = new SoundPlayer(engine, audioFormat, dataProvider)
                {
                    Volume = 1.0f // 音量（0.0~1.0）
                };

                // 步骤9：关键！将 SoundPlayer 加入设备组件列表（否则音频数据无法传递）
                playbackDevice.MasterMixer.AddComponent(player);

                // 步骤10：标记播放完成状态（双重保障：回调+循环等待）
                bool isPlaybackCompleted = false;
                player.PlaybackEnded += (s, e) =>
                {
                    isPlaybackCompleted = true;
                };

                // 步骤11：启动播放设备和播放器（原生顺序：先启动设备，再播放）
                playbackDevice.Start();
                player.Play();

                // 步骤12：等待播放完成（避免 Task 提前结束释放资源）
                while (!isPlaybackCompleted&& player.State == PlaybackState.Playing
                       && playbackDevice.IsRunning)
                {
                    await Task.Delay(50);
                }
                playbackDevice.MasterMixer.RemoveComponent(player);
                // 状态校验（贴合 SoundPlayerBase 原生逻辑）
                if (player.State == PlaybackState.Stopped && !isPlaybackCompleted)
                {
                    LoggerHelper.Warning("播放被中断（设备停止或流读取完毕）");
                }
            }
            catch (InvalidOperationException ex)
            {
                LoggerHelper.Error($"设备初始化失败：{ex.Message}", ex);
            }
            catch (IOException ex)
            {
                LoggerHelper.Error($"音频流操作失败：{ex.Message}", ex);
            }
            catch (Exception ex)
            {
                LoggerHelper.Error($"提示音播放未知异常：{ex.Message}", ex);
            }
            finally
            {
                // 原生资源释放顺序：先停播放器→再停设备（注意：不释放共享引擎）
                player?.Stop();
                playbackDevice?.Stop();
                player?.Dispose();
                dataProvider?.Dispose();
                playbackDevice?.Dispose();
                // 注意：不要释放 engine，它是共享的单例
            }
        }, "播放音频");
    }

    /// <summary>
    /// 释放共享音频引擎（应在应用程序退出时调用）
    /// </summary>
    public static void DisposeAudioEngine()
    {
        lock (_engineLock)
        {
            _sharedEngine?.Dispose();
            _sharedEngine = null;
        }
    }
}
