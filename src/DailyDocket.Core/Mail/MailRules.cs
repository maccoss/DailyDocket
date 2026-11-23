using System.Text;
using System.Text.RegularExpressions;
using DailyDocket.Core.Configuration;

namespace DailyDocket.Core.Mail;

/// <summary>Where a thread stands, before Claude reads it.</summary>
public enum MailCategory
{
    /// <summary>Mail no human wrote: auto-replies, machine senders, calendar status mail.</summary>
    Auto,

    /// <summary>A meeting invitation: answered in the calendar, not by writing back.</summary>
    Invite,

    /// <summary>In the trash or spam.</summary>
    Trash,

    /// <summary>He wrote after the newest message anyone else sent.</summary>
    LastWord,

    /// <summary>Copied on a conversation he never wrote in: being informed, not asked.</summary>
    Informed,

    /// <summary>Something arrived since his last reply; Claude decides whether it needs one.</summary>
    Open,

    /// <summary>He started a reply and did not send it. Outranks every quiet rule.</summary>
    Draft,
}

/// <summary>The rules' reading of one thread.</summary>
/// <param name="Trailing">How many messages from others arrived after his last one.</param>
/// <param name="WaitingSince">The newest message he has not answered, or the newest message.</param>
/// <param name="WaitingOn">Who is waiting: the most recent sender who is not him.</param>
public sealed record MailClassification(
    MailCategory Category,
    int Trailing,
    bool RepliedEarlier,
    DateTimeOffset? WaitingSince,
    string NewestMessageId,
    string WaitingOn,
    string Subject,
    string Snippet,
    bool Unread)
{
    /// <summary>Hidden without asking Claude anything.</summary>
    public bool IsQuiet => Category is not (MailCategory.Open or MailCategory.Draft);
}

/// <summary>
/// The dashboard artifact's mail rules, as pure functions. Each comment records why the rule is
/// the way it is; most were learned from a real thread that was shown or hidden wrongly.
/// </summary>
public static partial class MailRules
{
    // Auto-responders. "out of office Re: <original subject>" is the common shape, so these match
    // anywhere in the subject, not just at the start.
    private static readonly Regex[] AutoSubject =
    [
        new(@"\bout of (the )?office\b", RegexOptions.IgnoreCase),
        new(@"\bautomatic(ally)? repl(y|ies)\b", RegexOptions.IgnoreCase),
        new(@"\bauto[- ]?repl(y|ies)\b", RegexOptions.IgnoreCase),
        new(@"^\s*(re:\s*)*(away from|absence|abwesenheit|r[ée]ponse automatique|respuesta autom[áa]tica)", RegexOptions.IgnoreCase),
        new(@"\b(on|currently on) (annual|parental|medical|sabbatical) leave\b", RegexOptions.IgnoreCase),
        new(@"^\s*undeliverable\b", RegexOptions.IgnoreCase),
        new(@"^\s*(delivery status notification|mail delivery (subsystem|failed))", RegexOptions.IgnoreCase),
    ];

    // Machines and workflow systems: replying goes nowhere. The owner's own additions (vendors,
    // newsletters, institutional systems) are in docket.yaml, since this repository is public.
    private static readonly string[] SystemSenders =
    [
        "noreply", "no-reply", "donotreply", "do-not-reply", "mailer-daemon@", "postmaster@",
        "@docusign.net", "notifications@github.com", "announcements@", "marketing@", "webinars@",
        "product-news@", "@medium.com",
    ];

    // Calendar status mail.
    private static readonly Regex[] NoticeSubject =
    [
        new(@"^\s*(canceled|cancelled|accepted|declined|tentative|updated invitation|invitation|new time proposed):\s", RegexOptions.IgnoreCase),
        new(@"^\[TC\]", RegexOptions.IgnoreCase),
    ];

