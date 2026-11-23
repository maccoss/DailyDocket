using System.Collections;
using System.Text.Json;
using DailyDocket.Core.Claude;
using DailyDocket.Core.Configuration;
using DailyDocket.Core.Infrastructure;
using DailyDocket.Core.Processes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DailyDocket.Core.Jobs;

/// <summary>How a run ended.</summary>
/// <param name="Detail">For the user when it failed: what happened and what to do.</param>
public sealed record JobOutcome(string Job, bool Ok, string? Detail, int Captured, decimal? CostUsd)
{
    /// <summary>Claude's last message: for a send, what it reports when the call was refused.</summary>
    public string? FinalText { get; init; }

    /// <summary>The run's capture folder, for reading a send's result.</summary>
    public string? CaptureFolder { get; init; }
}

/// <summary>Runs one <see cref="JobPlan"/> as a headless Claude Code session.</summary>
/// <remarks>
/// Each run gets an empty folder under runs\ (outside any repository, so no CLAUDE.md is found by
/// walking up), its own settings file carrying only the capture hook, its own docket tool server
/// with a fresh token, and an environment with nothing inherited from a Claude session the app
/// might have been started from. The owner's own Claude settings are not loaded (setting sources
/// "local"), and anything off the allowlist is refused without asking (permission mode dontAsk).
/// </remarks>
public sealed class JobRunner
{
    /// <summary>A run that takes longer than this is stopped; the next one carries on.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(15);

    private readonly ToolLocator _tools;
    private readonly ProcessRunner _runner;
    private readonly AppPaths _paths;
    private readonly IJobStore _store;
    private readonly Func<DocketConfig> _config;
    private readonly string _hookPath;
    private readonly ILogger _log;

    public JobRunner(ToolLocator tools, ProcessRunner runner, AppPaths paths, IJobStore store, Func<DocketConfig> config, string hookPath, ILogger<JobRunner>? log = null)
    {
        _tools = tools;
        _runner = runner;
        _paths = paths;
        _store = store;
        _config = config;
        _hookPath = hookPath;
        _log = log ?? (ILogger)NullLogger.Instance;
    }

    public async Task<JobOutcome> RunAsync(JobPlan plan, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var runId = $"{now:yyyyMMdd-HHmmss}-{plan.Job}-{Guid.NewGuid().ToString("N")[..6]}";
        var runDir = Path.Combine(_paths.RunDirectory, runId);
        var captures = Path.Combine(runDir, "captures");
        Directory.CreateDirectory(runDir);
        _store.StartRun(runId, plan.Job, now);
        _log.LogInformation("Run {RunId} started", runId);

        string? sessionId = null;
        decimal? cost = null;
        string? failure = null;
        string? finalText = null;
        try
        {
            var context = new RunContext(_store, _config, runId, captures);
            var tools = new AppTools { Run = context };
            await using var server = await AppToolServer.StartAsync(tools, cancellationToken).ConfigureAwait(false);

            var options = new ClaudeSessionOptions
            {
                ClaudePath = _tools.Require(Tool.Claude),
                WorkingDirectory = runDir,
                Model = plan.Model,
                McpConfigPath = server.WriteMcpConfig(runDir),
                PermissionPromptTool = AppTools.PermissionToolName,
                PermissionMode = "dontAsk",
                AllowedTools = plan.AllowedTools,
                DisallowedTools = plan.DisallowedTools,
                SettingsPath = plan.Capture || plan.Claims is not null ? WriteSettings(runDir, captures, plan) : null,
                SettingSources = "local",
                MaxTurns = plan.MaxTurns,
                Environment = Environment(),
            };

            await using var session = ClaudeSession.Start(options, _runner);
            var done = new TaskCompletionSource<ClaudeEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            session.EventReceived += e =>
            {
                switch (e)
                {
                    case SessionStarted s:
                        sessionId = s.SessionId;
                        break;
                    case TurnFinished or SessionEnded:
                        done.TrySetResult(e);
                        break;
                }
            };

            await session.SendAsync(plan.Prompt, cancellationToken).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Timeout);
            ClaudeEvent end;
            try
            {
                end = await done.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                session.Stop();
                throw new TimeoutException($"The {plan.Job} sync took longer than {Timeout.TotalMinutes:0} minutes and was stopped. It will try again at the next sync.");
            }

            switch (end)
            {
                case TurnFinished { IsError: true } t:
                    cost = t.CostUsd;
                    failure = Explain(plan.Job, t.Result);
                    break;
                case TurnFinished t:
                    cost = t.CostUsd;
                    sessionId ??= t.SessionId;
                    finalText = t.Result;
                    break;
                case SessionEnded ended:
                    failure = Explain(plan.Job, ended.ErrorOutput);
                    break;
            }
        }
        catch (ToolMissingException e)
        {
            failure = e.Message;
        }
        catch (TimeoutException e)
        {
            failure = e.Message;
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _log.LogWarning(e, "Run {RunId} failed to start", runId);
            failure = $"The {plan.Job} sync could not start Claude Code: {e.Message}";
        }

        // Whatever happened, keep what was captured: a run stopped halfway still fetched real data.
        var captured = _store.IngestFolder(runId, captures);
        if (failure is null)
        {
            foreach (var (key, value) in plan.Watermarks)
            {
                var parts = key.Split('/');
                _store.SetWatermark(parts[0], parts[1], value);
            }
        }

