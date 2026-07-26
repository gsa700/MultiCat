using System.Collections.Concurrent;
using MultiCat.Core;
using MultiCat.Core.Framing;
using MultiCat.Core.Protocol;
using MultiCat.Hamlib;
using MultiCat.Service.Rigctld;
using MultiCat.Service.Transports;

namespace MultiCat.Service.Sessions;

public sealed record RadioSessionOptions
{
    public required string Name { get; init; }

    public string Protocol { get; init; } = "Kenwood";

    /// <summary>Hamlib rig model id (e.g. 2047 = Elecraft K4); 0 if not chosen. Used to
    /// launch a real rigctld. When 0, resolved by name from the rig database.</summary>
    public int HamlibModel { get; init; }

    /// <summary>When true, runs against the built-in simulated K3 instead of hardware.</summary>
    public bool Simulator { get; init; }

    /// <summary>"Serial" (default) or "Tcp" for networked rigs like the Elecraft K4.</summary>
    public string Connection { get; init; } = "Serial";

    public string? ComPort { get; init; }

    public int BaudRate { get; init; } = 38400;

    /// <summary>Hostname or IP for a network radio ("192.168.1.40" or "K4-SN1234.local").</summary>
    public string? Host { get; init; }

    /// <summary>TCP CAT port for a network radio (Elecraft K4 uses 9200).</summary>
    public int? TcpPort { get; init; }

    public bool IsNetwork => Connection.Equals("Tcp", StringComparison.OrdinalIgnoreCase);

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

    /// <summary>hamlib rigctld-protocol listener port on localhost (WSJT-X, fldigi, …).</summary>
    public int? RigctldPort { get; init; }

    /// <summary>1 or 2 when this rigctld port is also exposed through OmniRig as that Rig.
    /// Mutable so an OmniRig assignment can be added to an existing rigctld port.</summary>
    public int? OmnirigRig { get; set; }
}

/// <summary>One radio: transport + arbiter + state tracker + client endpoints, plus an
/// internal status poller that keeps the event stream fed even with no clients polling.</summary>
public sealed class RadioSession : IAsyncDisposable
{
    private readonly ICatTransport? _transport;
    private readonly TransactionArbiter? _arbiter;
    private readonly RadioStateTracker _tracker = new();
    private readonly List<Task> _loops = [];
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<ClientPortEndpoint, byte> _endpoints = new();
    private readonly List<TcpRawListener> _listeners = [];
    private readonly Dictionary<string, RigctldListener> _rigctldListeners = [];
    private readonly Dictionary<string, RigctldSupervisor> _supervisors = [];
    private readonly List<ClientPortEndpoint> _ownedEndpoints = [];
    private readonly Dictionary<string, string> _portStatus = [];
    private readonly ILoggerFactory _loggerFactory;
    private RigctldClientPoller? _poller;

    public RadioSession(RadioSessionOptions options, ILoggerFactory loggerFactory)
    {
        Options = options;
        _loggerFactory = loggerFactory;

        // Serial sole-owner mode: a serial radio with a rigctld port lets rigctld own
        // the COM port (it can only be opened once). MultiCAT opens no CAT connection
        // of its own; the GUI feed comes from a rigctld client poller (see Start).
        if (IsSoleOwnerRigctld)
        {
            return;
        }

        _transport = options switch
        {
            { Simulator: true } => new SimulatedKenwoodTransport(),
            { IsNetwork: true } => new NetworkCatTransport(
                options.Host ?? throw new InvalidOperationException($"Radio '{options.Name}' has no Host configured"),
                options.TcpPort ?? 9200),
            _ => new SerialPortTransport(
                options.ComPort ?? throw new InvalidOperationException($"Radio '{options.Name}' has no ComPort configured"),
                options.BaudRate),
        };

        // On a real radio, arm auto-information so the rig pushes FA/FB/MD the instant
        // they change (proven against a real K4D in virtual-flex). For the network
        // transport this re-arms on every reconnect; for serial we arm once at Start.
        if (_transport is NetworkCatTransport network)
        {
            network.Connected += () => _ = ArmPushModeAsync();
        }

        // CI-V support exists in Core; sessions are Kenwood-family until the
        // config UI can express per-protocol defaults.
        _arbiter = new TransactionArbiter(
            _transport, new KenwoodFramer(), new KenwoodRules(),
            new PollCache(TimeProvider.System, TimeSpan.FromMilliseconds(300)),
            TimeProvider.System);

        _arbiter.UnsolicitedReceived += frame =>
        {
            foreach (var endpoint in _endpoints.Keys)
            {
                _ = endpoint.BroadcastAsync(frame);
            }
        };

        _arbiter.Activity += activity =>
        {
            long frequency = 0;
            var mode = string.Empty;
            var ptt = string.Empty;
            if (activity.Kind is ArbiterActivityKind.ResponseReceived or ArbiterActivityKind.Unsolicited)
            {
                var beforeHz = _tracker.FrequencyHz;
                var beforeMode = _tracker.Mode;
                var beforeTx = _tracker.Transmitting;
                _tracker.Observe(activity.Frame);
                if (_tracker.FrequencyHz != beforeHz && _tracker.FrequencyHz is { } hz)
                {
                    frequency = hz;
                }

                if (_tracker.Mode != beforeMode && _tracker.Mode is { } m)
                {
                    mode = m;
                }

                if (_tracker.Transmitting != beforeTx && _tracker.Transmitting is { } tx)
                {
                    ptt = tx ? "tx" : "rx";
                }
            }

            ActivityObserved?.Invoke(this, activity, frequency, mode, ptt);
        };
    }

