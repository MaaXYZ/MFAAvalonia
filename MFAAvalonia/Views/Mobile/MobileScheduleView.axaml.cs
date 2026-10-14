using Avalonia.Controls;
using MFAAvalonia.Helper;
using MFAAvalonia.ViewModels.Other;

namespace MFAAvalonia.Views.Mobile;

public partial class MobileScheduleView : UserControl
{
    public MobileScheduleView()
    {
        InitializeComponent();
        DataContext = TimerModel.Instance;
        TimerModel.Instance.RefreshInstanceList();
        UpdateLanguage();
        LanguageHelper.LanguageChanged += OnLanguageChanged;
        MobileInstanceCoordinator.CurrentChanged += OnInstancesChanged;
        DetachedFromVisualTree += (_, _) =>
        {
            LanguageHelper.LanguageChanged -= OnLanguageChanged;
            MobileInstanceCoordinator.CurrentChanged -= OnInstancesChanged;
        };
    }

    private void OnLanguageChanged(object? sender, LanguageHelper.LanguageEventArgs e) => UpdateLanguage();
    private void OnInstancesChanged(object? sender, System.EventArgs e) => TimerModel.Instance.RefreshInstanceList();

    private void UpdateLanguage()
    {
        PageTitle.Text = MobileLocalization.Get("Schedule");
        PageDescription.Text = MobileLocalization.Get("ScheduleDescription");
        ForceStartTitle.Text = MobileLocalization.Get("ForceScheduledStart");
        ForceStartDescription.Text = MobileLocalization.Get("ForceScheduledStartDescription");
    }
}
