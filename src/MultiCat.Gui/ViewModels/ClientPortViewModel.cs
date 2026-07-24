using CommunityToolkit.Mvvm.ComponentModel;

namespace MultiCat.Gui.ViewModels;

public partial class ClientPortViewModel : ViewModelBase
{
    public required string PortDisplay { get; init; }

    public required string Label { get; init; }

    public required string Ptt { get; init; }

    // Status and active state change as clients connect/disconnect, so they refresh live.
    [ObservableProperty]
    public partial string Status { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsActive { get; set; }

    /// <summary>Short label used by the signal-flow diagram node.</summary>
    public string FlowLabel => Label.Length > 0 && Label != "spare" ? Label : PortDisplay;
}
