using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace DailyDocket.Core.Configuration;

/// <summary>
/// Who the docket belongs to and what it reads: <c>config/docket.yaml</c> in the data repository.
/// </summary>
/// <remarks>
/// Everything personal lives here rather than in code, because the app's repository is public:
/// the owner's addresses and Slack id, the calendar ids, and the extra senders that are machines.
/// The rules ship generic defaults; this file adds to them. Until the data repository is cloned
/// (phase 3) the app reads the same file from its data folder.
/// </remarks>
public sealed class DocketConfig
{
    public OwnerConfig Owner { get; set; } = new();

    public List<CalendarConfig> Calendars { get; set; } = [new() { Key = "work", Id = null, Primary = true }];

    public MailConfig Mail { get; set; } = new();

    public SlackConfig Slack { get; set; } = new();

    public ScheduleConfig Schedule { get; set; } = new();

    /// <summary>Which machine runs the shared nightly jobs (deep sweep, meeting prep), and when.</summary>
    public NightlyConfig Nightly { get; set; } = new();

    /// <summary>Which events get a meeting brief.</summary>
    public PrepConfig Prep { get; set; } = new();

    /// <summary>Reads <paramref name="path"/>, or returns the defaults when it does not exist.</summary>
    public static DocketConfig Load(string path)
    {
        if (!File.Exists(path))
        {
            return new DocketConfig();
        }

        try
        {
            return Parse(File.ReadAllText(path));
        }
        catch (YamlDotNet.Core.YamlException e)
        {
            throw new InvalidDataException(
                $"{path} could not be read (line {e.Start.Line}): {e.InnerException?.Message ?? e.Message} Fix the file and refresh.", e);
        }
    }

    public static DocketConfig Parse(string yaml)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
        return deserializer.Deserialize<DocketConfig?>(yaml) ?? new DocketConfig();
    }

    /// <summary>The calendar a <c>list_events</c> call read, by the calendarId it was given.</summary>
    public CalendarConfig CalendarFor(string? calendarId)
    {
        var id = string.IsNullOrWhiteSpace(calendarId) || calendarId == "primary" ? null : calendarId;
        return Calendars.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase))
            ?? new CalendarConfig { Key = id ?? "work", Id = id, Primary = id is null };
    }
}

public sealed class OwnerConfig
{
    public string Name { get; set; } = "";

    /// <summary>Mail from any of these addresses was written by the owner.</summary>
    public List<string> Emails { get; set; } = [];

    public string SlackUserId { get; set; } = "";

    /// <summary>One sentence about the owner, given to the triage model for context.</summary>
    public string About { get; set; } = "";

    public bool IsMe(string? sender) =>
        !string.IsNullOrEmpty(sender) && Emails.Any(e => sender.Contains(e, StringComparison.OrdinalIgnoreCase));
}

public sealed class CalendarConfig
{
    /// <summary>A short stable name (work, home, lab, dept) used as the row key.</summary>
    public string Key { get; set; } = "";

    /// <summary>The calendar id; null for the account's primary calendar.</summary>
    public string? Id { get; set; }

    /// <summary>The label shown on its events; empty for the main calendar.</summary>
    public string Tag { get; set; } = "";

    /// <summary>Primary calendars are his commitments: checked for clashes. Others are shown quietly.</summary>
    public bool Primary { get; set; } = true;
}

public sealed class MailConfig
{
    /// <summary>How far back the live inbox sweep reaches.</summary>
    public int WindowDays { get; set; } = 30;

    /// <summary>How far back the nightly deep sweep reaches, beyond the live window, for old unanswered threads.</summary>
    public int DeepDays { get; set; } = 90;

    /// <summary>Senders that are machines, added to the generic list in the rules.</summary>
    public List<string> SystemSenders { get; set; } = [];
}

public sealed class SlackConfig
{
    public int WindowDays { get; set; } = 21;
}

public sealed class ScheduleConfig
{
    public int Days { get; set; } = 7;

    public string TimeZone { get; set; } = "America/Los_Angeles";
}

/// <summary>
/// Which events meeting prep covers. Declined, cancelled and all-day events, and meetings with no
/// one else invited, are always skipped; a secondary calendar's events (primary: false) only when
/// the owner accepted them. Here so the rules can change without a release.
/// </summary>
public sealed class PrepConfig
{
    /// <summary>How many working days (Monday to Friday) ahead, starting today, get briefs.</summary>
    public int WorkingDays { get; set; } = 2;

    /// <summary>Calendar keys never prepped, for example the home calendar.</summary>
    public List<string> SkipCalendars { get; set; } = [];

    /// <summary>At most this many briefs per run, so a crowded week cannot spend the night's usage.</summary>
    public int MaxPerRun { get; set; } = 20;
}

/// <summary>
/// The shared nightly jobs run on one machine so the team seat's usage is spent once, not once per
/// computer. A lease file in the data repository lets a fallback take over when that machine is off.
/// </summary>
public sealed class NightlyConfig
{
    /// <summary>The machine that runs the nightly jobs. Empty: none does, and briefs are made only on demand.</summary>
    public string Machine { get; set; } = "";

    /// <summary>Machines that take over, in this order, when the designated one misses its runs.</summary>
    public List<string> Fallback { get; set; } = [];

    /// <summary>Local time of the nightly run (deep sweep and meeting prep), "HH:mm".</summary>
    public string At { get; set; } = "02:00";

    /// <summary>Local time of the midday catch-up (meeting prep for events added or changed since), "HH:mm".</summary>
    public string CatchUpAt { get; set; } = "12:30";

    /// <summary>
    /// How long past due the lease must be before the first fallback takes over; the second waits
    /// twice as long, and so on, so two fallbacks rarely both run.
    /// </summary>
    public double GraceHours { get; set; } = 2;

    /// <summary>Machine names compare without case: Windows reports them upper case, the repository records them lower.</summary>
    public static bool SameMachine(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a) && string.Equals(a.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);

    public bool IsDesignated(string machine) => SameMachine(Machine, machine);

    /// <summary>The machine's place among the fallbacks, or -1.</summary>
    public int FallbackRank(string machine) => Fallback.FindIndex(f => SameMachine(f, machine));
}