    /// <summary>A serial radio with a rigctld port: rigctld owns the port, we don't.</summary>
    public bool IsSoleOwnerRigctld =>
        !Options.Simulator && !Options.IsNetwork && Options.ClientPorts.Any(p => p.RigctldPort is not null);

    public bool IsTransmitting =>
        IsSoleOwnerRigctld ? _poller?.Transmitting == true : _tracker.Transmitting == true;

    public RadioSessionOptions Options { get; }

    /// <summary>The arbiter that owns the radio. Absent in sole-owner mode, where
    /// rigctld owns it — arbiter-based endpoints aren't created there.</summary>
    public TransactionArbiter Arbiter =>
        _arbiter ?? throw new InvalidOperationException($"Radio '{Options.Name}' has no arbiter (rigctld owns the port)");

    private bool _started;

    public bool IsConnected => IsSoleOwnerRigctld
        ? _poller?.Connected == true
        : _transport switch
        {
            NetworkCatTransport network => network.IsConnected,
            _ => _started,
        };

    public string ConnectionSummary => Options switch
    {
        { Simulator: true } => "simulator · connected",
        { IsNetwork: true } => $"{Options.Host}:{Options.TcpPort ?? 9200} · {(IsConnected ? "connected" : "connecting…")}",
        _ when IsSoleOwnerRigctld => $"{Options.ComPort} · {(IsConnected ? "rigctld" : "starting rigctld…")}",
        _ => $"{Options.ComPort} · {(IsConnected ? "connected" : "idle")}",
    };

    public string StatusText
    {
        get
        {
            if (IsSoleOwnerRigctld)
            {
                if (_poller is not { Connected: true })
                {
                    return "starting rigctld…";
                }

                var f = _poller.FrequencyHz is { } phz ? $" · {phz / 1000.0:N2} kHz" : string.Empty;
                var md = _poller.Mode is { } pm ? $" · {pm}" : string.Empty;
                return $"connected{f}{md}";
            }

            if (!IsConnected)
            {
                return "idle";
            }

            var freq = _tracker.FrequencyHz is { } hz ? $" · {hz / 1000.0:N2} kHz" : string.Empty;
            var mode = _tracker.Mode is { } m ? $" · {m}" : string.Empty;
            return $"connected{freq}{mode}";
        }
    }

    public event Action<RadioSession, ArbiterActivity, long, string, string>? ActivityObserved;

    public void Start()
    {
        if (IsSoleOwnerRigctld)
        {
            StartSoleOwner();
            return;
        }

        switch (_transport)
        {
            case SerialPortTransport serial:
                serial.Open();
                _ = ArmPushModeAsync();
                break;
            case NetworkCatTransport network:
                network.Open(); // connects (and re-arms push mode) in the background
                break;
        }

        _started = true;
        _loops.Add(PollLoop("status", TimeSpan.FromMilliseconds(1000)));
        if (Options.Simulator)
        {
            _loops.Add(PollLoop("n1mm", TimeSpan.FromMilliseconds(250)));
            _loops.Add(PollLoop("wsjtx", TimeSpan.FromMilliseconds(400)));
        }
        else
        {
            // Fast, dedicated PTT poll so "on air" shows live (virtual-flex's TQX
            // pattern). PTT can't ride the AI push path, so it must be polled.
            _loops.Add(PttPollLoop(TimeSpan.FromMilliseconds(200)));
        }

        foreach (var port in Options.ClientPorts)
        {
            StartClientPort(port);
        }
    }

