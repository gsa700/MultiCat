namespace MultiCat.Service.Flex;

/// <summary>
/// Brings the virtual radio online or offline as the real radio comes and goes.
/// <para>
/// Going offline stops advertising AND drops every stack connection, so the boxes
/// see the radio vanish exactly as they would a real Flex powering off, and each
/// reverts to its configured no-transceiver antenna. Leaving a radio "present"
/// while the real one is off would defeat that failover, so this teardown is a
/// safety requirement rather than tidiness.
/// </para>
/// Both directions are debounced: a radio must be present for a while before it is
/// advertised, and absent for longer before the stack is dropped, so a brief network
/// hiccup does not throw the antennas back and forth.
/// </summary>
public sealed class FlexPresenceSupervisor(
    Func<bool> isPresent,
    Func<Task> goOnline,
    Func<Task> goOffline,
    ILogger logger,
    TimeSpan? presentAfter = null,
    TimeSpan? absentAfter = null,
    TimeSpan? pollInterval = null) : IAsyncDisposable
{
    private readonly TimeSpan _presentAfter = presentAfter ?? TimeSpan.FromSeconds(1);
    private readonly TimeSpan _absentAfter = absentAfter ?? TimeSpan.FromSeconds(5);
    private readonly TimeSpan _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(250);
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public bool Online { get; private set; }

    public void Start() => _loop = Task.Run(() => RunAsync(_cts.Token));

    private async Task RunAsync(CancellationToken ct)
    {
        DateTimeOffset? pendingSince = null;

        while (!ct.IsCancellationRequested)
        {
            var now = DateTimeOffset.UtcNow;
            var present = isPresent();

            if (present && !Online)
            {
                pendingSince ??= now;
                if (now - pendingSince >= _presentAfter)
                {
                    logger.LogInformation("Flex: radio present — advertising to the stack");
                    await goOnline();
                    Online = true;
                    pendingSince = null;
                }
            }
            else if (!present && Online)
            {
                pendingSince ??= now;
                if (now - pendingSince >= _absentAfter)
                {
                    logger.LogWarning(
                        "Flex: radio absent — dropping the stack; boxes revert to their no-transceiver antenna");
                    await goOffline();
                    Online = false;
                    pendingSince = null;
                }
            }
            else
            {
                pendingSince = null;    // steady state, reset the debounce
            }

            try
            {
                await Task.Delay(_pollInterval, ct);
            }
            catch (OperationCanceledException)
            {
                return;
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
