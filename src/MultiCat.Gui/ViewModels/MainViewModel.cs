using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Grpc.Core;
using MultiCat.Contracts;
using MultiCat.Gui.Services;

namespace MultiCat.Gui.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private ServiceConnection? _connection;
    private CancellationTokenSource? _streamCts;

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

        var direction = evt.Kind is "ResponseReceived" or "Unsolicited" ? "radio →" : $"{evt.ClientId} →";
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

        if (evt.FrequencyHz > 0 || evt.Mode.Length > 0)
        {
            var freq = radio.LastFrequencyHz is { } hz ? $" · {hz / 1000.0:N2} kHz" : string.Empty;
            var mode = radio.LastMode is { } m ? $" · {m}" : string.Empty;
            radio.StatusText = $"connected{freq}{mode}";
        }
    }

    private static RadioItemViewModel ToViewModel(RadioInfo radio)
    {
        var vm = new RadioItemViewModel
        {
            Name = radio.Name,
            ConnectionSummary = radio.ConnectionSummary,
            IsConnected = radio.Connected,
            StatusText = radio.StatusText,
            RigModels = [radio.Name.Replace(" (simulated)", string.Empty)],
            PortChoices = [radio.ConnectionSummary.Split(" · ")[0]],
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
            RigModels = ["Elecraft K3"],
            PortChoices = ["COM7 — FTDI"],
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
        _streamCts?.Cancel();
        _connection?.Dispose();
    }
}
