using Grpc.Core;
using MultiCat.Contracts;
using MultiCat.Service.Sessions;
using MultiCat.Service.VirtualPorts;

namespace MultiCat.Service;

/// <summary>gRPC surface consumed by the GUI over the named pipe.</summary>
public sealed class ControlService(
    SessionManager sessions,
    Com0ComManager driver,
    IHostEnvironment environment) : MultiCatControl.MultiCatControlBase
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
        sessions.PersistClientPort(request.Radio, port, environment.ContentRootPath);

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
