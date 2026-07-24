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

    // Separate counters per direction so the signal-flow diagram pulses in step
    // with actual traffic on the radio↔hub link: commands flow toward the radio,
    // responses flow back toward the hub. Two counters (not a value + direction)
    // so each carries its own change notification and there's no ordering race.
    [ObservableProperty]
    public partial long ToRadioTick { get; set; }

    [ObservableProperty]
    public partial long FromRadioTick { get; set; }

    public void RegisterActivity(bool fromRadio)
    {
        if (fromRadio)
        {
            FromRadioTick++;
        }
        else
        {
            ToRadioTick++;
        }
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
