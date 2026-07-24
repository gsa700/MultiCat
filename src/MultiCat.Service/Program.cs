using Microsoft.AspNetCore.Server.Kestrel.Core;
using MultiCat.Service;
using MultiCat.Service.Sessions;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    // Anchor config + content to the exe's own folder so appsettings.json is found
    // no matter the working directory — launched as a shortcut, autostart, or service.
    ContentRootPath = AppContext.BaseDirectory,
});

// The control API lives on a named pipe only — nothing listens on TCP.
builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.ListenNamedPipe(MultiCatPipe.Name, listen => listen.Protocols = HttpProtocols.Http2);
});

builder.Services.AddGrpc();
builder.Services.AddSingleton<SessionManager>();
builder.Services.AddSingleton<MultiCat.Service.VirtualPorts.Com0ComManager>();
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