    /// <summary>
    /// Serial sole-owner: rigctld owns the COM port; MultiCAT launches it and then
    /// reads freq/mode/PTT back from it as a client to keep the GUI live. Only the
    /// first rigctld port is served — a serial port can't be shared by two rigctld.
    /// </summary>
    private void StartSoleOwner()
    {
        var rigctldPort = Options.ClientPorts.First(p => p.RigctldPort is not null);
        StartRigctldPort(rigctldPort, rigctldPort.RigctldPort!.Value);

        _poller = new RigctldClientPoller(
            rigctldPort.RigctldPort.Value, TimeSpan.FromMilliseconds(500),
            _loggerFactory.CreateLogger<RigctldClientPoller>());
        _poller.FrequencyChanged += hz => RaisePollActivity($"f {hz / 1000.0:N2} kHz", frequency: hz);
        _poller.ModeChanged += m => RaisePollActivity($"m {m}", mode: m);
        _poller.TransmitChanged += tx => RaisePollActivity(tx ? "TX" : "RX", ptt: tx ? "tx" : "rx");
        _poller.Start();

        foreach (var port in Options.ClientPorts)
        {
            if (!_portStatus.ContainsKey(port.PortDisplay))
            {
                _portStatus[port.PortDisplay] = "unavailable — rigctld owns the serial port";
            }
        }
    }

    // Surfaces a rigctld poll result as an activity event so the GUI status line,
    // traffic monitor, and PTT indicator update in sole-owner mode.
    private void RaisePollActivity(string display, long frequency = 0, string mode = "", string ptt = "")
    {
        var activity = new ArbiterActivity("rigctld", ArbiterActivityKind.ResponseReceived, CatFrame.FromAscii(display));
        ActivityObserved?.Invoke(this, activity, frequency, mode, ptt);
    }

