using DailyDocket.Core.Calendar;
using DailyDocket.Core.Capture;
using DailyDocket.Core.Mail;
using DailyDocket.Core.Slack;
using DailyDocket.Tests.Fixtures;

namespace DailyDocket.Tests.Captures;

/// <summary>
/// Opt-in: runs every parser over real captures, such as those under
/// %LOCALAPPDATA%\DailyDocket\spike. Set DAILYDOCKET_CAPTURES to that folder. The captures hold real
/// mail, so they are read in place and never copied into this public repository.
/// </summary>
public sealed class RealCaptureTests
{
    private static readonly string? Folder = Environment.GetEnvironmentVariable("DAILYDOCKET_CAPTURES");

    [Fact]
    public void Every_real_capture_parses()
    {
        Assert.SkipWhen(string.IsNullOrEmpty(Folder) || !Directory.Exists(Folder), "Set DAILYDOCKET_CAPTURES to a folder of real captures.");

        var parsed = 0;
        foreach (var file in Directory.EnumerateFiles(Folder!, "*.json", SearchOption.AllDirectories)
                     .Where(f => Path.GetDirectoryName(f)!.EndsWith("captures", StringComparison.OrdinalIgnoreCase)))
        {
            var call = CapturedCall.Parse(File.ReadAllText(file), File.GetLastWriteTimeUtc(file));
            switch (call.Tool)
            {
                case "search_threads":
                    var (threads, _) = GmailParser.ParseSearch(call.ResponseText);
                    threads.ShouldAllBe(t => t.ThreadId.Length > 0 && t.NewestMessageId.Length > 0, file);
                    break;
                case "get_thread":
                    var (_, messages) = GmailParser.ParseThread(call.ResponseText);
                    messages.ShouldNotBeEmpty(file);
                    messages.ShouldAllBe(m => m.Date != null, file);
                    break;
                case "list_drafts":
                    GmailParser.ParseDrafts(call.ResponseText);
                    break;
                case "list_events":
                    var (_, events, _) = CalendarParser.Parse(call.ResponseText, "work", Fixture.Pacific);
                    events.ShouldNotBeEmpty(file);
                    break;
                case "slack_search_public_and_private":
                    var (slack, _) = SlackParser.ParseSearch(call.ResponseText);
                    slack.ShouldNotBeEmpty(file);
                    // Text may be empty: a file posted with no comment.
                    slack.ShouldAllBe(m => m.ChannelId.Length > 0 && m.FromId.Length > 0 && m.TsValue > 0, file);
                    slack.ShouldContain(m => m.Text.Length > 0, file);
                    break;
                default:
                    continue;
            }

            parsed++;
        }

        parsed.ShouldBeGreaterThan(0);
    }
}
