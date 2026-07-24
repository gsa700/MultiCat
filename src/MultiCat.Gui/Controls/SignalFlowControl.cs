using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;

namespace MultiCat.Gui.Controls;

/// <summary>
/// The signal-flow diagram: radio on the left, the MultiCAT hub in the middle,
/// client ports fanned out on the right. Animated pulses ride the links —
/// amber toward the radio (commands), teal toward the clients (responses).
/// </summary>
public class SignalFlowControl : Control
{
    public static readonly StyledProperty<string?> RadioNameProperty =
        AvaloniaProperty.Register<SignalFlowControl, string?>(nameof(RadioName), "Radio");

    public static readonly StyledProperty<IEnumerable<string>?> PortsProperty =
        AvaloniaProperty.Register<SignalFlowControl, IEnumerable<string>?>(nameof(Ports));

    /// <summary>Bumped by the view model on each real activity event; a rising value
    /// spawns one pulse, so the animation tracks actual traffic rather than a timer.</summary>
    public static readonly StyledProperty<long> ActivityTickProperty =
        AvaloniaProperty.Register<SignalFlowControl, long>(nameof(ActivityTick));

    public static readonly StyledProperty<bool> LastTowardRadioProperty =
        AvaloniaProperty.Register<SignalFlowControl, bool>(nameof(LastTowardRadio));

    private static readonly IBrush CommandBrush = new SolidColorBrush(Color.Parse("#EF9F27"));
    private static readonly IBrush ResponseBrush = new SolidColorBrush(Color.Parse("#1D9E75"));
    private static readonly IPen LinkPen = new Pen(new SolidColorBrush(Color.Parse("#808080"), 0.35), 1.5);

    private readonly List<Pulse> _pulses = [];
    private readonly Random _random = new();
    private DispatcherTimer? _timer;

    static SignalFlowControl()
    {
        AffectsRender<SignalFlowControl>(RadioNameProperty, PortsProperty);
    }

    public string? RadioName
    {
        get => GetValue(RadioNameProperty);
        set => SetValue(RadioNameProperty, value);
    }

    public IEnumerable<string>? Ports
    {
        get => GetValue(PortsProperty);
        set => SetValue(PortsProperty, value);
    }

    public long ActivityTick
    {
        get => GetValue(ActivityTickProperty);
        set => SetValue(ActivityTickProperty, value);
    }

    public bool LastTowardRadio
    {
        get => GetValue(LastTowardRadioProperty);
        set => SetValue(LastTowardRadioProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ActivityTickProperty)
        {
            SpawnPulse(LastTowardRadio);
        }
    }

    private void SpawnPulse(bool towardRadio)
    {
        if (_pulses.Count >= 12)
        {
            return;
        }

        // Radio-bound traffic pulses the radio↔hub link; client-bound traffic
        // pulses one of the client links so both halves of the mux show life.
        var portCount = Ports?.Count() ?? 0;
        var link = towardRadio || portCount == 0 ? 0 : 1 + _random.Next(portCount);
        _pulses.Add(new Pulse { Link = link, TowardRadio = towardRadio });
    }

