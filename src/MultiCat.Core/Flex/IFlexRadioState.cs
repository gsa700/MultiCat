namespace MultiCat.Core.Flex;

/// <summary>
/// The radio state a client session reads and mutates. Kept behind an interface so
/// the protocol can be exercised without a radio, and so slice/interlock modelling
/// can evolve independently of the wire handling.
/// </summary>
public interface IFlexRadioState
{
    /// <summary>Allocates a connection or object handle (Flex handles are 32-bit).</summary>
    uint AllocateHandle();

    int AllocateMeterId();

    /// <summary>Status line describing the radio itself, sent on connect.</summary>
    string RadioStatusLine();

    /// <summary>Interlock configuration, sent on connect.</summary>
    string InterlockConfigLine();

    /// <summary>Current interlock state — sent on connect so the transmit path is
    /// valid from the first moment rather than only after the first change.</summary>
    string InterlockStatusLine();

    string TransmitStatusLine();

    /// <summary>One status line per slice, in index order.</summary>
    IReadOnlyList<string> SliceStatusLines();

    void AddAmplifier(uint handle, IReadOnlyDictionary<string, string> properties);

    void AddMeter(int meterId, IReadOnlyDictionary<string, string> properties);

    /// <summary>Registers an interlock and returns its id.</summary>
    int AddInterlock(IReadOnlyDictionary<string, string> properties);
}
