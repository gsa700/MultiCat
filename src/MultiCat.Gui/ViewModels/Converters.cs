using Avalonia.Data.Converters;
using Avalonia.Media;

namespace MultiCat.Gui.ViewModels;

public static class Converters
{
    private static readonly IBrush Active = new SolidColorBrush(Color.Parse("#3B9E5F"));
    private static readonly IBrush Idle = new SolidColorBrush(Color.Parse("#8E8E8E"));

    public static readonly IValueConverter ActiveBrush =
        new FuncValueConverter<bool, IBrush>(isActive => isActive ? Active : Idle);
}
