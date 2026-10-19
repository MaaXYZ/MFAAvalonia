using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MFAAvalonia.Features.CombatStrategies;
using MFAAvalonia.Features.CombatStrategies.Controls;
using MFAAvalonia.Helper;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MFAAvalonia.Views.Pages;

public partial class CombatStrategyView : UserControl
{
    public StrategyEditorSession Session { get; }
    private bool _refreshing;
    private bool _operationPending;
    private string? _renderedStrategyId;
    private StrategySummary? _menuStrategy;
    private readonly Func<string, string, Task<string?>> _renameStrategy;
    private readonly Func<string, Task<bool>> _confirmDelete;
    private readonly SemaphoreSlim _leaveGate = new(1, 1);

    public CombatStrategyView() : this(Instances.CombatStrategyViewModel.Session) { }

    public CombatStrategyView(StrategyEditorSession session,
        Func<string, string, Task<string?>>? renameStrategy = null,
        Func<string, Task<bool>>? confirmDelete = null)
    {
        Session = session;
        _renameStrategy = renameStrategy ?? StrategyDialogs.RenameAsync;
        _confirmDelete = confirmDelete ?? StrategyDialogs.ConfirmDeleteAsync;
        InitializeComponent();
        // 框架自动打开菜单后会处理掉请求，仍需记录实际右键条目的策略。
        StrategyList.AddHandler(Control.ContextRequestedEvent, OnStrategyContextRequested, handledEventsToo: true);
        Session.DraftChanged += RefreshEditor;
        Session.StateChanged += RefreshStatus;
        RefreshEditor();
    }

    private void RefreshEditor()
    {
        var same = _renderedStrategyId == Session.Current.Id;
        var expanded = StageCards.Children.OfType<StrategyStageCard>().ToDictionary(x => x.Stage.Id, x => x.IsExpanded);
        StageCards.Children.Clear();
        foreach (var stage in Session.Current.Stages)
        {
            var card = new StrategyStageCard(Session, stage, ShowError, StrategyDialogs.RenameAsync);
            card.IsExpanded = same && expanded.TryGetValue(stage.Id, out var open) ? open : same || StageCards.Children.Count == 0;
            StageCards.Children.Add(card);
            StrategyReorder.Attach(card.DragHandle, card, StageCards, Session.Current.Stages, stage, Session.StructuralChange);
        }
        if (!same) StageScroll.Offset = default;
        _renderedStrategyId = Session.Current.Id;
        RefreshList();
        RefreshStatus();
    }

    private void RefreshList()
    {
        _refreshing = true;
        try
        {
            StrategyList.ItemsSource = Session.Catalog.Strategies;
            StrategyList.SelectedItem = Session.Catalog.Strategies.FirstOrDefault(x => x.Id == Session.Current.Id);
            LoadWarnings.IsVisible = Session.Catalog.Errors.Count != 0;
        }
        finally { _refreshing = false; }
    }

    private void RefreshStatus()
    {
        SaveStrategy.IsEnabled = !Session.IsReadOnly;
        AddStage.IsEnabled = !Session.IsReadOnly;
        ToolTip.SetTip(AddStage, Session.IsReadOnly ? "默认策略只读，请先新增或切换到可编辑策略" : "新增阶段");
        SaveStatus.Text = Session.Error != null ? "保存或读取失败，请查看提示"
            : Session.IsReadOnly ? "默认策略只读，可复制并粘贴后编辑"
            : Session.HasUnsavedChanges ? "有未保存的修改" : "已保存";
        ToolTip.SetTip(SaveStatus, Session.Error);
    }

    public async Task<bool> ConfirmLeaveAsync()
    {
        await _leaveGate.WaitAsync();
        try
        {
            var result = await Session.TryLeaveAsync(StrategyDialogs.UnsavedAsync);
            if (!result && Session.Error != null) await StrategyDialogs.ErrorAsync(Session.Error);
            RefreshList();
            RefreshStatus();
            return result;
        }
        finally { _leaveGate.Release(); }
    }

