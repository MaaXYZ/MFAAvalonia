using Avalonia.Controls;
using Avalonia.Interactivity;
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
    private readonly SemaphoreSlim _leaveGate = new(1, 1);

    public CombatStrategyView() : this(Instances.CombatStrategyViewModel.Session) { }

    public CombatStrategyView(StrategyEditorSession session)
    {
        Session = session;
        InitializeComponent();
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
        DeleteStrategy.IsEnabled = !Session.IsReadOnly;
        RenameStrategy.IsEnabled = !Session.IsReadOnly;
        AddStage.IsEnabled = !Session.IsReadOnly;
        SaveStatus.Text = Session.Error != null ? "保存或读取失败，请查看提示"
            : Session.IsReadOnly ? "默认策略只读，可复制后编辑"
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

    private async void OnCopyStrategy(object? sender, RoutedEventArgs e)
    {
        if (_operationPending) return;
        _operationPending = true;
        try { if (await ConfirmLeaveAsync()) Session.Duplicate(); }
        finally { _operationPending = false; }
    }

    private async void OnRenameStrategy(object? sender, RoutedEventArgs e)
    {
        if (Session.IsReadOnly || _operationPending) return;
        _operationPending = true;
        try
        {
            var name = await StrategyDialogs.RenameAsync("重命名策略", Session.Current.Name);
            if (name != null) { Session.Rename(name); RefreshList(); }
        }
        finally { _operationPending = false; }
    }

    private async void OnSaveStrategy(object? sender, RoutedEventArgs e)
    {
        if (!Session.TrySave()) await StrategyDialogs.ErrorAsync(Session.Error ?? "保存失败。");
        RefreshList();
        RefreshStatus();
    }

    private async void OnDeleteStrategy(object? sender, RoutedEventArgs e)
    {
        if (Session.IsReadOnly || _operationPending) return;
        _operationPending = true;
        try
        {
            if (!await StrategyDialogs.ConfirmDeleteAsync(Session.Current.Name)) return;
            try { Session.DeleteCurrent(); }
            catch (Exception ex) when (StrategyStore.IsFileError(ex)) { await StrategyDialogs.ErrorAsync(ex.Message); }
        }
        finally { _operationPending = false; }
    }

    private void OnAddStage(object? sender, RoutedEventArgs e) => Session.AddStage();
    private void OnExpandAll(object? sender, RoutedEventArgs e) { foreach (var card in StageCards.Children.OfType<StrategyStageCard>()) card.IsExpanded = true; }
    private void OnCollapseAll(object? sender, RoutedEventArgs e) { foreach (var card in StageCards.Children.OfType<StrategyStageCard>()) card.IsExpanded = false; }
    private async void OnLoadWarnings(object? sender, RoutedEventArgs e) => await StrategyDialogs.ErrorAsync(string.Join(Environment.NewLine, Session.Catalog.Errors));
    private static async void ShowError(string message) => await StrategyDialogs.ErrorAsync(message);
}
