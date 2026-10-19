using MFAAvalonia.Features.CombatStrategies;
using MFAAvalonia.Helper;
using System.IO;

namespace MFAAvalonia.ViewModels.Pages;

public sealed class CombatStrategyViewModel : ViewModelBase
{
    public StrategyEditorSession Session { get; } = new(new StrategyStore(Path.Combine(AppPaths.ConfigDirectory, "strategies")));
}
