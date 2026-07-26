using System.Diagnostics;

namespace MultiCat.Service.Rigctld;

/// <summary>
/// Clears rigctld processes left behind by an earlier run that ended without
/// cleaning up. Children started from now on are tied to this process by
/// <see cref="ChildProcessJob"/>, but anything orphaned before that — by a crash, or
/// by a build of MultiCAT that predates it — is still out there holding a
/// connection to the radio, which is enough to stop the next start connecting.
/// </summary>
internal static class OrphanedRigctld
{
    /// <summary>
    /// Kills only processes running <paramref name="exePath"/>, our bundled copy.
    /// A rigctld the operator runs themselves lives at a different path and is left
    /// alone — MultiCAT has no business killing a process it did not start.
    /// </summary>
    public static int Sweep(string exePath, ILogger logger)
    {
        var killed = 0;
        var full = Path.GetFullPath(exePath);

        foreach (var process in Process.GetProcessesByName("rigctld"))
        {
            try
            {
                if (process.Id == Environment.ProcessId)
                {
                    continue;
                }

                // Reading the module path can fail for processes we cannot open;
                // that also means it is not one of ours, so skip it.
                var path = process.MainModule?.FileName;
                if (path is null || !string.Equals(Path.GetFullPath(path), full, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                process.Kill(entireProcessTree: true);
                process.WaitForExit(2000);
                killed++;
                logger.LogWarning("Cleared an orphaned rigctld (pid {Pid}) left by an earlier run", process.Id);
            }
            catch (Exception ex)
            {
                logger.LogDebug("Could not inspect or stop rigctld pid {Pid}: {Message}", process.Id, ex.Message);
            }
            finally
            {
                process.Dispose();
            }
        }

        if (killed > 0)
        {
            logger.LogInformation(
                "Cleared {Count} orphaned rigctld process(es); they would have kept holding the radio", killed);
        }

        return killed;
    }
}
