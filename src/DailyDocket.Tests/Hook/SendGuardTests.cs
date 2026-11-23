using System.Text.Json.Nodes;
using DailyDocket.Hook;

namespace DailyDocket.Tests.Hook;

public sealed class SendGuardTests : IDisposable
{
    private const string Tool = "mcp__claude_ai_Gmail__reply";
    private readonly string _dir = Directory.CreateTempSubdirectory("dd-guard-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Claims(params (string Id, string Tool, string Input)[] claims)
    {
        var array = new JsonArray(claims.Select(c => (JsonNode)new JsonObject
        {
            ["id"] = c.Id,
            ["tool_name"] = c.Tool,
            ["tool_input"] = JsonNode.Parse(c.Input),
        }).ToArray());
        var path = Path.Combine(_dir, "claims.json");
        File.WriteAllText(path, array.ToJsonString());
        return path;
    }

    private static string Call(string tool, string input) =>
        new JsonObject { ["hook_event_name"] = "PreToolUse", ["tool_name"] = tool, ["tool_input"] = JsonNode.Parse(input) }.ToJsonString();

    [Fact]
    public void Parallel_identical_calls_let_only_one_through()
    {
        var claims = Claims(("o1", Tool, """{"threadId":"t1","body":"Hi"}"""));
        var decisions = Enumerable.Range(0, 16).AsParallel()
            .Select(_ => SendGuard.Decide(Call(Tool, """{"threadId":"t1","body":"Hi"}"""), claims))
            .ToList();

        decisions.Count(d => d.Contains("\"allow\"")).ShouldBe(1);
    }

    private static void ShouldDeny(string? decision, string because)
    {
        decision.ShouldNotBeNull();
        var node = JsonNode.Parse(decision)!["hookSpecificOutput"]!;
        node["permissionDecision"]!.GetValue<string>().ShouldBe("deny");
        node["permissionDecisionReason"]!.GetValue<string>().ShouldContain(because);
    }

    [Fact]
    public void An_exact_claimed_call_is_allowed_explicitly()
    {
        var claims = Claims(("o1", Tool, """{"threadId":"t1","body":"Thanks, will do."}"""));
        SendGuard.Decide(Call(Tool, """{"threadId":"t1","body":"Thanks, will do."}"""), claims).ShouldContain("\"allow\"");
    }

    [Fact]
    public void Key_order_does_not_matter()
    {
        var claims = Claims(("o1", Tool, """{"threadId":"t1","body":"Hi"}"""));
        SendGuard.Decide(Call(Tool, """{"body":"Hi","threadId":"t1"}"""), claims).ShouldContain("\"allow\"");
    }

    [Theory]
    [InlineData("""{"threadId":"t1","body":"Thanks, will do!"}""")]
    [InlineData("""{"threadId":"t1","body":"Thanks, will do. "}""")]
    [InlineData("""{"threadId":"t2","body":"Thanks, will do."}""")]
    [InlineData("""{"threadId":"t1","body":"Thanks, will do.","bcc":"someone@example.com"}""")]
    public void Any_change_to_the_claimed_input_is_denied(string input)
    {
        var claims = Claims(("o1", Tool, """{"threadId":"t1","body":"Thanks, will do."}"""));
        ShouldDeny(SendGuard.Decide(Call(Tool, input), claims), "does not match");
    }

    [Fact]
    public void A_different_tool_with_the_same_input_is_denied()
    {
        var claims = Claims(("o1", Tool, """{"threadId":"t1","body":"Hi"}"""));
        ShouldDeny(SendGuard.Decide(Call("mcp__claude_ai_Gmail__send_message", """{"threadId":"t1","body":"Hi"}"""), claims), "does not match");
    }

    [Fact]
    public void A_claim_is_used_once()
    {
        var claims = Claims(("o1", Tool, """{"threadId":"t1","body":"Hi"}"""));
        SendGuard.Decide(Call(Tool, """{"threadId":"t1","body":"Hi"}"""), claims).ShouldContain("\"allow\"");
        ShouldDeny(SendGuard.Decide(Call(Tool, """{"threadId":"t1","body":"Hi"}"""), claims), "already been sent");
    }

    [Fact]
    public void Without_a_claims_file_everything_is_denied()
    {
        ShouldDeny(SendGuard.Decide(Call(Tool, """{"threadId":"t1","body":"Hi"}"""), Path.Combine(_dir, "none.json")), "no outbox item");
    }
}
