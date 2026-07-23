namespace MultiCat.Gui.ViewModels;

public sealed class ClientPortViewModel
{
    public required string PortDisplay { get; init; }

    public required string Label { get; init; }

    public required string Ptt { get; init; }

    public required string Status { get; init; }

    public bool IsActive { get; init; }

    /// <summary>Short label used by the signal-flow diagram node.</summary>
    public string FlowLabel => Label.Length > 0 && Label != "spare" ? Label : PortDisplay;
}
