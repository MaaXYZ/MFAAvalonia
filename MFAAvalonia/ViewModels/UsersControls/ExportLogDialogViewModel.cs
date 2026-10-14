using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MFAAvalonia.Extensions;
using MFAAvalonia.Helper;
using MFAAvalonia.ViewModels;
using SukiUI.Dialogs;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace MFAAvalonia.ViewModels.UsersControls;

public sealed partial class ExportLogDialogViewModel : ViewModelBase
{
    private readonly ISukiDialog _dialog;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartExportCommand))]
    private bool _includeMaaLog = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartExportCommand))]
    private bool _includeGuiLog = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartExportCommand))]
    private bool _includeCustomLog = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartExportCommand))]
    private bool _includeOnErrorImages = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartExportCommand))]
    private bool _includeVisionImages;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartExportCommand))]
    private bool _includeOtherImages = true;

    [ObservableProperty] private ExportLogTimeRangeOption _selectedOnErrorImageTimeRange;
    [ObservableProperty] private ExportLogTimeRangeOption _selectedVisionImageTimeRange;
    [ObservableProperty] private ExportLogTimeRangeOption _selectedOtherImageTimeRange;
    [ObservableProperty] private bool _isExporting;

    public IReadOnlyList<ExportLogTimeRangeOption> ImageTimeRangeOptions { get; }

    public string MaaLogLabel => "maa.log";
    public string GuiLogLabel => "gui.log";
    public string CustomLogLabel => "custom.log";
    public string OnErrorFolderLabel => "on_error";
    public string VisionFolderLabel => "vision";
    public string StartExportLabel => IsExporting
        ? LangKeys.ExportLogInProgress.ToLocalization()
        : LangKeys.ExportLogDialogStart.ToLocalization();

    public bool CanStartExport =>
        !IsExporting && (IncludeMaaLog || IncludeGuiLog || IncludeCustomLog || IncludeOnErrorImages || IncludeVisionImages || IncludeOtherImages);

    public ExportLogDialogViewModel(ISukiDialog dialog)
    {
        _dialog = dialog;

        ImageTimeRangeOptions =
        [
            new ExportLogTimeRangeOption(ExportLogTimeRange.All, LangKeys.AllFilter.ToLocalization()),
            new ExportLogTimeRangeOption(ExportLogTimeRange.Last24Hours, LangKeys.ExportLogTimeRangeLast24Hours.ToLocalization()),
            new ExportLogTimeRangeOption(ExportLogTimeRange.Last3Days, LangKeys.ExportLogTimeRangeLast3Days.ToLocalization()),
            new ExportLogTimeRangeOption(ExportLogTimeRange.Last7Days, LangKeys.ExportLogTimeRangeLast7Days.ToLocalization())
        ];

        _selectedOnErrorImageTimeRange = ImageTimeRangeOptions[0];
        _selectedVisionImageTimeRange = ImageTimeRangeOptions[0];
        _selectedOtherImageTimeRange = ImageTimeRangeOptions[0];
    }

    partial void OnIsExportingChanged(bool value) => OnPropertyChanged(nameof(StartExportLabel));

    [RelayCommand(CanExecute = nameof(CanStartExport))]
    private async Task StartExport()
    {
        if (IsExporting)
            return;

        IsExporting = true;
        StartExportCommand.NotifyCanExecuteChanged();

        try
        {
            var result = await FileLogExporter.CompressRecentLogs(Instances.StorageProvider, BuildOptions());
            if (result == ExportLogResult.Success)
            {
                _dialog.Dismiss();
            }
        }
        finally
        {
            IsExporting = false;
            StartExportCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand]
    private void Close()
    {
        if (!IsExporting)
            _dialog.Dismiss();
    }

    private ExportLogPackageOptions BuildOptions()
    {
        return new ExportLogPackageOptions
        {
            IncludeMaaLog = IncludeMaaLog,
            IncludeGuiLog = IncludeGuiLog,
            IncludeCustomLog = IncludeCustomLog,
            IncludeOnErrorImages = IncludeOnErrorImages,
            IncludeVisionImages = IncludeVisionImages,
            IncludeOtherImages = IncludeOtherImages,
            OnErrorImageTimeRange = SelectedOnErrorImageTimeRange.Value,
            VisionImageTimeRange = SelectedVisionImageTimeRange.Value,
            OtherImageTimeRange = SelectedOtherImageTimeRange.Value
        };
    }
}

public sealed class ExportLogTimeRangeOption(ExportLogTimeRange value, string displayName)
{
    public ExportLogTimeRange Value { get; } = value;
    public string DisplayName { get; } = displayName;
}
