namespace MultiCat.Core.Protocol;

/// <summary>
/// Rules for the Kenwood/Elecraft ASCII protocol. A frame consisting of only the
/// alphabetic command prefix plus ';' is a read; anything with parameters is a set.
/// Sets are not answered. TX/RX/UP/DN are parameterless actions, not reads.
/// </summary>
public sealed class KenwoodRules : ICatProtocolRules
{
    private static readonly HashSet<string> ActionCommands = ["TX", "RX", "UP", "DN", "SWT", "SWH"];

    public bool ExpectsResponse(CatFrame command)
    {
        var prefix = GetPrefix(command);
        if (prefix.Length == 0 || ActionCommands.Contains(prefix))
        {
            return false;
        }

        return command.Length == prefix.Length + 1;
    }

    public bool IsResponseTo(CatFrame response, CatFrame command)
    {
        var commandPrefix = GetPrefix(command);
        return commandPrefix.Length > 0 && GetPrefix(response) == commandPrefix;
    }

    public bool IsCacheable(CatFrame command) => ExpectsResponse(command);

    private static string GetPrefix(CatFrame frame)
    {
        var span = frame.Data.Span;
        var end = 0;
        while (end < span.Length && span[end] is >= (byte)'A' and <= (byte)'Z')
        {
            end++;
        }

        return System.Text.Encoding.ASCII.GetString(span[..end]);
    }
}
