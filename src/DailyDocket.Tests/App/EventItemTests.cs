using DailyDocket.App.ViewModels;

namespace DailyDocket.Tests.App;

public sealed class EventItemTests
{
    private static EventItem Event(string location, string? join) =>
        new("10a-11a", "Check-in", "", location, false, false, false, null, "e1", JoinLink: join);

    [Fact]
    public void The_location_stands_in_for_the_link_only_when_it_is_the_meeting_link()
    {
        var zoom = "https://example.zoom.us/j/89161478146?pwd=secret.1";

        Event("https://example.zoom.us/j/89161478146?pwd=secret.1", zoom).LocationJoins.ShouldBeTrue();
        // Another address in the location (a map) is not drawn as the link that joins.
        Event("Room 101, https://maps.example.org/b/42", zoom).LocationJoins.ShouldBeFalse();
        // Either way the link text carries no password.
        Event("https://example.zoom.us/j/89161478146?pwd=secret.1", zoom).JoinText.ShouldBe("example.zoom.us/j/89161478146");
        Event("", null).JoinText.ShouldBeNull();
    }
}