    // Meeting invitations. The subject is only the meeting title, so these are recognized by the
    // invitation boilerplate in the body. A note ASKING for times has none of it and still shows.
    private static readonly Regex[] InviteText =
    [
        new(@"\bis inviting you to a scheduled zoom meeting\b", RegexOptions.IgnoreCase),
        new(@"\bjoin zoom meeting\b", RegexOptions.IgnoreCase),
        new(@"\b(microsoft teams meeting|join the meeting now)\b", RegexOptions.IgnoreCase),
        new(@"\bmeeting id:\s*\d", RegexOptions.IgnoreCase),
        new(@"\bhas invited you to (a|an|the)?\s*(meeting|event)\b", RegexOptions.IgnoreCase),
        new(@"^\s*when:\s.*\bwhere:\s", RegexOptions.IgnoreCase | RegexOptions.Singleline),
        new(@"\bgoogle calendar\b.*\binvitation\b", RegexOptions.IgnoreCase),
        new(@"\bgoing \(.*\)\?\s*(yes|maybe|no)\b", RegexOptions.IgnoreCase),
        // Outlook and Teams invitations often open with a recording notice, which pushes the
        // invitation wording past the preview entirely.
        new(@"\bthis meeting (may|will) be recorded\b", RegexOptions.IgnoreCase),
        new(@"\bmeeting organizer\b", RegexOptions.IgnoreCase),
        new(@"\bmicrosoft teams\b", RegexOptions.IgnoreCase),
        // The most reliable tell: the text leads with a join link. Almost nothing that needs a
        // written answer opens with a bare meeting URL.
        new(@"zoom\.us/(j|my|s)/", RegexOptions.IgnoreCase),
        new(@"teams\.microsoft\.com/(meet|l/meetup)", RegexOptions.IgnoreCase),
        new(@"meet\.google\.com/", RegexOptions.IgnoreCase),
        new(@"webex\.com/(meet|join)", RegexOptions.IgnoreCase),
    ];

    private static readonly string[] InviteSenders = ["calendar-notification@google.com"];

    /// <summary>How much of a thread the triage model reads, split across the messages it judges.</summary>
    public const int TriageBudget = 2400;

    /// <summary>No message gets less than this, however many there are.</summary>
    public const int MinimumPerMessage = 600;

    /// <summary>
    /// The ONLY things kept out without being read: mail no human wrote, and invitations.
    /// Everything else is judged on what it says, because a pattern over a subject line cannot tell
    /// a real request from a form letter: that mistake once buried a live request behind a "bulk
    /// mail" rule.
    /// </summary>
    public static MailCategory? DropReason(string? subject, string? text, string? sender, IEnumerable<string>? extraSystemSenders = null)
    {
        subject ??= "";
        text ??= "";
        if (AutoSubject.Any(r => r.IsMatch(subject)) ||
            SenderIn(SystemSenders, sender) ||
            SenderIn(extraSystemSenders ?? [], sender) ||
            NoticeSubject.Any(r => r.IsMatch(subject)))
        {
            return MailCategory.Auto;
        }

        if (InviteText.Any(r => r.IsMatch(text)) || SenderIn(InviteSenders, sender))
        {
            return MailCategory.Invite;
        }

        return null;
    }

    /// <summary>Classifies a thread from every one of its messages (a get_thread read).</summary>
    public static MailClassification Classify(IReadOnlyList<MailMessage> messages, bool hasDraft, DocketConfig config)
    {
        ArgumentOutOfRangeException.ThrowIfZero(messages.Count);
        var msgs = messages.OrderBy(m => m.Date ?? DateTimeOffset.MinValue).ToList();
        var last = msgs[^1];
        var subject = msgs.Select(m => m.Subject).LastOrDefault(s => !string.IsNullOrWhiteSpace(s)) ?? "(no subject)";
        var waitingOn = DisplayName(msgs.LastOrDefault(m => !config.Owner.IsMe(m.Sender))?.Sender ?? last.Sender);
        var snippet = Clean(last.Snippet ?? Excerpt(last.Body, 200));
        var unread = msgs.Any(m => m.Labels.Contains("UNREAD"));

        // "Owed" means something arrived since his last reply, not "someone else spoke last".
        // A thread he answered that then drew three new requests is the most urgent kind there is.
        var lastMine = msgs.FindLastIndex(m => config.Owner.IsMe(m.Sender));
        var after = msgs.Skip(lastMine + 1).Where(m => !config.Owner.IsMe(m.Sender)).ToList();
        var waitingSince = (after.Count > 0 ? after[^1] : last).Date;

        MailClassification Result(MailCategory c) =>
            new(c, after.Count, lastMine >= 0, waitingSince, last.Id, waitingOn, subject, snippet, unread);

        if (last.Labels.Contains("TRASH") || last.Labels.Contains("SPAM"))
        {
            return Result(MailCategory.Trash);
        }

        // Machine mail and invitations never reach the reader: no point paying to read a Zoom link.
        if (DropReason(last.Subject ?? subject, last.Snippet ?? last.Body, last.Sender, config.Mail.SystemSenders) is { } drop)
        {
            return Result(drop);
        }

        // An unsent draft is the strongest evidence in the mailbox that he meant to reply and did
        // not. It outranks every quiet rule, including "you answered last".
        if (hasDraft)
        {
            return Result(MailCategory.Draft);
        }

        if (after.Count == 0)
        {
            return Result(MailCategory.LastWord);
        }

        // Copied on a conversation between other people that he has never written in: being kept
        // informed, not being asked. The commonest kind of row that looked like work and was not.
        var directTo = last.To.Any(config.Owner.IsMe);
        if (!directTo && lastMine < 0)
        {
            return Result(MailCategory.Informed);
        }

        return Result(MailCategory.Open);
    }

