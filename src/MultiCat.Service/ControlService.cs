using Grpc.Core;
using MultiCat.Contracts;
using MultiCat.Service.Sessions;
using MultiCat.Service.VirtualPorts;

namespace MultiCat.Service;

/// <summary>gRPC surface consumed by the GUI over the named pipe.</summary>
public sealed class ControlService(
    SessionManager sessions,
    Com0ComManager driver) : MultiCatControl.MultiCatControlBase
{
    public override Task<DriverState> GetDriverState(GetDriverStateRequest request, ServerCallContext context)
    {
        return Task.FromResult(new DriverState
        {
            Installed = driver.IsInstalled,
            Detail = driver.IsInstalled
                ? $"com0com at {driver.SetupcPath}"
                : "virtual COM driver not installed",
        });
    }

    public override async Task<AddClientPortReply> AddClientPort(AddClientPortRequest request, ServerCallContext context)
    {
        var session = sessions.FindSession(request.Radio);
        if (session is null)
        {
            return new AddClientPortReply { Ok = false, Message = $"unknown radio '{request.Radio}'" };
        }

        if (!driver.IsInstalled)
        {
            return new AddClientPortReply
            {
                Ok = false,
                Message = "Virtual COM driver not installed — see Settings for setup",
            };
        }

        var (appPort, muxPort) = Com0ComManager.PickFreePair(sessions.KnownPortNames());
        var created = await driver.CreatePairAsync(appPort, muxPort, context.CancellationToken);
        if (!created)
        {
            return new AddClientPortReply
            {
                Ok = false,
                Message = $"Could not create {appPort} — elevation declined or driver error",
            };
        }

        var port = new ClientPortOptions
        {
            PortDisplay = appPort,
            Label = request.Label.Length > 0 ? request.Label : appPort,
            Ptt = request.Ptt.Length > 0 ? request.Ptt : "CAT only",
            MuxPort = muxPort,
        };
        session.AddClientPort(port);
        sessions.Persist();

        return new AddClientPortReply { Ok = true, Message = $"{appPort} ready", PortDisplay = appPort };
    }
    public override Task<RadioList> GetRadios(GetRadiosRequest request, ServerCallContext context)
    {
        var list = new RadioList();
        foreach (var session in sessions.Sessions)
        {
            var info = new RadioInfo
            {
                Name = session.Options.Name,
                ConnectionSummary = session.ConnectionSummary,
                Connected = session.IsConnected,
                StatusText = session.StatusText,
            };

            foreach (var port in session.Options.ClientPorts)
            {
                var (status, active) = session.PortStatus(port);
                info.Ports.Add(new ClientPortInfo
                {
                    PortDisplay = port.PortDisplay,
                    Label = port.Label,
                    Ptt = port.Ptt,
                    Status = status,
                    Active = active,
                });
            }

            if (session.Options.Simulator)
            {
                info.Ports.Add(new ClientPortInfo
                {
                    PortDisplay = "internal", Label = "n1mm / wsjtx demo pollers",
                    Ptt = "none", Status = "active", Active = true,
                });
            }

            list.Radios.Add(info);
        }

        return Task.FromResult(list);
    }

    public override Task<RadioConfigList> GetRadioConfigs(GetRadioConfigsRequest request, ServerCallContext context)
    {
        var list = new RadioConfigList();
        foreach (var options in sessions.GetConfigs())
        {
            list.Radios.Add(ToProto(options));
        }

        return Task.FromResult(list);
    }

    public override Task<ComPortList> ListComPorts(ListComPortsRequest request, ServerCallContext context)
    {
        var list = new ComPortList();
        list.Ports.AddRange(System.IO.Ports.SerialPort.GetPortNames().OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
        return Task.FromResult(list);
    }

    public override async Task<SaveRadioReply> SaveRadio(SaveRadioRequest request, ServerCallContext context)
    {
        var (ok, message) = await sessions.SaveRadioAsync(request.OriginalName, FromProto(request.Radio));
        return new SaveRadioReply { Ok = ok, Message = message };
    }

    public override async Task<SaveRadioReply> DeleteRadio(DeleteRadioRequest request, ServerCallContext context)
    {
        var (ok, message) = await sessions.DeleteRadioAsync(request.Name);
        return new SaveRadioReply { Ok = ok, Message = message };
    }

    private static RadioConfig ToProto(RadioSessionOptions options)
    {
        var config = new RadioConfig
        {
            Name = options.Name,
            Protocol = options.Protocol,
            Simulator = options.Simulator,
            Connection = options.Connection,
            ComPort = options.ComPort ?? string.Empty,
            BaudRate = options.BaudRate,
            Host = options.Host ?? string.Empty,
            TcpPort = options.TcpPort ?? 0,
        };

        foreach (var port in options.ClientPorts)
        {
            config.ClientPorts.Add(new RadioClientPort
            {
                PortDisplay = port.PortDisplay,
                Label = port.Label,
                Ptt = port.Ptt,
                MuxPort = port.MuxPort ?? string.Empty,
                TcpPort = port.TcpPort ?? 0,
                RigctldPort = port.RigctldPort ?? 0,
            });
        }

        return config;
    }

    private static RadioSessionOptions FromProto(RadioConfig config) => new()
    {
        Name = config.Name,
        Protocol = string.IsNullOrEmpty(config.Protocol) ? "Kenwood" : config.Protocol,
        Simulator = config.Simulator,
        Connection = string.IsNullOrEmpty(config.Connection) ? "Serial" : config.Connection,
        ComPort = string.IsNullOrEmpty(config.ComPort) ? null : config.ComPort,
        BaudRate = config.BaudRate == 0 ? 38400 : config.BaudRate,
        Host = string.IsNullOrEmpty(config.Host) ? null : config.Host,
        TcpPort = config.TcpPort == 0 ? null : config.TcpPort,
        ClientPorts = [.. config.ClientPorts.Select(p => new ClientPortOptions
        {
            PortDisplay = p.PortDisplay,
            Label = p.Label,
            Ptt = string.IsNullOrEmpty(p.Ptt) ? "CAT only" : p.Ptt,
            MuxPort = string.IsNullOrEmpty(p.MuxPort) ? null : p.MuxPort,
            TcpPort = p.TcpPort == 0 ? null : p.TcpPort,
            RigctldPort = p.RigctldPort == 0 ? null : p.RigctldPort,
        })],
    };

    public override async Task StreamActivity(
        StreamActivityRequest request, IServerStreamWriter<ActivityEvent> responseStream, ServerCallContext context)
    {
        var (id, reader) = sessions.Subscribe();
        try
        {
            await foreach (var evt in reader.ReadAllAsync(context.CancellationToken))
            {
                await responseStream.WriteAsync(evt);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            sessions.Unsubscribe(id);
        }
    }
}
