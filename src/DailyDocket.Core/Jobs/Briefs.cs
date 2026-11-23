using DailyDocket.Core.Configuration;

namespace DailyDocket.Core.Jobs;

/// <summary>What the triage model is told, from the dashboard artifact's briefs.</summary>
/// <remarks>
/// The owner's description comes from docket.yaml (owner.about), so it is not in this public code.
/// Each verdict is a yes or no with a reason of ten words or fewer, which the tab shows as is.
/// </remarks>
public static class Briefs
{
    public static string Owner(DocketConfig config)
    {
        var name = config.Owner.Name.Length > 0 ? config.Owner.Name : "the owner";
        return config.Owner.About.Length > 0 ? $"{name}, {config.Owner.About}" : name;
    }

    public static string Mail(DocketConfig config) =>
        $"""
        Decide whether an email thread still needs a written reply FROM {Owner(config)}.

        It does NOT need a reply when it is: a calendar invitation or meeting notice (he answers those in the calendar); an automated, system or workflow notification; a newsletter, announcement or FYI with no question or request; a journal, vendor or conference solicitation; a thank-you, confirmation or sign-off that closes the conversation; or something another person in the thread has already handled.

        It DOES need a reply when someone is asking him a question, requesting a document, decision, signature, letter, review or data, proposing or asking for meeting times, or otherwise waiting on him.
        """;

    public static string Slack(DocketConfig config) =>
        $"""
        Decide whether a Slack conversation needs a reply from {Owner(config)}. Someone else sent the most recent message. "Mike" in a transcript is him.

        It does NOT need a reply when the last messages are an acknowledgement, a thank-you, a status update or FYI with no question, banter, a message clearly aimed at someone else in the conversation, or something already answered.
        It DOES need a reply when someone asks him a question, requests a decision, review, file or access, proposes a time, or is plainly waiting on him.
        """;
}
