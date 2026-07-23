using MultiCat.Core.Framing;
using MultiCat.Core.Protocol;

namespace MultiCat.Core;

/// <summary>
/// Serializes CAT traffic from many clients onto one radio: a single command is
/// outstanding at a time, its response is routed back to the sender only, and
/// anything the radio volunteers on its own is surfaced as unsolicited.
/// </summary>
public sealed class TransactionArbiter : IAsyncDisposable
{
    private readonly ICatTransport _transport;
    private readonly ICatFramer _framer;
    private readonly ICatProtocolRules _rules;
    private readonly PollCache _cache;
    private readonly TimeProvider _time;
    private readonly TimeSpan _responseTimeout;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _pendingLock = new();
    private TaskCompletionSource<CatFrame>? _pending;
    private CatFrame _pendingCommand;

    public TransactionArbiter(
        ICatTransport transport,
        ICatFramer framer,
        ICatProtocolRules rules,
        PollCache cache,
        TimeProvider time,
        TimeSpan? responseTimeout = null)
    {
        _transport = transport;
        _framer = framer;
        _rules = rules;
        _cache = cache;
        _time = time;
        _responseTimeout = responseTimeout ?? TimeSpan.FromMilliseconds(500);
        _transport.DataReceived += OnDataReceived;
    }

    /// <summary>Raised for frames the radio sent on its own (auto-information, transceive).</summary>
    public event Action<CatFrame>? UnsolicitedReceived;

    public event Action<ArbiterActivity>? Activity;

    /// <summary>
    /// Send one command for a client. Returns the response frame, or null when the
    /// command has no response (sets/actions) or the radio did not answer in time.
    /// </summary>
    public async Task<CatFrame?> ExecuteAsync(string clientId, CatFrame command, CancellationToken cancellationToken = default)
    {
        var expectsResponse = _rules.ExpectsResponse(command);

        if (expectsResponse && _rules.IsCacheable(command) && _cache.TryGet(command, out var cached))
        {
            Activity?.Invoke(new ArbiterActivity(clientId, ArbiterActivityKind.CacheHit, command));
            return cached;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!expectsResponse)
            {
                _cache.Invalidate();
                await _transport.SendAsync(command.Data, cancellationToken).ConfigureAwait(false);
                Activity?.Invoke(new ArbiterActivity(clientId, ArbiterActivityKind.SetSent, command));
                return null;
            }

            var tcs = new TaskCompletionSource<CatFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_pendingLock)
            {
                _pending = tcs;
                _pendingCommand = command;
            }

            try
            {
                await _transport.SendAsync(command.Data, cancellationToken).ConfigureAwait(false);
                Activity?.Invoke(new ArbiterActivity(clientId, ArbiterActivityKind.CommandSent, command));

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var timeout = Task.Delay(_responseTimeout, _time, timeoutCts.Token);
                var winner = await Task.WhenAny(tcs.Task, timeout).ConfigureAwait(false);
                if (winner == timeout)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Activity?.Invoke(new ArbiterActivity(clientId, ArbiterActivityKind.Timeout, command));
                    return null;
                }

                timeoutCts.Cancel();
                var response = await tcs.Task.ConfigureAwait(false);
                if (_rules.IsCacheable(command))
                {
                    _cache.Set(command, response);
                }

                Activity?.Invoke(new ArbiterActivity(clientId, ArbiterActivityKind.ResponseReceived, response));
                return response;
            }
            finally
            {
                lock (_pendingLock)
                {
                    _pending = null;
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private void OnDataReceived(byte[] data)
    {
        foreach (var frame in _framer.Push(data))
        {
            TaskCompletionSource<CatFrame>? matched = null;
            lock (_pendingLock)
            {
                if (_pending is not null && _rules.IsResponseTo(frame, _pendingCommand))
                {
                    matched = _pending;
                    _pending = null;
                }
            }

            if (matched is not null)
            {
                matched.TrySetResult(frame);
            }
            else
            {
                Activity?.Invoke(new ArbiterActivity(null, ArbiterActivityKind.Unsolicited, frame));
                UnsolicitedReceived?.Invoke(frame);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _transport.DataReceived -= OnDataReceived;
        _gate.Dispose();
        await ValueTask.CompletedTask;
    }
}
