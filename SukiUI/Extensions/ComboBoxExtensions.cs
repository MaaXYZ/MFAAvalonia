using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Reactive;
using Avalonia.VisualTree;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace SukiUI.Extensions;

public static class ComboBoxExtensions
{
    #region DisableNavigationOnLostFocus Property

    public static readonly AttachedProperty<bool> DisableNavigationOnLostFocusProperty =
        AvaloniaProperty.RegisterAttached<ComboBox, bool>(
            "DisableNavigationOnLostFocus",
            typeof(ComboBoxExtensions),
            defaultValue: false);

    public static bool GetDisableNavigationOnLostFocus(ComboBox box) =>
        box.GetValue(DisableNavigationOnLostFocusProperty);

    public static void SetDisableNavigationOnLostFocus(ComboBox box, bool value) =>
        box.SetValue(DisableNavigationOnLostFocusProperty, value);

    private static void OnDisableNavigationOnLostFocusChanged(AvaloniaPropertyChangedEventArgs<bool> args)
    {
        if (args.Sender is ComboBox comboBox)
        {

            if (args.NewValue.Value)
            {
                comboBox.AddHandler(
                    InputElement.KeyDownEvent,
                    (EventHandler<KeyEventArgs>)HandleKeyDown,
                    RoutingStrategies.Tunnel);
                comboBox.AddHandler(
                    InputElement.PointerWheelChangedEvent,
                    (EventHandler<PointerWheelEventArgs>)HandlePointerWheel,
                    RoutingStrategies.Tunnel);
            }
            else
            {
                comboBox.RemoveHandler(InputElement.KeyDownEvent, (EventHandler<KeyEventArgs>)HandleKeyDown);
                comboBox.RemoveHandler(InputElement.PointerWheelChangedEvent, (EventHandler<PointerWheelEventArgs>)HandlePointerWheel);
            }
        }
    }


    private static bool IsInputAllowed(ComboBox comboBox) =>
        comboBox.IsDropDownOpen;

    private static void HandleKeyDown(object sender, KeyEventArgs e)
    {
        var comboBox = (ComboBox)sender;
        if (!IsInputAllowed(comboBox) && (e.Key == Key.Up || e.Key == Key.Down))
        {
            e.Handled = true;
        }
    }

    private static void HandlePointerWheel(object sender, PointerWheelEventArgs e)
    {
        var comboBox = (ComboBox)sender;
        if (!IsInputAllowed(comboBox))
        {
            e.Handled = true;
        }
    }


    private static void HandleDropDownOpened(object sender, EventArgs e) => UpdateInputState((ComboBox)sender);
    private static void HandleDropDownClosed(object sender, EventArgs e) => UpdateInputState((ComboBox)sender);

    // 更新输入拦截状态
    private static void UpdateInputState(ComboBox comboBox)
    {

    }

    #endregion

    #region CanSearch Property

    // 存储搜索框引用的字典
    private static readonly Dictionary<ComboBox, TextBox> _searchBoxes = new();

    /// <summary>
    /// 附加属性：是否启用搜索功能
    /// </summary>
    public static readonly AttachedProperty<bool> CanSearchProperty =
        AvaloniaProperty.RegisterAttached<ComboBox, bool>(
            "CanSearch",
            typeof(ComboBoxExtensions),
            defaultValue: false);

    /// <summary>
    /// 附加属性：搜索框的水印文本
    /// </summary>
    public static readonly AttachedProperty<string> SearchWatermarkProperty =
        AvaloniaProperty.RegisterAttached<ComboBox, string>(
            "SearchWatermark",
            typeof(ComboBoxExtensions),
            defaultValue: "搜索...");

    /// <summary>
    /// 附加属性：用于搜索的属性路径，类似于DisplayMemberBinding
    /// </summary>
    public static readonly AttachedProperty<string?> SearchMemberPathProperty =
        AvaloniaProperty.RegisterAttached<ComboBox, string?>(
            "SearchMemberPath",
            typeof(ComboBoxExtensions),
            defaultValue: null);

    public static bool GetCanSearch(ComboBox box) => box.GetValue(CanSearchProperty);
    public static void SetCanSearch(ComboBox box, bool value) => box.SetValue(CanSearchProperty, value);

    public static string GetSearchWatermark(ComboBox box) => box.GetValue(SearchWatermarkProperty);
    public static void SetSearchWatermark(ComboBox box, string value) => box.SetValue(SearchWatermarkProperty, value);

    public static string? GetSearchMemberPath(ComboBox box) => box.GetValue(SearchMemberPathProperty);
    public static void SetSearchMemberPath(ComboBox box, string? value) => box.SetValue(SearchMemberPathProperty, value);

    private static void OnCanSearchChanged(AvaloniaPropertyChangedEventArgs<bool> args)
    {
        if (args.Sender is not ComboBox comboBox)
            return;

        if (args.NewValue.Value)
        {
            // 搜索逻辑基于下拉项容器的可见性；禁用虚拟化以确保未滚动到的选项也会生成并参与匹配。
            comboBox.ItemsPanel = new FuncTemplate<Panel>(() => new StackPanel());
            comboBox.DropDownOpened += OnSearchableDropDownOpened;
            comboBox.DropDownClosed += OnSearchableDropDownClosed;
        }
        else
        {
            comboBox.DropDownOpened -= OnSearchableDropDownOpened;
            comboBox.DropDownClosed -= OnSearchableDropDownClosed;
            CleanupSearch(comboBox);
        }
    }

