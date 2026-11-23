using System.Globalization;
using System.Text;
using System.Text.Json;
using DailyDocket.Core.Configuration;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace DailyDocket.Core.Nightly;

/// <summary>
/// <c>state/lease.yaml</c> in the data repository: which machine is running the nightly jobs, when it
/// last did, and when it is next expected to.
/// </summary>
/// <param name="Holder">The machine that last claimed or ran a nightly job.</param>
/// <param name="Renewed">When the holder last touched the lease: a claim, a success or a failure. Shows it is alive.</param>
/// <param name="Due">
/// The holder's next scheduled run after its last success: the lease is past due when that time has
/// gone by unrenewed. Not moved by a failure, so a holder that keeps failing falls behind.
/// </param>
/// <param name="LastNightly">When the last nightly run (whose sweep succeeded) finished, on any machine.</param>
/// <param name="LastCatchUp">When the last midday catch-up finished, on any machine.</param>
/// <param name="Running">When the holder claimed the run it is doing now; null when no run is going.</param>
/// <param name="Failures">How many slots in a row the holder failed; two hand the job to a fallback.</param>
/// <param name="FailedSlot">The slot the last failure was for, so hourly retries of one slot count once.</param>
public sealed record JobLease(
    string Holder,
    DateTimeOffset Renewed,
    DateTimeOffset Due,
    DateTimeOffset? LastNightly,
    DateTimeOffset? LastCatchUp,
    DateTimeOffset? Running = null,
    int Failures = 0,
    DateTimeOffset? FailedSlot = null);

/// <summary>Whether this machine may run the nightly jobs now, and why, for the log.</summary>
public sealed record LeaseDecision(bool MayRun, string Reason);

/// <summary>The lease rules, as pure functions.</summary>
public static class LeaseRules
{
    /// <summary>Failed slots in a row after which a fallback takes over from a holder that is still on.</summary>
    public const int FailuresBeforeTakeover = 2;

    /// <summary>
    /// Nobody may start while another machine's claim is live. Otherwise the designated machine
    /// always may (it takes the job back when it returns). A fallback may while it holds the lease;
    /// yields while the holder has renewed since <paramref name="latestSlot"/> and has not failed
    /// <see cref="FailuresBeforeTakeover"/> slots in a row; takes over after that many failures; and
    /// otherwise once the lease is past due (its due time or last renewal, whichever is later) by the
    /// grace period times its place in the fallback list, so the first fallback goes first and the
    /// second only if the first is off too.
    /// </summary>
    /// <param name="latestSlot">The most recent scheduled slot (nightly or catch-up); null skips the rules that need it.</param>
    /// <param name="abandonAfter">A claim older than this is a run that died with its machine, and is ignored.</param>
    public static LeaseDecision Decide(
        NightlyConfig config, JobLease? lease, string machine, DateTimeOffset now, DateTimeOffset? latestSlot = null, TimeSpan? abandonAfter = null)
    {
        if (string.IsNullOrWhiteSpace(config.Machine))
        {
            return new LeaseDecision(false, "docket.yaml names no machine for the nightly jobs (nightly.machine).");
        }

        var isDesignated = config.IsDesignated(machine);
        var rank = config.FallbackRank(machine);
        if (!isDesignated && rank < 0)
        {
            return new LeaseDecision(false, $"{machine} is neither the designated machine nor a fallback.");
        }

        var mine = lease is not null && NightlyConfig.SameMachine(lease.Holder, machine);
        var abandon = abandonAfter ?? NightlyJobs.AbandonAfter;
        if (lease is { Running: { } since } && !mine && now - since < abandon)
        {
            // Even the designated machine waits: two runs at once would judge and brief everything twice.
            return new LeaseDecision(false, $"{lease.Holder} started a nightly run at {since:HH:mm} and is still on it");
        }

        if (isDesignated)
        {
            return new LeaseDecision(true, "this is the designated machine");
        }

        if (lease is null)
        {
            // Nobody has ever run the jobs: there is no due time to wait on, so only the first
            // fallback starts, and the designated machine takes over at its first run.
            return rank == 0
                ? new LeaseDecision(true, "no lease yet; first fallback starts")
                : new LeaseDecision(false, "no lease yet; waiting for the first fallback");
        }

        if (mine)
        {
            return new LeaseDecision(true, "this machine holds the lease");
        }

        var grace = TimeSpan.FromHours(Math.Max(0, config.GraceHours));
        if (lease.Failures >= FailuresBeforeTakeover)
        {
            // The holder is on but its runs keep failing (signed out of Claude, say): renewing
            // after each failure must not keep the fallbacks away for ever.
            var waited = now - lease.Renewed;
            return waited >= grace * rank
                ? new LeaseDecision(true, $"{lease.Holder} failed {lease.Failures} slots in a row; taking over")
                : new LeaseDecision(false, $"{lease.Holder} failed {lease.Failures} slots in a row; an earlier fallback goes first");
        }

        var abandoned = lease.Running is { } claimed && now - claimed >= abandon;
        if (latestSlot is { } slot && lease.Renewed >= slot && !abandoned)
        {
            return new LeaseDecision(false, $"{lease.Holder} is on duty for this slot");
        }

        // Past due from the later of its due time and its last renewal: a holder retrying a failed
        // run is alive even though its due time has passed.
        var from = lease.Due > lease.Renewed || abandoned ? lease.Due : lease.Renewed;
        var overdue = now - from;
        return overdue > grace * (rank + 1)
            ? new LeaseDecision(true, $"the lease held by {lease.Holder} is {overdue.TotalHours:0.0} hours past due; taking over")
            : new LeaseDecision(false, $"{lease.Holder} holds the lease");
    }

