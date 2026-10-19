using Avalonia;
using SukiUI.Controls;
using System;
using System.Threading.Tasks;

namespace MFAAvalonia.Features.CombatStrategies.Controls;

/// <summary>导航决定完成前恢复原选择，取消时保留当前编辑页面。</summary>
public sealed class CombatStrategyNavigationGuard : IDisposable
{
    private readonly SukiSideMenu _menu;
    private readonly SukiSideMenuItem _editorItem;
    private readonly Func<bool> _isDirty;
    private readonly Func<Task<bool>> _confirm;
    private bool _restoring;
    private bool _pending;
    public Task PendingNavigation { get; private set; } = Task.CompletedTask;

    public CombatStrategyNavigationGuard(SukiSideMenu menu, SukiSideMenuItem editorItem, Func<bool> isDirty, Func<Task<bool>> confirm)
    {
        _menu = menu;
        _editorItem = editorItem;
        _isDirty = isDirty;
        _confirm = confirm;
        _menu.PropertyChanged += OnSelectionChanged;
    }

    private void OnSelectionChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (_restoring || e.Property != SukiSideMenu.SelectedItemProperty || e.OldValue != _editorItem || e.NewValue is not SukiSideMenuItem target || !_isDirty()) return;
        SetSelection(_editorItem);
        if (_pending) return;
        _pending = true;
        PendingNavigation = CompleteNavigationAsync(target);
    }

    private async Task CompleteNavigationAsync(SukiSideMenuItem target)
    {
        try { if (await _confirm()) SetSelection(target); }
        finally { _pending = false; }
    }

    private void SetSelection(SukiSideMenuItem item)
    {
        _restoring = true;
        try { _menu.SelectedItem = item; }
        finally { _restoring = false; }
    }

    public void Dispose() => _menu.PropertyChanged -= OnSelectionChanged;
}
