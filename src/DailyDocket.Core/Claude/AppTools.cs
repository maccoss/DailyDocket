using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace DailyDocket.Core.Claude;

/// <summary>A tool Claude wants to use that is not on the run's allowlist.</summary>
public sealed record PermissionRequest(string ToolName, JsonElement Input, string Description);

/// <summary>The answer to a <see cref="PermissionRequest"/>.</summary>
public sealed record PermissionDecision(bool Allow, string? Message = null);

/// <summary>Answers permission prompts for a run that has someone watching (a drafting chat).</summary>
public interface IPermissionHandler
{
    Task<PermissionDecision> RequestPermissionAsync(PermissionRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// The tools the app gives Claude, served over MCP by an app tool server (LabOps' pattern).
/// </summary>
/// <remarks>
/// For now only <c>approve</c>, which Claude Code calls (as <c>--permission-prompt-tool</c>) for
/// anything outside a run's allowlist. The docket tools (watermarks, verdicts, the outbox) join it
/// with the pull and send jobs.
/// </remarks>
[McpServerToolType]
public sealed class AppTools
{
    public const string ServerName = "docket";

    /// <summary>The name Claude Code knows the permission tool by.</summary>
    public const string PermissionToolName = $"mcp__{ServerName}__approve";

    /// <summary>
    /// Who answers permission prompts. Null for unattended runs (pulls, sends, the nightly jobs),
    /// where everything outside the allowlist is denied: a scheduled run must never widen itself.
    /// </summary>
    public IPermissionHandler? Handler { get; set; }

    /// <summary>
    /// Answers Claude Code's permission prompts. Claude Code calls this itself, with these exact
    /// parameter names, whenever a tool is not pre-approved.
    /// </summary>
    [McpServerTool(Name = "approve", Title = "Permission prompt")]
    [Description("Decides whether a tool call may run. Called by Claude Code, not by the model.")]
    public async Task<string> Approve(
        [Description("The tool that wants to run.")] string tool_name,
        [Description("The tool's input.")] JsonElement input,
        [Description("The tool use id.")] string? tool_use_id = null,
        CancellationToken cancellationToken = default)
    {
        _ = tool_use_id;
        var decision = Handler is null
            ? new PermissionDecision(false, "This run is unattended; only its pre-approved tools may run.")
            : await Handler.RequestPermissionAsync(
                new PermissionRequest(tool_name, input.Clone(), ToolDescriptions.Describe(tool_name, input)),
                cancellationToken).ConfigureAwait(false);

        return decision.Allow
            ? JsonSerializer.Serialize(new { behavior = "allow", updatedInput = input })
            : JsonSerializer.Serialize(new { behavior = "deny", message = decision.Message ?? "Mike declined this step." });
    }
}
