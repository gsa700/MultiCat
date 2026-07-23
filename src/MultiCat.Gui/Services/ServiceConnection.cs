using System.IO.Pipes;
using System.Net.Sockets;
using System.Security.Principal;
using Grpc.Net.Client;
using MultiCat.Contracts;

namespace MultiCat.Gui.Services;

/// <summary>
/// gRPC client for the MultiCAT service over its named pipe. Connection is
/// attempted once at startup; callers decide what to do when it's absent.
/// </summary>
public sealed class ServiceConnection : IDisposable
{
    public const string PipeName = "MultiCat.Control";

    private readonly GrpcChannel _channel;

    public ServiceConnection()
    {
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, cancellationToken) =>
            {
                var pipe = new NamedPipeClientStream(
                    ".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Anonymous);
                try
                {
                    await pipe.ConnectAsync(cancellationToken);
                    return pipe;
                }
                catch
                {
                    await pipe.DisposeAsync();
                    throw;
                }
            },
        };

        // The URL is nominal — all traffic rides the pipe via ConnectCallback.
        _channel = GrpcChannel.ForAddress("http://multicat-pipe", new GrpcChannelOptions { HttpHandler = handler });
        Client = new MultiCatControl.MultiCatControlClient(_channel);
    }

    public MultiCatControl.MultiCatControlClient Client { get; }

    public void Dispose() => _channel.Dispose();
}
