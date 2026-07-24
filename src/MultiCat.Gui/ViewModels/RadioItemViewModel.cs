using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MultiCat.Gui.ViewModels;

public partial class RadioItemViewModel : ViewModelBase
{
    public required string Name { get; init; }

    public required string ConnectionSummary { get; init; }

    public bool IsConnected { get; init; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = "idle";

    [ObservableProperty]
    public partial bool IsTransmitting { get; set; }

    public long? LastFrequencyHz { get; set; }

    public string? LastMode { get; set; }

    public ObservableCollection<ClientPortViewModel> Ports { get; init; } = [];

    public ObservableCollection<TrafficEntry> Traffic { get; init; } = [];

    public string[] FlowPortLabels => [.. Ports.Select(p => p.FlowLabel)];

    /// <summary>Call after mutating Ports so the signal-flow diagram re-reads its labels.</summary>
    public void OnPortsChanged() => OnPropertyChanged(nameof(FlowPortLabels));

    public string[] RigModels { get; init; } = Services.RigList.DisplayNames;

    public string[] ConnectionKinds { get; } = ["Serial (COM)", "Network (TCP)"];

    public string[] PortChoices { get; init; } = ["COM7"];

    public string[] BaudRates { get; } = ["4800", "9600", "19200", "38400", "115200"];

    public int SelectedRigModel { get; set; }

    public int SelectedConnectionKind { get; set; }

    public int SelectedPortChoice { get; set; }

    public int SelectedBaudRate { get; set; } = 3;
}
