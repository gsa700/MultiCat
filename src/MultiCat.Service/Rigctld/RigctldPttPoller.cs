using System.Diagnostics;
using System.Net.Sockets;

namespace MultiCat.Service.Rigctld;

/// <summary>
/// Reads PTT and nothing else, on a rigctld connection of its own.
///
/// A key edge matters at a different speed than a dial does. Sharing the state poll
/// meant an edge waited for that poll's whole cycle — the VFO reads run first, and
/// the tick is 150 ms — so an amplifier heard about a key 150–250 ms late. Measured
/// on a K4 over COM5 at 38400, the PTT read itself costs about 2 ms, so asking for it
/// on its own connection is cheap. rigctld serves several clients at once and
/// serializes to the port itself, so this composes with the state poll rather than
/// fighting it — though it does queue behind one, which is where the jitter comes
/// from and why the interval is set to match the network path rather than to be
/// merely better than what came before.
/// </summary>
public sealed class RigctldPttPoller(int port, TimeSpan interval, ILogger logger) : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public bool? Transmitting { get; private set; }

    public bool Connected { get; private set; }

    /// <summary>When rigctld last answered this poller, judged on replies rather than
    /// on the socket still looking open.</summary>
    public DateTimeOffset LastReplyAt { get; private set; } = DateTimeOffset.MinValue;

    public event Action<bool>? TransmitChanged;

    /// <summary>See <see cref="RigctldClientPoller"/> for how to switch this on: it
    /// reports at Debug under this class's own category.</summary>
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private static readonly TimeSpan TimingWindow = TimeSpan.FromSeconds(5);
    private TimeSpan _lastSampleAt = TimeSpan.Zero;
    private TimeSpan _windowStartedAt = TimeSpan.Zero;
    private int _windowSamples;
    private double _readTotalMs, _readMaxMs;
    private double _gapTotalMs, _gapMaxMs;

    public void Start() => _loop = Task.Run(() => RunAsync(_cts.Token));

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync("127.0.0.1", port, ct);
                Connected = true;
                logger.LogInformation(
                    "PTT poller connected to localhost:{Port}, reading every {Interval:F0} ms",
                    port,
                    interval.TotalMilliseconds);

                var stream = client.GetStream();
                using var reader = new StreamReader(stream);
                using var writer = new StreamWriter(stream) { AutoFlush = true };

                while (!ct.IsCancellationRequested)
                {
                    await PollOnceAsync(reader, writer, ct);

                    // Self-paced rather than on a PeriodicTimer. Each read is a full
                    // round trip, so the loop already follows the link's own rate and
                    // the delay only separates reads. A timer would instead skip ticks
                    // whenever a read ran long — which is precisely what puts the
                    // occasional 250 ms tail on the shared state poll.
                    await Task.Delay(interval, ct);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Connected = false;
                logger.LogDebug("PTT poller (port {Port}) disconnected: {Message}; retrying", port, ex.Message);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private async Task PollOnceAsync(StreamReader reader, StreamWriter writer, CancellationToken ct)
    {
        var timing = logger.IsEnabled(LogLevel.Debug);
        var startedAt = timing ? _clock.Elapsed : TimeSpan.Zero;

        // get_ptt -> "0" or "1"
        await writer.WriteAsync("t\n");
        var line = await reader.ReadLineAsync(ct);
        if (line is null)
        {
            throw new IOException("rigctld closed the connection");
        }

        if (line is "0" or "1")
        {
            LastReplyAt = DateTimeOffset.UtcNow;

            var tx = line == "1";
            if (tx != Transmitting)
            {
                Transmitting = tx;

                if (timing)
                {
                    // The edge happened somewhere between the previous read and this
                    // one, so that gap is the most it can have gone unnoticed by. This
                    // is the number an amplifier actually feels.
                    var noticedWithinMs = _lastSampleAt > TimeSpan.Zero
                        ? (_clock.Elapsed - _lastSampleAt).TotalMilliseconds
                        : 0;
                    logger.LogDebug(
                        "PTT {Edge}: noticed within {WithinMs:F0} ms of the radio doing it",
                        tx ? "TX" : "RX",
                        noticedWithinMs);
                }

                TransmitChanged?.Invoke(tx);
            }
        }

        if (timing)
        {
            RecordTiming(startedAt, _clock.Elapsed);
        }
    }

    /// <summary>
    /// The sample gap is the figure that matters: an edge can go unnoticed for that
    /// long however quick an individual read is.
    /// </summary>
    private void RecordTiming(TimeSpan startedAt, TimeSpan doneAt)
    {
        var readMs = (doneAt - startedAt).TotalMilliseconds;
        _readTotalMs += readMs;
        _readMaxMs = Math.Max(_readMaxMs, readMs);

        if (_lastSampleAt > TimeSpan.Zero)
        {
            var gapMs = (doneAt - _lastSampleAt).TotalMilliseconds;
            _gapTotalMs += gapMs;
            _gapMaxMs = Math.Max(_gapMaxMs, gapMs);
            _windowSamples++;
        }

        _lastSampleAt = doneAt;

        if (doneAt - _windowStartedAt < TimingWindow || _windowSamples == 0)
        {
            return;
        }

        logger.LogDebug(
            "PTT poll over {Samples} samples: sample gap avg {GapAvg:F0} ms / max {GapMax:F0} ms; "
            + "read avg {ReadAvg:F0} ms / max {ReadMax:F0} ms; delay between reads {Interval:F0} ms",
            _windowSamples,
            _gapTotalMs / _windowSamples,
            _gapMaxMs,
            _readTotalMs / _windowSamples,
            _readMaxMs,
            interval.TotalMilliseconds);

        _windowStartedAt = doneAt;
        _windowSamples = 0;
        _readTotalMs = _readMaxMs = _gapTotalMs = _gapMaxMs = 0;
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        if (_loop is not null)
        {
            try
            {
                await _loop;
            }
            catch (Exception)
            {
            }
        }

        _cts.Dispose();
    }
}