    private static void OnSearchableDropDownOpened(object? sender, EventArgs e)
    {
        if (sender is not ComboBox comboBox)
            return;

        // 延迟执行以确保下拉框已完全打开
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            SetupSearchBox(comboBox);
        }, Avalonia.Threading.DispatcherPriority.Loaded);
    }

    private static void OnSearchableDropDownClosed(object? sender, EventArgs e)
    {
        if (sender is not ComboBox comboBox)
            return;

        // 恢复所有项目的可见性
        RestoreAllItemsVisibility(comboBox);

        // 清理搜索框文本
        if (_searchBoxes.TryGetValue(comboBox, out var searchBox))
        {
            searchBox.Text = string.Empty;
        }
    }

    /// <summary>
    /// 恢复所有ComboBoxItem的可见性
    /// </summary>
    private static void RestoreAllItemsVisibility(ComboBox comboBox)
    {
        var itemsPresenter = comboBox.FindDescendantOfType<ItemsPresenter>();
        if (itemsPresenter == null)
            return;

        foreach (var item in itemsPresenter.GetVisualDescendants().OfType<ComboBoxItem>())
        {
            item.IsVisible = true;
        }
    }

    private static void SetupSearchBox(ComboBox comboBox)
    {
        // 查找模板中的PART_SearchBox
        var popup = comboBox.FindDescendantOfType<Popup>();
        if (popup?.Child == null)
            return;

        // 从Popup.Child开始查找TextBox
        TextBox? searchBox = null;
        
        // 首先尝试从Popup.Child的视觉后代中查找
        if (popup.Child is Visual visual)
        {
            foreach (var textBox in visual.GetVisualDescendants().OfType<TextBox>())
            {
                if (textBox.Name == "PART_SearchBox")
                {
                    searchBox = textBox;
                    break;
                }
            }
        }
        
        if (searchBox == null)
            return;

        // 检查是否已经设置过
        if (_searchBoxes.ContainsKey(comboBox))
            return;

        _searchBoxes[comboBox] = searchBox;

        // 监听搜索文本变化
        searchBox.TextChanged += (s, args) => OnSearchTextChanged(comboBox, searchBox.Text);

        // 聚焦搜索框
        searchBox.Focus();
    }

    private static void OnSearchTextChanged(ComboBox comboBox, string? searchText)
    {
        // 查找ItemsPresenter以获取所有ComboBoxItem
        var popup = comboBox.FindDescendantOfType<Popup>();
        if (popup?.Child == null)
            return;

        var itemsPresenter = popup.Child.FindDescendantOfType<ItemsPresenter>();
        if (itemsPresenter == null)
            return;

        var searchLower = searchText?.ToLowerInvariant() ?? string.Empty;
        var isSearchEmpty = string.IsNullOrWhiteSpace(searchText);

        // 遍历所有ComboBoxItem并设置可见性
        foreach (var comboBoxItem in itemsPresenter.GetVisualDescendants().OfType<ComboBoxItem>())
        {
            if (isSearchEmpty)
            {
                comboBoxItem.IsVisible = true;
            }
            else
            {
                var itemText = GetItemDisplayText(comboBox, comboBoxItem.DataContext);
                comboBoxItem.IsVisible = itemText.ToLowerInvariant().Contains(searchLower);
            }
        }
    }

    private static string GetItemDisplayText(ComboBox comboBox, object? item)
    {
        if (item == null)
            return string.Empty;

        // 首先尝试使用SearchMemberPath附加属性
        var searchMemberPath = GetSearchMemberPath(comboBox);
        if (!string.IsNullOrEmpty(searchMemberPath))
        {
            var property = item.GetType().GetProperty(searchMemberPath);
            if (property != null)
            {
                return property.GetValue(item)?.ToString() ?? string.Empty;
            }
        }

        // 尝试使用DisplayMemberBinding
        if (comboBox.DisplayMemberBinding is Avalonia.Data.Binding binding
            && !string.IsNullOrEmpty(binding.Path))
        {
            var property = item.GetType().GetProperty(binding.Path);
            if (property != null)
            {
                return property.GetValue(item)?.ToString() ?? string.Empty;
            }
        }

        // 尝试查找常见的显示属性名称
        var displayPropertyNames = new[]
        {
            "DisplayName",
            "Name",
            "Text",
            "Title",
            "Label",
            "Description"
        };
        foreach (var propName in displayPropertyNames)
        {
            var property = item.GetType().GetProperty(propName);
            if (property != null)
            {
                var value = property.GetValue(item)?.ToString();
                if (!string.IsNullOrEmpty(value))
                {
                    return value;
                }
            }
        }

        return item.ToString() ?? string.Empty;
    }

    private static void CleanupSearch(ComboBox comboBox)
    {
        _searchBoxes.Remove(comboBox);
    }

    #endregion

    static ComboBoxExtensions()
    {
        DisableNavigationOnLostFocusProperty.Changed.Subscribe(OnDisableNavigationOnLostFocusChanged);
        CanSearchProperty.Changed.Subscribe(OnCanSearchChanged);
    }
    public static IDisposable Subscribe<T>(this IObservable<T> observable, Action<T> action)
    {
        return observable.Subscribe((IObserver<T>)new AnonymousObserver<T>((Action<T>)action));
    }
}
