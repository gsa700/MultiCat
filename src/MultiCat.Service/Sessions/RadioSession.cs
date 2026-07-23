using System.Collections.Concurrent;
using MultiCat.Core;
using MultiCat.Core.Framing;
using MultiCat.Core.Protocol;
using MultiCat.Service.Transports;

namespace MultiCat.Service.Sessions;

public sealed record RadioSessionOptions
{
    public required string Name { get; init; }

    public string Protocol { get; init; } = "Kenwood";

    /// <summary>When true, runs against the built-in simulated K3 instead of hardware.</summary>
    public bool Simulator { get; init; }

    public string? ComPort { get; init; }

    public int BaudRate { get; init; } = 38400;

    public List<ClientPortOptions> ClientPorts { get; init; } = [];
}

public sealed record ClientPortOptions
{
    /// <summary>What the user sees and the client app opens: "COM11" or "TCP 4532".</summary>
    public required string PortDisplay { get; init; }

    public required string Label { get; init; }

    public string Ptt { get; init; } = "CAT only";

    /// <summary>Our side of a com0com pair (the app opens PortDisplay, we open this).</summary>
    public string? MuxPort { get; init; }

    /// <summary>Raw-CAT TCP listener port on localhost.</summary>
    public int? TcpPort { get; init; }
}

/// <summary>One radio: transport + arbiter + state tracker + client endpoints, plus an
/// internal status poller that keeps the event stream fed even with no clients polling.</summary>
public sealed class RadioSession : IAsyncDisposable
{
    private readonly ICatTransport _transport;
    private readonly RadioStateTracker _tracker = new();
    private readonly List<Task> _loops = [];
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<ClientPortEndpoint, byte> _endpoints = new();
    private readonly List<TcpRawListener> _listeners = [];
    private readonly List<ClientPortEndpoint> _ownedEndpoints = [];
    private readonly Dictionary<string, string> _portStatus = [];

    public RadioSession(RadioSessionOptions options)
    {
        Options = options;
        _transport = options.Simulator
            ? new SimulatedKenwoodTransport()
            : new SerialPortTransport(
                options.ComPort ?? throw new InvalidOperationException($"Radio '{options.Name}' has no ComPort configured"),
                options.BaudRate);

        // CI-V support exists in Core; sessions are Kenwood-family until the
        // config UI can express per-protocol defaults.
        Arbiter = new TransactionArbiter(
            _transport, new KenwoodFramer(), new KenwoodRules(),
            new PollCache(TimeProvider.System, TimeSpan.FromMilliseconds(300)),
            TimeProvider.System);

        Arbiter.UnsolicitedReceived += frame =>
        {
            foreach (var endpoint in _endpoints.Keys)
            {
                _ = endpoint.BroadcastAsync(frame);
            }
        };

        Arbiter.Activity += activity =>
        {
            long frequency = 0;
            var mode = string.Empty;
            if (activity.Kind is ArbiterActivityKind.ResponseReceived or ArbiterActivityKind.Unsolicited)
            {
                var beforeHz = _tracker.FrequencyHz;
                var beforeMode = _tracker.Mode;
                _tracker.Observe(activity.Frame);
                if (_tracker.FrequencyHz != beforeHz && _tracker.FrequencyHz is { } hz)
                {
                    frequency = hz;
                }

                if (_tracker.Mode != beforeMode && _tracker.Mode is { } m)
                {
                    mode = m;
                }
            }

            ActivityObserved?.Invoke(this, activity, frequency, mode);
        };
    }

    public RadioSessionOptions Options { get; }

    public TransactionArbiter Arbiter { get; }

    public bool IsConnected { get; private set; }

    public string ConnectionSummary => Options.Simulator
        ? "simulator · connected"
        : $"{Options.ComPort} · {(IsConnected ? "connected" : "idle")}";

    public string StatusText
    {
        get
        {
            if (!IsConnected)
            {
                return "idle";
            }

            var freq = _tracker.FrequencyHz is { } hz ? $" · {hz / 1000.0:N2} kHz" : string.Empty;
            var mode = _tracker.Mode is { } m ? $" · {m}" : string.Empty;
            return $"connected{freq}{mode}";
        }
    }

    public event Action<RadioSession, ArbiterActivity, long, string>? ActivityObserved;

    public void Start()
    {
        if (_transport is SerialPortTransport serial)
        {
            serial.Open();
        }

        IsConnected = true;
        _loops.Add(PollLoop("status", TimeSpan.FromMilliseconds(1000)));
        if (Options.Simulator)
        {
            _loops.Add(PollLoop("n1mm", TimeSpan.FromMilliseconds(250)));
            _loops.Add(PollLoop("wsjtx", TimeSpan.FromMilliseconds(400)));
        }

        foreach (var port in Options.ClientPorts)
        {
            StartClientPort(port);
        }
    }

    private void StartClientPort(ClientPortOptions port)
    {
        if (port.MuxPort is { } muxPort)
        {
            try
            {
                var transport = new SerialPortTransport(muxPort, Options.BaudRate);
                transport.Open();
                var endpoint = new ClientPortEndpoint(port.Label, transport, new KenwoodFramer(), Arbiter);
                _ownedEndpoints.Add(endpoint);
                RegisterEndpoint(endpoint);
                _portStatus[port.PortDisplay] = $"active via {muxPort}";
            }
            catch (Exception)
            {
                _portStatus[port.PortDisplay] = $"unavailable — create com0com pair {port.PortDisplay} ↔ {muxPort}";
            }
        }
        else if (port.TcpPort is { } tcpPort)
        {
            try
            {
                _listeners.Add(new TcpRawListener(port.Label, tcpPort, () => new KenwoodFramer(), this));
                _portStatus[port.PortDisplay] = $"listening on localhost:{tcpPort}";
            }
            catch (Exception ex)
            {
                _portStatus[port.PortDisplay] = $"failed: {ex.Message}";
            }
        }
        else
        {
            _portStatus[port.PortDisplay] = "not configured";
        }
    }

    public void RegisterEndpoint(ClientPortEndpoint endpoint) => _endpoints[endpoint] = 0;

    public void UnregisterEndpoint(ClientPortEndpoint endpoint) => _endpoints.TryRemove(endpoint, out _);

    public (string Status, bool Active) PortStatus(ClientPortOptions port)
    {
        if (!_portStatus.TryGetValue(port.PortDisplay, out var status))
        {
            return ("unknown", false);
        }

        var active = status.StartsWith("active") || status.StartsWith("listening");
        if (port.TcpPort is not null && active)
        {
            var count = _listeners.Sum(l => l.ConnectionCount);
            if (count > 0)
            {
                status = $"{count} client(s) connected";
            }
        }

        return (status, active);
    }

    private async Task PollLoop(string clientId, TimeSpan interval)
    {
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(_cts.Token))
            {
                await Arbiter.ExecuteAsync(clientId, CatFrame.FromAscii("FA;"), _cts.Token);
                await Arbiter.ExecuteAsync(clientId, CatFrame.FromAscii("MD;"), _cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        foreach (var listener in _listeners)
        {
            await listener.DisposeAsync();
        }

        foreach (var endpoint in _ownedEndpoints)
        {
            await endpoint.DisposeAsync();
        }

        try
        {
            await Task.WhenAll(_loops);
        }
        catch (OperationCanceledException)
        {
        }

        await Arbiter.DisposeAsync();
        await _transport.DisposeAsync();
        _cts.Dispose();
    }
}
