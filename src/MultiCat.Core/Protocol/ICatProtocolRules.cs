namespace MultiCat.Core.Protocol;

/// <summary>
/// Protocol-family knowledge the arbiter needs: which commands produce a response,
/// how to pair a response with its command, and which responses are safe to cache.
/// </summary>
public interface ICatProtocolRules
{
    bool ExpectsResponse(CatFrame command);

    bool IsResponseTo(CatFrame response, CatFrame command);

    /// <summary>True for read-only queries whose response may be served from the poll cache.</summary>
    bool IsCacheable(CatFrame command);
}