    private sealed class Pulse
    {
        public int Link;
        public double T;
        public bool TowardRadio;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, OnTick);
        _timer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _timer?.Stop();
        _timer = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_pulses.Count == 0)
        {
            return; // nothing moving; idle when there's no traffic
        }

        for (var i = _pulses.Count - 1; i >= 0; i--)
        {
            _pulses[i].T += 0.022;
            if (_pulses[i].T >= 1.0)
            {
                _pulses.RemoveAt(i);
            }
        }

        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var bounds = Bounds;
        if (bounds.Width < 200 || bounds.Height < 80)
        {
            return;
        }

        var isDark = ActualThemeVariant == ThemeVariant.Dark;
        var textBrush = new SolidColorBrush(isDark ? Color.Parse("#E8E8E8") : Color.Parse("#2C2C2A"));
        var mutedBrush = new SolidColorBrush(Color.Parse("#8E8E8E"));

        var ports = Ports?.ToList() ?? [];
        var cy = bounds.Height / 2;

        var radioRect = new Rect(12, cy - 24, 118, 48);
        var hubRect = new Rect((bounds.Width - 112) / 2, cy - 26, 112, 52);
        var portWidth = 126.0;
        var portX = bounds.Width - portWidth - 12;

        var portRects = new List<Rect>();
        if (ports.Count > 0)
        {
            var step = (bounds.Height - 16) / ports.Count;
            for (var i = 0; i < ports.Count; i++)
            {
                var y = 8 + (step * i) + ((step - 26) / 2);
                portRects.Add(new Rect(portX, y, portWidth, 26));
            }
        }

        var links = new List<(Point P0, Point P1, Point P2, Point P3)>
        {
            Bezier(new Point(radioRect.Right, radioRect.Center.Y), new Point(hubRect.Left, hubRect.Center.Y)),
        };
        foreach (var rect in portRects)
        {
            links.Add(Bezier(new Point(hubRect.Right, hubRect.Center.Y), new Point(rect.Left, rect.Center.Y)));
        }

        foreach (var link in links)
        {
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(link.P0, false);
                ctx.CubicBezierTo(link.P1, link.P2, link.P3);
                ctx.EndFigure(false);
            }

            context.DrawGeometry(null, LinkPen, geometry);
        }

        foreach (var pulse in _pulses)
        {
            if (pulse.Link >= links.Count)
            {
                continue;
            }

            var t = pulse.TowardRadio ? 1.0 - pulse.T : pulse.T;
            var point = PointOnBezier(links[pulse.Link], t);
            context.DrawEllipse(pulse.TowardRadio ? CommandBrush : ResponseBrush, null, point, 3.5, 3.5);
        }

        DrawNode(context, radioRect, RadioName ?? "Radio", "#378ADD", isDark, textBrush, 12);
        DrawNode(context, hubRect, "MultiCAT", "#7F77DD", isDark, textBrush, 13);
        for (var i = 0; i < portRects.Count; i++)
        {
            DrawNode(context, portRects[i], ports[i], "#8E8E8E", isDark, mutedBrush, 11);
        }
    }

    private static (Point, Point, Point, Point) Bezier(Point from, Point to)
    {
        var dx = (to.X - from.X) * 0.5;
        return (from, new Point(from.X + dx, from.Y), new Point(to.X - dx, to.Y), to);
    }

    private static Point PointOnBezier((Point P0, Point P1, Point P2, Point P3) b, double t)
    {
        var u = 1 - t;
        var x = (u * u * u * b.P0.X) + (3 * u * u * t * b.P1.X) + (3 * u * t * t * b.P2.X) + (t * t * t * b.P3.X);
        var y = (u * u * u * b.P0.Y) + (3 * u * u * t * b.P1.Y) + (3 * u * t * t * b.P2.Y) + (t * t * t * b.P3.Y);
        return new Point(x, y);
    }

    private static void DrawNode(
        DrawingContext context, Rect rect, string label, string accentHex, bool isDark, IBrush textBrush, double fontSize)
    {
        var accent = Color.Parse(accentHex);
        var fill = new SolidColorBrush(accent, isDark ? 0.18 : 0.10);
        var stroke = new Pen(new SolidColorBrush(accent, 0.8), 1);
        context.DrawRectangle(fill, stroke, rect, 8, 8);

        var text = new FormattedText(
            label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Inter, Segoe UI"), fontSize, textBrush)
        {
            MaxTextWidth = rect.Width - 12,
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        context.DrawText(text, new Point(
            rect.X + ((rect.Width - Math.Min(text.Width, rect.Width - 12)) / 2),
            rect.Y + ((rect.Height - text.Height) / 2)));
    }
}
