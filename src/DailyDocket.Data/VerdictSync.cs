using DailyDocket.Core.Verdicts;

namespace DailyDocket.Data;

/// <summary>
/// Moves Claude's verdicts between the cache and the data repository: each verdict judged here is
/// written to <c>verdicts/</c>, and verdicts judged on other machines are read back into the cache,
/// so a conversation is judged (and paid for) once, not once per computer.
/// </summary>
/// <remarks>
/// Both directions use the same merge rule (<see cref="VerdictRules.ShouldReplace"/>): a verdict
/// for an older message never overwrites one for a newer message, whichever side holds it.
/// </remarks>
public sealed class VerdictSync
{
    private readonly Store _store;
    private readonly Func<string> _repositoryRoot;
    private readonly Action<string> _changed;
    private readonly string _machine;
    private readonly object _lock = new();

    public VerdictSync(Store store, Func<string> repositoryRoot, Action<string> changed, string machine)
    {
        _store = store;
        _repositoryRoot = repositoryRoot;
        _changed = changed;
        _machine = machine;
    }

    /// <summary>Shares one verdict just recorded in the cache; true when the file changed.</summary>
    public bool Export(string source, string key, string forMessage, bool needsReply, string why)
    {
        if (source is not ("mail" or "slack"))
        {
            return false;
        }

        var verdict = new SharedVerdict(
            source,
            key,
            forMessage,
            source == "mail" ? _store.MailMessageTime(forMessage) : null,
            needsReply,
            VerdictRules.Why(why),
            DateTimeOffset.Now,
            _machine);

        bool written;
        lock (_lock)
        {
            written = VerdictFile.Merge(_repositoryRoot(), verdict);
        }

        if (written)
        {
            _changed($"Verdicts ({_machine})");
        }

        return written;
    }

    /// <summary>Takes in the repository's verdicts that are newer than the cache's; returns how many.</summary>
    public int Import()
    {
        var root = _repositoryRoot();
        var imported = 0;
        foreach (var source in new[] { "mail", "slack" })
        {
            var cached = _store.CachedVerdicts(source).ToDictionary(v => v.Key, StringComparer.Ordinal);
            foreach (var shared in VerdictFile.LoadAll(root, source))
            {
                if (VerdictRules.ShouldReplace(cached.GetValueOrDefault(shared.Key), shared))
                {
                    _store.ImportVerdict(shared);
                    imported++;
                }
            }
        }

        return imported;
    }
}
