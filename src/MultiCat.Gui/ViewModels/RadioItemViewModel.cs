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

    // Increments on every real activity event so the signal-flow diagram can pulse
    // in step with actual traffic (not a decorative timer).
    [ObservableProperty]
    public partial long ActivityTick { get; set; }

    public bool LastActivityTowardRadio { get; private set; }

    public void RegisterActivity(bool towardRadio)
    {
        LastActivityTowardRadio = towardRadio;
        ActivityTick++;
    }

    public long? LastFrequencyHz { get; set; }

    public string? LastMode { get; set; }

    public ObservableCollection<ClientPortViewModel> Ports { get; init; } = [];

    public ObservableCollection<TrafficEntry> Traffic { get; init; } = [];

    public string[] FlowPortLabels => [.. Ports.Select(p => p.FlowLabel)];

    /// <summary>Call after mutating Ports so the signal-flow diagram re-reads its labels.</summary>
    public void OnPortsChanged() => OnPropertyChanged(nameof(FlowPortLabels));

    // Read-only display of the radio's actual configuration (set in ToViewModel).
    public string Protocol { get; init; } = "Kenwood";

    public string Connection { get; init; } = "Serial";

    public string ComPort { get; init; } = string.Empty;

    public int BaudRate { get; init; } = 38400;

    public string Host { get; init; } = string.Empty;

    public int TcpPort { get; init; }

    public bool IsSimulator => Connection == "Simulator";

    public bool IsNetwork => Connection.Equals("Tcp", StringComparison.OrdinalIgnoreCase);

    public bool IsSerial => !IsSimulator && !IsNetwork;

    public string ProtocolText => Protocol.Equals("IcomCiv", StringComparison.OrdinalIgnoreCase)
        ? "Icom CI-V"
        : "Kenwood / Elecraft";

    public string ConnectionText => Connection switch
    {
        "Simulator" => "Simulator (no hardware)",
        "Tcp" => "Network (TCP/IP)",
        _ => "Serial (COM port)",
    };

    public string AddressText => $"{Host}:{TcpPort}";

    public string BaudText => BaudRate.ToString();
}