    /// <summary>Arms Kenwood auto-information (AI2) so the radio pushes state changes,
    /// then does one full read. Sets are no-reply; the reads feed the state tracker.</summary>
    private async Task ArmPushModeAsync()
    {
        if (Options.Simulator)
        {
            return; // the simulator has no AI mode; its poll loop drives state
        }

        try
        {
            await Arbiter.ExecuteAsync("mux", CatFrame.FromAscii("AI2;"), _cts.Token);
            await Arbiter.ExecuteAsync("mux", CatFrame.FromAscii("FA;"), _cts.Token);
            await Arbiter.ExecuteAsync("mux", CatFrame.FromAscii("MD;"), _cts.Token);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void StartClientPort(ClientPortOptions port)
    {
        // In sole-owner mode rigctld holds the radio, so there's no arbiter to back
        // com0com/raw-TCP endpoints. Only the rigctld port (started separately) works.
        if (_arbiter is null && port.RigctldPort is null)
        {
            _portStatus[port.PortDisplay] = "unavailable — rigctld owns the serial port";
            return;
        }

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
        else if (port.RigctldPort is { } rigctldPort)
        {
            StartRigctldPort(port, rigctldPort);
        }
        else
        {
            _portStatus[port.PortDisplay] = "not configured";
        }
    }

    /// <summary>
    /// A rigctld port on a real radio is served by a supervised, bundled hamlib
    /// rigctld (the reference implementation — broad client compatibility). The
    /// simulator can't be driven by rigctld, so it falls back to the built-in
    /// emulation. rigctld opens its own CAT connection to the radio, alongside the
    /// arbiter's, which the K4's network server allows.
    /// </summary>
    private void StartRigctldPort(ClientPortOptions port, int rigctldPort)
    {
        if (Options.Simulator)
        {
            _rigctldListeners[port.PortDisplay] = new RigctldListener(port.Label, rigctldPort, this);
            _portStatus[port.PortDisplay] = $"rigctld (emulated) on localhost:{rigctldPort}";
            return;
        }

        var model = Options.HamlibModel > 0
            ? Options.HamlibModel
            : RigDatabase.FindByName(Options.Name)?.Id ?? 0;
        if (model == 0)
        {
            _portStatus[port.PortDisplay] = "no hamlib model — pick a rig in the editor";
            return;
        }

        var exe = Path.Combine(AppContext.BaseDirectory, "hamlib", "rigctld.exe");
        var device = Options.IsNetwork ? $"{Options.Host}:{Options.TcpPort ?? 9200}" : Options.ComPort ?? "";
        try
        {
            var supervisor = new RigctldSupervisor(
                new RigctldOptions
                {
                    ExePath = exe,
                    HamlibModel = model,
                    Device = device,
                    BaudRate = Options.IsNetwork ? null : Options.BaudRate,
                    ListenPort = rigctldPort,
                },
                _loggerFactory.CreateLogger<RigctldSupervisor>());
            supervisor.Start();
            _supervisors[port.PortDisplay] = supervisor;
            _portStatus[port.PortDisplay] = $"real rigctld on localhost:{rigctldPort} (hamlib model {model})";
        }
        catch (Exception ex)
        {
            _portStatus[port.PortDisplay] = $"rigctld failed: {ex.Message}";
        }
    }

    /// <summary>Adds and starts a client port at runtime (Add port button).</summary>
    public void AddClientPort(ClientPortOptions port)
    {
        Options.ClientPorts.Add(port);
        StartClientPort(port);
    }

    /// <summary>Returns an existing rigctld client port, or creates and starts one on
    /// <paramref name="autoPort"/>. OmniRig needs a rigctld endpoint to forward to.</summary>
    public ClientPortOptions EnsureRigctldPort(int autoPort)
    {
        var existing = Options.ClientPorts.FirstOrDefault(p => p.RigctldPort is not null);
        if (existing is not null)
        {
            return existing;
        }

        var port = new ClientPortOptions
        {
            PortDisplay = $"rigctld {autoPort}",
            Label = "rigctld (WSJT-X, fldigi)",
            Ptt = "via CAT",
            RigctldPort = autoPort,
        };
        AddClientPort(port);
        return port;
    }

    /// <summary>One live client app connected to a rigctld port for this radio.</summary>
    public readonly record struct ConnectedClient(string ProcessName, int Pid, int ConnectionId, int RigctldPort);

    /// <summary>Enumerates the apps currently connected to this radio's rigctld
    /// port(s), so the GUI can show a bubble per connection. Excludes our own poller.</summary>
    public IReadOnlyList<ConnectedClient> ConnectedClients()
    {
        var self = Environment.ProcessId;
        var clients = new List<ConnectedClient>();
        foreach (var port in Options.ClientPorts)
        {
            if (port.RigctldPort is { } rp && _supervisors.ContainsKey(port.PortDisplay))
            {
                foreach (var (pid, proc, connId) in TcpConnections.ClientsOnLoopbackPort(rp, self))
                {
                    clients.Add(new ConnectedClient(proc, pid, connId, rp));
                }
            }
        }

        return clients;
    }

    public void RegisterEndpoint(ClientPortEndpoint endpoint) => _endpoints[endpoint] = 0;

    public void UnregisterEndpoint(ClientPortEndpoint endpoint) => _endpoints.TryRemove(endpoint, out _);

    public (string Status, bool Active) PortStatus(ClientPortOptions port)
    {
        if (!_portStatus.TryGetValue(port.PortDisplay, out var status))
        {
            return ("unknown", false);
        }

        var active = status.StartsWith("active") || status.StartsWith("listening") ||
                     status.StartsWith("rigctld") || status.StartsWith("real rigctld");
        if (port.TcpPort is not null && active)
        {
            var count = _listeners.Sum(l => l.ConnectionCount);
            if (count > 0)
            {
                status = $"{count} client(s) connected";
            }
        }
        else if (port.RigctldPort is { } rigctldPort && active &&
                 _rigctldListeners.TryGetValue(port.PortDisplay, out var listener) && listener.ConnectionCount > 0)
        {
            status = $"{listener.ConnectionCount} client(s) on localhost:{rigctldPort}";
        }

        if (port.OmnirigRig is { } rig)
        {
            status = $"{status} · OmniRig Rig {rig}";
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

    private async Task PttPollLoop(TimeSpan interval)
    {
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(_cts.Token))
            {
                await Arbiter.ExecuteAsync("ptt", CatFrame.FromAscii("TQX;"), _cts.Token);
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

        foreach (var listener in _rigctldListeners.Values)
        {
            await listener.DisposeAsync();
        }

        foreach (var supervisor in _supervisors.Values)
        {
            await supervisor.DisposeAsync();
        }

        foreach (var endpoint in _ownedEndpoints)
        {
            await endpoint.DisposeAsync();
        }

        if (_poller is not null)
        {
            await _poller.DisposeAsync();
        }

        try
        {
            await Task.WhenAll(_loops);
        }
        catch (OperationCanceledException)
        {
        }

        if (_arbiter is not null)
        {
            await _arbiter.DisposeAsync();
        }

        if (_transport is not null)
        {
            await _transport.DisposeAsync();
        }

        _cts.Dispose();
    }
}
