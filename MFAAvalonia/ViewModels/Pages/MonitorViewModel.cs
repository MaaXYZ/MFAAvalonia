using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MFAAvalonia.Extensions.MaaFW;
using MFAAvalonia.Helper;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Threading;

namespace MFAAvalonia.ViewModels.Pages;

public partial class MonitorViewModel : ViewModelBase, IDisposable
{
    private const double BaseCardWidth = 360;
    private const double BaseCardHeight = 240;
    private const double MinCardScale = 0.6;
    private const double MaxCardScale = 1.8;
    private const double CardScaleStep = 0.1;

    public ObservableCollection<MonitorItemViewModel> Items { get; } = new();
    private readonly DispatcherTimer _timer;
    private int _liveViewOverrideCount;

    public bool IsLiveViewOverrideActive => _liveViewOverrideCount > 0;

    [ObservableProperty]
    private int _sortIndex;

    [ObservableProperty]
    private double _cardScale = 1.0;

    public double CardWidth => BaseCardWidth * CardScale;
    public double CardHeight => BaseCardHeight * CardScale;
    public string CardScaleText => $"{(int)Math.Round(CardScale * 100)}%";

    partial void OnSortIndexChanged(int value) => ApplySort();
    partial void OnCardScaleChanged(double value)
    {
        OnPropertyChanged(nameof(CardWidth));
        OnPropertyChanged(nameof(CardHeight));
        OnPropertyChanged(nameof(CardScaleText));
    }

    public MonitorViewModel()
    {
        RefreshItems();
        
        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1.5) 
        };
        _timer.Tick += (s, e) => UpdateAll();
        _timer.Start();
        
        MaaProcessor.Processors.CollectionChanged += Processors_CollectionChanged;
    }

    private void Processors_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
       RefreshItems();
    }

    private void RefreshItems()
    {
        DispatcherHelper.PostOnMainThread(() =>
        {
            var processors = MaaProcessor.Processors.ToList();
            
            var toRemove = Items.Where(i => !processors.Contains(i.Processor)).ToList();
            foreach(var item in toRemove)
            {
                item.Dispose();
                Items.Remove(item);
            }

            foreach(var p in processors)
            {
                if (!Items.Any(i => i.Processor == p))
                {
                    Items.Add(new MonitorItemViewModel(p, this));
                }
            }

            if (SortIndex != 0)
                ApplySort();
        });
    }

    private void ApplySort()
    {
        DispatcherHelper.PostOnMainThread(() =>
        {
            var sorted = SortIndex switch
            {
                1 => Items.OrderByDescending(i => i.IsRunning)
                    .ThenByDescending(i => i.IsConnected).ToList(),
                2 => Items.OrderBy(i => i.Name, StringComparer.CurrentCulture).ToList(),
                3 => Items.OrderBy(i => i.CreatedAt).ToList(),
                _ => null
            };

            if (sorted == null) return;

            for (int i = 0; i < sorted.Count; i++)
            {
                var oldIndex = Items.IndexOf(sorted[i]);
                if (oldIndex != i)
                    Items.Move(oldIndex, i);
            }
        });
    }

    private void UpdateAll()
    {
        foreach(var item in Items)
            item.UpdateInfo();
    }

    public void EnterLiveViewOverride()
    {
        _liveViewOverrideCount++;
        if (_liveViewOverrideCount != 1)
            return;

        ResumeAllLiveViews();
        OnPropertyChanged(nameof(IsLiveViewOverrideActive));
    }

    public void LeaveLiveViewOverride()
    {
        if (_liveViewOverrideCount <= 0)
            return;

        _liveViewOverrideCount--;
        if (_liveViewOverrideCount != 0)
            return;

        RestoreActiveTabLiveViewOnly();
        OnPropertyChanged(nameof(IsLiveViewOverrideActive));
    }

    private static void ResumeAllLiveViews()
    {
        foreach (var processor in MaaProcessor.Processors.ToList())
            processor.ViewModel?.ResumeLiveView();
    }

    public static void RestoreActiveTabLiveViewOnly()
    {
        var activeViewModel = Instances.InstanceTabBarViewModel.ActiveTab?.TaskQueueViewModel;

        foreach (var processor in MaaProcessor.Processors.ToList())
        {
            var viewModel = processor.ViewModel;
            if (viewModel == null)
                continue;

            if (ReferenceEquals(viewModel, activeViewModel))
                viewModel.ResumeLiveView();
            else
                viewModel.PauseLiveView();
        }
    }

    [RelayCommand]
    private void ZoomIn()
    {
        var next = Math.Round(CardScale + CardScaleStep, 2);
        CardScale = Math.Clamp(next, MinCardScale, MaxCardScale);
    }

    [RelayCommand]
    private void ZoomOut()
    {
        var next = Math.Round(CardScale - CardScaleStep, 2);
        CardScale = Math.Clamp(next, MinCardScale, MaxCardScale);
    }

    public void Dispose()
    {
        LeaveLiveViewOverride();
        _timer.Stop();
        MaaProcessor.Processors.CollectionChanged -= Processors_CollectionChanged;
        foreach(var item in Items) item.Dispose();
        Items.Clear();
        GC.SuppressFinalize(this);
    }
}
