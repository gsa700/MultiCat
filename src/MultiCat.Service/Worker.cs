using MultiCat.Core;
using MultiCat.Core.Framing;
using MultiCat.Core.Protocol;

namespace MultiCat.Service;

/// <summary>
/// Development harness: runs the arbiter against a simulated K3 with two fake
/// clients polling concurrently, logging every arbiter decision. Replaced by real
/// radio sessions once serial transports and virtual COM ports are wired up.
/// </summary>
public class Worker(ILogger<Worker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using var transport = new SimulatedKenwoodTransport();
        var cache = new PollCache(TimeProvider.System, TimeSpan.FromMilliseconds(300));
        await using var arbiter = new TransactionArbiter(
            transport, new KenwoodFramer(), new KenwoodRules(), cache, TimeProvider.System);

        var tracker = new RadioStateTracker();
        tracker.FrequencyChanged += hz => logger.LogInformation("event: frequency {Khz:N2} kHz", hz / 1000.0);
        tracker.ModeChanged += mode => logger.LogInformation("event: mode {Mode}", mode);

        arbiter.Activity += activity =>
        {
            logger.LogDebug("{Kind,-16} {Client,-8} {Frame}",
                activity.Kind, activity.ClientId ?? "-", activity.Frame.ToAscii());
            if (activity.Kind is ArbiterActivityKind.ResponseReceived or ArbiterActivityKind.Unsolicited)
            {
                tracker.Observe(activity.Frame);
            }
        };

        var client1 = PollLoop("n1mm", TimeSpan.FromMilliseconds(250), arbiter, stoppingToken);
        var client2 = PollLoop("wsjtx", TimeSpan.FromMilliseconds(400), arbiter, stoppingToken);
        await Task.WhenAll(client1, client2);
    }

    private static async Task PollLoop(string clientId, TimeSpan interval, TransactionArbiter arbiter, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                await arbiter.ExecuteAsync(clientId, CatFrame.FromAscii("FA;"), ct);
                await arbiter.ExecuteAsync(clientId, CatFrame.FromAscii("MD;"), ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
