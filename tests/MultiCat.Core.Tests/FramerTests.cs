using MultiCat.Core;
using MultiCat.Core.Framing;

namespace MultiCat.Core.Tests;

public class KenwoodFramerTests
{
    private readonly KenwoodFramer _framer = new();

    private IReadOnlyList<CatFrame> Push(string ascii) =>
        _framer.Push(System.Text.Encoding.ASCII.GetBytes(ascii));

    [Fact]
    public void CompleteFrame_IsEmitted()
    {
        var frames = Push("FA00014074000;");
        Assert.Single(frames);
        Assert.Equal("FA00014074000;", frames[0].ToAscii());
    }

    [Fact]
    public void FrameSplitAcrossChunks_IsReassembled()
    {
        Assert.Empty(Push("FA000140"));
        var frames = Push("74000;");
        Assert.Single(frames);
        Assert.Equal("FA00014074000;", frames[0].ToAscii());
    }

    [Fact]
    public void MultipleFramesInOneChunk_AllEmittedInOrder()
    {
        var frames = Push("FA00014074000;MD2;IF;");
        Assert.Equal(3, frames.Count);
        Assert.Equal("FA00014074000;", frames[0].ToAscii());
        Assert.Equal("MD2;", frames[1].ToAscii());
        Assert.Equal("IF;", frames[2].ToAscii());
    }

    [Fact]
    public void OversizedGarbage_IsDiscardedThroughItsTerminator_AndFramingRecovers()
    {
        Assert.Empty(Push(new string('X', 300) + ";"));
        var frames = Push("MD2;");
        Assert.Single(frames);
        Assert.Equal("MD2;", frames[0].ToAscii());
    }
}

public class IcomCivFramerTests
{
    private readonly IcomCivFramer _framer = new();

    [Fact]
    public void CompleteFrame_IsEmitted()
    {
        var frames = _framer.Push([0xFE, 0xFE, 0x94, 0xE0, 0x03, 0xFD]);
        Assert.Single(frames);
        Assert.Equal("FEFE94E003FD", frames[0].ToHex());
    }

    [Fact]
    public void LeadingGarbageBeforePreamble_IsSkipped()
    {
        var frames = _framer.Push([0x00, 0x42, 0xFE, 0xFE, 0x94, 0xE0, 0x03, 0xFD]);
        Assert.Single(frames);
        Assert.Equal("FEFE94E003FD", frames[0].ToHex());
    }

    [Fact]
    public void FrameSplitAcrossChunks_IsReassembled()
    {
        Assert.Empty(_framer.Push([0xFE, 0xFE, 0x94]));
        var frames = _framer.Push([0xE0, 0x03, 0xFD]);
        Assert.Single(frames);
        Assert.Equal("FEFE94E003FD", frames[0].ToHex());
    }

    [Fact]
    public void TwoFramesBackToBack_AreBothEmitted()
    {
        var frames = _framer.Push([0xFE, 0xFE, 0x94, 0xE0, 0x03, 0xFD, 0xFE, 0xFE, 0xE0, 0x94, 0xFB, 0xFD]);
        Assert.Equal(2, frames.Count);
    }
}
