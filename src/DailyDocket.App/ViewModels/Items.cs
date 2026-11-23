namespace DailyDocket.App.ViewModels;

/// <summary>A day on the Schedule tab.</summary>
public sealed record DayItem(string Key, string Heading, string Date, bool IsToday, IReadOnlyList<BannerItem> Background, IReadOnlyList<EventItem> Events);

/// <summary>An all-day or long event, shown as scenery across the top of a day.</summary>
public sealed record BannerItem(string Title, string Tag, string? Link);

/// <summary>A meeting.</summary>
/// <param name="Quiet">From a secondary calendar (department events): present, but not his commitment.</param>
public sealed record EventItem(string Time, string Title, string Tag, string Location, bool Clash, bool NeedsReply, bool Quiet, string? Link);

/// <summary>A thread on the Email tab.</summary>
/// <param name="Staleness">0 recent, 1 over a month, 2 over three months: the age chip's emphasis.</param>
public sealed record MailItem(string Key, string From, string Age, int Staleness, string Subject, string Snippet, string? Why, bool IsDraft, bool Unread);

/// <summary>One message in the reader.</summary>
/// <param name="Recipients">"to Pat, Sam; cc Lee" for mail, empty for Slack.</param>
/// <param name="Quoted">The quoted history below the message, folded away; null when there is none.</param>
public sealed record MessageView(string From, string When, string Recipients, string Text, string? Quoted, bool IsMine);

/// <summary>A conversation on the Slack tab.</summary>
public sealed record SlackItem(string Key, string From, string Age, bool Stale, string Where, string Text, string? Why);
