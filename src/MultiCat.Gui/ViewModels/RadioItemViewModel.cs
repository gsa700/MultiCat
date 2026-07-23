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

    public ObservableCollection<ClientPortViewModel> Ports { get; init; } = [];

    public ObservableCollection<TrafficEntry> Traffic { get; init; } = [];

    public string[] FlowPortLabels => [.. Ports.Select(p => p.FlowLabel)];

    public string[] RigModels { get; init; } = ["Elecraft K3"];

    public string[] ConnectionKinds { get; } = ["Serial (COM)", "Network (TCP)"];

    public string[] PortChoices { get; init; } = ["COM7"];

    public string[] BaudRates { get; } = ["4800", "9600", "19200", "38400", "115200"];

    public int SelectedRigModel { get; set; }

    public int SelectedConnectionKind { get; set; }

    public int SelectedPortChoice { get; set; }

    public int SelectedBaudRate { get; set; } = 3;
}
