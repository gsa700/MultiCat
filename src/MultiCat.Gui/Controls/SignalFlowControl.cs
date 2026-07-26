using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using MultiCat.Gui.ViewModels;

namespace MultiCat.Gui.Controls;

/// <summary>
/// The signal-flow diagram: radio on the left, the MultiCAT hub in the middle, and a
/// bubble per live client connection fanned out on the right. Pulses ride the links —
/// amber toward the radio (commands), teal toward the clients (responses) — driven by
/// real activity plus a gentle heartbeat so connected clients read as alive. Click a
/// client bubble to rename it.
/// </summary>
public class SignalFlowControl : Control
{
    public static readonly StyledProperty<string?> RadioNameProperty =
        AvaloniaProperty.Register<SignalFlowControl, string?>(nameof(RadioName), "Radio");

    public static readonly StyledProperty<IEnumerable<ClientConnectionViewModel>?> ClientsProperty =
        AvaloniaProperty.Register<SignalFlowControl, IEnumerable<ClientConnectionViewModel>?>(nameof(Clients));

    private static readonly IBrush CommandBrush = new SolidColorBrush(Color.Parse("#EF9F27"));
    private static readonly IBrush ResponseBrush = new SolidColorBrush(Color.Parse("#1D9E75"));
    private static readonly IPen LinkPen = new Pen(new SolidColorBrush(Color.Parse("#808080"), 0.35), 1.5);

    private readonly List<Pulse> _pulses = [];
    private readonly List<(Rect Rect, ClientConnectionViewModel Client)> _clientHitboxes = [];
    private DispatcherTimer? _timer;
    private RadioItemViewModel? _boundVm;
    private int _heartbeat;

    static SignalFlowControl()
    {
        AffectsRender<SignalFlowControl>(RadioNameProperty, ClientsProperty);
    }

    /// <summary>Raised when the user clicks a client bubble (to rename it).</summary>
    public event Action<ClientConnectionViewModel>? ClientClicked;

    public string? RadioName
    {
        get => GetValue(RadioNameProperty);
        set => SetValue(RadioNameProperty, value);
    }

    public IEnumerable<ClientConnectionViewModel>? Clients
    {
        get => GetValue(ClientsProperty);
        set => SetValue(ClientsProperty, value);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_boundVm is not null)
        {
            _boundVm.PulseRequested -= OnPulse;
        }

        _boundVm = DataContext as RadioItemViewModel;
        if (_boundVm is not null)
        {
            _boundVm.PulseRequested += OnPulse;
        }
    }

    // link 0 = radio↔hub; link N (1-based) = the Nth client. TowardRadio moves the
    // pulse toward the radio/hub end (amber command), else toward the far end (teal).
    private void OnPulse(int link, bool towardRadio)
    {
        if (_pulses.Count < 48)
        {
            _pulses.Add(new Pulse { Link = link, TowardRadio = towardRadio });
        }
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
        if (_boundVm is not null)
        {
            _boundVm.PulseRequested -= OnPulse;
            _boundVm = null;
        }

        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var p = e.GetPosition(this);
        foreach (var (rect, client) in _clientHitboxes)
        {
            if (rect.Contains(p))
            {
                ClientClicked?.Invoke(client);
                return;
            }
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        var clientCount = Clients?.Count() ?? 0;

        // Heartbeat: every ~1.4 s, if clients are connected, send a teal pulse out to
        // each of them (and one in from the radio) so the topology reads as alive.
        if (++_heartbeat >= 42 && clientCount > 0)
        {
            _heartbeat = 0;
            OnPulse(0, towardRadio: false);
            for (var i = 1; i <= clientCount; i++)
            {
                OnPulse(i, towardRadio: false);
            }
        }

        if (_pulses.Count == 0)
        {
            return;
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
        _clientHitboxes.Clear();
        var bounds = Bounds;
        if (bounds.Width < 200 || bounds.Height < 80)
        {
            return;
        }

        var isDark = ActualThemeVariant == ThemeVariant.Dark;
        var textBrush = new SolidColorBrush(isDark ? Color.Parse("#E8E8E8") : Color.Parse("#2C2C2A"));
        var mutedBrush = new SolidColorBrush(Color.Parse("#8E8E8E"));

        var clients = Clients?.ToList() ?? [];
        var cy = bounds.Height / 2;

        var radioRect = new Rect(12, cy - 24, 118, 48);
        var hubRect = new Rect((bounds.Width - 112) / 2, cy - 26, 112, 52);
        var clientWidth = 140.0;
        var clientX = bounds.Width - clientWidth - 12;

        var clientRects = new List<Rect>();
        if (clients.Count > 0)
        {
            var step = (bounds.Height - 16) / clients.Count;
            for (var i = 0; i < clients.Count; i++)
            {
                var y = 8 + (step * i) + ((step - 28) / 2);
                clientRects.Add(new Rect(clientX, y, clientWidth, 28));
            }
        }

        var links = new List<(Point P0, Point P1, Point P2, Point P3)>
        {
            Bezier(new Point(radioRect.Right, radioRect.Center.Y), new Point(hubRect.Left, hubRect.Center.Y)),
        };
        foreach (var rect in clientRects)
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
        for (var i = 0; i < clientRects.Count; i++)
        {
            DrawNode(context, clientRects[i], clients[i].DisplayName, "#1D9E75", isDark, textBrush, 11);
            _clientHitboxes.Add((clientRects[i], clients[i]));
        }

        if (clients.Count == 0)
        {
            var hint = new FormattedText(
                "no apps connected", CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface("Inter, Segoe UI"), 11, mutedBrush);
            context.DrawText(hint, new Point(clientX, cy - (hint.Height / 2)));
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
