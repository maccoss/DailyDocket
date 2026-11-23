using System.Text;

namespace DailyDocket.Hook;

/// <summary>
/// The hook Claude Code runs around connector tool calls, from a per-run <c>--settings</c> file.
/// </summary>
/// <remarks>
/// <para><c>capture --dir D [--receipt]</c> (PostToolUse): saves the hook input, verbatim, as one
/// file in D, so the app parses what the connector returned rather than what the model retyped.
/// With <c>--receipt</c> it also replaces what Claude sees with a short receipt (ids and paging
/// tokens), so a large pull costs paging decisions rather than every body as input tokens.</para>
/// <para><c>guard --claims F</c> (PreToolUse): lets a send through only when it matches, exactly
/// and once, a call the app wrote to F for a claimed outbox item. Anything else is denied, and so
/// is everything when the guard itself fails: it exits 2, which Claude Code treats as a block.</para>
/// </remarks>
internal static class Program
{
    public static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        var mode = args.Length > 0 ? args[0] : "";
        string? Option(string name)
        {
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        string input;
        try
        {
            using var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
            input = stdin.ReadToEnd();
        }
        catch (IOException ex)
        {
            return Fail(mode, $"could not read the hook input: {ex.Message}");
        }

        try
        {
            switch (mode)
            {
                case "capture":
                    var dir = Option("--dir") ?? throw new ArgumentException("capture needs --dir.");
                    var output = Capture.Run(input, dir, args.Contains("--receipt"), DateTimeOffset.UtcNow);
                    if (output is not null)
                    {
                        Console.Out.Write(output);
                    }

                    return 0;

                case "guard":
                    var claims = Option("--claims") ?? throw new ArgumentException("guard needs --claims.");
                    var decision = SendGuard.Decide(input, claims);
                    if (decision is not null)
                    {
                        Console.Out.Write(decision);
                    }

                    return 0;

                default:
                    return Fail(mode, "usage: DailyDocket.Hook capture --dir <folder> [--receipt] | guard --claims <file>");
            }
        }
        catch (Exception ex)
        {
            return Fail(mode, ex.Message);
        }
    }

    /// <summary>
    /// A failed guard blocks (exit 2); a failed capture does not (exit 1), since the pull can be
    /// repeated and blocking a read gains nothing.
    /// </summary>
    private static int Fail(string mode, string message)
    {
        Console.Error.WriteLine($"DailyDocket hook ({mode}): {message}");
        return mode == "capture" ? 1 : 2;
    }
}
