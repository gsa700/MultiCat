using System.Net.Sockets;

namespace MultiCat.Service.Rigctld;

/// <summary>
/// Polls a rigctld instance as a client (f/m/t) so MultiCAT can still show a radio's
/// frequency, mode, and PTT when rigctld — not our arbiter — owns the CAT connection.
/// This is how the GUI stays live for serial radios in sole-owner mode, where the COM
/// port can only be opened once. Reconnects until rigctld is up.
/// </summary>
public sealed class RigctldClientPoller(int port, TimeSpan interval, ILogger logger) : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public long? FrequencyHz { get; private set; }

    /// <summary>True when the radio transmits on a different VFO than it receives on.</summary>
    public bool Split { get; private set; }

    /// <summary>
    /// The frequency about to be transmitted on. Band-following gear must use this:
    /// during a split QSO it differs from the receive frequency, and following the
    /// receive VFO would select the wrong band.
    /// </summary>
    public long? TransmitFrequencyHz { get; private set; }

    public string? Mode { get; private set; }

    public bool? Transmitting { get; private set; }

    public bool Connected { get; private set; }

    public event Action<long>? FrequencyChanged;

    public event Action<long>? TransmitFrequencyChanged;

    public event Action<string>? ModeChanged;

    public event Action<bool>? TransmitChanged;

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
                logger.LogInformation("rigctld client poller connected to localhost:{Port}", port);

                var stream = client.GetStream();
                using var reader = new StreamReader(stream);
                using var writer = new StreamWriter(stream) { AutoFlush = true };
                using var timer = new PeriodicTimer(interval);

                while (await timer.WaitForNextTickAsync(ct))
                {
                    await PollOnceAsync(reader, writer, ct);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Connected = false;
                logger.LogDebug("rigctld poller (port {Port}) disconnected: {Message}; retrying", port, ex.Message);
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
        // get_freq -> one line of Hz
        await writer.WriteAsync("f\n");
        var freqLine = await reader.ReadLineAsync(ct);
        if (long.TryParse(freqLine, out var hz) && hz != FrequencyHz)
        {
            FrequencyHz = hz;
            FrequencyChanged?.Invoke(hz);
        }

        // get_mode -> mode line + passband line
        await writer.WriteAsync("m\n");
        var modeLine = await reader.ReadLineAsync(ct);
        _ = await reader.ReadLineAsync(ct);
        if (modeLine is { Length: > 0 } && !modeLine.StartsWith("RPRT") && modeLine != Mode)
        {
            Mode = modeLine;
            ModeChanged?.Invoke(modeLine);
        }

        // get_split_vfo -> split flag + transmit VFO. Both replies are fixed-length,
        // which keeps this request/response socket in step.
        await writer.WriteAsync("s\n");
        var splitLine = await reader.ReadLineAsync(ct);
        _ = await reader.ReadLineAsync(ct);
        Split = splitLine == "1";

        var transmitFrequency = FrequencyHz;
        if (Split)
        {
            // get_split_freq is the transmit frequency, and is meaningful only while
            // split is on — it answers 0 on a simplex radio.
            await writer.WriteAsync("i\n");
            var splitFreq = await reader.ReadLineAsync(ct);
            if (long.TryParse(splitFreq, out var txHz) && txHz > 0)
            {
                transmitFrequency = txHz;
            }
        }

        if (transmitFrequency is { } txHertz && txHertz != TransmitFrequencyHz)
        {
            TransmitFrequencyHz = txHertz;
            TransmitFrequencyChanged?.Invoke(txHertz);
        }

        // get_ptt -> "0" or "1"
        await writer.WriteAsync("t\n");
        var pttLine = await reader.ReadLineAsync(ct);
        if (pttLine is "0" or "1")
        {
            var tx = pttLine == "1";
            if (tx != Transmitting)
            {
                Transmitting = tx;
                TransmitChanged?.Invoke(tx);
            }
        }
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
