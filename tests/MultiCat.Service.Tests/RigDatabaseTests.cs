using MultiCat.Hamlib;

namespace MultiCat.Service.Tests;

public class RigDatabaseTests
{
    [Fact]
    public void Database_HasSubstantialCoverage()
    {
        Assert.True(RigDatabase.All.Count > 250, $"only {RigDatabase.All.Count} rigs harvested");
        Assert.False(string.IsNullOrEmpty(RigDatabase.HamlibVersion));
    }

    [Fact]
    public void ModelIds_AreUnique()
    {
        Assert.Equal(RigDatabase.All.Count, RigDatabase.All.Select(r => r.Id).Distinct().Count());
    }

    [Fact]
    public void K3_HasCorrectSerialParameters()
    {
        var k3 = RigDatabase.All.Single(r => r is { Manufacturer: "Elecraft", Model: "K3" });

        Assert.Equal("Elecraft K3", k3.DisplayName);
        Assert.Equal(4800, k3.SerialSpeedMin);
        Assert.Equal(38400, k3.SerialSpeedMax);
        Assert.Equal("8N1", k3.SerialConfig);
        Assert.Equal("RS-232", k3.PortType);
    }

    [Fact]
    public void StationRadios_ArePresent()
    {
        Assert.Contains(RigDatabase.All, r => r.DisplayName == "Icom IC-7610");
        Assert.Contains(RigDatabase.All, r => r.DisplayName == "Elecraft KX3");
    }
}
