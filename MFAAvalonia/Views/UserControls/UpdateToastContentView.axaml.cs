using Avalonia.Controls;

namespace MFAAvalonia.Views.UserControls;

public partial class UpdateToastContentView : UserControl
{
    public UpdateToastContentView()
    {
        InitializeComponent();
    }

    public UpdateToastContentView(string markdown)
        : this()
    {
        DataContext = markdown;
    }
}
