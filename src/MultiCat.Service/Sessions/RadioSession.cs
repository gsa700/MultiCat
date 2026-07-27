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

    /// <summary>
    /// FlexRadio command port, presenting this radio to a 4O3A Genius stack.
    /// Normally 4992 — real radios are separate boxes, so a second radio on the
    /// same host needs a different port (or its own address).
    /// </summary>
    public int? FlexPort { get; init; }

    /// <summary>Subnet-directed broadcast address for Flex discovery. The host must
    /// share the stack's subnet.</summary>
    public string? FlexBroadcastAddress { get; init; }

    /// <summary>When set, discovery is unicast only to these boxes and the virtual
    /// radio stays invisible to every other picker on the LAN.</summary>
    public List<string> FlexTargets { get; init; } = [];

    /// <summary>Station callsign advertised to the stack.</summary>
    public string? FlexCallsign { get; init; }

    /// <summary>
    /// Serial to advertise, overriding the one derived from the radio's name. The
    /// Genius boxes follow a serial they have been paired to, so pointing them at a
    /// different radio means advertising the serial they already know — otherwise
    /// they ignore it, or offer it as a new radio to pair.
    /// </summary>
    public string? FlexSerial { get; init; }

    /// <summary>
    /// Whether to actually advertise. Off by default and mutable: announcing makes a
    /// Genius stack follow this radio and move real antenna and amplifier state, so
    /// it should be a decision the operator takes, not a side effect of adding a port.
    /// </summary>
    public bool FlexAdvertising { get; set; }

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
    private readonly Dictionary<string, RigctldRelay> _relays = [];
    private int _internalRigctldPort;
    private readonly List<ClientPortEndpoint> _ownedEndpoints = [];
    private readonly Dictionary<string, string> _portStatus = [];
    private readonly ILoggerFactory _loggerFactory;
    private RigctldClientPoller? _poller;
    private Core.Flex.FlexRadioState? _flexState;
    private Flex.FlexCommandServer? _flexServer;
    private Flex.FlexDiscoveryBroadcaster? _flexDiscovery;
    private Flex.FlexPresenceSupervisor? _flexPresence;
    private ClientPortOptions? _flexPort;
    private Core.Flex.FlexIdentity? _flexIdentity;

    /// <summary>What the Flex endpoint is doing, for the panel that shows it.</summary>
    public readonly record struct FlexStatusInfo(
        bool Configured,
        bool Advertising,
        bool Online,
        string Serial,
        int CommandPort,
        string Targets,
        int ConnectedBoxes,
        string Callsign);

    public FlexStatusInfo FlexStatus()
    {
        if (_flexPort is not { FlexPort: { } port } flex || _flexIdentity is not { } identity)
        {
            return new FlexStatusInfo(false, false, false, string.Empty, 0, string.Empty, 0, string.Empty);
        }

        return new FlexStatusInfo(
            Configured: true,
            Advertising: flex.FlexAdvertising,
            Online: _flexPresence?.Online == true,
            Serial: identity.Serial,
            CommandPort: port,
            Targets: flex.FlexTargets.Count > 0
                ? string.Join(", ", flex.FlexTargets)
                : $"broadcast {flex.FlexBroadcastAddress ?? "255.255.255.255"}",
            ConnectedBoxes: _flexServer?.ClientCount ?? 0,
            Callsign: identity.Callsign);
    }

    /// <summary>
    /// Starts or stops advertising. The supervisor picks the change up on its next
    /// tick and brings the stack up or down through the usual debounced path.
    /// </summary>
    public void SetFlexAdvertising(bool advertising)
    {
        if (_flexPort is not null)
        {
            _flexPort.FlexAdvertising = advertising;
        }
    }

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
                    mode = ModeNames.ToDisplay(m);
                }

                if (_tracker.Transmitting != beforeTx && _tracker.Transmitting is { } tx)
                {
                    ptt = tx ? "tx" : "rx";
                }
            }

            FeedFlex(frequency, mode, ptt);
            ActivityObserved?.Invoke(this, activity, frequency, mode, ptt);
        };
    }

    /// <summary>
    /// Passes a state change to the Flex endpoint, if one is running. Frequency and
    /// mode are coalesced into a delta; keying is applied immediately and separately,
    /// because an amplifier sequences off it.
    /// </summary>
    private void FeedFlex(long frequencyHz, string mode, string ptt)
    {
        if (_flexState is null)
        {
            return;
        }

        // Band-following gear must track the TRANSMIT frequency, which in split is
        // VFO B, not what the operator is listening on. Following the receive VFO
        // would band-select the amplifier, tuner and switch for the wrong band.
        var transmitHz = IsSoleOwnerRigctld ? _poller?.TransmitFrequencyHz : _tracker.TransmitFrequencyHz;
        var effectiveHz = transmitHz ?? (frequencyHz > 0 ? frequencyHz : null);

        if (effectiveHz is not null || mode.Length > 0)
        {
            _flexState.UpdateSlice(
                0,
                frequencyHz: effectiveHz,
                mode: mode.Length > 0 ? mode : null);
            _flexState.EmitPending();
        }

        if (ptt.Length > 0)
        {
            _flexState.SetTransmit(ptt == "tx");
        }
    }

    /// <summary>A serial radio with a rigctld port: rigctld owns the port, we don't.</summary>
    public bool IsSoleOwnerRigctld =>
        !Options.Simulator && !Options.IsNetwork && Options.ClientPorts.Any(p => p.RigctldPort is not null);

    public bool IsTransmitting =>
        IsSoleOwnerRigctld ? _poller?.Transmitting == true : _tracker.Transmitting == true;

    /// <summary>The receive VFO's frequency.</summary>
    public long VfoAHz => (IsSoleOwnerRigctld ? _poller?.FrequencyHz : _tracker.FrequencyHz) ?? 0;

    /// <summary>
    /// VFO B's frequency, or 0 when unknown. Via rigctld only the split transmit
    /// frequency is available, so VFO B is known only while split is on — reading
    /// its dial otherwise needs a variable-length reply this client doesn't do.
    /// </summary>
    public long VfoBHz => IsSoleOwnerRigctld
        ? _poller?.VfoBHz ?? 0
        : _tracker.VfoBHz ?? 0;

    public bool Split => IsSoleOwnerRigctld ? _poller?.Split == true : _tracker.Split;

    /// <summary>VFO A's mode.</summary>
    public string ModeA => (IsSoleOwnerRigctld ? _poller?.Mode : _tracker.Mode) ?? string.Empty;

    /// <summary>VFO B's mode, or empty when unknown — rigctld exposes only one mode.</summary>
    public string ModeB => IsSoleOwnerRigctld
        ? _poller?.ModeB ?? string.Empty
        : _tracker.ModeB ?? string.Empty;

    /// <summary>Which VFO the radio will transmit on — what the arrow points at.</summary>
    public bool TransmitOnVfoB => Split;

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
                var md = _poller.Mode is { } pm ? $" · {ModeNames.ToDisplay(pm)}" : string.Empty;
                return $"connected{f}{md}";
            }

            if (!IsConnected)
            {
                return "idle";
            }

            var freq = _tracker.FrequencyHz is { } hz ? $" · {hz / 1000.0:N2} kHz" : string.Empty;
            var mode = _tracker.Mode is { } m ? $" · {ModeNames.ToDisplay(m)}" : string.Empty;
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
            // Dedicated PTT poll — PTT can't ride the AI push path, so it is polled.
            _loops.Add(PttPollLoop());
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

        // Poll rigctld's internal port directly so our own poller never shows up as a
        // client connection or as relay traffic.
        // A serial radio has no push equivalent of the network path's auto-info, so
        // the dial only moves as fast as this poll. 150 ms keeps it feeling live
        // without crowding a CAT link that is shared with the logger and digital apps.
        _poller = new RigctldClientPoller(
            _internalRigctldPort > 0 ? _internalRigctldPort : rigctldPort.RigctldPort.Value,
            TimeSpan.FromMilliseconds(150),
            _loggerFactory.CreateLogger<RigctldClientPoller>());
        _poller.FrequencyChanged += hz => RaisePollActivity($"f {hz / 1000.0:N2} kHz", frequency: hz);
        _poller.ModeChanged += m => RaisePollActivity($"m {m}", mode: ModeNames.ToDisplay(m));
        _poller.TransmitChanged += tx => RaisePollActivity(tx ? "TX" : "RX", ptt: tx ? "tx" : "rx");
        // VFO B carries no frequency/mode of its own in the event, which describes
        // VFO A; the panel reads both dials from the radio's status instead. Raising
        // it still refreshes that status promptly rather than at the slow poll.
        _poller.VfoBChanged += hz => RaisePollActivity($"VFO B {hz / 1000.0:N2} kHz");
        _poller.ModeBChanged += m => RaisePollActivity($"VFO B {m}");
        _poller.Start();

        foreach (var port in Options.ClientPorts)
        {
            if (port.FlexPort is { } flexPort)
            {
                // Reads its state from the poller, so it works here too.
                StartFlexPort(port, flexPort);
            }
            else if (!_portStatus.ContainsKey(port.PortDisplay))
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
        FeedFlex(frequency, mode, ptt);
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
            // VFO B and the transmit-VFO select, so split is known from the start
            // rather than only after the operator next touches it.
            await Arbiter.ExecuteAsync("mux", CatFrame.FromAscii("FB;"), _cts.Token);
            await Arbiter.ExecuteAsync("mux", CatFrame.FromAscii("FT;"), _cts.Token);
            await Arbiter.ExecuteAsync("mux", CatFrame.FromAscii("MD;"), _cts.Token);
            // VFO B's mode, which differs from VFO A during a cross-mode split.
            await Arbiter.ExecuteAsync("mux", CatFrame.FromAscii("MD$;"), _cts.Token);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Presents this radio to a 4O3A Genius stack: a command port the boxes connect
    /// to, and a discovery beacon so they can find it. Announcing starts immediately,
    /// so this only runs for a radio the user has explicitly configured for it.
    /// </summary>
    private void StartFlexPort(ClientPortOptions port, int flexPort)
    {
        try
        {
            var identity = new Core.Flex.FlexIdentity
            {
                Serial = port.FlexSerial is { Length: > 0 } configured
                    ? configured
                    : Core.Flex.FlexIdentity.DeriveSerial(Options.Name),
                AdvertiseIp = Flex.FlexDiscoveryBroadcaster.DetectLocalIp() ?? "127.0.0.1",
                Nickname = Options.Name,
                Name = Options.Name,
                Callsign = port.FlexCallsign ?? string.Empty,
                CommandPort = flexPort,
            };

            _flexState = new Core.Flex.FlexRadioState(identity);
            _flexServer = new Flex.FlexCommandServer(
                _flexState, flexPort, _loggerFactory.CreateLogger<Flex.FlexCommandServer>());

            // Attribute what goes to each box so its link on the diagram pulses.
            // No frequency or mode rides along: this says "traffic happened", and
            // the state it carried has already been reported by the radio's own event.
            _flexServer.BoxTraffic += name => ActivityObserved?.Invoke(
                this,
                new ArbiterActivity(name, ArbiterActivityKind.ResponseReceived, CatFrame.FromAscii("slice")),
                0, string.Empty, string.Empty);
            _flexServer.Start();

            var discoveryOptions = new Flex.FlexDiscoveryOptions
            {
                BroadcastAddress = port.FlexBroadcastAddress ?? "255.255.255.255",
                UnicastTargets = port.FlexTargets,
            };

            // Discovery is started by the supervisor rather than here: the stack
            // should only ever see a radio that is actually answering.
            _flexPort = port;
            _flexIdentity = identity;

            // Advertising is gated on the operator's switch as well as the radio, so
            // turning it off tears the stack down through the same debounced path a
            // radio going away would — boxes revert to their no-transceiver antenna.
            _flexPresence = new Flex.FlexPresenceSupervisor(
                isPresent: () => IsConnected && port.FlexAdvertising,
                goOnline: () =>
                {
                    _flexServer!.Accepting = true;
                    _flexDiscovery = new Flex.FlexDiscoveryBroadcaster(
                        identity, discoveryOptions,
                        _loggerFactory.CreateLogger<Flex.FlexDiscoveryBroadcaster>(),
                        () => _flexServer?.ConnectedPeers() ?? []);
                    _flexDiscovery.Start();
                    return Task.CompletedTask;
                },
                goOffline: async () =>
                {
                    // Stop advertising first, then drop the boxes, so none of them
                    // reconnects to a radio that is on its way out.
                    if (_flexDiscovery is not null)
                    {
                        await _flexDiscovery.DisposeAsync();
                        _flexDiscovery = null;
                    }

                    _flexServer!.Accepting = false;
                    _flexServer.DropAllClients();
                },
                _loggerFactory.CreateLogger<Flex.FlexPresenceSupervisor>());
            _flexPresence.Start();

            // Seed the slice from what the radio has already told us, so a box that
            // connects before the next dial movement still sees the right band.
            var seedFrequency = IsSoleOwnerRigctld ? _poller?.FrequencyHz : _tracker.FrequencyHz;
            var seedMode = IsSoleOwnerRigctld ? _poller?.Mode : _tracker.Mode;
            if (seedFrequency is { } hz)
            {
                _flexState.UpdateSlice(0, frequencyHz: hz, mode: seedMode);
                _flexState.EmitPending();
            }

            _portStatus[port.PortDisplay] = $"advertising as {identity.Serial} on {flexPort}";
        }
        catch (Exception ex)
        {
            _portStatus[port.PortDisplay] = $"failed: {ex.Message}";
        }
    }

    private void StartClientPort(ClientPortOptions port)
    {
        if (port.FlexPort is { } flexPort)
        {
            StartFlexPort(port, flexPort);
            return;
        }

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
            // rigctld listens on an internal port; MultiCAT's relay takes the public
            // one, so every client's traffic passes through us for attribution and
            // visualization (the whole point of the mux having a face).
            var internalPort = FreeTcpPort();
            _internalRigctldPort = internalPort;
            var supervisor = new RigctldSupervisor(
                new RigctldOptions
                {
                    ExePath = exe,
                    HamlibModel = model,
                    Device = device,
                    BaudRate = Options.IsNetwork ? null : Options.BaudRate,
                    ListenPort = internalPort,
                },
                _loggerFactory.CreateLogger<RigctldSupervisor>());
            supervisor.Start();
            _supervisors[port.PortDisplay] = supervisor;

            var relay = new RigctldRelay(rigctldPort, internalPort, _loggerFactory.CreateLogger<RigctldRelay>());
            relay.Traffic += (clientId, line, isCommand) =>
                ActivityObserved?.Invoke(
                    this,
                    new ArbiterActivity(clientId,
                        isCommand ? ArbiterActivityKind.ClientCommand : ArbiterActivityKind.ClientResponse,
                        CatFrame.FromAscii(line)),
                    0, string.Empty, string.Empty);
            _relays[port.PortDisplay] = relay;

            _portStatus[port.PortDisplay] = $"real rigctld on localhost:{rigctldPort} (hamlib model {model})";
        }
        catch (Exception ex)
        {
            _portStatus[port.PortDisplay] = $"rigctld failed: {ex.Message}";
        }
    }

    private static int FreeTcpPort()
    {
        var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
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
    /// port(s), so the GUI can show a bubble per connection. The relay is the source
    /// of truth; our own poller talks to the internal port and never appears.</summary>
    public IReadOnlyList<ConnectedClient> ConnectedClients()
    {
        var clients = new List<ConnectedClient>();
        foreach (var port in Options.ClientPorts)
        {
            if (port.RigctldPort is { } rp && _relays.TryGetValue(port.PortDisplay, out var relay))
            {
                foreach (var (pid, proc, connId) in relay.Connections)
                {
                    clients.Add(new ConnectedClient(proc, pid, connId, rp));
                }
            }
        }

        // Genius boxes arrive on the Flex port rather than through a relay, but they
        // are consumers of this radio just the same and belong on the diagram. They
        // have no process id — they are not on this machine — so that stays 0.
        if (_flexServer is not null && _flexPort?.FlexPort is { } flexPort)
        {
            foreach (var box in _flexServer.ConnectedBoxes())
            {
                clients.Add(new ConnectedClient(box.Name, 0, box.ConnectionId, flexPort));
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
                 _relays.TryGetValue(port.PortDisplay, out var relay) && relay.ConnectionCount > 0)
        {
            status = $"{relay.ConnectionCount} client(s) on localhost:{rigctldPort}";
        }
        else if (port.RigctldPort is { } emulatedPort && active &&
                 _rigctldListeners.TryGetValue(port.PortDisplay, out var listener) && listener.ConnectionCount > 0)
        {
            status = $"{listener.ConnectionCount} client(s) on localhost:{emulatedPort}";
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

    /// <summary>
    /// A key edge matters at two very different speeds. For the GUI's "on air" light,
    /// 200 ms is instant to a human. For a Genius stack, the interlock must reach the
    /// amplifier ahead of RF, and it can only be sent when this poll notices the edge —
    /// at 200 ms the amp keys late and hangs after unkey (field-observed). So while a
    /// Flex port exists the poll runs flat out, gated by the radio's own response
    /// rate: each TQX; is a full arbiter transaction, so the loop self-paces to the
    /// CAT link and a brief delay only separates transactions. The reference
    /// implementation polls the same radio at 3 ms over the network, so the rig
    /// sustains this comfortably. Checked per-cycle, so adding a Flex port speeds an
    /// idle radio up without a restart.
    /// </summary>
    private TimeSpan PttPollInterval() =>
        Options.ClientPorts.Any(p => p.FlexPort is not null)
            ? TimeSpan.FromMilliseconds(3)
            : TimeSpan.FromMilliseconds(200);

    private async Task PttPollLoop()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await Arbiter.ExecuteAsync("ptt", CatFrame.FromAscii("TQX;"), _cts.Token);
                await Task.Delay(PttPollInterval(), _cts.Token);
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

        foreach (var relay in _relays.Values)
        {
            await relay.DisposeAsync();
        }

        foreach (var supervisor in _supervisors.Values)
        {
            await supervisor.DisposeAsync();
        }

        foreach (var endpoint in _ownedEndpoints)
        {
            await endpoint.DisposeAsync();
        }

        // Stop advertising first, then drop the boxes: they should see the radio go
        // away rather than keep a stale advert for something that no longer answers.
        if (_flexPresence is not null)
        {
            await _flexPresence.DisposeAsync();
        }

        if (_flexDiscovery is not null)
        {
            await _flexDiscovery.DisposeAsync();
        }

        if (_flexServer is not null)
        {
            await _flexServer.DisposeAsync();
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
