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
    private DispatcherTimer? _statusTimer;
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

    /// <summary>Adds a network client endpoint (rigctld, raw TCP, or an OmniRig
    /// assignment) to the selected radio. Port 0 lets the service auto-pick;
    /// omnirigRig is 1 or 2 for OmniRig. Reloads so the new port shows live.</summary>
    public async Task AddPortAsync(string endpointType, int port, string label, int omnirigRig = 0)
    {
        if (_connection is null || SelectedRadio is null)
        {
            ServiceStatus = "service offline — cannot add ports";
            return;
        }

        try
        {
            var reply = await _connection.Client.AddClientPortAsync(
                new AddClientPortRequest
                {
                    Radio = SelectedRadio.Name,
                    EndpointType = endpointType,
                    Port = port,
                    Label = label,
                    OmnirigRig = omnirigRig,
                },
                deadline: DateTime.UtcNow.AddSeconds(90)); // OmniRig registration may prompt for elevation

            ServiceStatus = reply.Message;
            if (reply.Ok)
            {
                await ReloadRadiosAsync(SelectedRadio.Name);
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

    // Updates per-port status (client counts, driver readiness) in place without
    // rebuilding the list, so it doesn't disturb selection, traffic, or the diagram.
    private async Task RefreshStatusesAsync()
    {
        if (_connection is null)
        {
            return;
        }

        try
        {
            var list = await _connection.Client.GetRadiosAsync(
                new GetRadiosRequest(), deadline: DateTime.UtcNow.AddSeconds(2));
            foreach (var info in list.Radios)
            {
                var radio = Radios.FirstOrDefault(r => r.Name == info.Name);
                if (radio is null)
                {
                    continue;
                }

                foreach (var portInfo in info.Ports)
                {
                    var port = radio.Ports.FirstOrDefault(p => p.PortDisplay == portInfo.PortDisplay);
                    if (port is not null)
                    {
                        port.Status = portInfo.Status;
                        port.IsActive = portInfo.Active;
                    }
                }

                radio.IsConnected = info.Connected;

                // Resync the sidebar and status line from the service. A radio can
                // drop and recover (rig powered off, PC standby) with no activity
                // event to correct these, which used to leave a stale "connecting…".
                radio.ConnectionSummary = info.ConnectionSummary;
                radio.StatusText = info.StatusText;

                ReconcileClients(radio, info.Clients);
                UpdateQuietNote(radio);
            }
        }
        catch (Exception)
        {
            // Transient; the next tick tries again.
        }
    }

    // "quiet · last traffic …" when nothing has flowed for a while — so a silent
    // monitor reads as healthy-but-quiet instead of dead. Cleared on any event.
    private static void UpdateQuietNote(RadioItemViewModel radio)
    {
        if (!radio.IsConnected)
        {
            radio.QuietNote = string.Empty;
            return;
        }

        if (radio.LastActivityAt is not { } last)
        {
            radio.QuietNote = "quiet · no traffic yet";
            return;
        }

        var idle = DateTime.Now - last;
        radio.QuietNote = idle.TotalSeconds < 10
            ? string.Empty
            : $"quiet · last traffic {last:HH:mm:ss} ({(idle.TotalSeconds < 120 ? $"{(int)idle.TotalSeconds} s" : $"{(int)idle.TotalMinutes} min")} ago)";
    }

    // Bring a radio's live client bubbles in line with the service without rebuilding
    // the collection (which would flicker the diagram): drop gone, add new, rename.
    private static void ReconcileClients(RadioItemViewModel radio, IEnumerable<ClientConnection> live)
    {
        var liveList = live.ToList();
        var liveKeys = liveList.Select(c => $"{c.ProcessName}:{c.ConnectionId}").ToHashSet();

        for (var i = radio.Clients.Count - 1; i >= 0; i--)
        {
            if (!liveKeys.Contains(radio.Clients[i].Key))
            {
                radio.Clients.RemoveAt(i);
            }
        }

        foreach (var c in liveList)
        {
            var existing = radio.Clients.FirstOrDefault(x => x.Key == $"{c.ProcessName}:{c.ConnectionId}");
            if (existing is null)
            {
                radio.Clients.Add(new ClientConnectionViewModel
                {
                    ProcessName = c.ProcessName,
                    ConnectionId = c.ConnectionId,
                    DisplayName = c.DisplayName,
                });
            }
            else if (existing.DisplayName != c.DisplayName)
            {
                existing.DisplayName = c.DisplayName;
            }
        }
    }

    /// <summary>Persists a friendly name for a client process, then refreshes so every
    /// bubble for that app updates.</summary>
    public async Task SetClientNicknameAsync(string processName, string nickname)
    {
        if (_connection is null)
        {
            return;
        }

        try
        {
            await _connection.Client.SetClientNicknameAsync(
                new SetClientNicknameRequest { ProcessName = processName, Nickname = nickname });
            await RefreshStatusesAsync();
        }
        catch (Exception ex)
        {
            ServiceStatus = $"rename failed: {ex.Message}";
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

            _streamCts = new CancellationTokenSource();
            _ = PumpActivityAsync(_streamCts.Token);

            // Port statuses (client counts, driver state) change without an activity
            // event, so refresh them on a slow timer — the stream handles the rest.
            _statusTimer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background,
                (_, _) => _ = RefreshStatusesAsync());
            _statusTimer.Start();
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

        radio.LastActivityAt = DateTime.Now;
        radio.QuietNote = string.Empty;

        // Relayed rigctld traffic: a client's command flows client → hub → radio;
        // rigctld's reply pulses hub → client. Commands get a traffic line with the
        // bubble's display name; replies are pulse-only (they'd swamp the monitor).
        if (evt.Kind is "ClientCommand" or "ClientResponse")
        {
            var (link, display) = MatchClient(radio, evt.ClientId);
            if (evt.Kind == "ClientCommand")
            {
                if (link > 0)
                {
                    radio.Pulse(link, towardRadio: true);
                }

                radio.Pulse(0, towardRadio: true);
                radio.Traffic.Add(new TrafficEntry(evt.Time, $"{display} → {evt.Frame}", evt.Note));
                while (radio.Traffic.Count > 9)
                {
                    radio.Traffic.RemoveAt(0);
                }
            }
            else if (link > 0)
            {
                radio.Pulse(link, towardRadio: false);
            }

            return;
        }

        var fromRadio = evt.Kind is "ResponseReceived" or "Unsolicited";
        var reachedRadio = evt.Kind is "CommandSent" or "SetSent" or "ResponseReceived" or "Unsolicited";
        if (reachedRadio)
        {
            // Radio↔hub link: command toward radio (amber), response back to hub (teal).
            radio.Pulse(0, towardRadio: !fromRadio);
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

    // Maps a relayed client id ("process#connId") to its bubble: (1-based link index
    // for the diagram, display name for the traffic line). Unknown ids fall back to
    // the raw process name with no link.
    private static (int Link, string Display) MatchClient(RadioItemViewModel radio, string clientId)
    {
        var parts = clientId.Split('#');
        var process = parts[0];
        var connId = parts.Length > 1 && int.TryParse(parts[1], out var id) ? id : -1;

        for (var i = 0; i < radio.Clients.Count; i++)
        {
            var client = radio.Clients[i];
            if (client.ProcessName.Equals(process, StringComparison.OrdinalIgnoreCase) &&
                (connId < 0 || client.ConnectionId == connId))
            {
                return (i + 1, client.DisplayName);
            }
        }

        return (0, process);
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

        foreach (var client in radio.Clients)
        {
            vm.Clients.Add(new ClientConnectionViewModel
            {
                ProcessName = client.ProcessName,
                ConnectionId = client.ConnectionId,
                DisplayName = client.DisplayName,
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
        _statusTimer?.Stop();
        StopCapture(string.Empty);
        _streamCts?.Cancel();
        _connection?.Dispose();
    }
}
