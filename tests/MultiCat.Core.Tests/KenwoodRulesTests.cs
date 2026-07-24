using MultiCat.Core;
using MultiCat.Core.Protocol;

namespace MultiCat.Core.Tests;

public class KenwoodRulesTests
{
    private readonly KenwoodRules _rules = new();

    [Fact]
    public void ReadCommand_ExpectsMatchingResponse()
    {
        Assert.True(_rules.ExpectsResponse(CatFrame.FromAscii("FA;")));
        Assert.True(_rules.IsResponseTo(CatFrame.FromAscii("FA00014074000;"), CatFrame.FromAscii("FA;")));
    }

    [Fact]
    public void SetCommand_ExpectsNoResponse()
    {
        Assert.False(_rules.ExpectsResponse(CatFrame.FromAscii("FA00014074000;")));
    }

    [Fact]
    public void TransmitQuery_IsAnsweredByTqStatus()
    {
        // "TQX;" is answered "TQ0;"/"TQ1;" — different prefix, must still match.
        Assert.True(_rules.ExpectsResponse(CatFrame.FromAscii("TQX;")));
        Assert.True(_rules.IsResponseTo(CatFrame.FromAscii("TQ1;"), CatFrame.FromAscii("TQX;")));
        Assert.True(_rules.IsResponseTo(CatFrame.FromAscii("TQ0;"), CatFrame.FromAscii("TQX;")));
    }

    [Fact]
    public void TransmitQuery_IsNeverCached()
    {
        // A stale cached "receive" would hide a live transmit.
        Assert.False(_rules.IsCacheable(CatFrame.FromAscii("TQX;")));
        Assert.True(_rules.IsCacheable(CatFrame.FromAscii("FA;")));
    }

    [Fact]
    public void UnrelatedResponse_DoesNotMatchTransmitQuery()
    {
        Assert.False(_rules.IsResponseTo(CatFrame.FromAscii("FA00014074000;"), CatFrame.FromAscii("TQX;")));
    }
}
