using Microsoft.Extensions.Logging.Abstractions;
using MultiCat.Service.Rigctld;

namespace MultiCat.Service.Tests;

public class RigctldSupervisorTests
{
    private static RigctldSupervisor Supervisor(RigctldOptions options) =>
        new(options, NullLogger<RigctldSupervisor>.Instance);

    [Fact]
    public void BuildArguments_Serial_IncludesModelPortBaudAndListen()
    {
        var args = Supervisor(new RigctldOptions
        {
            ExePath = "rigctld.exe",
            HamlibModel = 2047, // Elecraft K4
            Device = "COM7",
            BaudRate = 38400,
            ListenPort = 4532,
        }).BuildArguments();

        Assert.Equal("-m 2047 -r COM7 -T 127.0.0.1 -t 4532 -s 38400", args);
    }

    [Fact]
    public void BuildArguments_Network_UsesHostPortAsDevice_NoBaud()
    {
        var args = Supervisor(new RigctldOptions
        {
            ExePath = "rigctld.exe",
            HamlibModel = 2047,
            Device = "192.168.1.40:9200",
            ListenPort = 4532,
        }).BuildArguments();

        Assert.Equal("-m 2047 -r 192.168.1.40:9200 -T 127.0.0.1 -t 4532", args);
    }

    [Fact]
    public void BuildArguments_AppendsExtraArgs()
    {
        var args = Supervisor(new RigctldOptions
        {
            ExePath = "rigctld.exe",
            HamlibModel = 1,
            Device = "COM3",
            ListenPort = 4533,
            ExtraArgs = "--set-conf=post_write_delay=1",
        }).BuildArguments();

        Assert.EndsWith("--set-conf=post_write_delay=1", args);
    }
}
