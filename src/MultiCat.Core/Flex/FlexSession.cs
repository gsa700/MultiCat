namespace MultiCat.Core.Flex;

/// <summary>
/// One client connection's command/status handling, as plain line-in / lines-out so
/// it can be exercised without a socket.
/// <para>
/// A 4O3A box drives the exchange documented in FlexRadio's PowerGenius XL API:
/// after the V/H handshake it registers itself (<c>amplifier create</c>), creates
/// meters, creates an interlock, enables keepalive, then subscribes to the slice
/// subsystem. Each command is acknowledged and slice status is streamed.
/// </para>
/// Wire framing:
/// <code>
/// client -> radio:  C&lt;seq&gt;|&lt;command&gt;
/// radio  -> client: R&lt;seq&gt;|&lt;hex&gt;|&lt;message&gt;   one reply per command
///                   S&lt;handle&gt;|&lt;object&gt; k=v ... async status
///                   V&lt;ver&gt; / H&lt;hex&gt;             handshake, sent on connect
/// </code>
/// </summary>
public sealed class FlexSession(IFlexRadioState radio)
{
    private readonly HashSet<string> _subscriptions = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>This connection's handle, echoed to the client during the handshake.</summary>
    public uint Handle { get; } = radio.AllocateHandle();

    /// <summary>Set once the client registers itself with <c>amplifier create</c>.</summary>
    public bool IsAmplifier { get; private set; }

    public bool KeepAlive { get; private set; }

    /// <summary>The client's version banner, e.g. the Antenna Genius sends "V4.1.16 AG".</summary>
    public string? ClientBanner { get; private set; }

    public bool IsSubscribedTo(string subsystem) =>
        _subscriptions.Contains(subsystem) || _subscriptions.Contains("all");

    /// <summary>
    /// What the radio says first. The client waits for this before issuing commands,
    /// and the interlock lines make the transmit path valid from the outset.
    /// </summary>
    public IReadOnlyList<string> Greeting() =>
    [
        "V1.4.0.0",
        $"H{Handle:X8}",
        radio.RadioStatusLine(),
        radio.InterlockConfigLine(),
        radio.InterlockStatusLine(),
    ];

    /// <summary>Handles one received line and returns whatever should be sent back.</summary>
    /// <summary>
    /// Drops console noise that arrives glued to the front of a command. The
    /// TunerGenius periodically emits its terminal banner — a screen-clear escape,
    /// "TunerGenius", "Password:" — into the same stream, with the next real command
    /// stuck on the end. Taking the command from the last command marker in the line
    /// keeps those from being lost; a dropped command is never acknowledged, so the
    /// box eventually gives up and reconnects.
    /// </summary>
    public static string StripConsoleNoise(string line)
    {
        var marker = line.LastIndexOf('|');
        if (marker <= 0)
        {
            return line;
        }

        // Walk back over the sequence number to the command letter that starts it.
        var start = marker - 1;
        while (start >= 0 && char.IsAsciiDigit(line[start]))
        {
            start--;
        }

        return start >= 0 && line[start] is 'C' or 'c' or 'R' or 'V' ? line[start..] : line;
    }

    public IReadOnlyList<string> Receive(string line)
    {
        if (line.Length == 0)
        {
            return [];
        }

        line = StripConsoleNoise(line);
        if (line.Length == 0)
        {
            return [];
        }

        switch (line[0])
        {
            case 'V':
                // Client version banner, informational.
                ClientBanner = line;
                return [];

            case 'R':
                // The client's own reply/NAK — e.g. the Antenna Genius answers "R0|1|"
                // when its parser rejects a line. Diagnostic only; never answered.
                return [];

            case 'C':
            case 'c':
                break;

            default:
                return [];
        }

        var body = line[1..];
        if (body.Length > 0 && (body[0] == 'D' || body[0] == 'd'))
        {
            body = body[1..];   // optional debug flag
        }

        var bar = body.IndexOf('|');
        if (bar < 0)
        {
            return [];          // malformed: no sequence separator
        }

        var seq = body[..bar];
        var command = body[(bar + 1)..].Trim();
        return Dispatch(seq, command);
    }

    private IReadOnlyList<string> Dispatch(string seq, string command)
    {
        var tokens = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var verb = tokens.Length > 0 ? tokens[0].ToLowerInvariant() : string.Empty;
        var args = tokens.Skip(1).ToArray();

        switch (verb)
        {
            case "sub":
                return Subscribe(seq, args);

            case "amplifier":
                return Amplifier(seq, args);

            case "meter":
                return Meter(seq, args);

            case "interlock":
                return Interlock(seq, args);

            case "keepalive":
                KeepAlive = true;
                return [Ok(seq)];

            default:
                // Permissive by design: acknowledge anything else so an unmodelled
                // command cannot stall a box mid-handshake.
                return [Ok(seq)];
        }
    }

    private IReadOnlyList<string> Subscribe(string seq, string[] args)
    {
        var subsystem = args.Length > 0 ? args[0].ToLowerInvariant() : "all";
        _subscriptions.Add(subsystem);

        var lines = new List<string> { Ok(seq) };
        if (subsystem is "slice" or "tx" or "all")
        {
            // Dump current state so a subscriber is immediately correct rather than
            // waiting for the next change.
            lines.AddRange(radio.SliceStatusLines());
            lines.Add(radio.TransmitStatusLine());
        }

        return lines;
    }

    private IReadOnlyList<string> Amplifier(string seq, string[] args)
    {
        if (args.Length == 0 || !args[0].Equals("create", StringComparison.OrdinalIgnoreCase))
        {
            return [Ok(seq)];
        }

        var handle = radio.AllocateHandle();
        radio.AddAmplifier(handle, ParseKeyValues(args.Skip(1)));
        IsAmplifier = true;     // so this connection appears in the interlock's amplifier list
        return [Ok(seq, $"0x{handle:X8}")];   // Flex answers with the new object's handle
    }

    private IReadOnlyList<string> Meter(string seq, string[] args)
    {
        if (args.Length == 0 || !args[0].Equals("create", StringComparison.OrdinalIgnoreCase))
        {
            return [Ok(seq)];
        }

        var meterId = radio.AllocateMeterId();
        radio.AddMeter(meterId, ParseKeyValues(args.Skip(1)));
        return [Ok(seq, meterId.ToString())];
    }

    private IReadOnlyList<string> Interlock(string seq, string[] args)
    {
        if (args.Length == 0 || !args[0].Equals("create", StringComparison.OrdinalIgnoreCase))
        {
            return [Ok(seq)];
        }

        var id = radio.AddInterlock(ParseKeyValues(args.Skip(1)));
        return [Ok(seq, id.ToString())];
    }

    private static string Ok(string seq, string message = "") => $"R{seq}|0|{message}";

    /// <summary>Parses <c>key=value</c> tokens; bare tokens are ignored.</summary>
    public static Dictionary<string, string> ParseKeyValues(IEnumerable<string> tokens)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in tokens)
        {
            var equals = token.IndexOf('=');
            if (equals > 0)
            {
                result[token[..equals]] = token[(equals + 1)..];
            }
        }

        return result;
    }
}
