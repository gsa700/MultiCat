using MultiCat.Service.Updates;

namespace MultiCat.Service.Tests;

public class UpdateCheckerTests
{
    [Theory]
    [InlineData("0.3.2", "0.3.1", true)]
    [InlineData("0.4.0", "0.3.9", true)]
    [InlineData("1.0.0", "0.9.9", true)]
    [InlineData("0.3.1", "0.3.1", false)]
    [InlineData("0.3.0", "0.3.1", false)]
    [InlineData("0.2.9", "0.3.0", false)]
    public void NewerReleasesAreRecognised(string candidate, string running, bool expected)
    {
        Assert.Equal(expected, UpdateChecker.IsNewer(candidate, running));
    }

    [Fact]
    public void VersionsAreComparedNumerically_NotAsText()
    {
        // The case a string comparison gets backwards: "0.3.10" sorts below "0.3.9".
        Assert.True(UpdateChecker.IsNewer("0.3.10", "0.3.9"));
        Assert.False(UpdateChecker.IsNewer("0.3.9", "0.3.10"));
    }

    [Fact]
    public void PreReleaseSuffixesDoNotAffectOrdering()
    {
        Assert.True(UpdateChecker.IsNewer("0.3.2-alpha", "0.3.1-alpha"));
        Assert.False(UpdateChecker.IsNewer("0.3.1-alpha", "0.3.1"));
    }

    [Fact]
    public void AShorterVersionIsPaddedRatherThanMisread()
    {
        Assert.False(UpdateChecker.IsNewer("0.3", "0.3.1"));
        Assert.True(UpdateChecker.IsNewer("0.4", "0.3.9"));
    }

    [Fact]
    public void RubbishNeverReadsAsAnUpdate()
    {
        Assert.False(UpdateChecker.IsNewer("not-a-version", "0.3.1"));
    }
}
