using Microsoft.Extensions.Logging.Abstractions;
using MultiCat.Service.Flex;

namespace MultiCat.Service.Tests;

public class FlexPresenceSupervisorTests
{
    private bool _present;
    private int _onlineCalls;
    private int _offlineCalls;

    private FlexPresenceSupervisor Create(TimeSpan presentAfter, TimeSpan absentAfter) =>
        new(
            isPresent: () => _present,
            goOnline: () => { _onlineCalls++; return Task.CompletedTask; },
            goOffline: () => { _offlineCalls++; return Task.CompletedTask; },
            NullLogger.Instance,
            presentAfter,
            absentAfter,
            TimeSpan.FromMilliseconds(10));

    private static async Task Until(Func<bool> condition, string because)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            cts.Token.ThrowIfCancellationRequested();
            await Task.Delay(10, cts.Token);
        }
    }

    [Fact]
    public async Task APresentRadioIsAdvertised()
    {
        _present = true;
        await using var supervisor = Create(TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(30));
        supervisor.Start();

        await Until(() => supervisor.Online, "the radio is present");

        Assert.Equal(1, _onlineCalls);
    }

    [Fact]
    public async Task AnAbsentRadioIsNeverAdvertised()
    {
        _present = false;
        await using var supervisor = Create(TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(20));
        supervisor.Start();

        await Task.Delay(200);

        Assert.False(supervisor.Online);
        Assert.Equal(0, _onlineCalls);
    }

    [Fact]
    public async Task WhenTheRadioGoesAway_TheStackIsDropped()
    {
        _present = true;
        await using var supervisor = Create(TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(30));
        supervisor.Start();
        await Until(() => supervisor.Online, "the radio started present");

        _present = false;

        await Until(() => !supervisor.Online, "the radio went away");
        Assert.Equal(1, _offlineCalls);
    }

    [Fact]
    public async Task ABriefDropoutDoesNotThrowTheAntennasAround()
    {
        _present = true;
        // Absence must persist well beyond a blip before the stack is torn down.
        await using var supervisor = Create(TimeSpan.FromMilliseconds(20), TimeSpan.FromSeconds(5));
        supervisor.Start();
        await Until(() => supervisor.Online, "the radio started present");

        _present = false;
        await Task.Delay(100);      // a hiccup, far shorter than the absent window
        _present = true;
        await Task.Delay(150);

        Assert.True(supervisor.Online);
        Assert.Equal(0, _offlineCalls);
        Assert.Equal(1, _onlineCalls);   // never went offline, so never re-advertised
    }

    [Fact]
    public async Task ARadioThatFlickersOnIsNotAdvertisedUntilItSettles()
    {
        _present = false;
        await using var supervisor = Create(TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(20));
        supervisor.Start();

        _present = true;
        await Task.Delay(100);      // present, but not yet for long enough
        _present = false;
        await Task.Delay(100);

        Assert.False(supervisor.Online);
        Assert.Equal(0, _onlineCalls);
    }

    [Fact]
    public async Task ARadioThatComesBackIsAdvertisedAgain()
    {
        _present = true;
        await using var supervisor = Create(TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(20));
        supervisor.Start();
        await Until(() => supervisor.Online, "present");

        _present = false;
        await Until(() => !supervisor.Online, "absent");
        _present = true;
        await Until(() => supervisor.Online, "present again");

        Assert.Equal(2, _onlineCalls);
        Assert.Equal(1, _offlineCalls);
    }
}
