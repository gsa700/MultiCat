using System.IO.Ports;
using MultiCat.Core;

namespace MultiCat.Service.Transports;

/// <summary>
/// Real serial connection to a radio. Owns the COM port exclusively for the
/// lifetime of the session. Only ever opens the port named in configuration —
/// never probes.
/// </summary>
public sealed class SerialPortTransport : ICatTransport
{
    private readonly SerialPort _port;

    public SerialPortTransport(string portName, int baudRate)
    {
        _port = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.None,
            ReadTimeout = SerialPort.InfiniteTimeout,
            WriteTimeout = 2000,
        };
        _port.DataReceived += OnPortDataReceived;
    }

    public event Action<byte[]>? DataReceived;

    public void Open() => _port.Open();

    public ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        return _port.BaseStream.WriteAsync(data, cancellationToken);
    }

    private void OnPortDataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        try
        {
            var available = _port.BytesToRead;
            if (available <= 0)
            {
                return;
            }

            var buffer = new byte[available];
            var read = _port.Read(buffer, 0, available);
            if (read > 0)
            {
                if (read < buffer.Length)
                {
                    Array.Resize(ref buffer, read);
                }

                DataReceived?.Invoke(buffer);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException)
        {
            // Port closed underneath us during shutdown; nothing to deliver.
        }
    }

    public ValueTask DisposeAsync()
    {
        _port.DataReceived -= OnPortDataReceived;
        if (_port.IsOpen)
        {
            _port.Close();
        }

        _port.Dispose();
        return ValueTask.CompletedTask;
    }
}
