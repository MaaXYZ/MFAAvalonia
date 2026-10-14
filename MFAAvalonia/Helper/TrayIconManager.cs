using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Data;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using AvaloniaExtensions.Axaml.Markup;
using FluentAvalonia.UI.Controls;
using Lang.Avalonia.MarkupExtensions;
using MFAAvalonia.Configuration;
using MFAAvalonia.Extensions.MaaFW;
using MFAAvalonia.Helper.Converters;
using MFAAvalonia.ViewModels.Windows;
using MFAAvalonia.Views.Windows;
using SukiUI.Content;
using System;
using System.IO;
using System.Linq;


namespace MFAAvalonia.Helper;

public class TrayIconManager
{
    private static TrayIcon? _trayIcon;
    private static DateTime _lastClickTime = DateTime.MinValue;

    public static void InitializeTrayIcon(Application application, RootView mainWindow, RootViewModel viewModel)
    {
        // 先清理旧的托盘实例，避免残留
        DisposeTrayIcon(application);

        // 创建 TrayIcon 实例
        _trayIcon = new TrayIcon
        {
            IsVisible = true
        };

        _trayIcon.Bind(TrayIcon.ToolTipTextProperty, new MultiBinding
        {
            Converter = new TrayToolTipConverter(),
            Bindings =
            {
                new Binding("CustomTitle") { Source = viewModel },
                new Binding("IsCustomTitleVisible") { Source = viewModel },
                new Binding("ResourceName") { Source = viewModel },
                new I18nBinding(LangKeys.AppTitle)
            }
        });
        
        var menu = new NativeMenu();
        // 绑定 Icon
        _trayIcon.Bind(TrayIcon.IconProperty, new Binding
        {
            Source = IconHelper.WindowIcon
        });

        var menuItem1 = new NativeMenuItem();
        menuItem1.Bind(NativeMenuItem.HeaderProperty, new I18nBinding(LangKeys.StartTask));
        menuItem1.Bind(NativeMenuItem.IsVisibleProperty, new Binding("!IsRunning")
        {
            Source = viewModel,
        });
        menuItem1.Click += StartTask;
        var menuItem2 = new NativeMenuItem();
        menuItem2.Bind(NativeMenuItem.HeaderProperty, new I18nBinding(LangKeys.StopTask));
        menuItem2.Bind(NativeMenuItem.IsVisibleProperty, new Binding("IsRunning")
        {
            Source = viewModel,
        });
        menuItem2.Click += StopTask;

        var menuItem3 = new NativeMenuItem()
        {
        };

        menuItem3.Bind(NativeMenuItem.HeaderProperty, new I18nBinding(LangKeys.SwitchLanguage));
        menuItem3.Menu = new NativeMenu();

        foreach (var lang in LanguageHelper.SupportedLanguages)
        {
            var langMenu = new NativeMenuItem
            {
                Header = lang.Name
            };
            langMenu.Click += (sender, _) =>
            {
                LanguageHelper.ChangeLanguage(lang);
                ConfigurationManager.Current.SetValue(ConfigurationKeys.CurrentLanguage, lang.Key);
            };
            menuItem3.Menu.Add(langMenu);
        }

        var menuItem4 = new NativeMenuItem();
        menuItem4.Bind(NativeMenuItem.HeaderProperty, new I18nBinding(LangKeys.Hide));
        menuItem4.Bind(NativeMenuItem.IsVisibleProperty, new Binding("IsWindowVisible")
        {
            Source = viewModel,
        });
        menuItem4.Click += App_hide;
        var menuItem5 = new NativeMenuItem();
        menuItem5.Bind(NativeMenuItem.HeaderProperty, new I18nBinding(LangKeys.Show));
        menuItem5.Bind(NativeMenuItem.IsVisibleProperty, new Binding("!IsWindowVisible")
        {
            Source = viewModel,
        });
        menuItem5.Click += App_show;
        var menuItem6 = new NativeMenuItem();
        menuItem6.Bind(NativeMenuItem.HeaderProperty, new I18nBinding(LangKeys.Quit));
        menuItem6.Click += App_exit;
        menu.Add(menuItem1);
        menu.Add(menuItem2);
        menu.Add(menuItem3);
        menu.Add(menuItem4);
        menu.Add(menuItem5);
        menu.Add(menuItem6);
        // 将菜单绑定到 TrayIcon
        _trayIcon.Menu = menu;
        // 监听 Clicked 事件
        _trayIcon.Clicked += TrayIconOnClicked;

        // 将 TrayIcon 添加到托盘
        TrayIcon.SetIcons(application, [_trayIcon]);
    }

    public static void DisposeTrayIcon(Application? application)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            DisposeTrayIconCore(application);
            return;
        }

        DispatcherHelper.PostOnMainThread(() => DisposeTrayIconCore(application));
    }

    private static void DisposeTrayIconCore(Application? application)
    {
        if (_trayIcon == null)
            return;

        try
        {
            _trayIcon.Clicked -= TrayIconOnClicked;
        }
        catch
        {
            // ignore
        }

        if (_trayIcon.Menu is NativeMenu menu)
        {
            foreach (var item in menu.Items.OfType<NativeMenuItem>())
            {
                item.Click -= StartTask;
                item.Click -= StopTask;
                item.Click -= App_hide;
                item.Click -= App_show;
                item.Click -= App_exit;
            }
        }

        try
        {
            _trayIcon.IsVisible = false;
        }
        catch
        {
            // ignore
        }

        if (application != null)
        {
            try
            {
                TrayIcon.SetIcons(application, null);
            }
            catch
            {
                // ignore
            }
        }

        try
        {
            _trayIcon.Dispose();
        }
        catch
        {
            // Native tray cleanup must not abort the application shutdown path.
        }
        _trayIcon = null;
    }

    private static void NotifyIcon_MouseClick(object sender, EventArgs e) =>
        Instances.RootView.ShowWindow();

    private static void TrayIconOnClicked(object sender, EventArgs args)
    {
        var now = DateTime.Now;
        var clickInterval = now - _lastClickTime;

        // 判断是否为双击（时间间隔小于 500 毫秒）
        if (clickInterval.TotalMilliseconds < 500)
        {
            DispatcherHelper.PostOnMainThread(() =>
            {
                Instances.RootView.ShowWindow();
            });
        }

        _lastClickTime = now;
    }

    private static void StartTask(object sender, EventArgs e) =>
        Instances.InstanceTabBarViewModel.ActiveTab?.TaskQueueViewModel.StartTask();


    private static void StopTask(object sender, EventArgs e) =>
        Instances.InstanceTabBarViewModel.ActiveTab?.TaskQueueViewModel.StopTask();
#pragma warning disable CS4014 // 由于此调用不会等待，因此在此调用完成之前将会继续执行当前方法。请考虑将 "await" 运算符应用于调用结果。

    private static async void App_exit(object sender, EventArgs e)
    {
        if (!await Instances.RootView.ConfirmExit())
            return;
        Instances.RootView.ShutdownAfterConfirmedExit();
    }

    private static void App_hide(object sender, EventArgs e) =>
        Instances.RootViewModel.IsWindowVisible = false;


    private static void App_show(object sender, EventArgs e)
        => Instances.RootViewModel.IsWindowVisible = true;
}