    private async void OnStrategySelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_refreshing || _operationPending || StrategyList.SelectedItem is not StrategySummary selected) return;
        _operationPending = true;
        try
        {
            if (selected.Id != Session.Current.Id && await ConfirmLeaveAsync())
            {
                try { Session.Select(selected.Id); }
                catch (Exception ex) when (StrategyStore.IsFileError(ex)) { await StrategyDialogs.ErrorAsync(ex.Message); }
            }
            RefreshList();
        }
        finally { _operationPending = false; }
    }

    private async void OnNewStrategy(object? sender, RoutedEventArgs e)
    {
        if (_operationPending) return;
        _operationPending = true;
        try
        {
            if (!await ConfirmLeaveAsync()) return;
            var suffix = 1;
            string name;
            do { name = $"新策略 {suffix++}"; } while (Session.Catalog.Strategies.Any(x => x.Name == name));
            Session.Create(name);
        }
        finally { _operationPending = false; }
    }

    private void OnStrategyContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        var strategy = (e.Source as Control)?.GetSelfAndVisualAncestors()
            .OfType<Control>().Select(x => x.DataContext).OfType<StrategySummary>().FirstOrDefault();
        if (strategy == null || Resources["StrategyMenu"] is not ContextMenu menu)
            return;

        _menuStrategy = strategy;
        menu.DataContext = strategy;
        ApplyStrategyMenuEnabledStates(menu, strategy);
    }

    private void OnStrategyMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (sender is not ContextMenu menu) return;
        var strategy = _menuStrategy ?? menu.DataContext as StrategySummary;
        menu.DataContext = strategy;
        if (strategy != null) ApplyStrategyMenuEnabledStates(menu, strategy);
    }

    private static void ApplyStrategyMenuEnabledStates(ContextMenu menu, StrategySummary strategy)
    {
        var menuItems = menu.Items?.OfType<MenuItem>().ToList();
        if (menuItems is not { Count: 4 }) return;
        menuItems[0].IsEnabled = true;
        menuItems[1].IsEnabled = true;
        menuItems[2].IsEnabled = !strategy.IsBuiltIn;
        menuItems[3].IsEnabled = !strategy.IsBuiltIn;
    }

    private StrategySummary? GetMenuStrategy(object? sender) =>
        (sender as MenuItem)?.DataContext as StrategySummary ?? _menuStrategy;

    private async void OnCopyStrategy(object? sender, RoutedEventArgs e)
    {
        if (GetMenuStrategy(sender) is not { } strategy) return;
        try
        {
            var document = strategy.Id == Session.Current.Id ? StrategyJson.Clone(Session.Current) : Session.Load(strategy.Id);
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard == null)
            {
                await StrategyDialogs.ErrorAsync("系统剪贴板不可用。");
                return;
            }
            await clipboard.SetTextAsync(StrategyJson.Serialize(document));
        }
        catch (Exception ex) { await StrategyDialogs.ErrorAsync(ex.Message); }
    }

    private async void OnPasteStrategy(object? sender, RoutedEventArgs e)
    {
        if (_operationPending) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        string? text;
        try { text = clipboard == null ? null : await clipboard.TryGetTextAsync(); }
        catch (Exception ex)
        {
            await StrategyDialogs.ErrorAsync(ex.Message);
            return;
        }
        if (string.IsNullOrWhiteSpace(text))
        {
            await StrategyDialogs.ErrorAsync("剪贴板中没有可粘贴的策略 JSON。");
            return;
        }

        StrategyDocument document;
        try { document = StrategyJson.Deserialize(text); }
        catch (StrategyValidationException ex)
        {
            await StrategyDialogs.ErrorAsync(ex.Message);
            return;
        }

        _operationPending = true;
        try
        {
            if (await ConfirmLeaveAsync()) Session.Duplicate(document);
        }
        finally { _operationPending = false; }
    }

    private async void OnRenameStrategy(object? sender, RoutedEventArgs e)
    {
        var strategy = GetMenuStrategy(sender);
        if (strategy == null || strategy.IsBuiltIn || _operationPending) return;
        _operationPending = true;
        try
        {
            var name = await _renameStrategy("重命名策略", strategy.Name);
            if (name != null)
            {
                Session.Rename(strategy.Id, name);
                RefreshList();
            }
        }
        catch (Exception ex) when (StrategyStore.IsFileError(ex) || ex is InvalidOperationException)
        { await StrategyDialogs.ErrorAsync(ex.Message); }
        finally { _operationPending = false; }
    }

    private async void OnDeleteStrategyFromMenu(object? sender, RoutedEventArgs e)
    {
        var strategy = GetMenuStrategy(sender);
        if (strategy == null || strategy.IsBuiltIn || _operationPending) return;
        _operationPending = true;
        try
        {
            if (!await _confirmDelete(strategy.Name)) return;
            try
            {
                Session.Delete(strategy.Id);
                RefreshList();
            }
            catch (Exception ex) when (StrategyStore.IsFileError(ex)) { await StrategyDialogs.ErrorAsync(ex.Message); }
        }
        finally { _operationPending = false; }
    }

    private void OnRefreshStrategies(object? sender, RoutedEventArgs e)
    {
        Session.RefreshCatalog();
        RefreshList();
        RefreshStatus();
    }

    private async void OnSaveStrategy(object? sender, RoutedEventArgs e)
    {
        if (!Session.TrySave()) await StrategyDialogs.ErrorAsync(Session.Error ?? "保存失败。");
        RefreshList();
        RefreshStatus();
    }

    private void OnAddStage(object? sender, RoutedEventArgs e) => Session.AddStage();
    private void OnExpandAll(object? sender, RoutedEventArgs e) { foreach (var card in StageCards.Children.OfType<StrategyStageCard>()) card.IsExpanded = true; }
    private void OnCollapseAll(object? sender, RoutedEventArgs e) { foreach (var card in StageCards.Children.OfType<StrategyStageCard>()) card.IsExpanded = false; }
    private async void OnLoadWarnings(object? sender, RoutedEventArgs e) => await StrategyDialogs.ErrorAsync(string.Join(Environment.NewLine, Session.Catalog.Errors));
    private static async void ShowError(string message) => await StrategyDialogs.ErrorAsync(message);
}
