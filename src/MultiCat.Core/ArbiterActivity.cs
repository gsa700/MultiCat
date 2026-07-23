namespace MultiCat.Core;

public enum ArbiterActivityKind
{
    CommandSent,
    SetSent,
    ResponseReceived,
    CacheHit,
    Timeout,
    Unsolicited,
}

/// <summary>One traffic-monitor line: what moved, for whom, and how it was handled.</summary>
public readonly record struct ArbiterActivity(string? ClientId, ArbiterActivityKind Kind, CatFrame Frame);
