using CommunityToolkit.Mvvm.ComponentModel;

namespace MultiCat.Gui.ViewModels;

/// <summary>One live app connected to a radio (a bubble in the signal-flow diagram).</summary>
public partial class ClientConnectionViewModel : ViewModelBase
{
    public required string ProcessName { get; init; }

    public required int ConnectionId { get; init; }

    /// <summary>Nickname if set, else a friendly default, else the process name.</summary>
    [ObservableProperty]
    public partial string DisplayName { get; set; } = string.Empty;

    /// <summary>Stable key for reconciling the live list across refreshes.</summary>
    public string Key => $"{ProcessName}:{ConnectionId}";
}
