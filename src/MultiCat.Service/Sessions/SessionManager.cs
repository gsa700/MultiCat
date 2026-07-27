using System.Collections.Concurrent;
using System.Threading.Channels;
using MultiCat.Contracts;
using MultiCat.Core;

namespace MultiCat.Service.Sessions;

/// <summary>
/// Owns every RadioSession and fans arbiter activity out to any number of
/// subscribed activity streams (one per connected GUI). Radios can be added,
/// edited, and removed live; changes persist to appsettings.json via the store.
/// </summary>
public sealed class SessionManager : IHostedService, IAsyncDisposable
{
    private readonly ILogger<SessionManager> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly RadioConfigStore _store;
    private readonly OmniRig.OmniRigCoordinator _omnirig;
    private readonly List<RadioSession> _sessions = [];
    private readonly ConcurrentDictionary<Guid, Channel<ActivityEvent>> _subscribers = new();
    private readonly SemaphoreSlim _mutation = new(1, 1);

    public SessionManager(ILogger<SessionManager> logger, ILoggerFactory loggerFactory, IHostEnvironment environment, OmniRig.OmniRigCoordinator omnirig)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _omnirig = omnirig;
        _store = new RadioConfigStore(Path.Combine(environment.ContentRootPath, "appsettings.json"));
    }

    public IReadOnlyList<RadioSession> Sessions => _sessions;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // An earlier run that crashed or was killed leaves its rigctld behind, still
        // holding a connection to the radio — enough to stop this start connecting.
        // Clear those before opening anything.
        Rigctld.OrphanedRigctld.Sweep(
            Path.Combine(AppContext.BaseDirectory, "hamlib", "rigctld.exe"), _logger);

        var configs = _store.Load();
        if (configs.Count == 0)
        {
            _logger.LogWarning("No radios configured; starting with the built-in simulator");
            configs.Add(new RadioSessionOptions { Name = "Elecraft K3 (simulated)", Simulator = true });
        }

        foreach (var config in configs)
        {
            TryStartSession(config);
        }

        // Re-establish OmniRig mappings so a logger still finds its radio after a restart.
        foreach (var session in _sessions)
        {
            foreach (var port in session.Options.ClientPorts)
            {
                if (port is { OmnirigRig: { } rig, RigctldPort: { } rigctldPort })
                {
                    _omnirig.AssignRig(rig, "127.0.0.1", rigctldPort);
                }
            }
        }

        return Task.CompletedTask;
    }

    private bool TryStartSession(RadioSessionOptions config)
    {
        try
        {
            var session = new RadioSession(config, _loggerFactory);
            session.ActivityObserved += OnActivity;
            session.Start();
            _sessions.Add(session);
            _logger.LogInformation("Radio session started: {Name} ({Connection})", config.Name, session.ConnectionSummary);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start radio session {Name}", config.Name);
            return false;
        }
    }

    public IReadOnlyList<RadioSessionOptions> GetConfigs() =>
        [.. _sessions.Select(s => s.Options)];

    /// <summary>Adds a radio (originalName empty) or replaces an existing one. The old
    /// session is stopped and a fresh one started so changes take effect immediately.</summary>
    public async Task<(bool Ok, string Message)> SaveRadioAsync(string? originalName, RadioSessionOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Name))
        {
            return (false, "Radio name is required");
        }

        if (!options.Simulator && options.IsNetwork && string.IsNullOrWhiteSpace(options.Host))
        {
            return (false, "A host or IP address is required for a network radio");
        }

        if (!options.Simulator && !options.IsNetwork && string.IsNullOrWhiteSpace(options.ComPort))
        {
            return (false, "A COM port is required for a serial radio");
        }

        await _mutation.WaitAsync();
        try
        {
            var editing = !string.IsNullOrEmpty(originalName);
            if (!editing && _sessions.Any(s => s.Options.Name == options.Name))
            {
                return (false, $"A radio named '{options.Name}' already exists");
            }

            var target = editing ? originalName : options.Name;
            var existing = _sessions.FirstOrDefault(s => s.Options.Name == target);
            if (editing && existing is null)
            {
                return (false, $"Radio '{originalName}' not found");
            }

            if (existing is not null)
            {
                existing.ActivityObserved -= OnActivity;
                await existing.DisposeAsync();
                _sessions.Remove(existing);
            }

            if (!TryStartSession(options))
            {
                // Persist the intended config anyway so the user can fix it in the file,
                // but tell them the live start failed (bad port, rig offline, …).
                Persist();
                return (false, $"Saved, but could not open '{options.Name}' — check the COM port and that the radio is on");
            }

            Persist();
            return (true, editing ? $"Updated '{options.Name}'" : $"Added '{options.Name}'");
        }
        finally
        {
            _mutation.Release();
        }
    }

    public async Task<(bool Ok, string Message)> DeleteRadioAsync(string name)
    {
        await _mutation.WaitAsync();
        try
        {
            var session = _sessions.FirstOrDefault(s => s.Options.Name == name);
            if (session is null)
            {
                return (false, $"Radio '{name}' not found");
            }

            session.ActivityObserved -= OnActivity;
            await session.DisposeAsync();
            _sessions.Remove(session);
            Persist();
            return (true, $"Removed '{name}'");
        }
        finally
        {
            _mutation.Release();
        }
    }

    /// <summary>Writes the current radio set back to appsettings.json.</summary>
    public void Persist()
    {
        try
        {
            _store.Save(_sessions.Select(s => s.Options));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist radio configuration to {Path}", _store.FilePath);
        }
    }

    private void OnActivity(RadioSession session, ArbiterActivity activity, long frequencyHz, string mode, string ptt)
    {
        if (_subscribers.IsEmpty)
        {
            return;
        }

        var evt = new ActivityEvent
        {
            Radio = session.Options.Name,
            Time = DateTime.Now.ToString("HH:mm:ss.fff"),
            Kind = activity.Kind.ToString(),
            ClientId = activity.ClientId ?? string.Empty,
            Frame = activity.Frame.ToAscii(),
            Note = NoteFor(activity.Kind),
            FrequencyHz = frequencyHz,
            Mode = mode,
            Ptt = ptt,
            VfoAHz = session.VfoAHz,
            VfoBHz = session.VfoBHz,
            Split = session.Split,
            TxOnVfoB = session.TransmitOnVfoB,
            ModeA = session.ModeA,
            ModeB = session.ModeB,
        };

        foreach (var channel in _subscribers.Values)
        {
            channel.Writer.TryWrite(evt);
        }
    }

    private static string NoteFor(ArbiterActivityKind kind) => kind switch
    {
        ArbiterActivityKind.CacheHit => "cache hit, radio not polled",
        ArbiterActivityKind.CommandSent => "sent to radio",
        ArbiterActivityKind.SetSent => "set sent, cache invalidated",
        ArbiterActivityKind.ResponseReceived => "routed to sender",
        ArbiterActivityKind.Timeout => "no response from radio",
        ArbiterActivityKind.Unsolicited => "broadcast to all clients",
        ArbiterActivityKind.ClientCommand => "via rigctld → radio",
        ArbiterActivityKind.ClientResponse => "reply to client",
        _ => string.Empty,
    };

    public RadioSession? FindSession(string radioName) =>
        _sessions.FirstOrDefault(s => s.Options.Name == radioName);

    /// <summary>How far apart each radio's block of endpoint ports sits.</summary>
    public const int PortBlockSize = 10;

    /// <summary>
    /// Ports hamlib's own daemons use — ampctld and rotctld. They sit inside the
    /// first radio's block, so they are skipped wherever they fall: handing rotctld's
    /// port to a rig-control endpoint would collide with a rotator setup that has
    /// every right to be running alongside us.
    /// </summary>
    private static readonly HashSet<int> HamlibReserved = [4531, 4533];

    /// <summary>
    /// The port a radio's endpoints of a given family start from. Each radio gets its
    /// own block, so a second radio lands on 4542 rather than borrowing 4533 from the
    /// first — endpoints stay grouped per radio and stay guessable.
    /// </summary>
    public static int BlockBase(int familyBase, int radioIndex) =>
        familyBase + (Math.Max(radioIndex, 0) * PortBlockSize);

    public static bool IsReservedPort(int port) => HamlibReserved.Contains(port);

    /// <summary>Index of a radio for port-block purposes; unknown radios take the
    /// first block.</summary>
    public int RadioIndex(string? radioName) => radioName is null
        ? 0
        : Math.Max(_sessions.FindIndex(s => s.Options.Name == radioName), 0);

    /// <summary>Lowest free localhost TCP port in this radio's block (or at or above
    /// <paramref name="basePort"/> when no radio is named), skipping hamlib's own
    /// daemon ports, ports already claimed by any radio, and anything bound on the
    /// machine. Allocation spills past the block rather than failing.</summary>
    public int PickFreeTcpPort(int basePort, string? radioName = null)
    {
        basePort = BlockBase(basePort, RadioIndex(radioName));
        return PickFreeTcpPortFrom(basePort);
    }

    private int PickFreeTcpPortFrom(int basePort)
    {
        var claimed = new HashSet<int>();
        foreach (var session in _sessions)
        {
            foreach (var port in session.Options.ClientPorts)
            {
                if (port.RigctldPort is { } r) claimed.Add(r);
                if (port.TcpPort is { } t) claimed.Add(t);
            }
        }

        var systemBusy = System.Net.NetworkInformation.IPGlobalProperties
            .GetIPGlobalProperties().GetActiveTcpListeners()
            .Where(e => e.Address.Equals(System.Net.IPAddress.Loopback) || e.Address.Equals(System.Net.IPAddress.Any))
            .Select(e => e.Port).ToHashSet();

        for (var p = basePort; p < basePort + 500; p++)
        {
            if (!claimed.Contains(p) && !systemBusy.Contains(p) && !IsReservedPort(p))
            {
                return p;
            }
        }

        throw new InvalidOperationException($"No free TCP port found at or above {basePort}");
    }

    /// <summary>Every COM name the mux must avoid: real system ports, names reserved
    /// in the COM Name Arbiter database (com0com refuses those), and both sides of
    /// every configured client port.</summary>
    public IEnumerable<string> KnownPortNames()
    {
        foreach (var name in System.IO.Ports.SerialPort.GetPortNames())
        {
            yield return name;
        }

        foreach (var name in VirtualPorts.ComNameArbiter.ReadReservedNames())
        {
            yield return name;
        }

        foreach (var session in _sessions)
        {
            if (session.Options.ComPort is { } radioPort)
            {
                yield return radioPort;
            }

            foreach (var port in session.Options.ClientPorts)
            {
                yield return port.PortDisplay;
                if (port.MuxPort is { } mux)
                {
                    yield return mux;
                }
            }
        }
    }

    public (Guid Id, ChannelReader<ActivityEvent> Reader) Subscribe()
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<ActivityEvent>(
            new BoundedChannelOptions(512) { FullMode = BoundedChannelFullMode.DropOldest });
        _subscribers[id] = channel;
        _logger.LogInformation("Activity stream opened ({Count} subscriber(s))", _subscribers.Count);
        return (id, channel.Reader);
    }

    public void Unsubscribe(Guid id)
    {
        if (_subscribers.TryRemove(id, out var channel))
        {
            channel.Writer.TryComplete();
            _logger.LogInformation("Activity stream closed ({Count} subscriber(s))", _subscribers.Count);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var session in _sessions)
        {
            await session.DisposeAsync();
        }

        _sessions.Clear();
    }
}
