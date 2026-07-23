using Grpc.Core;
using MultiCat.Contracts;
using MultiCat.Service.Sessions;

namespace MultiCat.Service;

/// <summary>gRPC surface consumed by the GUI over the named pipe.</summary>
public sealed class ControlService(SessionManager sessions) : MultiCatControl.MultiCatControlBase
{
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
