using Avalonia.Controls;
using System.Linq;
using MFAAvalonia.Extensions.MaaFW;
using MFAAvalonia.Helper;
using MFAAvalonia.ViewModels.Other;

namespace MFAAvalonia.Views.Mobile;

public partial class MobileHomeView : UserControl
{
    private readonly TimerModel _timerModel = TimerModel.Instance;
    private bool _refreshingInstances;

    public MobileHomeView()
    {
        InitializeComponent();
        UpdateInstance();
        MobileInstanceCoordinator.CurrentChanged += OnCurrentInstanceChanged;
        UpdateLanguage();
        LanguageHelper.LanguageChanged += OnLanguageChanged;
        DetachedFromVisualTree += (_, _) =>
        {
            LanguageHelper.LanguageChanged -= OnLanguageChanged;
            MobileInstanceCoordinator.CurrentChanged -= OnCurrentInstanceChanged;
        };
    }

    private void OnCurrentInstanceChanged(object? sender, System.EventArgs e) => UpdateInstance();

    private void UpdateInstance()
    {
        var manager = MaaProcessorManager.Instance;
        DataContext = manager.GetViewModel(manager.Current.InstanceId);
        _refreshingInstances = true;
        _timerModel.RefreshInstanceList();
        HomeInstanceSelector.ItemsSource = _timerModel.InstanceList;
        HomeInstanceSelector.SelectedItem = _timerModel.InstanceList
            .FirstOrDefault(entry => entry.InstanceId == manager.Current.InstanceId);
        _refreshingInstances = false;
    }

    private void OnInstanceChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_refreshingInstances || HomeInstanceSelector.SelectedItem is not TimerModel.InstanceEntry entry)
            return;

        if (!MobileInstanceCoordinator.TrySwitch(entry.InstanceId))
            UpdateInstance();
    }

    private void OnLanguageChanged(object? sender, LanguageHelper.LanguageEventArgs e) => UpdateLanguage();

    private void UpdateLanguage()
    {
        Instances.RootViewModel.RefreshApplicationDisplayName();
        CurrentConfigurationText.Text = MobileLocalization.Get("CurrentConfiguration");
        SingleInstanceText.Text = MobileLocalization.Get("SingleActiveConfiguration");
        // CurrentTask is a formatted Desktop string (for example "任务: {0}"). The mobile
        // card renders the label and task value in separate controls, so consume the placeholder
        // here instead of exposing it literally in the label.
        CurrentTaskLabel.Text = MobileLocalization.Get("CurrentTask")
            .Replace("{0}", string.Empty, System.StringComparison.Ordinal)
            .TrimEnd(' ', ':', '：');
        if (string.IsNullOrWhiteSpace(CurrentTaskValue.Text))
            CurrentTaskValue.Text = MobileLocalization.Get("Idle");
        StartStopText.Text = MobileLocalization.Get("StartStop");
    }
}