        _store.FinishRun(runId, sessionId, failure is null ? "ok" : "failed", failure, cost is null ? null : (double)cost);
        _log.LogInformation("Run {RunId} finished: {Outcome}, {Captured} captures", runId, failure is null ? "ok" : "failed", captured);
        return new JobOutcome(plan.Job, failure is null, failure, captured, cost) { FinalText = finalText, CaptureFolder = captures };
    }

    /// <summary>Readers' plans keep the old two-argument form.</summary>
    internal string WriteSettings(string runDir, string captures) => WriteSettings(runDir, captures, null);

    /// <summary>
    /// The run's only hooks: capture every connector result (with a receipt for readers) and, for a
    /// send, the guard on every write tool, which allows exactly the claimed call, once.
    /// </summary>
    internal string WriteSettings(string runDir, string captures, JobPlan? plan)
    {
        static string Slash(string path) => path.Replace('\\', '/');
        var hooks = new System.Text.Json.Nodes.JsonObject
        {
            ["PostToolUse"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
            {
                ["matcher"] = ConnectorTools.CaptureMatcher,
                ["hooks"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
                {
                    ["type"] = "command",
                    ["command"] = $"\"{Slash(_hookPath)}\" capture --dir \"{Slash(captures)}\"" + (plan?.Receipt == false ? "" : " --receipt"),
                    ["timeout"] = 30,
                }),
            }),
        };

        if (plan?.Claims is { } claims)
        {
            var claimsPath = Path.Combine(runDir, "claims.json");
            File.WriteAllText(claimsPath, claims);
            hooks["PreToolUse"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
            {
                ["matcher"] = ConnectorTools.WriteMatcher,
                ["hooks"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
                {
                    ["type"] = "command",
                    ["command"] = $"\"{Slash(_hookPath)}\" guard --claims \"{Slash(claimsPath)}\"",
                    ["timeout"] = 30,
                }),
            });
        }

        var path = Path.Combine(runDir, "settings.json");
        File.WriteAllText(path, new System.Text.Json.Nodes.JsonObject { ["hooks"] = hooks }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }

    /// <summary>
    /// Removes every CLAUDE* and ANTHROPIC* variable this process inherited (a Claude session that
    /// started the app passes host sign-in and session state), so the run uses the owner's own
    /// Claude Code sign-in and nothing else.
    /// </summary>
    internal static IReadOnlyDictionary<string, string?> Environment()
    {
        var env = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry e in System.Environment.GetEnvironmentVariables())
        {
            var name = (string)e.Key;
            if (name.StartsWith("CLAUDE", StringComparison.OrdinalIgnoreCase) || name.StartsWith("ANTHROPIC", StringComparison.OrdinalIgnoreCase))
            {
                env[name] = null;
            }
        }

        env["MCP_TOOL_TIMEOUT"] = "120000";
        return env;
    }

    internal static string Explain(string job, string? message)
    {
        var text = (message ?? "").Trim();
        if (text.Contains("OAuth", StringComparison.OrdinalIgnoreCase) || text.Contains("log in", StringComparison.OrdinalIgnoreCase) || text.Contains("login", StringComparison.OrdinalIgnoreCase))
        {
            return "Claude Code is not signed in on this computer. Open a terminal and run: claude auth login --claudeai";
        }

        if (text.Contains("max turns", StringComparison.OrdinalIgnoreCase) || text.Contains("max_turns", StringComparison.OrdinalIgnoreCase))
        {
            return $"The {job} sync stopped at its step limit before finishing. What it fetched is saved; the next sync carries on.";
        }

        var firstLine = text.Split('\n').FirstOrDefault()?.Trim() ?? "";
        return firstLine.Length == 0
            ? $"The {job} sync ended without saying why. The next sync will try again."
            : $"The {job} sync failed: {firstLine}";
    }

    /// <summary>What the docket tools act on during one run.</summary>
    private sealed class RunContext : IRunContext
    {
        private readonly IJobStore _store;
        private readonly Func<DocketConfig> _config;
        private readonly string _runId;
        private readonly string _captures;

        public RunContext(IJobStore store, Func<DocketConfig> config, string runId, string captures)
        {
            _store = store;
            _config = config;
            _runId = runId;
            _captures = captures;
        }

        public IReadOnlyDictionary<string, string> TriageInstructions => new Dictionary<string, string>
        {
            ["mail"] = Briefs.Mail(_config()),
            ["slack"] = Briefs.Slack(_config()),
        };

        public IReadOnlyList<string> ThreadsToRead()
        {
            // The searches this run just made are on disk; take them in first.
            _store.IngestFolder(_runId, _captures);
            var seenSince = DateTimeOffset.UtcNow.AddDays(-_config().Mail.WindowDays);
            return _store.ThreadsToRead(seenSince, JobPlans.ThreadsPerRun);
        }

        public IReadOnlyList<TriageItem> TriageBatch() => _store.TriageBatch(DateTimeOffset.UtcNow, 15);

        public void RecordVerdict(string source, string key, string forMessage, bool needsReply, string why) =>
            _store.RecordVerdict(source, key, forMessage, needsReply, why);
    }
}

/// <summary>Where the hook exe is: beside the app.</summary>
public static class HookLocator
{
    public static string Find(string? appDirectory = null)
    {
        var path = Path.Combine(appDirectory ?? AppContext.BaseDirectory, "DailyDocket.Hook.exe");
        return File.Exists(path)
            ? path
            : throw new FileNotFoundException($"DailyDocket.Hook.exe is missing from {Path.GetDirectoryName(path)}. Reinstall DailyDocket.", path);
    }
}
