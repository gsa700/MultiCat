using System.Diagnostics;

namespace MultiCat.Service.Rigctld;

public sealed record RigctldOptions
{
    /// <summary>Full path to rigctld.exe (bundled with MultiCAT, or a system copy).</summary>
    public required string ExePath { get; init; }

    /// <summary>Hamlib rig model number (from the harvested rig database).</summary>
    public required int HamlibModel { get; init; }

    /// <summary>Serial port ("COM7") or network endpoint ("192.168.1.40:9200") the
    /// hamlib backend connects to — passed to rigctld's -r.</summary>
    public required string Device { get; init; }

    /// <summary>Serial line speed, when the backend is serial. Ignored for network rigs.</summary>
    public int? BaudRate { get; init; }

    /// <summary>Localhost TCP port clients (WSJT-X, Log4OM, …) connect to.</summary>
    public required int ListenPort { get; init; }

    /// <summary>Extra rigctld arguments, appended verbatim (e.g. rig-specific set-conf).</summary>
    public string? ExtraArgs { get; init; }
}

/// <summary>
/// Runs a real hamlib rigctld as the radio's client-facing endpoint. rigctld is the
/// reference implementation and multiplexes many hamlib clients over one radio, so
/// this replaces MultiCAT's own rigctld emulation with something broadly compatible.
/// The supervisor keeps it alive: it restarts rigctld if it exits unexpectedly.
/// </summary>
public sealed class RigctldSupervisor(RigctldOptions options, ILogger<RigctldSupervisor> logger) : IAsyncDisposable
{
    private static readonly TimeSpan RestartDelay = TimeSpan.FromSeconds(2);
    private readonly CancellationTokenSource _cts = new();
    private Process? _process;
    private Task? _monitor;

    public bool IsRunning => _process is { HasExited: false };

    /// <summary>Emitted for each rigctld stdout/stderr line, for the traffic monitor / logs.</summary>
    public event Action<string>? Output;

    public string BuildArguments()
    {
        var args = new List<string>
        {
            "-m", options.HamlibModel.ToString(),
            "-r", options.Device,
            "-T", "127.0.0.1",
            "-t", options.ListenPort.ToString(),
        };

        if (options.BaudRate is { } baud)
        {
            args.Add("-s");
            args.Add(baud.ToString());
        }

        var line = string.Join(' ', args.Select(Quote));
        return options.ExtraArgs is { Length: > 0 } extra ? $"{line} {extra}" : line;
    }

    private static string Quote(string arg) => arg.Contains(' ') ? $"\"{arg}\"" : arg;

    public void Start()
    {
        if (!File.Exists(options.ExePath))
        {
            throw new FileNotFoundException($"rigctld not found at {options.ExePath}");
        }

        _monitor = Task.Run(() => RunLoopAsync(_cts.Token));
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var arguments = BuildArguments();
            logger.LogInformation("Starting rigctld: {Exe} {Args}", options.ExePath, arguments);

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = options.ExePath,
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(options.ExePath),
                },
            };

            process.OutputDataReceived += (_, e) => { if (e.Data is { } d) OnOutput(d); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is { } d) OnOutput(d); };

            try
            {
                process.Start();
                _process = process;

                // Tie it to this process so a crash or a force-kill cannot leave it
                // running and holding the radio's CAT connection.
                if (OperatingSystem.IsWindows())
                {
                    ChildProcessJob.TryAssign(process, logger);
                }

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }

                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "rigctld failed to start on port {Port}", options.ListenPort);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            logger.LogWarning("rigctld (port {Port}) exited (code {Code}); restarting in {Delay}s",
                options.ListenPort, TryExitCode(process), RestartDelay.TotalSeconds);
            try
            {
                await Task.Delay(RestartDelay, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private static int? TryExitCode(Process p)
    {
        try
        {
            return p.HasExited ? p.ExitCode : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void OnOutput(string line)
    {
        logger.LogDebug("rigctld[{Port}]: {Line}", options.ListenPort, line);
        Output?.Invoke(line);
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        if (_process is { HasExited: false } p)
        {
            try
            {
                p.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
            }
        }

        if (_monitor is not null)
        {
            try
            {
                await _monitor;
            }
            catch (Exception)
            {
            }
        }

        _cts.Dispose();
    }
}