    /// <summary>
    /// What the triage model reads: only the messages that arrived after his last reply, since only
    /// they decide whether he still owes an answer, each capped to share the budget.
    /// </summary>
    public static string TriageTranscript(IReadOnlyList<MailMessage> messages, DocketConfig config)
    {
        var msgs = messages.OrderBy(m => m.Date ?? DateTimeOffset.MinValue).ToList();
        var lastMine = msgs.FindLastIndex(m => config.Owner.IsMe(m.Sender));
        var after = msgs.Skip(lastMine + 1).Where(m => !config.Owner.IsMe(m.Sender)).ToList();
        if (after.Count == 0)
        {
            after = [msgs[^1]];
        }

        var budget = Math.Max(MinimumPerMessage, TriageBudget / after.Count);
        var sb = new StringBuilder();
        sb.Append("Subject: ").AppendLine(msgs.Select(m => m.Subject).LastOrDefault(s => !string.IsNullOrWhiteSpace(s)) ?? "(no subject)");
        sb.Append("Thread length: ").Append(msgs.Count).AppendLine(" messages");
        sb.AppendLine(lastMine >= 0
            ? $"He ALREADY replied in this thread. The {after.Count} message(s) below arrived AFTER his last reply; judge only whether THESE require a further answer from him."
            : "He has never written in this thread.");
        foreach (var m in after)
        {
            sb.AppendLine().Append("--- From: ").Append(m.Sender).Append("  (").Append(m.Date?.ToString("u") ?? "").AppendLine(")");
            sb.AppendLine(Excerpt(m.Body ?? m.Snippet, budget));
        }

        return sb.ToString();
    }

    /// <summary>"Pat Example" from <c>"Pat Example" &lt;pat@example.org&gt;</c>; the address when there is no name.</summary>
    public static string DisplayName(string? sender)
    {
        var s = sender ?? "";
        var named = NamePart().Match(s);
        var name = named.Success ? named.Groups[1].Value.Trim() : "";
        return name.Length > 0 ? name : s.Trim('<', '>', ' ');
    }

    private static bool SenderIn(IEnumerable<string> list, string? sender)
    {
        var s = (sender ?? "").ToLowerInvariant();
        return s.Length > 0 && list.Any(x => x.Length > 0 && s.Contains(x.ToLowerInvariant(), StringComparison.Ordinal));
    }

    private static string Excerpt(string? text, int max)
    {
        var t = (text ?? "").Trim();
        return t.Length <= max ? t : t[..max];
    }

    private static string Clean(string text) =>
        text.Replace("&#39;", "’", StringComparison.Ordinal)
            .Replace("&quot;", "\"", StringComparison.Ordinal)
            .Replace("&amp;", "&", StringComparison.Ordinal)
            .Replace("&nbsp;", " ", StringComparison.Ordinal);

    [GeneratedRegex("^\"?([^\"<]*?)\"?\\s*<.*$")]
    private static partial Regex NamePart();
}
