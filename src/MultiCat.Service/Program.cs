using Microsoft.AspNetCore.Server.Kestrel.Core;
using MultiCat.Service;
using MultiCat.Service.Sessions;

var builder = WebApplication.CreateBuilder(args);

// The control API lives on a named pipe only — nothing listens on TCP.
builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.ListenNamedPipe(MultiCatPipe.Name, listen => listen.Protocols = HttpProtocols.Http2);
});

builder.Services.AddGrpc();
builder.Services.AddSingleton<SessionManager>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SessionManager>());

var app = builder.Build();
app.MapGrpcService<ControlService>();
app.Run();

namespace MultiCat.Service
{
    public static class MultiCatPipe
    {
        public const string Name = "MultiCat.Control";
    }
}
