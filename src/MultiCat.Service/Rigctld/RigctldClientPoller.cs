using System.Net.Sockets;

namespace MultiCat.Service.Rigctld;

/// <summary>
/// Polls a rigctld instance as a client (f/m/t) so MultiCAT can still show a radio's
/// frequency, mode, and PTT when rigctld — not our arbiter — owns the CAT connection.
/// This is how the GUI stays live for serial radios in sole-owner mode, where the COM
/// port can only be opened once. Reconnects until rigctld is up.
/// </summary>
public sealed class RigctldClientPoller(int port, TimeSpan interval, ILogger logger) : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public long? FrequencyHz { get; private set; }

    /// <summary>True when the radio transmits on a different VFO than it receives on.</summary>
    public bool Split { get; private set; }

    /// <summary>
    /// The frequency about to be transmitted on. Band-following gear must use this:
    /// during a split QSO it differs from the receive frequency, and following the
    /// receive VFO would select the wrong band.
    /// </summary>
    public long? TransmitFrequencyHz { get; private set; }

    public string? Mode { get; private set; }

    /// <summary>VFO B's frequency, read directly rather than inferred from split, so
    /// the second dial is shown whether or not the radio is split.</summary>
    public long VfoBHz { get; private set; }

    public string? ModeB { get; private set; }

    public bool? Transmitting { get; private set; }

    public bool Connected { get; private set; }

    /// <summary>Cleared if the backend cannot read a named VFO, after which only the
    /// selected one is polled.</summary>
    private bool _vfoInfoSupported = true;

    /// <summary>How often to refresh VFO B while it is NOT the transmit VFO — it is
    /// a resting dial then, and skipping it keeps the transmit VFO's poll quick.</summary>
    private const int VfoBEveryNthCycle = 8;

    private int _cyclesSinceVfoB = VfoBEveryNthCycle;

    public event Action<long>? FrequencyChanged;

    public event Action<long>? VfoBChanged;

    public event Action<string>? ModeBChanged;

    public event Action<long>? TransmitFrequencyChanged;

    public event Action<string>? ModeChanged;

    public event Action<bool>? TransmitChanged;

    public void Start() => _loop = Task.Run(() => RunAsync(_cts.Token));

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync("127.0.0.1", port, ct);
                Connected = true;
                logger.LogInformation("rigctld client poller connected to localhost:{Port}", port);

                var stream = client.GetStream();
                using var reader = new StreamReader(stream);
                using var writer = new StreamWriter(stream) { AutoFlush = true };
                using var timer = new PeriodicTimer(interval);

                while (await timer.WaitForNextTickAsync(ct))
                {
                    await PollOnceAsync(reader, writer, ct);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Connected = false;
                logger.LogDebug("rigctld poller (port {Port}) disconnected: {Message}; retrying", port, ex.Message);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private async Task PollOnceAsync(StreamReader reader, StreamWriter writer, CancellationToken ct)
    {
        if (_vfoInfoSupported)
        {
            await PollBothVfosAsync(reader, writer, ct);
        }
        else
        {
            await PollCurrentVfoOnlyAsync(reader, writer, ct);
        }

        await PollPttAsync(reader, writer, ct);
    }

    /// <summary>
    /// Reads both VFOs with "get_vfo_info", which answers exactly five lines —
    /// frequency, mode, passband, split, satmode — for a NAMED VFO without
    /// disturbing which one the radio has selected. Two of these give both dials
    /// and both modes in fewer exchanges than asking for each piece separately,
    /// and VFO B stays known whether or not split is on.
    /// </summary>
    private async Task PollBothVfosAsync(StreamReader reader, StreamWriter writer, CancellationToken ct)
    {
        var a = await AskAsync(reader, writer, "\\get_vfo_info VFOA", 5, ct);
        if (a is null)
        {
            // An older backend answers a single RPRT line instead. Fall back for
            // the rest of this session rather than desynchronising every poll.
            _vfoInfoSupported = false;
            logger.LogInformation("rigctld does not support get_vfo_info; reading the current VFO only");
            return;
        }

        var split = a[3] == "1";

        // Every exchange costs time on the wire, and on a serial rig that time is
        // what the dial lag is made of. VFO B is only read every cycle when it is
        // the transmit VFO — anything following the radio's band needs that one
        // promptly. Otherwise it is a resting dial and a slower refresh will do.
        var readVfoB = split || _cyclesSinceVfoB++ >= VfoBEveryNthCycle;
        string[]? b = null;
        if (readVfoB)
        {
            _cyclesSinceVfoB = 0;
            b = await AskAsync(reader, writer, "\\get_vfo_info VFOB", 5, ct);
        }

        // State is settled before any event fires: a handler reads the transmit
        // frequency straight back, and would otherwise get the previous cycle's.
        var vfoAHz = long.TryParse(a[0], out var parsedA) ? parsedA : FrequencyHz;
        var vfoBHz = b is not null && long.TryParse(b[0], out var parsedB) ? parsedB : VfoBHz;

        Split = split;
        var previousA = FrequencyHz;
        var previousB = VfoBHz;
        var previousMode = Mode;
        var previousModeB = ModeB;

        FrequencyHz = vfoAHz;
        VfoBHz = vfoBHz;
        Mode = a[1] is { Length: > 0 } modeA ? modeA : Mode;
        if (b is not null && b[1] is { Length: > 0 } modeB)
        {
            ModeB = modeB;
        }

        var transmitHz = split && VfoBHz > 0 ? VfoBHz : FrequencyHz;
        var transmitChanged = transmitHz != TransmitFrequencyHz;
        TransmitFrequencyHz = transmitHz;

        // Now that everything is consistent, tell the world.
        if (FrequencyHz != previousA && FrequencyHz is { } hzA)
        {
            FrequencyChanged?.Invoke(hzA);
        }

        if (VfoBHz != previousB && VfoBHz > 0)
        {
            VfoBChanged?.Invoke(VfoBHz);
        }

        if (Mode != previousMode && Mode is { } m)
        {
            ModeChanged?.Invoke(m);
        }

        if (ModeB != previousModeB && ModeB is { } mb)
        {
            ModeBChanged?.Invoke(mb);
        }

        if (transmitChanged && transmitHz is { } tx)
        {
            TransmitFrequencyChanged?.Invoke(tx);
        }
    }

    /// <summary>Fallback for backends without get_vfo_info: the selected VFO only,
    /// with VFO B knowable just from the split transmit frequency.</summary>
    private async Task PollCurrentVfoOnlyAsync(StreamReader reader, StreamWriter writer, CancellationToken ct)
    {
        var freq = await AskAsync(reader, writer, "f", 1, ct);
        if (freq is not null && long.TryParse(freq[0], out var hz))
        {
            ApplyVfoA(freq[0], null);
        }

        var mode = await AskAsync(reader, writer, "m", 2, ct);
        if (mode is not null)
        {
            ApplyVfoA(null, mode[0]);
        }

        var split = await AskAsync(reader, writer, "s", 2, ct);
        Split = split is not null && split[0] == "1";

        if (Split)
        {
            var tx = await AskAsync(reader, writer, "i", 1, ct);
            if (tx is not null && long.TryParse(tx[0], out var txHz) && txHz > 0)
            {
                ApplyVfoB(tx[0], null);
            }
        }

        ApplyTransmitFrequency(Split && VfoBHz > 0 ? VfoBHz : FrequencyHz);
    }

    /// <summary>Sends a command and reads its fixed number of reply lines. Returns
    /// null when the radio answers an error instead, which is a single line — reading
    /// the expected count anyway would leave the socket out of step.</summary>
    private async Task<string[]?> AskAsync(
        StreamReader reader, StreamWriter writer, string command, int lines, CancellationToken ct)
    {
        await writer.WriteAsync($"{command}\n");
        var first = await reader.ReadLineAsync(ct);
        if (first is null)
        {
            throw new IOException("rigctld closed the connection");
        }

        if (first.StartsWith("RPRT"))
        {
            return null;
        }

        var reply = new string[lines];
        reply[0] = first;
        for (var i = 1; i < lines; i++)
        {
            reply[i] = await reader.ReadLineAsync(ct) ?? string.Empty;
        }

        return reply;
    }

    private void ApplyVfoA(string? frequency, string? mode)
    {
        if (frequency is not null && long.TryParse(frequency, out var hz) && hz != FrequencyHz)
        {
            FrequencyHz = hz;
            FrequencyChanged?.Invoke(hz);
        }

        if (mode is { Length: > 0 } && mode != Mode)
        {
            Mode = mode;
            ModeChanged?.Invoke(mode);
        }
    }

    private void ApplyVfoB(string? frequency, string? mode)
    {
        if (frequency is not null && long.TryParse(frequency, out var hz) && hz != VfoBHz)
        {
            VfoBHz = hz;
            VfoBChanged?.Invoke(hz);
        }

        if (mode is { Length: > 0 } && mode != ModeB)
        {
            ModeB = mode;
            ModeBChanged?.Invoke(mode);
        }
    }

    private void ApplyTransmitFrequency(long? frequency)
    {
        if (frequency is { } hz && hz != TransmitFrequencyHz)
        {
            TransmitFrequencyHz = hz;
            TransmitFrequencyChanged?.Invoke(hz);
        }
    }

    private async Task PollPttAsync(StreamReader reader, StreamWriter writer, CancellationToken ct)
    {
        // get_ptt -> "0" or "1"
        await writer.WriteAsync("t\n");
        var pttLine = await reader.ReadLineAsync(ct);
        if (pttLine is "0" or "1")
        {
            var tx = pttLine == "1";
            if (tx != Transmitting)
            {
                Transmitting = tx;
                TransmitChanged?.Invoke(tx);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        if (_loop is not null)
        {
            try
            {
                await _loop;
            }
            catch (Exception)
            {
            }
        }

        _cts.Dispose();
    }
}
