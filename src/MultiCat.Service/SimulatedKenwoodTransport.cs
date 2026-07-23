using System.Text;
using MultiCat.Core;

namespace MultiCat.Service;

/// <summary>
/// A pretend K3 for development without hardware: answers FA/FB/MD/IF reads,
/// applies FA sets, and drifts VFO A slowly so clients see changes.
/// </summary>
public sealed class SimulatedKenwoodTransport : ICatTransport
{
    private readonly Random _random = new();
    private long _vfoA = 14_074_000;
    private long _vfoB = 7_074_000;
    private char _mode = '2';

    public event Action<byte[]>? DataReceived;

    public ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        var command = Encoding.ASCII.GetString(data.Span);
        _vfoA += _random.Next(-2, 3) * 10;

        var response = command switch
        {
            "FA;" => $"FA{_vfoA:00000000000};",
            "FB;" => $"FB{_vfoB:00000000000};",
            "MD;" => $"MD{_mode};",
            "IF;" => $"IF{_vfoA:00000000000}     +00000000002{_mode}0000000 ;",
            _ when command.StartsWith("FA") && command.Length == 14 => Apply(ref _vfoA, command),
            _ when command.StartsWith("FB") && command.Length == 14 => Apply(ref _vfoB, command),
            _ when command.StartsWith("MD") && command.Length == 4 => ApplyMode(command),
            _ => null,
        };

        if (response is not null)
        {
            var bytes = Encoding.ASCII.GetBytes(response);
            Task.Run(() => DataReceived?.Invoke(bytes), CancellationToken.None);
        }

        return ValueTask.CompletedTask;
    }

    private static string? Apply(ref long vfo, string command)
    {
        if (long.TryParse(command.AsSpan(2, 11), out var hz))
        {
            vfo = hz;
        }

        return null;
    }

    private string? ApplyMode(string command)
    {
        _mode = command[2];
        return null;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
