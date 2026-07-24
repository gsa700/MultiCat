using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MultiCat.Contracts;
using MultiCat.Gui.Services;
using MultiCat.Hamlib;

namespace MultiCat.Gui.ViewModels;

/// <summary>Backs the add/edit-radio dialog. Produces a RadioConfig for SaveRadio.</summary>
public partial class RadioEditorViewModel : ViewModelBase
{
    private const string NoRigModel = "— pick to prefill from hamlib —";
    private readonly List<RadioClientPort> _preservedPorts;

    public RadioEditorViewModel(RadioConfig? existing, IEnumerable<string> comPorts)
    {
        OriginalName = existing?.Name;
        Title = existing is null ? "Add radio" : $"Edit {existing.Name}";

        ComPorts = [.. comPorts];
        RigModels = [NoRigModel, .. RigList.DisplayNames];

        if (existing is null)
        {
            Name = string.Empty;
            IsSimulator = false;
            SelectedConnectionIndex = 0;
            SelectedProtocolIndex = 0;
            ComPort = ComPorts.FirstOrDefault() ?? string.Empty;
            BaudRate = "38400";
            Host = string.Empty;
            TcpPort = "9200";
            ExposeRigctld = true;
            RigctldPort = "4532";
            ExposeRawTcp = false;
            RawTcpPort = "4533";
            _preservedPorts = [];
        }
        else
        {
            Name = existing.Name;
            IsSimulator = existing.Simulator;
            SelectedConnectionIndex = existing.Connection.Equals("Tcp", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            SelectedProtocolIndex = existing.Protocol.Equals("IcomCiv", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            ComPort = existing.ComPort;
            BaudRate = existing.BaudRate > 0 ? existing.BaudRate.ToString() : "38400";
            Host = existing.Host;
            TcpPort = existing.TcpPort > 0 ? existing.TcpPort.ToString() : "9200";

            var rigctld = existing.ClientPorts.FirstOrDefault(p => p.RigctldPort > 0);
            ExposeRigctld = rigctld is not null;
            RigctldPort = (rigctld?.RigctldPort ?? 4532).ToString();

            var raw = existing.ClientPorts.FirstOrDefault(p => p.TcpPort > 0);
            ExposeRawTcp = raw is not null;
            RawTcpPort = (raw?.TcpPort ?? 4533).ToString();

            // Virtual COM ports are managed by the Add-port flow; carry them through untouched.
            _preservedPorts = [.. existing.ClientPorts.Where(p => p.MuxPort.Length > 0)];
        }
    }

    public string Title { get; }

    public string? OriginalName { get; }

    public ObservableCollection<string> ComPorts { get; }

    public string[] RigModels { get; }

    public string[] Protocols { get; } = ["Kenwood / Elecraft / Yaesu (ASCII)", "Icom CI-V"];

    public string[] ConnectionKinds { get; } = ["Serial (COM port)", "Network (TCP/IP)"];

    public string[] BaudRates { get; } = ["1200", "4800", "9600", "19200", "38400", "57600", "115200"];

    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsSimulator { get; set; }

    [ObservableProperty]
    public partial int SelectedConnectionIndex { get; set; }

    [ObservableProperty]
    public partial string Host { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string TcpPort { get; set; } = "9200";

    [ObservableProperty]
    public partial int SelectedProtocolIndex { get; set; }

    // Visibility helpers for the editor: simulator hides all connection details;
    // otherwise the serial or network fields show depending on the connection kind.
    public bool ConnectionVisible => !IsSimulator;

    public bool ShowSerial => !IsSimulator && SelectedConnectionIndex == 0;

    public bool ShowNetwork => !IsSimulator && SelectedConnectionIndex == 1;

    partial void OnIsSimulatorChanged(bool value) => RaiseVisibility();

    partial void OnSelectedConnectionIndexChanged(int value) => RaiseVisibility();

    private void RaiseVisibility()
    {
        OnPropertyChanged(nameof(ConnectionVisible));
        OnPropertyChanged(nameof(ShowSerial));
        OnPropertyChanged(nameof(ShowNetwork));
    }

    [ObservableProperty]
    public partial int SelectedRigModelIndex { get; set; }

    [ObservableProperty]
    public partial string ComPort { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string BaudRate { get; set; } = "38400";

    [ObservableProperty]
    public partial bool ExposeRigctld { get; set; } = true;

    [ObservableProperty]
    public partial string RigctldPort { get; set; } = "4532";

    [ObservableProperty]
    public partial bool ExposeRawTcp { get; set; }

    [ObservableProperty]
    public partial string RawTcpPort { get; set; } = "4533";

    [ObservableProperty]
    public partial string? ErrorText { get; set; }

    // Picking a rig model prefills protocol, baud, and (if blank) the name.
    partial void OnSelectedRigModelIndexChanged(int value)
    {
        if (value <= 0 || value - 1 >= RigList.DisplayNames.Length)
        {
            return;
        }

        var display = RigList.DisplayNames[value - 1];
        var model = RigDatabase.All.FirstOrDefault(r => r.DisplayName == display);
        if (model is null)
        {
            return;
        }

        SelectedProtocolIndex = model.Manufacturer.Equals("Icom", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        if (model.SerialSpeedMax > 0)
        {
            BaudRate = model.SerialSpeedMax.ToString();
        }

        if (string.IsNullOrWhiteSpace(Name))
        {
            Name = model.DisplayName;
        }
    }

    /// <summary>Validates and builds the config, or returns null and sets ErrorText.</summary>
    public RadioConfig? Build()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            ErrorText = "Enter a name for this radio.";
            return null;
        }

        var isNetwork = !IsSimulator && SelectedConnectionIndex == 1;

        if (isNetwork && string.IsNullOrWhiteSpace(Host))
        {
            ErrorText = "Enter the radio's hostname or IP address (e.g. 192.168.1.40 or K4-SN1234.local).";
            return null;
        }

        if (isNetwork && !IsValidPort(TcpPort))
        {
            ErrorText = "The radio's TCP port must be a number between 1 and 65535 (Elecraft K4 uses 9200).";
            return null;
        }

        if (!IsSimulator && !isNetwork && string.IsNullOrWhiteSpace(ComPort))
        {
            ErrorText = "Choose the radio's COM port, or switch to Network / Simulator.";
            return null;
        }

        if (ExposeRigctld && !IsValidPort(RigctldPort))
        {
            ErrorText = "rigctld port must be a number between 1 and 65535.";
            return null;
        }

        if (ExposeRawTcp && !IsValidPort(RawTcpPort))
        {
            ErrorText = "Raw CAT port must be a number between 1 and 65535.";
            return null;
        }

        var config = new RadioConfig
        {
            Name = Name.Trim(),
            Protocol = SelectedProtocolIndex == 1 ? "IcomCiv" : "Kenwood",
            Simulator = IsSimulator,
            Connection = isNetwork ? "Tcp" : "Serial",
            ComPort = isNetwork || IsSimulator ? string.Empty : ComPort.Trim(),
            BaudRate = int.TryParse(BaudRate, out var baud) ? baud : 38400,
            Host = isNetwork ? Host.Trim() : string.Empty,
            TcpPort = isNetwork ? int.Parse(TcpPort) : 0,
        };

        if (ExposeRigctld)
        {
            config.ClientPorts.Add(new RadioClientPort
            {
                PortDisplay = $"TCP {RigctldPort}", Label = "rigctld (WSJT-X, fldigi)",
                Ptt = "via CAT", RigctldPort = int.Parse(RigctldPort),
            });
        }

        if (ExposeRawTcp)
        {
            config.ClientPorts.Add(new RadioClientPort
            {
                PortDisplay = $"TCP {RawTcpPort}", Label = "raw CAT over TCP",
                Ptt = "via CAT", TcpPort = int.Parse(RawTcpPort),
            });
        }

        config.ClientPorts.Add(_preservedPorts);
        ErrorText = null;
        return config;
    }

    private static bool IsValidPort(string text) => int.TryParse(text, out var port) && port is > 0 and <= 65535;
}
