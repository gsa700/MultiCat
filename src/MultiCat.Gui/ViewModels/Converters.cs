using Avalonia.Data.Converters;
using Avalonia.Media;

namespace MultiCat.Gui.ViewModels;

public static class Converters
{
    private static readonly IBrush Active = new SolidColorBrush(Color.Parse("#3B9E5F"));
    private static readonly IBrush Idle = new SolidColorBrush(Color.Parse("#8E8E8E"));

    private static readonly IBrush ArrowLit = new SolidColorBrush(Color.Parse("#E8A33D"));
    private static readonly IBrush ArrowDark = new SolidColorBrush(Color.Parse("#00000000"));

    /// <summary>
    /// The transmit VFO reads at full strength and the other is dimmed, so both stay
    /// visible but the live one is unmistakable. Done with opacity rather than a
    /// colour so it holds up in a light or a dark theme — a fixed "bright" colour is
    /// bright in only one of them.
    /// </summary>
    public static readonly IValueConverter VfoOpacity =
        new FuncValueConverter<bool, double>(isTransmit => isTransmit ? 1.0 : 0.4);

    public static readonly IValueConverter ArrowBrush =
        new FuncValueConverter<bool, IBrush>(isLit => isLit ? ArrowLit : ArrowDark);

    public static readonly IValueConverter ActiveBrush =
        new FuncValueConverter<bool, IBrush>(isActive => isActive ? Active : Idle);
}