    /// <summary>
    /// This machine's claim on the run it is about to start, written and pushed before any work so
    /// another machine deciding at the same moment sees it (or wins, and this one sees that).
    /// </summary>
    public static JobLease Claim(JobLease? lease, string machine, DateTimeOffset now)
    {
        var same = lease is not null && NightlyConfig.SameMachine(lease.Holder, machine);
        return new JobLease(
            machine,
            now,
            lease?.Due ?? now,
            lease?.LastNightly,
            lease?.LastCatchUp,
            now,
            same ? lease!.Failures : 0,
            same ? lease!.FailedSlot : null);
    }

    /// <summary>The lease after this machine ran a job well: renewed now, due at its next slot, the failure count cleared.</summary>
    public static JobLease Renew(JobLease? lease, string machine, DateTimeOffset now, DateTimeOffset nextSlot, NightlySlot ran) =>
        new(
            machine,
            now,
            nextSlot,
            ran == NightlySlot.Nightly ? now : lease?.LastNightly,
            ran == NightlySlot.CatchUp ? now : lease?.LastCatchUp);

    /// <summary>
    /// The lease after a failed run: renewed (the machine is alive and will retry), but not due
    /// later and not done, and one more failed slot unless this slot already failed once.
    /// </summary>
    public static JobLease Fail(JobLease lease, string machine, DateTimeOffset now, DateTimeOffset latestSlot)
    {
        var same = NightlyConfig.SameMachine(lease.Holder, machine);
        var failures = same ? lease.Failures : 0;
        var counted = same && lease.FailedSlot is { } f && f.ToUnixTimeSeconds() == latestSlot.ToUnixTimeSeconds();
        return lease with
        {
            Holder = machine,
            Renewed = now,
            Running = null,
            Failures = counted ? failures : failures + 1,
            FailedSlot = latestSlot,
        };
    }

    /// <summary>The claim withdrawn, nothing else changed: the run was stopped (the app closed).</summary>
    public static JobLease Release(JobLease lease) => lease with { Running = null };

    /// <summary>Whether <paramref name="lease"/> is exactly the claim this machine wrote (the file keeps whole seconds).</summary>
    public static bool IsClaim(JobLease? lease, JobLease claim) =>
        lease is { Running: { } running }
        && NightlyConfig.SameMachine(lease.Holder, claim.Holder)
        && claim.Running is { } mine
        && running.ToUnixTimeSeconds() == mine.ToUnixTimeSeconds();
}

/// <summary>Reads and writes <c>state/lease.yaml</c>.</summary>
public static class LeaseFile
{
    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private static readonly JsonSerializerOptions Quote = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string PathIn(string repositoryRoot) => Path.Combine(repositoryRoot, "state", "lease.yaml");

    public static JobLease? Load(string repositoryRoot)
    {
        var path = PathIn(repositoryRoot);
        return File.Exists(path) ? Parse(File.ReadAllText(path)) : null;
    }

    public static void Save(string repositoryRoot, JobLease lease)
    {
        var path = PathIn(repositoryRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, Format(lease), new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Written by hand, one field per line, so a renewal shows in git as the lines that changed.</summary>
    public static string Format(JobLease lease)
    {
        var sb = new StringBuilder();

        // Quoted, as verdict files are: a machine named null, yes or 0123 would otherwise read
        // back as a null, a boolean or a number.
        sb.Append("holder: ").Append(JsonSerializer.Serialize(lease.Holder, Quote)).Append('\n');
        sb.Append("renewed: ").Append(Iso(lease.Renewed)).Append('\n');
        sb.Append("due: ").Append(Iso(lease.Due)).Append('\n');
        if (lease.LastNightly is { } n)
        {
            sb.Append("nightly: ").Append(Iso(n)).Append('\n');
        }

        if (lease.LastCatchUp is { } c)
        {
            sb.Append("catchUp: ").Append(Iso(c)).Append('\n');
        }

        if (lease.Running is { } r)
        {
            sb.Append("running: ").Append(Iso(r)).Append('\n');
        }

        if (lease.Failures > 0)
        {
            sb.Append("failures: ").Append(lease.Failures.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }

        if (lease.FailedSlot is { } s)
        {
            sb.Append("failedSlot: ").Append(Iso(s)).Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>Null for a file that is unreadable or incomplete: treated as no lease, never fatal.</summary>
    public static JobLease? Parse(string text)
    {
        Front? f;
        try
        {
            f = Yaml.Deserialize<Front?>(text);
        }
        catch (YamlDotNet.Core.YamlException)
        {
            return null;
        }

        if (f?.Holder is not { Length: > 0 } holder || Time(f.Renewed) is not { } renewed || Time(f.Due) is not { } due)
        {
            return null;
        }

        var failures = int.TryParse(f.Failures, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;
        return new JobLease(holder, renewed, due, Time(f.Nightly), Time(f.CatchUp), Time(f.Running), failures, Time(f.FailedSlot));
    }

    private static string Iso(DateTimeOffset d) => d.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);

    private static DateTimeOffset? Time(string? text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t) ? t : null;

    private sealed class Front
    {
        public string? Holder { get; set; }

        public string? Renewed { get; set; }

        public string? Due { get; set; }

        public string? Nightly { get; set; }

        public string? CatchUp { get; set; }

        public string? Running { get; set; }

        public string? Failures { get; set; }

        public string? FailedSlot { get; set; }
    }
}
