using System.Runtime.InteropServices;

namespace MultiCat.OmniRig;

[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
[ComDefaultInterface(typeof(IRigX))]
public sealed class RigXImpl : IRigX
{
    private const RigParamX SupportedParams =
        RigParamX.PM_FREQ | RigParamX.PM_FREQA | RigParamX.PM_RX | RigParamX.PM_TX |
        RigParamX.PM_CW_U | RigParamX.PM_CW_L | RigParamX.PM_SSB_U | RigParamX.PM_SSB_L |
        RigParamX.PM_DIG_U | RigParamX.PM_DIG_L | RigParamX.PM_AM | RigParamX.PM_FM;

    private readonly RigctldClient? _client;
    private readonly int _rigNumber;
    private readonly Action<int, int>? _paramsChanged;
    private readonly Action<int>? _statusChanged;
    private readonly System.Timers.Timer? _pollTimer;
    private readonly PortBitsImpl _portBits = new();

    private long _lastFreq;
    private string _lastMode = "USB";
    private bool _lastPtt;
    private RigStatusX _lastStatus = RigStatusX.ST_NOTCONFIGURED;

    public RigXImpl(RigctldClient? client, int rigNumber, Action<int, int>? paramsChanged, Action<int>? statusChanged)
    {
        _client = client;
        _rigNumber = rigNumber;
        _paramsChanged = paramsChanged;
        _statusChanged = statusChanged;

        if (_client is not null)
        {
            Poll();
            _pollTimer = new System.Timers.Timer(500) { AutoReset = true };
            _pollTimer.Elapsed += (_, _) => Poll();
            _pollTimer.Start();
        }
    }

    private void Poll()
    {
        if (_client is null)
        {
            return;
        }

        var freq = _client.GetFrequency();
        var status = freq is null ? RigStatusX.ST_NOTRESPONDING : RigStatusX.ST_ONLINE;
        if (status != _lastStatus)
        {
            _lastStatus = status;
            _statusChanged?.Invoke(_rigNumber);
        }

        if (freq is null)
        {
            return;
        }

        var changed = 0;
        if (freq.Value != _lastFreq)
        {
            _lastFreq = freq.Value;
            changed |= (int)(RigParamX.PM_FREQ | RigParamX.PM_FREQA);
        }

        var mode = _client.GetMode();
        if (mode is not null && mode != _lastMode)
        {
            _lastMode = mode;
            changed |= (int)ModeToParam(mode);
        }

        var ptt = _client.GetPtt();
        if (ptt is not null && ptt.Value != _lastPtt)
        {
            _lastPtt = ptt.Value;
            changed |= (int)(ptt.Value ? RigParamX.PM_TX : RigParamX.PM_RX);
        }

        if (changed != 0)
        {
            _paramsChanged?.Invoke(_rigNumber, changed);
        }
    }

    private static RigParamX ModeToParam(string mode) => mode switch
    {
        "USB" => RigParamX.PM_SSB_U,
        "LSB" => RigParamX.PM_SSB_L,
        "CW" => RigParamX.PM_CW_U,
        "CWR" => RigParamX.PM_CW_L,
        "PKTUSB" or "RTTY" or "DATA" => RigParamX.PM_DIG_U,
        "PKTLSB" or "RTTYR" => RigParamX.PM_DIG_L,
        "AM" => RigParamX.PM_AM,
        "FM" => RigParamX.PM_FM,
        _ => RigParamX.PM_UNKNOWN,
    };

    private static string? ParamToMode(RigParamX param) => param switch
    {
        RigParamX.PM_SSB_U => "USB",
        RigParamX.PM_SSB_L => "LSB",
        RigParamX.PM_CW_U => "CW",
        RigParamX.PM_CW_L => "CWR",
        RigParamX.PM_DIG_U => "PKTUSB",
        RigParamX.PM_DIG_L => "PKTLSB",
        RigParamX.PM_AM => "AM",
        RigParamX.PM_FM => "FM",
        _ => null,
    };

    public string RigType => _client is null ? "" : "MultiCAT";

    public int ReadableParams => _client is null ? 0 : (int)SupportedParams;

    public int WriteableParams => _client is null ? 0 : (int)SupportedParams;

    public bool IsParamReadable(RigParamX param) => (ReadableParams & (int)param) == (int)param;

    public bool IsParamWriteable(RigParamX param) => (WriteableParams & (int)param) == (int)param;

    public RigStatusX Status => _client is null ? RigStatusX.ST_NOTCONFIGURED : _lastStatus;

    public string StatusStr => Status switch
    {
        RigStatusX.ST_NOTCONFIGURED => "Rig is not configured",
        RigStatusX.ST_DISABLED => "Rig is disabled",
        RigStatusX.ST_PORTBUSY => "Port is not available",
        RigStatusX.ST_NOTRESPONDING => "Rig is not responding",
        _ => "On-line",
    };

    public int Freq
    {
        get => (int)(_client?.GetFrequency() ?? _lastFreq);
        set
        {
            if (_client?.SetFrequency(value) == true)
            {
                _lastFreq = value;
            }
        }
    }

    public int FreqA
    {
        get => Freq;
        set => Freq = value;
    }

    public int FreqB { get; set; }

    public int RitOffset { get; set; }

    public int Pitch { get; set; }

    public RigParamX Vfo
    {
        get => RigParamX.PM_VFOA;
        set { }
    }

    public RigParamX Split
    {
        get => RigParamX.PM_SPLITOFF;
        set { }
    }

    public RigParamX Rit
    {
        get => RigParamX.PM_RITOFF;
        set { }
    }

    public RigParamX Xit
    {
        get => RigParamX.PM_XITOFF;
        set { }
    }

    public RigParamX Tx
    {
        get => _lastPtt ? RigParamX.PM_TX : RigParamX.PM_RX;
        set
        {
            if (_client?.SetPtt(value == RigParamX.PM_TX) == true)
            {
                _lastPtt = value == RigParamX.PM_TX;
            }
        }
    }

    public RigParamX Mode
    {
        get
        {
            var mode = _client?.GetMode();
            if (mode is not null)
            {
                _lastMode = mode;
            }

            return ModeToParam(_lastMode);
        }
        set
        {
            if (ParamToMode(value) is { } mode && _client?.SetMode(mode) == true)
            {
                _lastMode = mode;
            }
        }
    }

    public void ClearRit()
    {
    }

    public void SetSimplexMode(int freq) => Freq = freq;

    public void SetSplitMode(int rxFreq, int txFreq) => Freq = rxFreq;

    public int FrequencyOfTone(int tone) => Freq;

    public void SendCustomCommand(object command, int replyLength, object replyEnd)
    {
    }

    public int GetRxFrequency() => Freq;

    public int GetTxFrequency() => Freq;

    public IPortBits PortBits => _portBits;
}

[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
[ComDefaultInterface(typeof(IPortBits))]
public sealed class PortBitsImpl : IPortBits
{
    public bool Lock() => false;

    public bool Rts { get; set; }

    public bool Dtr { get; set; }

    public bool Cts => false;

    public bool Dsr => false;

    public void Unlock()
    {
    }
}
