namespace MultiCat.Core.Flex;

/// <summary>
/// The virtual radio's state: slices, the transmit and interlock objects, and the
/// object registrations clients create. Object formats mirror a real FLEX-8600,
/// because the Genius boxes are strict about them — notably the PowerGenius reads
/// its band from the <c>transmit</c> object's <c>freq</c>, not from the slice, so a
/// slice update alone leaves it showing "N/A".
/// </summary>
public sealed class FlexRadioState : IFlexRadioState
{
    /// <summary>Incoming mode names normalised to canonical Flex slice modes.</summary>
    private static readonly Dictionary<string, string> ModeMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["USB"] = "USB", ["LSB"] = "LSB", ["CW"] = "CW", ["CWR"] = "CW",
        ["AM"] = "AM", ["FM"] = "FM", ["FMN"] = "NFM",
        ["RTTY"] = "RTTY", ["RTTYR"] = "RTTY",
        ["PKTUSB"] = "DIGU", ["PKTLSB"] = "DIGL", ["PKTFM"] = "NFM",
        ["DATA"] = "DIGU", ["DIGU"] = "DIGU", ["DIGL"] = "DIGL",
    };

    private readonly Lock _gate = new();
    private readonly Dictionary<int, FlexSlice> _slices = new() { [0] = new FlexSlice() };
    private readonly Dictionary<int, (bool Frequency, bool Mode)> _pending = [];
    private readonly FlexIdentity _identity;

    private uint _nextHandle = 0x40000000;   // object handles start high, like SmartSDR
    private int _nextMeterId = 1;

    public FlexRadioState(FlexIdentity identity) => _identity = identity;

    /// <summary>Lines for clients subscribed to the slice subsystem.</summary>
    public event Action<string>? SliceLineReady;

    /// <summary>Lines for every client regardless of subscription — the interlock
    /// is sent unconditionally, because a box must never miss a key transition.</summary>
    public event Action<string>? BroadcastLineReady;

    public bool Transmitting { get; private set; }

    public IReadOnlyDictionary<int, FlexSlice> Slices => _slices;

    /// <summary>Connection handles of clients that registered as amplifiers.</summary>
    public List<string> EngagedAmplifiers { get; } = [];

    public uint AllocateHandle()
    {
        lock (_gate)
        {
            var handle = _nextHandle;
            _nextHandle += 0x01000000;
            return handle;
        }
    }

    public int AllocateMeterId()
    {
        lock (_gate)
        {
            return _nextMeterId++;
        }
    }

    public void AddAmplifier(uint handle, IReadOnlyDictionary<string, string> properties)
    {
        lock (_gate)
        {
            EngagedAmplifiers.Add($"0x{handle:X8}");
        }
    }

    public void AddMeter(int meterId, IReadOnlyDictionary<string, string> properties)
    {
    }

    private int _interlockCount;

    public int AddInterlock(IReadOnlyDictionary<string, string> properties)
    {
        lock (_gate)
        {
            return ++_interlockCount;
        }
    }

    /// <summary>Forgets every client registration — used when the radio goes absent,
    /// so the stack sees it vanish like a real Flex powering off and each box falls
    /// back to its no-transceiver antenna.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            EngagedAmplifiers.Clear();
            _pending.Clear();
            _nextMeterId = 1;
            _interlockCount = 0;
        }
    }

    // --- the transmit slice (what the amplifier follows) ---------------------
    public FlexSlice TransmitSlice()
    {
        foreach (var slice in _slices.Values)
        {
            if (slice.IsTransmitSlice)
            {
                return slice;
            }
        }

        return _slices[0];
    }

    public string TransmitStatusLine() => TransmitStatusLine("0");

    public string TransmitStatusLine(string handle)
    {
        var slice = TransmitSlice();
        return $"S{handle}|transmit freq={FlexSlice.Megahertz(slice.FrequencyHz)} rfpower=0 " +
               $"tunepower=0 tune=0 tx_slice_mode={slice.Mode} hwalc_enabled=0 inhibit=0 " +
               $"dax=0 lo=100 hi=2900 tx_filter_changes_allowed=1 tx_antenna={slice.TransmitAntenna} " +
               "max_power_level=100";
    }

    public IReadOnlyList<string> SliceStatusLines()
    {
        var lines = new List<string>();
        foreach (var index in _slices.Keys.Order())
        {
            lines.Add(_slices[index].StatusLine());
        }

        return lines;
    }

    // --- updates from the radio ---------------------------------------------
    /// <summary>
    /// Applies a change from the rig. Frequency and mode changes are collected as
    /// deltas for <see cref="EmitPending"/>; moving the transmit designation is
    /// structural and resends the full picture immediately.
    /// </summary>
    public void UpdateSlice(int index = 0, long? frequencyHz = null, string? mode = null, bool? isTransmitSlice = null)
    {
        bool frequencyChanged = false, modeChanged = false, structural = false;

        lock (_gate)
        {
            if (!_slices.TryGetValue(index, out var slice))
            {
                slice = new FlexSlice { Index = index };
                _slices[index] = slice;
            }

            if (frequencyHz is { } hz && hz != slice.FrequencyHz)
            {
                slice.FrequencyHz = hz;
                frequencyChanged = true;
            }

            if (mode is not null)
            {
                var mapped = ModeMap.TryGetValue(mode, out var m) ? m : mode.ToUpperInvariant();
                if (mapped != slice.Mode)
                {
                    slice.Mode = mapped;
                    modeChanged = true;
                }
            }

            if (isTransmitSlice is { } tx && tx != slice.IsTransmitSlice)
            {
                slice.IsTransmitSlice = tx;
                structural = true;
            }

            if (!frequencyChanged && !modeChanged && !structural)
            {
                return;
            }

            if (!structural)
            {
                var flags = _pending.TryGetValue(index, out var existing)
                    ? existing
                    : (Frequency: false, Mode: false);
                _pending[index] = (flags.Frequency || frequencyChanged, flags.Mode || modeChanged);
            }
            else
            {
                _pending.Remove(index);
            }
        }

        if (structural)
        {
            BroadcastSlice(index);
        }
    }

    /// <summary>
    /// Sends one terse delta per changed slice, built from current state — values
    /// superseded since the last emit are simply skipped. Deltas carry only the keys
    /// that changed, as a real Flex does: the boxes are embedded parsers, and full
    /// dumps on every dial click were what made the display trail the dial.
    /// </summary>
    public void EmitPending()
    {
        Dictionary<int, (bool Frequency, bool Mode)> pending;
        lock (_gate)
        {
            if (_pending.Count == 0)
            {
                return;
            }

            pending = new Dictionary<int, (bool, bool)>(_pending);
            _pending.Clear();
        }

        foreach (var (index, flags) in pending)
        {
            if (!_slices.TryGetValue(index, out var slice))
            {
                continue;
            }

            var sliceDelta = new List<string> { $"S0|slice {slice.Index}" };
            var transmitDelta = new List<string> { "S0|transmit" };

            if (flags.Frequency)
            {
                var mhz = FlexSlice.Megahertz(slice.FrequencyHz);
                sliceDelta.Add($"RF_frequency={mhz}");
                transmitDelta.Add($"freq={mhz}");
            }

            if (flags.Mode)
            {
                sliceDelta.Add($"mode={slice.Mode}");
                transmitDelta.Add($"tx_slice_mode={slice.Mode}");
            }

            SliceLineReady?.Invoke(string.Join(" ", sliceDelta));
            SliceLineReady?.Invoke(string.Join(" ", transmitDelta));
        }
    }

    public bool HasPendingUpdates
    {
        get
        {
            lock (_gate)
            {
                return _pending.Count > 0;
            }
        }
    }

    /// <summary>Full status for a slice — used at subscribe time and after a
    /// structural change. The amplifier follows the transmit object, so both go.</summary>
    public void BroadcastSlice(int index)
    {
        if (!_slices.TryGetValue(index, out var slice))
        {
            return;
        }

        SliceLineReady?.Invoke(slice.StatusLine());
        SliceLineReady?.Invoke(TransmitStatusLine());
    }

    // --- transmit / interlock ------------------------------------------------
    public string InterlockConfigLine() =>
        "S0|interlock acc_txreq_enable=0 rca_txreq_enable=0 acc_tx_enabled=1 " +
        "tx1_enabled=1 tx2_enabled=1 tx3_enabled=1 tx_delay=0 acc_tx_delay=0 " +
        "tx1_delay=0 tx2_delay=0 tx3_delay=0 acc_txreq_polarity=0 " +
        "rca_txreq_polarity=0 timeout=0";

    public string InterlockStatusLine() => InterlockStatusLine(null);

    /// <summary>
    /// Interlock state as a real 8600 reports it. Idle is READY; a key runs
    /// READY -> PTT_REQUESTED -> TRANSMITTING with the engaged amplifier handles in
    /// <c>amplifier=</c> (what an amp needs in order to LAN-key), then
    /// UNKEY_REQUESTED -> READY on release.
    /// </summary>
    public string InterlockStatusLine(string? state)
    {
        state ??= Transmitting ? "TRANSMITTING" : "READY";
        var keyed = state is "PTT_REQUESTED" or "TRANSMITTING" or "UNKEY_REQUESTED";
        var txClientHandle = keyed ? FlexSlice.GuiClientHandle : "0x00000000";
        var source = state is "PTT_REQUESTED" or "TRANSMITTING" ? "SW" : string.Empty;
        var amplifiers = state is "TRANSMITTING" or "UNKEY_REQUESTED"
            ? string.Join(",", EngagedAmplifiers)
            : string.Empty;

        return $"S0|interlock tx_client_handle={txClientHandle} state={state} " +
               $"reason= source={source} tx_allowed=1 amplifier={amplifiers}";
    }

    /// <summary>
    /// Keys or unkeys. Never paced and never coalesced: the amplifier sequences off
    /// these transitions, so both steps of each edge are sent immediately and to
    /// every client, subscribed or not.
    /// </summary>
    public void SetTransmit(bool transmitting)
    {
        if (transmitting == Transmitting)
        {
            return;
        }

        Transmitting = transmitting;
        if (transmitting)
        {
            BroadcastLineReady?.Invoke(InterlockStatusLine("PTT_REQUESTED"));
            BroadcastLineReady?.Invoke(InterlockStatusLine("TRANSMITTING"));
        }
        else
        {
            BroadcastLineReady?.Invoke(InterlockStatusLine("UNKEY_REQUESTED"));
            BroadcastLineReady?.Invoke(InterlockStatusLine("READY"));
        }
    }

    // --- radio object (sent on connect) --------------------------------------
    public string RadioStatusLine() =>
        "S0|radio slices=1 panadapters=1 lineout_gain=50 lineout_mute=0 " +
        "headphone_gain=0 headphone_mute=0 remote_on_enabled=0 pll_done=0 " +
        "freq_error_ppb=0 cal_freq=15.000000 tnf_enabled=0 " +
        $"nickname={_identity.Nickname.Replace(' ', '_')} callsign={_identity.Callsign} " +
        "binaural_rx=0 full_duplex_enabled=0 band_persistence_enabled=1 " +
        "rtty_mark_default=2125 backlight=50 daxiq_capacity=16 daxiq_available=16 " +
        "low_latency_digital_modes=0 mf_enable=1 auto_save=1 external_pa_allowed=1";
}
