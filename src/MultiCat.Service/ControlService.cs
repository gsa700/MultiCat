using Grpc.Core;
using MultiCat.Contracts;
using MultiCat.Service.Sessions;
using MultiCat.Service.VirtualPorts;

namespace MultiCat.Service;

/// <summary>gRPC surface consumed by the GUI over the named pipe.</summary>
public sealed class ControlService(
    SessionManager sessions,
    Com0ComManager driver,
    ClientNicknameStore nicknames,
    MultiCat.Service.OmniRig.OmniRigCoordinator omnirig) : MultiCatControl.MultiCatControlBase
{
    public override Task<SaveRadioReply> SetClientNickname(SetClientNicknameRequest request, ServerCallContext context)
    {
        nicknames.Set(request.ProcessName, request.Nickname);
        return Task.FromResult(new SaveRadioReply { Ok = true, Message = "nickname saved" });
    }

    public override Task<SaveRadioReply> SetFlexAdvertising(SetFlexAdvertisingRequest request, ServerCallContext context)
    {
        var session = sessions.FindSession(request.Radio);
        if (session is null)
        {
            return Task.FromResult(new SaveRadioReply { Ok = false, Message = $"unknown radio '{request.Radio}'" });
        }

        session.SetFlexAdvertising(request.Advertising);
        sessions.Persist();     // the operator's choice survives a restart
        return Task.FromResult(new SaveRadioReply
        {
            Ok = true,
            Message = request.Advertising
                ? "advertising to the Genius stack"
                : "stopped advertising; boxes revert to their no-transceiver antenna",
        });
    }

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

        // Network endpoints only — no driver, no elevation. Virtual COM ports are
        // config-file only now (the com0com seam remains for a future signed driver).
        var type = request.EndpointType.Length > 0 ? request.EndpointType : "rigctld";
        try
        {
            if (type == "omnirig")
            {
                // OmniRig forwards to a rigctld endpoint — reuse the radio's, or make one.
                var rig = request.OmnirigRig is 1 or 2 ? request.OmnirigRig : 1;
                var rigctld = session.EnsureRigctldPort(sessions.PickFreeTcpPort(4532));
                rigctld.OmnirigRig = rig;
                omnirig.AssignRig(rig, "127.0.0.1", rigctld.RigctldPort!.Value);
                sessions.Persist();

                var (regOk, regMsg) = await omnirig.EnsureRegisteredAsync(context.CancellationToken);
                var message = regOk
                    ? $"OmniRig Rig {rig} → {rigctld.PortDisplay} ({regMsg})"
                    : $"OmniRig Rig {rig} → {rigctld.PortDisplay}; {regMsg}";
                return new AddClientPortReply
                {
                    Ok = true, Message = message, PortDisplay = rigctld.PortDisplay, Port = rigctld.RigctldPort.Value,
                };
            }

            var (basePort, displayPrefix, defaultLabel) = type switch
            {
                "rigctld" => (4532, "rigctld", "rigctld (WSJT-X, fldigi)"),
                "rawtcp" => (4600, "raw TCP", "raw CAT over TCP"),
                _ => throw new ArgumentException($"unknown endpoint type '{type}'"),
            };

            var tcpPort = request.Port > 0 ? request.Port : sessions.PickFreeTcpPort(basePort);
            var display = $"{displayPrefix} {tcpPort}";
            var port = new ClientPortOptions
            {
                PortDisplay = display,
                Label = request.Label.Length > 0 ? request.Label : defaultLabel,
                Ptt = "via CAT",
                RigctldPort = type == "rigctld" ? tcpPort : null,
                TcpPort = type == "rawtcp" ? tcpPort : null,
            };

            session.AddClientPort(port);
            sessions.Persist();
            return new AddClientPortReply
            {
                Ok = true, Message = $"{display} ready on localhost:{tcpPort}", PortDisplay = display, Port = tcpPort,
            };
        }
        catch (Exception ex)
        {
            return new AddClientPortReply { Ok = false, Message = ex.Message };
        }
    }
    public override Task<RadioList> GetRadios(GetRadiosRequest request, ServerCallContext context)
    {
        var list = new RadioList();
        foreach (var session in sessions.Sessions)
        {
            var options = session.Options;
            var info = new RadioInfo
            {
                Name = options.Name,
                ConnectionSummary = session.ConnectionSummary,
                Connected = session.IsConnected,
                StatusText = session.StatusText,
                Transmitting = session.IsTransmitting,
                VfoAHz = session.VfoAHz,
                VfoBHz = session.VfoBHz,
                Split = session.Split,
                TxOnVfoB = session.TransmitOnVfoB,
                ModeA = session.ModeA,
                ModeB = session.ModeB,
                Flex = ToProto(session.FlexStatus()),
                Connection = options.Simulator ? "Simulator" : options.Connection,
                Protocol = options.Protocol,
                ComPort = options.ComPort ?? string.Empty,
                BaudRate = options.BaudRate,
                Host = options.Host ?? string.Empty,
                TcpPort = options.TcpPort ?? 0,
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

            foreach (var client in session.ConnectedClients())
            {
                info.Clients.Add(new ClientConnection
                {
                    ProcessName = client.ProcessName,
                    DisplayName = nicknames.Resolve(client.ProcessName),
                    ConnectionId = client.ConnectionId,
                    RigctldPort = client.RigctldPort,
                });
            }

            list.Radios.Add(info);
        }

        return Task.FromResult(list);
    }

    private static FlexStatus ToProto(RadioSession.FlexStatusInfo status) => new()
    {
        Configured = status.Configured,
        Advertising = status.Advertising,
        Online = status.Online,
        Serial = status.Serial,
        CommandPort = status.CommandPort,
        Targets = status.Targets,
        ConnectedBoxes = status.ConnectedBoxes,
        Callsign = status.Callsign,
    };

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
            HamlibModelId = options.HamlibModel,
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
                OmnirigRig = port.OmnirigRig ?? 0,
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
        HamlibModel = config.HamlibModelId,
        ClientPorts = [.. config.ClientPorts.Select(p => new ClientPortOptions
        {
            PortDisplay = p.PortDisplay,
            Label = p.Label,
            Ptt = string.IsNullOrEmpty(p.Ptt) ? "CAT only" : p.Ptt,
            MuxPort = string.IsNullOrEmpty(p.MuxPort) ? null : p.MuxPort,
            TcpPort = p.TcpPort == 0 ? null : p.TcpPort,
            RigctldPort = p.RigctldPort == 0 ? null : p.RigctldPort,
            OmnirigRig = p.OmnirigRig == 0 ? null : p.OmnirigRig,
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
