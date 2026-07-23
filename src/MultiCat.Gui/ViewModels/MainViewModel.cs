using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MultiCat.Gui.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private static readonly (string Body, string Note)[] DemoTraffic =
    [
        ("COM11 → FA;", "cache hit, radio not polled"),
        ("COM12 → FA;", "sent to radio"),
        ("radio → FA00014074000;", "event: frequency"),
        ("COM12 → MD;", "sent to radio"),
        ("radio → MD2;", "routed to WSJT-X"),
        ("TCP 4532 → f", "rigctld read, cache hit"),
        ("COM11 → IF;", "sent to radio"),
        ("radio → IF00014074000 ...;", "routed to N1MM"),
    ];

    private readonly DispatcherTimer _demoTimer;
    private readonly Random _random = new();
    private int _demoIndex;
    private long _frequencyHz = 14_074_000;

    public MainViewModel()
    {
        Radios =
        [
            new RadioItemViewModel
            {
                Name = "Elecraft K3",
                ConnectionSummary = "COM7 · connected",
                IsConnected = true,
                StatusText = "connected · 14,074.00 kHz · USB",
                RigModels = ["Elecraft K3"],
                PortChoices = ["COM7 — FTDI"],
                Ports =
                [
                    new() { PortDisplay = "COM11", Label = "N1MM Logger", Ptt = "CAT + RTS", Status = "active", IsActive = true },
                    new() { PortDisplay = "COM12", Label = "WSJT-X", Ptt = "CAT only", Status = "active", IsActive = true },
                    new() { PortDisplay = "COM13", Label = "spare", Ptt = "none", Status = "idle" },
                    new() { PortDisplay = "TCP 4532", Label = "rigctld network", Ptt = "via CAT", Status = "2 clients", IsActive = true },
                ],
            },
            new RadioItemViewModel
            {
                Name = "Icom IC-7610",
                ConnectionSummary = "192.168.1.40 · idle",
                StatusText = "idle",
                RigModels = ["Icom IC-7610"],
                PortChoices = ["192.168.1.40:50001"],
                SelectedConnectionKind = 1,
                Ports =
                [
                    new() { PortDisplay = "COM14", Label = "Log4OM", Ptt = "CAT only", Status = "idle" },
                    new() { PortDisplay = "TCP 4533", Label = "rigctld network", Ptt = "via CAT", Status = "idle" },
                ],
            },
        ];

        SelectedRadio = Radios[0];
        _demoTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(1400), DispatcherPriority.Background, OnDemoTick);
        _demoTimer.Start();
    }

    public ObservableCollection<RadioItemViewModel> Radios { get; }

    [ObservableProperty]
    public partial RadioItemViewModel? SelectedRadio { get; set; }

    // Demo feed until the service connection lands: scrolls canned traffic through
    // the monitor and drifts the K3 frequency so the UI feels alive.
    private void OnDemoTick(object? sender, EventArgs e)
    {
        var radio = Radios[0];
        var (body, note) = DemoTraffic[_demoIndex];
        _demoIndex = (_demoIndex + 1) % DemoTraffic.Length;

        if (body.Contains("FA00014074000"))
        {
            _frequencyHz += _random.Next(-3, 4) * 10;
            body = $"radio → FA{_frequencyHz:00000000000};";
            radio.StatusText = $"connected · {_frequencyHz / 1000.0:N2} kHz · USB";
        }

        radio.Traffic.Add(new TrafficEntry(DateTime.Now.ToString("HH:mm:ss.fff"), body, note));
        while (radio.Traffic.Count > 9)
        {
            radio.Traffic.RemoveAt(0);
        }
    }
}
