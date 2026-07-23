using System.Collections.Concurrent;
using System.Threading.Channels;
using MultiCat.Contracts;
using MultiCat.Core;

namespace MultiCat.Service.Sessions;

/// <summary>
/// Owns every RadioSession and fans arbiter activity out to any number of
/// subscribed activity streams (one per connected GUI).
/// </summary>
public sealed class SessionManager(ILogger<SessionManager> logger, IConfiguration configuration) : IHostedService, IAsyncDisposable
{
    private readonly List<RadioSession> _sessions = [];
    private readonly ConcurrentDictionary<Guid, Channel<ActivityEvent>> _subscribers = new();

    public IReadOnlyList<RadioSession> Sessions => _sessions;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var configs = configuration.GetSection("Radios").Get<List<RadioSessionOptions>>() ?? [];
        if (configs.Count == 0)
        {
            logger.LogWarning("No radios configured; starting with the built-in simulator");
            configs.Add(new RadioSessionOptions { Name = "Elecraft K3 (simulated)", Simulator = true });
        }

        foreach (var config in configs)
        {
            try
            {
                var session = new RadioSession(config);
                session.ActivityObserved += OnActivity;
                session.Start();
                _sessions.Add(session);
                logger.LogInformation("Radio session started: {Name} ({Connection})", config.Name, session.ConnectionSummary);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to start radio session {Name}", config.Name);
            }
        }

        return Task.CompletedTask;
    }

    private void OnActivity(RadioSession session, ArbiterActivity activity, long frequencyHz, string mode)
    {
        if (_subscribers.IsEmpty)
        {
            return;
        }

        var evt = new ActivityEvent
        {
            Radio = session.Options.Name,
            Time = DateTime.Now.ToString("HH:mm:ss.fff"),
            Kind = activity.Kind.ToString(),
            ClientId = activity.ClientId ?? string.Empty,
            Frame = activity.Frame.ToAscii(),
            Note = NoteFor(activity.Kind),
            FrequencyHz = frequencyHz,
            Mode = mode,
        };

        foreach (var channel in _subscribers.Values)
        {
            channel.Writer.TryWrite(evt);
        }
    }

    private static string NoteFor(ArbiterActivityKind kind) => kind switch
    {
        ArbiterActivityKind.CacheHit => "cache hit, radio not polled",
        ArbiterActivityKind.CommandSent => "sent to radio",
        ArbiterActivityKind.SetSent => "set sent, cache invalidated",
        ArbiterActivityKind.ResponseReceived => "routed to sender",
        ArbiterActivityKind.Timeout => "no response from radio",
        ArbiterActivityKind.Unsolicited => "broadcast to all clients",
        _ => string.Empty,
    };

    public (Guid Id, ChannelReader<ActivityEvent> Reader) Subscribe()
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<ActivityEvent>(
            new BoundedChannelOptions(512) { FullMode = BoundedChannelFullMode.DropOldest });
        _subscribers[id] = channel;
        logger.LogInformation("Activity stream opened ({Count} subscriber(s))", _subscribers.Count);
        return (id, channel.Reader);
    }

    public void Unsubscribe(Guid id)
    {
        if (_subscribers.TryRemove(id, out var channel))
        {
            channel.Writer.TryComplete();
            logger.LogInformation("Activity stream closed ({Count} subscriber(s))", _subscribers.Count);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var session in _sessions)
        {
            await session.DisposeAsync();
        }

        _sessions.Clear();
    }
}
