using System.Net.NetworkInformation;
using DailyDocket.Core.Configuration;
using DailyDocket.Core.Drafting;
using DailyDocket.Core.Infrastructure;
using DailyDocket.Core.Jobs;
using DailyDocket.Data;

namespace DailyDocket.App.Services;

/// <summary>Runs Draft with Claude: the conversation from the cache in, draft text out.</summary>
public sealed class Drafter
{
    private readonly JobRunner _runner;
    private readonly Store _store;
    private readonly ConfigSource _config;
    private readonly AppSettings _settings;

    public Drafter(JobRunner runner, Store store, ConfigSource config, AppSettings settings)
    {
        _runner = runner;
        _store = store;
        _config = config;
        _settings = settings;
    }

    /// <summary>Returns the draft, or throws <see cref="InvalidOperationException"/> with a message for the user.</summary>
    public async Task<string> DraftAsync(bool isEmail, string conversation, string? guidance, CancellationToken cancellationToken)
    {
        if (!NetworkInterface.GetIsNetworkAvailable())
        {
            throw new InvalidOperationException("Drafting with Claude needs a connection. Write the reply yourself, or try again when you are online.");
        }

        var config = _config.Get();
        var examples = isEmail ? _store.RecentFromMe(config, 4) : [];
        var outcome = await _runner.RunAsync(DraftPlans.Draft(config, isEmail, conversation, examples, guidance, _settings.JudgmentModel), cancellationToken).ConfigureAwait(false);
        if (!outcome.Ok || string.IsNullOrWhiteSpace(outcome.FinalText))
        {
            throw new InvalidOperationException(outcome.Detail ?? "Claude did not return a draft. Try again.");
        }

        return DraftPlans.HouseStyle(outcome.FinalText);
    }
}
