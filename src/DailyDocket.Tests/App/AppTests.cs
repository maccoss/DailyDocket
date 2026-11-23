using DailyDocket.App;
using DailyDocket.App.ViewModels;
using DailyDocket.Core.Infrastructure;
using DailyDocket.Core.Jobs;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace DailyDocket.Tests.App;

public sealed class AppTests
{
    [Fact]
    public void The_service_container_builds_everything_the_window_needs()
    {
        var root = Directory.CreateTempSubdirectory("dd-app-").FullName;
        try
        {
            var paths = new AppPaths(root);
            paths.EnsureCreated();

            // A fixed key: a test must not write to the developer's Credential Manager.
            using (var services = Program.BuildServiceProvider(paths, databaseKey: "test"))
            {
                services.GetRequiredService<MainViewModel>().ShouldNotBeNull();
                services.GetRequiredService<SyncScheduler>().ShouldNotBeNull();
            }

            SqliteConnection.ClearAllPools();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(30, "30m")]
    [InlineData(150, "3h")]
    [InlineData(60 * 24 * 3, "3d")]
    public void Ages_read_like_the_dashboards(int minutes, string expected) =>
        MainViewModel.RelativeAge(TimeSpan.FromMinutes(minutes)).ShouldBe(expected);

    [Theory]
    [InlineData(10, "10d")]
    [InlineData(120, "4mo")]
    [InlineData(400, "1.1y")]
    public void Mail_ages_grow_to_months_and_years(int days, string expected) =>
        MainViewModel.MailAge(TimeSpan.FromDays(days)).ShouldBe(expected);

    [Fact]
    public void The_reader_names_recipients_by_display_name()
    {
        MainViewModel.Recipients(["\"Pat Example\" <pat@example.org>", "sam@example.org"], ["Lee Lab <lee@example.org>"])
            .ShouldBe("to Pat Example, sam@example.org; cc Lee Lab");
        MainViewModel.Recipients([], []).ShouldBe("");
    }

    [Fact]
    public void Times_are_short_and_in_the_calendars_zone()
    {
        var start = new DateTimeOffset(2026, 11, 23, 18, 0, 0, TimeSpan.Zero);
        var e = new DailyDocket.Core.Calendar.CalendarEvent("e", "work", start, start.AddMinutes(90), false, "t", "", 0, null, null, "confirmed", null, null);

        MainViewModel.TimeRange(e, Fixtures.Fixture.Pacific).ShouldBe("10a–11:30a");
    }
}
