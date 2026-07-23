using MultiCat.Core;
using MultiCat.Core.Framing;
using MultiCat.Core.Protocol;
using MultiCat.Service.Transports;

namespace MultiCat.Service.Sessions;

public sealed record RadioSessionOptions
{
    public required string Name { get; init; }

    public string Protocol { get; init; } = "Kenwood";

    /// <summary>When true, runs against the built-in simulated K3 instead of hardware.</summary>
    public bool Simulator { get; init; }

    public string? ComPort { get; init; }

    public int BaudRate { get; init; } = 38400;

    public List<ClientPortOptions> ClientPorts { get; init; } = [];
}

public sealed record ClientPortOptions
{
    public required string PortDisplay { get; init; }

    public required string Label { get; init; }

    public string Ptt { get; init; } = "CAT only";
}

/// <summary>One radio: transport + arbiter + state tracker, plus an internal status
/// poller that keeps the event stream fed even when no client is polling.</summary>
public sealed class RadioSession : IAsyncDisposable
{
    private readonly ICatTransport _transport;
    private readonly TransactionArbiter _arbiter;
    private readonly RadioStateTracker _tracker = new();
    private readonly List<Task> _loops = [];
    private readonly CancellationTokenSource _cts = new();

    public RadioSession(RadioSessionOptions options)
    {
        Options = options;
        _transport = options.Simulator
            ? new SimulatedKenwoodTransport()
            : new SerialPortTransport(
                options.ComPort ?? throw new InvalidOperationException($"Radio '{options.Name}' has no ComPort configured"),
                options.BaudRate);

        // CI-V support exists in Core; sessions are Kenwood-family until the
        // config UI can express per-protocol defaults.
        _arbiter = new TransactionArbiter(
            _transport, new KenwoodFramer(), new KenwoodRules(),
            new PollCache(TimeProvider.System, TimeSpan.FromMilliseconds(300)),
            TimeProvider.System);

        _arbiter.Activity += activity =>
        {
            long frequency = 0;
            var mode = string.Empty;
            if (activity.Kind is ArbiterActivityKind.ResponseReceived or ArbiterActivityKind.Unsolicited)
            {
                var beforeHz = _tracker.FrequencyHz;
                var beforeMode = _tracker.Mode;
                _tracker.Observe(activity.Frame);
                if (_tracker.FrequencyHz != beforeHz && _tracker.FrequencyHz is { } hz)
                {
                    frequency = hz;
                }

                if (_tracker.Mode != beforeMode && _tracker.Mode is { } m)
                {
                    mode = m;
                }
            }

            ActivityObserved?.Invoke(this, activity, frequency, mode);
        };
    }

    public RadioSessionOptions Options { get; }

    public bool IsConnected { get; private set; }

    public string ConnectionSummary => Options.Simulator
        ? "simulator · connected"
        : $"{Options.ComPort} · {(IsConnected ? "connected" : "idle")}";

    public string StatusText
    {
        get
        {
            if (!IsConnected)
            {
                return "idle";
            }

            var freq = _tracker.FrequencyHz is { } hz ? $" · {hz / 1000.0:N2} kHz" : string.Empty;
            var mode = _tracker.Mode is { } m ? $" · {m}" : string.Empty;
            return $"connected{freq}{mode}";
        }
    }

    public event Action<RadioSession, ArbiterActivity, long, string>? ActivityObserved;

    public void Start()
    {
        if (_transport is SerialPortTransport serial)
        {
            serial.Open();
        }

        IsConnected = true;
        _loops.Add(PollLoop("status", TimeSpan.FromMilliseconds(1000)));
        if (Options.Simulator)
        {
            _loops.Add(PollLoop("n1mm", TimeSpan.FromMilliseconds(250)));
            _loops.Add(PollLoop("wsjtx", TimeSpan.FromMilliseconds(400)));
        }
    }

    private async Task PollLoop(string clientId, TimeSpan interval)
    {
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(_cts.Token))
            {
                await _arbiter.ExecuteAsync(clientId, CatFrame.FromAscii("FA;"), _cts.Token);
                await _arbiter.ExecuteAsync(clientId, CatFrame.FromAscii("MD;"), _cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try
        {
            await Task.WhenAll(_loops);
        }
        catch (OperationCanceledException)
        {
        }

        await _arbiter.DisposeAsync();
        await _transport.DisposeAsync();
        _cts.Dispose();
    }
}
