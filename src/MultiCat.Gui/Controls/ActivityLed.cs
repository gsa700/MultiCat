using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace MultiCat.Gui.Controls;

/// <summary>
/// A CAT activity LED, ham-shack style: flashes bright green on each activity event
/// and decays over ~700 ms to a steady dim green while the radio is connected —
/// "alive, just quiet". Gray when disconnected. Bind LastActivity to the radio's
/// last-event timestamp; each change is one flash.
/// </summary>
public class ActivityLed : Control
{
    public static readonly StyledProperty<DateTime?> LastActivityProperty =
        AvaloniaProperty.Register<ActivityLed, DateTime?>(nameof(LastActivity));

    public static readonly StyledProperty<bool> IsConnectedProperty =
        AvaloniaProperty.Register<ActivityLed, bool>(nameof(IsConnected));

    private static readonly Color Active = Color.Parse("#2ECC71");
    private static readonly Color IdleGreen = Color.Parse("#1D7A4C");
    private static readonly Color Offline = Color.Parse("#707070");

    private DispatcherTimer? _timer;

    public DateTime? LastActivity
    {
        get => GetValue(LastActivityProperty);
        set => SetValue(LastActivityProperty, value);
    }

    public bool IsConnected
    {
        get => GetValue(IsConnectedProperty);
        set => SetValue(IsConnectedProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(14, 14);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(80), DispatcherPriority.Render,
            (_, _) => InvalidateVisual());
        _timer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _timer?.Stop();
        _timer = null;
        base.OnDetachedFromVisualTree(e);
    }

    public override void Render(DrawingContext context)
    {
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);

        if (!IsConnected)
        {
            context.DrawEllipse(new SolidColorBrush(Offline, 0.6), null, center, 4, 4);
            return;
        }

        // 0 = just flashed, 1 = fully decayed to idle.
        var decay = 1.0;
        if (LastActivity is { } last)
        {
            decay = Math.Clamp((DateTime.Now - last).TotalMilliseconds / 700.0, 0.0, 1.0);
        }

        var color = Lerp(Active, IdleGreen, decay);
        var radius = 4.0 + ((1.0 - decay) * 1.5);

        // Fresh activity gets a soft halo that fades with the flash.
        if (decay < 1.0)
        {
            context.DrawEllipse(new SolidColorBrush(Active, 0.25 * (1.0 - decay)), null, center, radius + 3, radius + 3);
        }

        context.DrawEllipse(new SolidColorBrush(color), null, center, radius, radius);
    }

    private static Color Lerp(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + ((b.R - a.R) * t)),
        (byte)(a.G + ((b.G - a.G) * t)),
        (byte)(a.B + ((b.B - a.B) * t)));
}
