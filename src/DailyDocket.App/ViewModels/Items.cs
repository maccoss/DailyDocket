namespace DailyDocket.App.ViewModels;

/// <summary>A day on the Schedule tab.</summary>
/// <remarks>
/// Equal by content, lists included: a record compares lists by reference, so every reload made
/// every day "different", the Schedule redrew all of them, and an open Brief closed within seconds.
/// </remarks>
public sealed record DayItem(string Key, string Heading, string Date, bool IsToday, IReadOnlyList<BannerItem> Background, IReadOnlyList<EventItem> Events)
{
    public bool Equals(DayItem? other) =>
        other is not null && Key == other.Key && Heading == other.Heading && Date == other.Date && IsToday == other.IsToday
        && Background.SequenceEqual(other.Background) && Events.SequenceEqual(other.Events);

    public override int GetHashCode() => HashCode.Combine(Key, Heading, Date, Events.Count);
}

/// <summary>An all-day or long event, shown as scenery across the top of a day.</summary>
public sealed record BannerItem(string Title, string Tag, string? Link);

/// <summary>A meeting.</summary>
/// <param name="Quiet">From a secondary calendar (department events): present, but not his commitment.</param>
/// <param name="CanRespond">An invitation (he is an attendee): Accept, Tentative and Decline apply.</param>
/// <param name="Response">His response as the calendar last reported it, or the one on its way out.</param>
/// <param name="Brief">The meeting brief from the data repository, when one exists.</param>
/// <param name="PrepNote">"Preparing a brief..." while Prep now runs, or why it did not work.</param>
/// <param name="JoinLink">The Zoom, Teams, Meet or Webex link behind the Join button, when there is one.</param>
public sealed record EventItem(string Time, string Title, string Tag, string Location, bool Clash, bool NeedsReply, bool Quiet, string? Link,
    string EventId = "", string? CalendarId = null, bool CanRespond = false, string? Response = null,
    BriefView? Brief = null, bool IsPrepping = false, string? PrepNote = null, string? JoinLink = null)
{
    /// <summary>
    /// Whether Mike has the Brief open, bound two-way to its expander. A reload copies it onto the
    /// new item for the same event, so a day redrawn because something on it changed keeps it open.
    /// </summary>
    public bool BriefOpen { get; set; }

    /// <summary>
    /// The location is the meeting's link (a Zoom room as the location): the link text stands in
    /// for it. Only that link: another address in the location (a map) must not be drawn as one
    /// that joins the meeting.
    /// </summary>
    public bool LocationJoins => JoinLink is not null && Location.Contains(Core.Calendar.MeetingLinks.Short(JoinLink), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The clickable link text under the title: the join link in short (host and path, no
    /// password), so a link found only in the invitation's text shows too.
    /// </summary>
    public string? JoinText => JoinLink is null ? null : Core.Calendar.MeetingLinks.Short(JoinLink);
}

/// <summary>A meeting brief as the Schedule shows it under its event.</summary>
/// <param name="Note">Who wrote it and when, and "may be out of date" when the event changed since.</param>
/// <remarks>Equal by content, for the reason given on <see cref="DayItem"/>.</remarks>
public sealed record BriefView(IReadOnlyList<string> Who, IReadOnlyList<string> Agenda, IReadOnlyList<string> Context, IReadOnlyList<string> Owes, IReadOnlyList<string> Gaps, string Note, bool Stale)
{
    public bool Equals(BriefView? other) =>
        other is not null && Note == other.Note && Stale == other.Stale
        && Who.SequenceEqual(other.Who) && Agenda.SequenceEqual(other.Agenda) && Context.SequenceEqual(other.Context)
        && Owes.SequenceEqual(other.Owes) && Gaps.SequenceEqual(other.Gaps);

    public override int GetHashCode() => HashCode.Combine(Note, Stale, Who.Count, Agenda.Count);
}

/// <summary>A thread on the Email tab.</summary>
/// <param name="Staleness">0 recent, 1 over a month, 2 over three months: the age chip's emphasis.</param>
/// <param name="UpTo">The time of the message he is waiting on (ISO 8601): what a Handled mark covers.</param>
public sealed record MailItem(string Key, string From, string Age, int Staleness, string Subject, string Snippet, string? Why, bool IsDraft, bool Unread, string UpTo);

/// <summary>One message in the reader.</summary>
/// <param name="Recipients">"to Pat, Sam; cc Lee" for mail, empty for Slack.</param>
/// <param name="Quoted">The quoted history below the message, folded away; null when there is none.</param>
public sealed record MessageView(string From, string When, string Recipients, string Text, string? Quoted, bool IsMine);

/// <summary>A reply on its way out, shown under the conversation it answers.</summary>
/// <param name="CanEdit">Written text can be reopened for editing; a calendar response cannot.</param>
public sealed record OutboxView(DailyDocket.Core.Outbox.OutboxItem Item, string Status, string? Problem, bool CanUndo, bool CanRetry, bool CanEdit = true)
{
    public string Preview => Item.Text.Length > 140 ? Item.Text[..140] + "…" : Item.Text;

    /// <summary>Edit shows for a held or failed item that is written text.</summary>
    public bool CanEditNow => CanRetry && CanEdit;
}

/// <summary>A conversation on the Slack tab.</summary>
/// <param name="LastTs">The newest message's ts: what a Handled mark covers.</param>
public sealed record SlackItem(string Key, string From, string Age, bool Stale, string Where, string Text, string? Why, string LastTs);

/// <summary>One choice in a list's Sort menu.</summary>
public sealed record OrderChoice(string Label, DailyDocket.Core.Infrastructure.ListOrder Value);
