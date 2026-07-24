using MultiCat.Service.Sessions;

namespace MultiCat.Service.Tests;

public class RadioConfigStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"multicat-store-{Guid.NewGuid():N}.json");

    public void Dispose() => File.Delete(_path);

    [Fact]
    public void Load_MissingFile_ReturnsEmpty()
    {
        Assert.Empty(new RadioConfigStore(_path).Load());
    }

    [Fact]
    public void SaveThenLoad_RoundTripsRadios()
    {
        var store = new RadioConfigStore(_path);
        store.Save(
        [
            new RadioSessionOptions
            {
                Name = "K3", Protocol = "Kenwood", ComPort = "COM7", BaudRate = 38400,
                ClientPorts = [new ClientPortOptions { PortDisplay = "TCP 4532", Label = "rigctld", RigctldPort = 4532 }],
            },
            new RadioSessionOptions { Name = "Sim", Simulator = true },
        ]);

        var loaded = store.Load();

        Assert.Equal(2, loaded.Count);
        Assert.Equal("K3", loaded[0].Name);
        Assert.Equal("COM7", loaded[0].ComPort);
        Assert.Equal(4532, loaded[0].ClientPorts[0].RigctldPort);
        Assert.True(loaded[1].Simulator);
    }

    [Fact]
    public void Save_PreservesOtherSections()
    {
        File.WriteAllText(_path, """{ "Logging": { "LogLevel": { "Default": "Warning" } }, "Radios": [] }""");
        var store = new RadioConfigStore(_path);

        store.Save([new RadioSessionOptions { Name = "Sim", Simulator = true }]);

        var text = File.ReadAllText(_path);
        Assert.Contains("Logging", text);
        Assert.Contains("Warning", text);
        Assert.Contains("Sim", text);
    }

    [Fact]
    public void Load_ToleratesCommentsAndTrailingCommas()
    {
        File.WriteAllText(_path, """
            {
              // a comment
              "Radios": [
                { "Name": "K3", "ComPort": "COM7", "BaudRate": 9600 },
              ]
            }
            """);

        var loaded = new RadioConfigStore(_path).Load();

        Assert.Single(loaded);
        Assert.Equal(9600, loaded[0].BaudRate);
    }
}
