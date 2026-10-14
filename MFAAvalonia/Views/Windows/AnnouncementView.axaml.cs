using Avalonia.Controls;
using Avalonia.Interactivity;
using MFAAvalonia.ViewModels.Windows;
using SukiUI.Controls;

namespace MFAAvalonia.Views.Windows;

public partial class AnnouncementView : SukiWindow
{
    public AnnouncementView()
    {
        InitializeComponent();
    }
    
    private void Close(object sender, RoutedEventArgs e) => Close();

        protected override void OnClosing(WindowClosingEventArgs e)
        {
            base.OnClosing(e);
            
            // 清理 ViewModel
            if (DataContext is AnnouncementViewModel viewModel)
            {
                viewModel.Cleanup();
                viewModel.SetView(null);
            }
            
            // 显式释放 MarkdownScrollViewer 资源（调用 Dispose 会同时调用 Cleanup）
            if (Viewer != null)
            {
                Viewer.Dispose();
            }
            
            // 清空 DataContext 以断开绑定
            DataContext = null;
        }
}

