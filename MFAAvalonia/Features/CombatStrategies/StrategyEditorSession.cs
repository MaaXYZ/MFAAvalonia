using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace MFAAvalonia.Features.CombatStrategies;

public enum UnsavedDecision { Cancel, Save, Discard }

/// <summary>编辑草稿与持久化分离，只有保存成功才清除修改状态。</summary>
public sealed class StrategyEditorSession
{
    private readonly StrategyStore _store;
    private StrategyDocument _saved;
    private bool _isNew;
    public StrategyDocument Current { get; private set; }
    public StrategyCatalog Catalog { get; private set; }
    public bool HasUnsavedChanges { get; private set; }
    public bool IsReadOnly => Current.Id == "default";
    public string? Error { get; private set; }
    public string DirectoryPath => _store.DirectoryPath;
    public event Action? DraftChanged;
    public event Action? StateChanged;

    public StrategyEditorSession(StrategyStore store)
    {
        _store = store;
        Current = StrategyDefaults.Create();
        _saved = StrategyJson.Clone(Current);
        Catalog = _store.List();
    }

    public void MarkDirty()
    {
        EnsureWritable();
        HasUnsavedChanges = true;
        Error = null;
        StateChanged?.Invoke();
    }

    public void Select(string id)
    {
        if (HasUnsavedChanges) throw new InvalidOperationException("请先处理未保存的修改。");
        Open(_store.Load(id), isNew: false);
    }

    public void Create(string name)
    {
        if (HasUnsavedChanges) throw new InvalidOperationException("请先处理未保存的修改。");
        Open(StrategyDefaults.CreateEditable(name), isNew: true);
    }

    public void Duplicate()
    {
        var clone = StrategyJson.Clone(Current);
        clone.Id = NewId();
        clone.Name = Current.Name + " 副本";
        var ids = clone.Stages.ToDictionary(x => x.Id, _ => NewId());
        clone.InitialStageId = ids[clone.InitialStageId];
        foreach (var stage in clone.Stages)
        {
            stage.Id = ids[stage.Id];
            foreach (var transition in stage.Transitions)
            {
                transition.Id = NewId();
                if (ids.TryGetValue(transition.TargetStageId, out var target))
                    transition.TargetStageId = target;
            }
        }
        Open(clone, isNew: true);
    }

    public void Rename(string name)
    {
        EnsureWritable();
        Current.Name = name.Trim();
        MarkDirty();
        RefreshCatalog();
    }

    public void Save()
    {
        EnsureWritable();
        _store.Save(Current);
        _saved = StrategyJson.Clone(Current);
        _isNew = false;
        HasUnsavedChanges = false;
        Error = null;
        RefreshCatalog();
        StateChanged?.Invoke();
    }

    public bool TrySave()
    {
        try { Save(); return true; }
        catch (Exception ex) when (StrategyStore.IsFileError(ex) || ex is InvalidOperationException)
        {
            Error = ex.Message;
            StateChanged?.Invoke();
            return false;
        }
    }

    public async Task<bool> TryLeaveAsync(Func<Task<UnsavedDecision>> prompt)
    {
        if (!HasUnsavedChanges) return true;
        switch (await prompt())
        {
            case UnsavedDecision.Save:
                return TrySave();
            case UnsavedDecision.Discard:
                Current = _isNew ? StrategyDefaults.Create() : StrategyJson.Clone(_saved);
                _saved = StrategyJson.Clone(Current);
                _isNew = false;
                HasUnsavedChanges = false;
                Error = null;
                RefreshCatalog();
                DraftChanged?.Invoke();
                StateChanged?.Invoke();
                return true;
            default:
                return false;
        }
    }

    public async Task<bool> TrySwitchAsync(string id, Func<Task<UnsavedDecision>> prompt)
    {
        if (id == Current.Id) return true;
        if (!await TryLeaveAsync(prompt)) return false;
        try { Select(id); return true; }
        catch (Exception ex) when (StrategyStore.IsFileError(ex))
        {
            Error = ex.Message;
            StateChanged?.Invoke();
            return false;
        }
    }

    public void DeleteCurrent()
    {
        EnsureWritable();
        _store.Delete(Current.Id);
        Open(StrategyDefaults.Create(), false);
    }

    public StrategyStage AddStage()
    {
        EnsureWritable();
        var stage = new StrategyStage { Name = $"阶段 {Current.Stages.Count + 1}" };
        Current.Stages.Add(stage);
        StructuralChange();
        return stage;
    }

    public StrategyStage CopyStage(StrategyStage source)
    {
        EnsureWritable();
        var copy = StrategyJson.Clone(Current).Stages[Current.Stages.IndexOf(source)];
        copy.Id = NewId();
        copy.Name = source.Name + " 副本";
        foreach (var transition in copy.Transitions)
        {
            transition.Id = NewId();
            if (transition.TargetStageId == source.Id) transition.TargetStageId = copy.Id;
        }
        Current.Stages.Insert(Current.Stages.IndexOf(source) + 1, copy);
        StructuralChange();
        return copy;
    }

    public void DeleteStage(StrategyStage stage)
    {
        EnsureWritable();
        if (Current.Stages.Count <= 1) throw new InvalidOperationException("策略至少保留一个阶段。");
        if (Current.InitialStageId == stage.Id) throw new InvalidOperationException("请先将其他阶段设为初始阶段。");
        var references = Current.Stages.Where(x => x.Id != stage.Id && x.Transitions.Any(t => t.TargetStageId == stage.Id)).Select(x => x.Name).ToArray();
        if (references.Length != 0) throw new InvalidOperationException("以下阶段仍引用此阶段，请先修改跳转目标：" + string.Join("、", references));
        Current.Stages.Remove(stage);
        StructuralChange();
    }

    public void SetInitialStage(StrategyStage stage)
    {
        EnsureWritable();
        Current.InitialStageId = stage.Id;
        StructuralChange();
    }

    public void MoveStage(StrategyStage source, StrategyStage target)
    {
        EnsureWritable();
        var from = Current.Stages.IndexOf(source);
        var to = Current.Stages.IndexOf(target);
        if (from < 0 || to < 0 || from == to) return;
        Current.Stages.RemoveAt(from);
        Current.Stages.Insert(to, source);
        StructuralChange();
    }

    public void RefreshCatalog()
    {
        var loaded = _store.List();
        var list = loaded.Strategies.Where(x => x.Id != Current.Id).ToList();
        list.Add(new StrategySummary(Current.Id, Current.Name, IsReadOnly));
        Catalog = new(list.OrderByDescending(x => x.IsBuiltIn).ThenBy(x => x.Name, StringComparer.CurrentCulture).ToList(), loaded.Errors);
    }

    public void StructuralChange()
    {
        MarkDirty();
        DraftChanged?.Invoke();
    }

    private void Open(StrategyDocument document, bool isNew)
    {
        Current = document;
        _saved = StrategyJson.Clone(document);
        _isNew = isNew;
        HasUnsavedChanges = isNew;
        Error = null;
        RefreshCatalog();
        DraftChanged?.Invoke();
        StateChanged?.Invoke();
    }

    private void EnsureWritable()
    {
        if (IsReadOnly) throw new InvalidOperationException("默认策略为只读，请先复制。");
    }

    private static string NewId() => Guid.NewGuid().ToString("N");
}
