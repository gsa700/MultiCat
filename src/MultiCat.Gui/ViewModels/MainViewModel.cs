using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Grpc.Core;
using MultiCat.Contracts;
using MultiCat.Gui.Services;

namespace MultiCat.Gui.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private ServiceConnection? _connection;
    private CancellationTokenSource? _streamCts;
    private readonly Lock _captureLock = new();
    private StreamWriter? _captureWriter;

    public MainViewModel()
    {
        Radios = [];
        if (Design.IsDesignMode)
        {
            LoadDemoData();
            return;
        }

        _ = ConnectAsync();
    }

    public ObservableCollection<RadioItemViewModel> Radios { get; }

    [ObservableProperty]
    public partial RadioItemViewModel? SelectedRadio { get; set; }

    [ObservableProperty]
    public partial string ServiceStatus { get; set; } = "connecting to service…";

    [ObservableProperty]
    public partial string DriverStatus { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsLive { get; set; }

    [ObservableProperty]
    public partial bool IsCapturing { get; set; }

    [ObservableProperty]
    public partial string CaptureLabel { get; set; } = "Start capture";

    [ObservableProperty]
    public partial string CaptureStatus { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? LastCapturePath { get; set; }

    private string? _capturePath;

    [RelayCommand]
    private void ToggleCapture()
    {
        if (IsCapturing)
        {
            StopCapture("capture saved");
            return;
        }

        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "MultiCAT-logs");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"traffic-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            var writer = new StreamWriter(path, append: false) { AutoFlush = true };
            writer.WriteLine($"# MultiCAT traffic capture started {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            writer.WriteLine("# time  radio  kind  client  frame  note");
            lock (_captureLock)
            {
                _captureWriter = writer;
            }

            _capturePath = path;
            LastCapturePath = null; // hide the View button until this one is stopped
            IsCapturing = true;
            CaptureLabel = "Stop capture";
            CaptureStatus = $"capturing → {path}";
        }
        catch (Exception ex)
        {
            CaptureStatus = $"capture failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void ViewCapture()
    {
        if (LastCapturePath is null || !File.Exists(LastCapturePath))
        {
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(LastCapturePath)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            CaptureStatus = $"couldn't open log: {ex.Message}";
        }
    }

    private void StopCapture(string message)
    {
        var wasCapturing = _captureWriter is not null;
        lock (_captureLock)
        {
            _captureWriter?.WriteLine($"# stopped {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            _captureWriter?.Dispose();
            _captureWriter = null;
        }

        IsCapturing = false;
        CaptureLabel = "Start capture";
        if (wasCapturing && _capturePath is not null)
        {
            LastCapturePath = _capturePath;
        }

        if (message.Length > 0)
        {
            CaptureStatus = message;
        }
    }

    private void CaptureEvent(ActivityEvent evt)
    {
        lock (_captureLock)
        {
            if (_captureWriter is null)
            {
                return;
            }

            var extra = string.Empty;
            if (evt.FrequencyHz > 0) extra += $"  freq={evt.FrequencyHz}";
            if (evt.Mode.Length > 0) extra += $"  mode={evt.Mode}";
            if (evt.Ptt.Length > 0) extra += $"  ptt={evt.Ptt}";
            _captureWriter.WriteLine(
                $"{evt.Time}  {evt.Radio}  {evt.Kind}  {(evt.ClientId.Length > 0 ? evt.ClientId : "-")}  {evt.Frame}  {evt.Note}{extra}");
        }
    }

    [RelayCommand]
    private async Task AddPortAsync()
    {
        if (_connection is null || SelectedRadio is null)
        {
            ServiceStatus = "service offline — cannot add ports";
            return;
        }

        try
        {
            ServiceStatus = "creating virtual port…";
            var reply = await _connection.Client.AddClientPortAsync(
                new AddClientPortRequest { Radio = SelectedRadio.Name },
                deadline: DateTime.UtcNow.AddSeconds(60));

            ServiceStatus = reply.Message;
            if (reply.Ok)
            {
                SelectedRadio.Ports.Add(new ClientPortViewModel
                {
                    PortDisplay = reply.PortDisplay,
                    Label = reply.PortDisplay,
                    Ptt = "CAT only",
                    Status = "active",
                    IsActive = true,
                });
                SelectedRadio.OnPortsChanged();
            }
        }
        catch (Exception ex)
        {
            ServiceStatus = $"add port failed: {ex.Message}";
        }
    }

    /// <summary>True when connected to a live service (radio editing is possible).</summary>
    public bool CanEdit => _connection is not null;

    public async Task<string[]> GetComPortsAsync()
    {
        if (_connection is null)
        {
            return [];
        }

        var reply = await _connection.Client.ListComPortsAsync(new ListComPortsRequest());
        return [.. reply.Ports];
    }

    public async Task<RadioConfig?> GetConfigAsync(string radioName)
    {
        if (_connection is null)
        {
            return null;
        }

        var configs = await _connection.Client.GetRadioConfigsAsync(new GetRadioConfigsRequest());
        return configs.Radios.FirstOrDefault(r => r.Name == radioName);
    }

    public async Task<(bool Ok, string Message)> SaveRadioAsync(SaveRadioRequest request)
    {
        if (_connection is null)
        {
            return (false, "service offline");
        }

        try
        {
            var reply = await _connection.Client.SaveRadioAsync(request, deadline: DateTime.UtcNow.AddSeconds(30));
            ServiceStatus = reply.Message;
            if (reply.Ok)
            {
                await ReloadRadiosAsync(request.Radio.Name);
            }

            return (reply.Ok, reply.Message);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public async Task<(bool Ok, string Message)> DeleteRadioAsync(string radioName)
    {
        if (_connection is null)
        {
            return (false, "service offline");
        }

        try
        {
            var reply = await _connection.Client.DeleteRadioAsync(
                new DeleteRadioRequest { Name = radioName }, deadline: DateTime.UtcNow.AddSeconds(30));
            ServiceStatus = reply.Message;
            if (reply.Ok)
            {
                await ReloadRadiosAsync(null);
            }

            return (reply.Ok, reply.Message);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private async Task ReloadRadiosAsync(string? selectByName)
    {
        if (_connection is null)
        {
            return;
        }

        var wanted = selectByName ?? SelectedRadio?.Name;
        var radioList = await _connection.Client.GetRadiosAsync(new GetRadiosRequest());
        Radios.Clear();
        foreach (var radio in radioList.Radios)
        {
            Radios.Add(ToViewModel(radio));
        }

        SelectedRadio = Radios.FirstOrDefault(r => r.Name == wanted) ?? Radios.FirstOrDefault();
    }

    private async Task ConnectAsync()
    {
        try
        {
            _connection = new ServiceConnection();
            var radioList = await _connection.Client.GetRadiosAsync(
                new GetRadiosRequest(), deadline: DateTime.UtcNow.AddSeconds(3));

            foreach (var radio in radioList.Radios)
            {
                Radios.Add(ToViewModel(radio));
            }

            SelectedRadio = Radios.FirstOrDefault();
            IsLive = true;
            ServiceStatus = "service connected";

            var driver = await _connection.Client.GetDriverStateAsync(
                new GetDriverStateRequest(), deadline: DateTime.UtcNow.AddSeconds(3));
            DriverStatus = driver.Installed ? "virtual COM driver ready" : "virtual COM driver not installed";

            _streamCts = new CancellationTokenSource();
            _ = PumpActivityAsync(_streamCts.Token);
        }
        catch (Exception)
        {
            _connection?.Dispose();
            _connection = null;
            ServiceStatus = "service offline · demo data";
            LoadDemoData();
        }
    }

    private async Task PumpActivityAsync(CancellationToken ct)
    {
        try
        {
            using var call = _connection!.Client.StreamActivity(new StreamActivityRequest(), cancellationToken: ct);
            await foreach (var evt in call.ResponseStream.ReadAllAsync(ct))
            {
                CaptureEvent(evt);
                await Dispatcher.UIThread.InvokeAsync(() => ApplyActivity(evt));
            }
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                IsLive = false;
                ServiceStatus = "service connection lost";
            });
        }
    }

    private void ApplyActivity(ActivityEvent evt)
    {
        var radio = Radios.FirstOrDefault(r => r.Name == evt.Radio);
        if (radio is null)
        {
            return;
        }

        var fromRadio = evt.Kind is "ResponseReceived" or "Unsolicited";
        var reachedRadio = evt.Kind is "CommandSent" or "SetSent" or "ResponseReceived" or "Unsolicited";
        if (reachedRadio)
        {
            // Radio↔hub link: command toward radio (amber), response back to hub (teal).
            radio.Pulse(0, towardRadio: !fromRadio);
        }

        // Client link: pulse the port that this event belongs to, if any. Cache hits
        // never reach the radio but still serve a client, so they pulse here only.
        var clientLink = MatchClientPort(radio, evt.ClientId);
        if (clientLink > 0)
        {
            var toClient = evt.Kind is "ResponseReceived" or "CacheHit";
            radio.Pulse(clientLink, towardRadio: !toClient);
        }

        var direction = fromRadio ? "radio →" : $"{evt.ClientId} →";
        radio.Traffic.Add(new TrafficEntry(evt.Time, $"{direction} {evt.Frame}", evt.Note));
        while (radio.Traffic.Count > 9)
        {
            radio.Traffic.RemoveAt(0);
        }

        if (evt.FrequencyHz > 0)
        {
            radio.LastFrequencyHz = evt.FrequencyHz;
        }

        if (evt.Mode.Length > 0)
        {
            radio.LastMode = evt.Mode;
        }

        if (evt.Ptt.Length > 0)
        {
            radio.IsTransmitting = evt.Ptt == "tx";
        }

        if (evt.FrequencyHz > 0 || evt.Mode.Length > 0)
        {
            var freq = radio.LastFrequencyHz is { } hz ? $" · {hz / 1000.0:N2} kHz" : string.Empty;
            var mode = radio.LastMode is { } m ? $" · {m}" : string.Empty;
            radio.StatusText = $"connected{freq}{mode}";
        }
    }

    // Maps an activity event's client id (e.g. "rigctld (WSJT-X, fldigi)#2" or a COM
    // port label) to its client-port link index (1-based). Internal poll clients
    // ("status"/"ptt") match nothing and return 0.
    private static int MatchClientPort(RadioItemViewModel radio, string clientId)
    {
        if (clientId.Length == 0)
        {
            return 0;
        }

        var baseId = clientId.Split('#')[0].Trim();
        for (var i = 0; i < radio.Ports.Count; i++)
        {
            var port = radio.Ports[i];
            if (baseId.Equals(port.Label, StringComparison.OrdinalIgnoreCase) ||
                baseId.Equals(port.PortDisplay, StringComparison.OrdinalIgnoreCase) ||
                (port.Label.Length > 0 && baseId.StartsWith(port.Label, StringComparison.OrdinalIgnoreCase)))
            {
                return i + 1;
            }
        }

        return 0;
    }

    private static RadioItemViewModel ToViewModel(RadioInfo radio)
    {
        var vm = new RadioItemViewModel
        {
            Name = radio.Name,
            ConnectionSummary = radio.ConnectionSummary,
            IsConnected = radio.Connected,
            StatusText = radio.StatusText,
            IsTransmitting = radio.Transmitting,
            Protocol = radio.Protocol,
            Connection = radio.Connection,
            ComPort = radio.ComPort,
            BaudRate = radio.BaudRate,
            Host = radio.Host,
            TcpPort = radio.TcpPort,
        };

        foreach (var port in radio.Ports)
        {
            vm.Ports.Add(new ClientPortViewModel
            {
                PortDisplay = port.PortDisplay,
                Label = port.Label,
                Ptt = port.Ptt,
                Status = port.Status,
                IsActive = port.Active,
            });
        }

        return vm;
    }

    private void LoadDemoData()
    {
        Radios.Add(new RadioItemViewModel
        {
            Name = "Elecraft K3",
            ConnectionSummary = "COM7 · demo",
            IsConnected = true,
            StatusText = "demo · 14,074.00 kHz · USB",
            Connection = "Serial",
            ComPort = "COM7",
            BaudRate = 38400,
            Ports =
            [
                new() { PortDisplay = "COM11", Label = "N1MM Logger", Ptt = "CAT + RTS", Status = "active", IsActive = true },
                new() { PortDisplay = "COM12", Label = "WSJT-X", Ptt = "CAT only", Status = "active", IsActive = true },
                new() { PortDisplay = "TCP 4532", Label = "rigctld network", Ptt = "via CAT", Status = "2 clients", IsActive = true },
            ],
            Traffic =
            [
                new TrafficEntry("--:--:--.---", "COM11 → FA;", "demo data — start MultiCat.Service for live traffic"),
            ],
        });
        SelectedRadio = Radios[0];
    }

    public void Shutdown()
    {
        StopCapture(string.Empty);
        _streamCts?.Cancel();
        _connection?.Dispose();
    }
}
