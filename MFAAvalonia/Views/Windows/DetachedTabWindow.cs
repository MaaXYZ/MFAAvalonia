using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace MFAAvalonia.Views.Windows;

/// <summary>
/// 把某个实例标签页"撕"出来的独立窗口。
/// 窗口本身不持有任何业务状态：里面的 TaskQueueView 还是原来那一个，
/// DataContext 还是同一个 TaskQueueViewModel / MaaProcessor，所以任务不会中断。
/// 关闭窗口即把控件还给主窗口。
/// </summary>
public sealed class DetachedTabWindow : Window
{
    private readonly Control _content;
    private readonly Action _onReturn;
    private readonly DockPanel _root;
    private bool _returned;
    private bool _contentAttached;

    public DetachedTabWindow(string title, Control content, Action onReturn)
    {
        _content = content;
        _onReturn = onReturn;

        Title = title;
        Width = 1280;
        Height = 820;
        MinWidth = 900;
        MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var returnButton = new Button
        {
            Content = "收回主窗口",
            Padding = new Thickness(10, 4),
            VerticalAlignment = VerticalAlignment.Center
        };
        returnButton.Click += (_, _) => Close();

        var hint = new TextBlock
        {
            Text = "关闭本窗口即把该实例收回主窗口；里面的任务不会中断。",
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.75
        };

        var header = new Border
        {
            Padding = new Thickness(10, 6),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                Children = { returnButton, hint }
            }
        };
        DockPanel.SetDock(header, Dock.Top);

        _root = new DockPanel();
        _root.Children.Add(header);
        Content = _root;

        Closed += (_, _) => Return();
    }

    /// <summary>
    /// 在调用方确认控件已经从主窗口释放之后再挂上来。
    /// （同一个 Control 不能同时有两个父级，所以不能在构造里就加。）
    /// </summary>
    public void AttachContent()
    {
        if (_contentAttached)
            return;

        _contentAttached = true;
        _root.Children.Add(_content);
    }

    private void Return()
    {
        if (_returned)
            return;

        _returned = true;

        if (_contentAttached)
        {
            _root.Children.Remove(_content);
            _contentAttached = false;
        }

        Content = null;
        _onReturn();
    }
}
