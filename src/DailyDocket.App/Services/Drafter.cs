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
    private readonly Core.Todos.DataRepository _repository;

    public Drafter(JobRunner runner, Store store, ConfigSource config, AppSettings settings, Core.Todos.DataRepository repository)
    {
        _runner = runner;
        _store = store;
        _config = config;
        _settings = settings;
        _repository = repository;
    }

    /// <summary>Claude's suggested task for a message: the artifact's capture brief.</summary>
    public async Task<TaskSuggestion> SuggestTaskAsync(string conversation, IReadOnlyList<string> knownTags, CancellationToken cancellationToken)
    {
        if (!NetworkInterface.GetIsNetworkAvailable())
        {
            throw new InvalidOperationException("Claude's help needs a connection. Fill in the task yourself, or try again when you are online.");
        }

        var config = _config.Get();
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, _config.Zone).DateTime);
        var outcome = await _runner.RunAsync(CapturePlans.Capture(config, conversation, knownTags, today, _settings.PullModel), cancellationToken).ConfigureAwait(false);
        return (outcome.Ok ? CapturePlans.Parse(outcome.FinalText, today, knownTags) : null)
            ?? throw new InvalidOperationException(outcome.Detail ?? "Claude did not suggest a task. Fill it in yourself, or try again.");
    }

    /// <summary>The reply guides: config/replies/*.md in the data repository.</summary>
    public string GuideFolder => Path.Combine(_repository.Root, "config", "replies");

    /// <summary>Returns the draft, or throws <see cref="InvalidOperationException"/> with a message for the user.</summary>
    public async Task<(string Draft, string? Guide)> DraftAsync(bool isEmail, string conversation, string? guidance, CancellationToken cancellationToken)
    {
        var guide = ReplyGuide.Choose(ReplyGuide.Load(GuideFolder), conversation);
        if (!NetworkInterface.GetIsNetworkAvailable())
        {
            throw new InvalidOperationException("Drafting with Claude needs a connection. Write the reply yourself, or try again when you are online.");
        }

        var config = _config.Get();
        var examples = isEmail ? _store.RecentFromMe(config, 4) : [];
        var outcome = await _runner.RunAsync(DraftPlans.Draft(config, isEmail, conversation, examples, guidance, _settings.JudgmentModel, guide), cancellationToken).ConfigureAwait(false);
        if (!outcome.Ok || string.IsNullOrWhiteSpace(outcome.FinalText))
        {
            throw new InvalidOperationException(outcome.Detail ?? "Claude did not return a draft. Try again.");
        }

        return (DraftPlans.Defuse(DraftPlans.HouseStyle(outcome.FinalText), conversation + "\n" + guide?.Text), guide?.Title);
    }
}
