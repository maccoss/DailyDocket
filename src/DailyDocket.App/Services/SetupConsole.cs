using System.Diagnostics;
using DailyDocket.Core.Setup;

namespace DailyDocket.App.Services;

/// <summary>Runs a setup command in a console window of its own (from LabOps' Shell).</summary>
public static class SetupConsole
{
    /// <summary>Runs <paramref name="command"/> and waits until Mike closes its window.</summary>
    /// <remarks>
    /// A console program started from a windowed app without CreateNoWindow gets a console of its
    /// own, which is what a sign-in that prints a one-time code needs.
    /// </remarks>
    public static async Task RunAsync(ConsoleCommand command)
    {
        var info = new ProcessStartInfo(command.FileName) { UseShellExecute = false, CreateNoWindow = false };
        foreach (var argument in command.Arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info);
        if (process is not null)
        {
            await process.WaitForExitAsync().ConfigureAwait(true);
        }
    }
}
