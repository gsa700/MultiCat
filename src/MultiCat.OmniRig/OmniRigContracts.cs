// OmniRig COM contract, transcribed from OmniRig.ridl in VE3NEA's OmniRig
// (https://github.com/VE3NEA/OmniRig, MIT license, (c) Alex Shovkoplyas).
// GUIDs, dispids, and member order (= vtable order) must match exactly —
// clients early-bind against these through interop assemblies built from
// the original type library.

using System.Runtime.InteropServices;

namespace MultiCat.OmniRig;

public static class OmniRigGuids
{
    public const string TypeLib = "4FE359C5-A58F-459D-BE95-CA559FB4F270";
    public const string IOmniRigX = "501A2858-3331-467A-837A-989FDEDACC7D";
    public const string IRigX = "D30A7E51-5862-45B7-BFFA-6415917DA0CF";
    public const string IPortBits = "3DEE2CC8-1EA3-46E7-B8B4-3E7321F2446A";
    public const string IOmniRigXEvents = "2219175F-E561-47E7-AD17-73C4D8891AA1";
    public const string OmniRigXClass = "0839E8C6-ED30-4950-8087-966F970F0CAE";
    public const string ProgId = "OmniRig.OmniRigX";
}

[Flags]
public enum RigParamX
{
    PM_UNKNOWN = 1,
    PM_FREQ = 2,
    PM_FREQA = 4,
    PM_FREQB = 8,
    PM_PITCH = 16,
    PM_RITOFFSET = 32,
    PM_RIT0 = 64,
    PM_VFOAA = 128,
    PM_VFOAB = 256,
    PM_VFOBA = 512,
    PM_VFOBB = 1024,
    PM_VFOA = 2048,
    PM_VFOB = 4096,
    PM_VFOEQUAL = 8192,
    PM_VFOSWAP = 16384,
    PM_SPLITON = 32768,
    PM_SPLITOFF = 65536,
    PM_RITON = 131072,
    PM_RITOFF = 262144,
    PM_XITON = 524288,
    PM_XITOFF = 1048576,
    PM_RX = 2097152,
    PM_TX = 4194304,
    PM_CW_U = 8388608,
    PM_CW_L = 16777216,
    PM_SSB_U = 33554432,
    PM_SSB_L = 67108864,
    PM_DIG_U = 134217728,
    PM_DIG_L = 268435456,
    PM_AM = 536870912,
    PM_FM = 1073741824,
}

public enum RigStatusX
{
    ST_NOTCONFIGURED = 0,
    ST_DISABLED = 1,
    ST_PORTBUSY = 2,
    ST_NOTRESPONDING = 3,
    ST_ONLINE = 4,
}

[ComVisible(true)]
[Guid(OmniRigGuids.IOmniRigX)]
[InterfaceType(ComInterfaceType.InterfaceIsDual)]
public interface IOmniRigX
{
    [DispId(1)]
    int InterfaceVersion { get; }

    [DispId(2)]
    int SoftwareVersion { get; }

    [DispId(3)]
    IRigX Rig1 { get; }

    [DispId(4)]
    IRigX Rig2 { get; }

    [DispId(5)]
    bool DialogVisible
    {
        [return: MarshalAs(UnmanagedType.VariantBool)]
        get;
        [param: MarshalAs(UnmanagedType.VariantBool)]
        set;
    }
}

[ComVisible(true)]
[Guid(OmniRigGuids.IRigX)]
[InterfaceType(ComInterfaceType.InterfaceIsDual)]
public interface IRigX
{
    [DispId(1)]
    string RigType
    {
        [return: MarshalAs(UnmanagedType.BStr)]
        get;
    }

    [DispId(2)]
    int ReadableParams { get; }

    [DispId(3)]
    int WriteableParams { get; }

    [DispId(4)]
    [return: MarshalAs(UnmanagedType.VariantBool)]
    bool IsParamReadable(RigParamX param);

    [DispId(5)]
    [return: MarshalAs(UnmanagedType.VariantBool)]
    bool IsParamWriteable(RigParamX param);

    [DispId(6)]
    RigStatusX Status { get; }

    [DispId(7)]
    string StatusStr
    {
        [return: MarshalAs(UnmanagedType.BStr)]
        get;
    }

    [DispId(8)]
    int Freq { get; set; }

    [DispId(9)]
    int FreqA { get; set; }

    [DispId(10)]
    int FreqB { get; set; }

    [DispId(11)]
    int RitOffset { get; set; }

    [DispId(12)]
    int Pitch { get; set; }

    [DispId(13)]
    RigParamX Vfo { get; set; }

    [DispId(14)]
    RigParamX Split { get; set; }

    [DispId(15)]
    RigParamX Rit { get; set; }

    [DispId(16)]
    RigParamX Xit { get; set; }

    [DispId(17)]
    RigParamX Tx { get; set; }

    [DispId(18)]
    RigParamX Mode { get; set; }

    [DispId(19)]
    void ClearRit();

    [DispId(20)]
    void SetSimplexMode(int freq);

    [DispId(21)]
    void SetSplitMode(int rxFreq, int txFreq);

    [DispId(22)]
    int FrequencyOfTone(int tone);

    [DispId(23)]
    void SendCustomCommand(object command, int replyLength, object replyEnd);

    [DispId(24)]
    int GetRxFrequency();

    [DispId(25)]
    int GetTxFrequency();

    [DispId(26)]
    IPortBits PortBits { get; }
}

[ComVisible(true)]
[Guid(OmniRigGuids.IPortBits)]
[InterfaceType(ComInterfaceType.InterfaceIsDual)]
public interface IPortBits
{
    [DispId(1)]
    [return: MarshalAs(UnmanagedType.VariantBool)]
    bool Lock();

    [DispId(2)]
    bool Rts
    {
        [return: MarshalAs(UnmanagedType.VariantBool)]
        get;
        [param: MarshalAs(UnmanagedType.VariantBool)]
        set;
    }

    [DispId(3)]
    bool Dtr
    {
        [return: MarshalAs(UnmanagedType.VariantBool)]
        get;
        [param: MarshalAs(UnmanagedType.VariantBool)]
        set;
    }

    [DispId(4)]
    bool Cts
    {
        [return: MarshalAs(UnmanagedType.VariantBool)]
        get;
    }

    [DispId(5)]
    bool Dsr
    {
        [return: MarshalAs(UnmanagedType.VariantBool)]
        get;
    }

    [DispId(6)]
    void Unlock();
}

/// <summary>Event dispids on the IOmniRigXEvents dispinterface.</summary>
public static class OmniRigEventIds
{
    public const int VisibleChange = 1;
    public const int RigTypeChange = 2;
    public const int StatusChange = 3;
    public const int ParamsChange = 4;
    public const int CustomReply = 5;
}
