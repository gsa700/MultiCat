using System.Text;
using MultiCat.Core;

namespace MultiCat.Core.Tests;

/// <summary>Fake radio: replies to each sent command via the Responder callback.</summary>
public sealed class ScriptedTransport : ICatTransport
{
    public List<string> Sent { get; } = [];

    public Func<string, string?>? Responder { get; set; }

    public event Action<byte[]>? DataReceived;

    public ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        var command = Encoding.ASCII.GetString(data.Span);
        Sent.Add(command);
        var response = Responder?.Invoke(command);
        if (response is not null)
        {
            DataReceived?.Invoke(Encoding.ASCII.GetBytes(response));
        }

        return ValueTask.CompletedTask;
    }

    public void Inject(string ascii) => DataReceived?.Invoke(Encoding.ASCII.GetBytes(ascii));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
