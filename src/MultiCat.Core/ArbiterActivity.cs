namespace MultiCat.Core;

public enum ArbiterActivityKind
{
    CommandSent,
    SetSent,
    ResponseReceived,
    CacheHit,
    Timeout,
    Unsolicited,

    /// <summary>A rigctld-protocol command from a relayed client (Log4OM, WSJT-X, …),
    /// observed by MultiCAT's relay on its way to rigctld and the radio.</summary>
    ClientCommand,

    /// <summary>rigctld's reply heading back to a relayed client (pulse only).</summary>
    ClientResponse,
}

/// <summary>One traffic-monitor line: what moved, for whom, and how it was handled.</summary>
public readonly record struct ArbiterActivity(string? ClientId, ArbiterActivityKind Kind, CatFrame Frame);
