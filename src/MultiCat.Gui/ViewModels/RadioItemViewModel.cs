using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MultiCat.Gui.ViewModels;

public partial class RadioItemViewModel : ViewModelBase
{
    public required string Name { get; init; }

    // Observable, not init-only: a radio can drop and recover (rig powered off,
    // PC standby) long after the list was built, and the sidebar must follow.
    [ObservableProperty]
    public partial string ConnectionSummary { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsConnected { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = "idle";

    [ObservableProperty]
    public partial bool IsTransmitting { get; set; }

    /// <summary>When the last activity event arrived — drives the CAT LED flash.</summary>
    [ObservableProperty]
    public partial DateTime? LastActivityAt { get; set; }

    /// <summary>"quiet · last traffic …" note shown in the monitor when idle; empty
    /// while traffic is flowing. Updated by the main view model's status timer.</summary>
    [ObservableProperty]
    public partial string QuietNote { get; set; } = string.Empty;

    /// <summary>Raised per real activity event to pulse the signal-flow diagram.
    /// link 0 = radio↔hub, link N = the Nth client port; towardRadio picks direction
    /// (amber command toward the radio/hub end, teal response toward the far end).</summary>
    public event Action<int, bool>? PulseRequested;

    public void Pulse(int link, bool towardRadio) => PulseRequested?.Invoke(link, towardRadio);

    // --- the VFO picture, laid out the way a radio's own display reads:
    //     VFO A   <-TX / TX->   VFO B, with SPLIT called out when it is on.
    [ObservableProperty]
    public partial long VfoAHz { get; set; }

    [ObservableProperty]
    public partial long VfoBHz { get; set; }

    [ObservableProperty]
    public partial bool Split { get; set; }

    [ObservableProperty]
    public partial bool TransmitOnVfoB { get; set; }

    [ObservableProperty]
    public partial string ModeA { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ModeB { get; set; } = string.Empty;

    /// <summary>VFO B's mode is not always knowable, so its slot is left empty
    /// rather than echoing VFO A's and implying something we did not read.</summary>
    public bool ShowModeB => ModeB.Length > 0;

    partial void OnModeBChanged(string value) => OnPropertyChanged(nameof(ShowModeB));

    public static string FormatVfo(long hz) => hz > 0 ? $"{hz / 1000.0:N2}" : "—";

    public string VfoAText => FormatVfo(VfoAHz);

    public string VfoBText => FormatVfo(VfoBHz);

    /// <summary>Arrow pointing at the VFO that will transmit — lit on that side only.</summary>
    public string TransmitArrowLeft => TransmitOnVfoB ? " " : "◀";

    public string TransmitArrowRight => TransmitOnVfoB ? "▶" : " ";

    /// <summary>Dims the VFO that is not transmitting, so the live one reads first.</summary>
    public bool VfoAIsTransmit => !TransmitOnVfoB;

    public bool VfoBIsTransmit => TransmitOnVfoB;

    /// <summary>VFO B is not always knowable — over rigctld it is only visible while
    /// split is on — so the panel hides it rather than showing a false dash.</summary>
    public bool ShowVfoB => VfoBHz > 0 || Split;

    partial void OnVfoAHzChanged(long value) => OnPropertyChanged(nameof(VfoAText));

    partial void OnVfoBHzChanged(long value)
    {
        OnPropertyChanged(nameof(VfoBText));
        OnPropertyChanged(nameof(ShowVfoB));
    }

    partial void OnSplitChanged(bool value) => OnPropertyChanged(nameof(ShowVfoB));

    partial void OnTransmitOnVfoBChanged(bool value)
    {
        OnPropertyChanged(nameof(TransmitArrowLeft));
        OnPropertyChanged(nameof(TransmitArrowRight));
        OnPropertyChanged(nameof(VfoAIsTransmit));
        OnPropertyChanged(nameof(VfoBIsTransmit));
    }

    public long? LastFrequencyHz { get; set; }

    public string? LastMode { get; set; }

    public ObservableCollection<ClientPortViewModel> Ports { get; init; } = [];

    /// <summary>Live apps connected to this radio's rigctld port(s) — one bubble each
    /// in the signal-flow diagram. Reconciled from the service on the status timer.</summary>
    public ObservableCollection<ClientConnectionViewModel> Clients { get; init; } = [];

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
