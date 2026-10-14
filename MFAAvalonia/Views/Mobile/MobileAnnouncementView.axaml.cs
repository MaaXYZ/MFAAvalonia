using Avalonia.Controls;
using MFAAvalonia.ViewModels.Windows;

namespace MFAAvalonia.Views.Mobile;

public partial class MobileAnnouncementView : UserControl
{
    public MobileAnnouncementView()
    {
        InitializeComponent();
    }

    public MobileAnnouncementView(AnnouncementViewModel viewModel)
        : this()
    {
        DataContext = viewModel;
        DetachedFromVisualTree += (_, _) =>
        {
            Viewer?.Dispose();
            viewModel.Cleanup();
        };
    }
}
