using DailyDocket.Core.Drafting;

namespace DailyDocket.Tests.Drafting;

public sealed class CapturePlansTests
{
    private static readonly DateOnly Today = new(2026, 11, 22);

    [Fact]
    public void A_suggestion_is_read_with_its_date_and_an_existing_tag()
    {
        var s = CapturePlans.Parse("""{"title":"Sign Sarah's NIH mentorship letter","notes":"Needed before routing","dueInDays":3,"tag":"service"}""", Today, ["Service", "Collab"]).ShouldNotBeNull();

        s.Title.ShouldBe("Sign Sarah's NIH mentorship letter");
        s.Due.ShouldBe(new DateOnly(2026, 11, 25));
        s.Tag.ShouldBe("Service"); // written as the list writes it
    }

    [Fact]
    public void A_code_fence_and_an_em_dash_are_handled()
    {
        var s = CapturePlans.Parse("```json\n{\"title\":\"Reply to Pat — QC\",\"notes\":\"\",\"dueInDays\":null,\"tag\":null}\n```", Today, []).ShouldNotBeNull();

        s.Title.ShouldBe("Reply to Pat, QC");
        s.Due.ShouldBeNull();
    }

    [Fact]
    public void An_invented_tag_is_dropped_when_he_already_has_tags()
    {
        CapturePlans.Parse("""{"title":"Do it","notes":"","dueInDays":null,"tag":"NewThing"}""", Today, ["Service"])!.Tag.ShouldBeNull();
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"notes":"no title"}""")]
    [InlineData("""{"title":""}""")]
    public void An_unusable_answer_is_null(string answer) => CapturePlans.Parse(answer, Today, []).ShouldBeNull();

    [Fact]
    public void The_capture_run_has_no_tools()
    {
        var plan = CapturePlans.Capture(Fixtures.Fixture.Config(), "Can you sign by Friday?", ["Service"], Today, "haiku");

        plan.AllowedTools.ShouldBeEmpty();
        plan.Prompt.ShouldContain("Today is Sunday, November 22, 2026");
        plan.Prompt.ShouldContain("Service");
    }
}
