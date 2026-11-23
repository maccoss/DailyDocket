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
/// <param name="Holder">The machine that last ran a nightly job.</param>
/// <param name="Renewed">When it last renewed the lease (after each job run, whatever the outcome).</param>
/// <param name="Due">The holder's next scheduled run: the lease is past due when that time has gone by unrenewed.</param>
/// <param name="LastNightly">When the last nightly run (sweep and prep) finished, on any machine.</param>
/// <param name="LastCatchUp">When the last midday catch-up finished, on any machine.</param>
public sealed record JobLease(string Holder, DateTimeOffset Renewed, DateTimeOffset Due, DateTimeOffset? LastNightly, DateTimeOffset? LastCatchUp);

/// <summary>Whether this machine may run the nightly jobs now, and why, for the log.</summary>
public sealed record LeaseDecision(bool MayRun, string Reason);

/// <summary>The lease rules, as pure functions.</summary>
public static class LeaseRules
{
    /// <summary>
    /// The designated machine always may (it takes the job back when it returns). A fallback may while
    /// it holds the lease, or once the lease is past due by the grace period times its place in the
    /// fallback list, so the first fallback goes first and the second only if the first is off too.
    /// </summary>
    public static LeaseDecision Decide(NightlyConfig config, JobLease? lease, string machine, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(config.Machine))
        {
            return new LeaseDecision(false, "docket.yaml names no machine for the nightly jobs (nightly.machine).");
        }

        if (config.IsDesignated(machine))
        {
            return new LeaseDecision(true, "this is the designated machine");
        }

        var rank = config.FallbackRank(machine);
        if (rank < 0)
        {
            return new LeaseDecision(false, $"{machine} is neither the designated machine nor a fallback.");
        }

        if (lease is null)
        {
            // Nobody has ever run the jobs: there is no due time to wait on, so only the first
            // fallback starts, and the designated machine takes over at its first run.
            return rank == 0
                ? new LeaseDecision(true, "no lease yet; first fallback starts")
                : new LeaseDecision(false, "no lease yet; waiting for the first fallback");
        }

        if (NightlyConfig.SameMachine(lease.Holder, machine))
        {
            return new LeaseDecision(true, "this machine holds the lease");
        }

        var grace = TimeSpan.FromHours(Math.Max(0, config.GraceHours) * (rank + 1));
        var overdue = now - lease.Due;
        return overdue > grace
            ? new LeaseDecision(true, $"the lease held by {lease.Holder} is {overdue.TotalHours:0.0} hours past due; taking over")
            : new LeaseDecision(false, $"{lease.Holder} holds the lease");
    }

    /// <summary>The lease after this machine ran a job: renewed now, due at its next slot.</summary>
    public static JobLease Renew(JobLease? lease, string machine, DateTimeOffset now, DateTimeOffset nextSlot, NightlySlot ran) =>
        new(
            machine,
            now,
            nextSlot,
            ran == NightlySlot.Nightly ? now : lease?.LastNightly,
            ran == NightlySlot.CatchUp ? now : lease?.LastCatchUp);
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

        return new JobLease(holder, renewed, due, Time(f.Nightly), Time(f.CatchUp));
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
    }
}
