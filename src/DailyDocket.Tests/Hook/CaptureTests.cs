using System.Text.Json.Nodes;
using DailyDocket.Hook;

namespace DailyDocket.Tests.Hook;

public sealed class CaptureTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dd-capture-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private const string Input = """
        {"session_id":"s1","hook_event_name":"PostToolUse","tool_name":"mcp__claude_ai_Gmail__search_threads",
         "tool_use_id":"toolu_01ABC","tool_input":{"query":"after:2026/11/21"},
         "tool_response":[{"type":"text","text":"{\"threads\":[{\"id\":\"19a2c0f3e8b1\",\"snippet\":\"PRM targets\"},{\"id\":\"19a2c0f3e8b2\"}],\"nextPageToken\":\"p2\"}"}]}
        """;

    [Fact]
    public void The_input_is_saved_verbatim()
    {
        Capture.Run(Input, _dir, receipt: false, new DateTimeOffset(2026, 11, 22, 16, 20, 0, TimeSpan.Zero)).ShouldBeNull();

        var file = Directory.GetFiles(_dir).ShouldHaveSingleItem();
        Path.GetFileName(file).ShouldBe("20261122T162000000-toolu_01ABC.json");
        File.ReadAllText(file).ShouldBe(Input);
    }

    [Fact]
    public void A_receipt_keeps_ids_and_the_page_token_and_drops_the_text()
    {
        var output = Capture.Run(Input, _dir, receipt: true, DateTimeOffset.UtcNow).ShouldNotBeNull();

        var replaced = JsonNode.Parse(output)!["hookSpecificOutput"]!["updatedToolOutput"]!;
        var text = replaced.AsArray().ShouldHaveSingleItem()!["text"]!.GetValue<string>();
        text.ShouldContain("nextPageToken=p2");
        text.ShouldContain("19a2c0f3e8b1");
        text.ShouldContain("19a2c0f3e8b2");
        text.ShouldNotContain("PRM targets");
    }

    [Fact]
    public void A_slack_receipt_carries_the_cursor_and_each_results_channel_ts_and_thread()
    {
        var report = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "slack-search.txt"));
        var envelope = new JsonObject
        {
            ["results"] = report,
            ["pagination_info"] = "For the next page of results use cursor `abc123==`",
        };
        var response = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = envelope.ToJsonString() });

        var text = Capture.Receipt("mcp__claude_ai_Slack__slack_search_public_and_private", response, 1000);

        text.ShouldContain("cursor=abc123==");
        text.ShouldContain("D0000000001:1795878902.000100");
        text.ShouldContain("C0000000003:1795881600.000200:1795880000.000050");
        text.ShouldNotContain("QC report");
    }

    [Fact]
    public void A_string_result_gets_a_string_receipt()
    {
        Capture.Shaped(JsonValue.Create("plain"), "r").GetValue<string>().ShouldBe("r");
    }
}
