namespace MultiCat.Core;

/// <summary>
/// Short-TTL cache of read-command responses. When several clients poll the same
/// value (FA;/IF;) at once, only the first poll reaches the radio; the rest are
/// answered from here. Any set command must invalidate the whole cache.
/// </summary>
public sealed class PollCache(TimeProvider time, TimeSpan ttl)
{
    private readonly Dictionary<string, (CatFrame Response, long Timestamp)> _entries = [];
    private readonly Lock _lock = new();

    public bool TryGet(CatFrame command, out CatFrame response)
    {
        lock (_lock)
        {
            if (_entries.TryGetValue(command.CacheKey, out var entry) &&
                time.GetElapsedTime(entry.Timestamp) < ttl)
            {
                response = entry.Response;
                return true;
            }
        }

        response = default;
        return false;
    }

    public void Set(CatFrame command, CatFrame response)
    {
        lock (_lock)
        {
            _entries[command.CacheKey] = (response, time.GetTimestamp());
        }
    }

    public void Invalidate()
    {
        lock (_lock)
        {
            _entries.Clear();
        }
    }
}
