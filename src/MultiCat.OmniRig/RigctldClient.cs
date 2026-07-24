using System.Net.Sockets;
using System.Text;

namespace MultiCat.OmniRig;

/// <summary>
/// Minimal synchronous rigctld client. The OmniRig facade is just another
/// arbitrated MultiCAT client — everything rides the rigctld listener, so this
/// works for any radio protocol the service speaks, with poll-cache dedup free.
/// </summary>
public sealed class RigctldClient(string host, int port) : IDisposable
{
    private readonly object _lock = new();
    private TcpClient? _tcp;
    private StreamReader? _reader;
    private StreamWriter? _writer;

    public bool IsConnected
    {
        get
        {
            lock (_lock)
            {
                return _tcp?.Connected == true || TryConnect();
            }
        }
    }

    private bool TryConnect()
    {
        try
        {
            _tcp?.Dispose();
            _tcp = new TcpClient();
            if (!_tcp.ConnectAsync(host, port).Wait(1000))
            {
                _tcp.Dispose();
                _tcp = null;
                return false;
            }

            var stream = _tcp.GetStream();
            stream.ReadTimeout = 1500;
            _reader = new StreamReader(stream, Encoding.ASCII);
            _writer = new StreamWriter(stream, Encoding.ASCII) { AutoFlush = true };
            return true;
        }
        catch (Exception)
        {
            _tcp = null;
            return false;
        }
    }

    private string[]? Exchange(string command, int replyLines)
    {
        lock (_lock)
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                if (_tcp?.Connected != true && !TryConnect())
                {
                    return null;
                }

                try
                {
                    _writer!.Write(command + "\n");
                    var lines = new string[replyLines];
                    for (var i = 0; i < replyLines; i++)
                    {
                        lines[i] = _reader!.ReadLine() ?? throw new IOException("closed");
                    }

                    return lines;
                }
                catch (Exception)
                {
                    _tcp?.Dispose();
                    _tcp = null;
                }
            }

            return null;
        }
    }

    public long? GetFrequency()
    {
        var reply = Exchange("f", 1);
        return reply is not null && long.TryParse(reply[0], out var hz) ? hz : null;
    }

    public bool SetFrequency(long hz) => Exchange($"F {hz}", 1) is not null;

    public string? GetMode() => Exchange("m", 2)?[0];

    public bool SetMode(string mode) => Exchange($"M {mode} 0", 1) is not null;

    public bool? GetPtt()
    {
        var reply = Exchange("t", 1);
        return reply is null ? null : reply[0].Trim() == "1";
    }

    public bool SetPtt(bool on) => Exchange($"T {(on ? 1 : 0)}", 1) is not null;

    public void Dispose()
    {
        lock (_lock)
        {
            _tcp?.Dispose();
            _tcp = null;
        }
    }
}
