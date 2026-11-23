using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using DailyDocket.App;
using DailyDocket.App.ViewModels;
using DailyDocket.Core.Capture;
using DailyDocket.Core.Infrastructure;
using DailyDocket.Core.Prep;
using DailyDocket.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace DailyDocket.Tests.App;

/// <summary>
/// Opt-in (DAILYDOCKET_UI_SMOKE=1): opens the real window over a temporary data folder holding one
/// meeting and its brief, and checks that the Schedule shows the brief and Prep now. Templates are
/// only built when shown, so a XAML mistake in them would otherwise surface only in the running app.
/// Nothing syncs: the folder has no docket.yaml and no data repository to clone.
/// </summary>
public sealed class ScheduleSmokeTests
{
    [Fact]
    public void The_schedule_shows_a_brief_under_its_event()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("DAILYDOCKET_UI_SMOKE") != "1", "Set DAILYDOCKET_UI_SMOKE=1.");

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                Run();
            }
            catch (Exception e)
            {
                failure = e;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException(failure.ToString());
        }
    }

    private static void Run()
    {
        var root = Directory.CreateTempSubdirectory("dd-smoke-").FullName;
        var paths = new AppPaths(root);
        paths.EnsureCreated();
        var repo = Path.Combine(root, "repo");
        File.WriteAllText(paths.SettingsFile, JsonSerializer.Serialize(new { DataRepositoryUrl = "", TodoRepositoryPath = repo, Notifications = false, OpenSetupAtStartup = false }));

        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");
        var tomorrow = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).DateTime).AddDays(1);
        var start = new DateTimeOffset(tomorrow.ToDateTime(new TimeOnly(10, 0)), zone.GetUtcOffset(tomorrow.ToDateTime(new TimeOnly(10, 0))));
        var iso = start.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);
        var end = start.AddHours(1).ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);

        using (var services = Program.BuildServiceProvider(paths, databaseKey: "smoke"))
        {
            var events = $$"""{"timeZone":"America/Los_Angeles","events":[{"id":"smoke1","status":"confirmed","summary":"Renewal planning","location":"https://example.zoom.us/j/123","updated":"2026-11-20T18:00:00.000Z","start":{"dateTime":"{{iso}}"},"end":{"dateTime":"{{end}}"},"attendees":[{"email":"owner@example.org","self":true,"responseStatus":"needsAction"},{"email":"pat@example.org","responseStatus":"accepted"}]}]}""";
            services.GetRequiredService<CaptureIngestor>().Ingest("smoke", new CapturedCall(
                "mcp__claude_ai_Google_Calendar__list_events", JsonSerializer.SerializeToElement(new { startTime = iso }), events, "toolu_smoke", "s", DateTimeOffset.UtcNow));
            BriefFile.Save(repo, tomorrow, new MeetingBrief(
                "smoke1", "Renewal planning", start, new DateTimeOffset(2026, 11, 20, 18, 0, 0, TimeSpan.Zero),
                [new BriefAttendee("Pat Lee", "Acme Labs", "program officer", true)],
                ["Decide the renewal's aims"], ["Pat asked for a draft budget"], ["The draft budget"], ["Whether the budget includes travel"], [],
                DateTimeOffset.Now, "smoke", "sonnet"));

            using var instance = SingleInstance.Acquire($"DailyDocketSmoke-{Guid.NewGuid():N}", Path.Combine(root, "smoke.lock"));
            var app = new DailyDocket.App.App(services, instance);
            app.InitializeComponent();
            var window = services.GetRequiredService<MainWindow>();
            window.Show();
            Pump();

            var expander = Descendants<Expander>(window).ShouldHaveSingleItem();
            expander.Visibility.ShouldBe(Visibility.Visible);
            expander.IsExpanded = true;
            Pump();

            var text = string.Join("\n", Descendants<TextBox>(expander).Select(t => t.Text).Concat(Descendants<TextBlock>(expander).Select(t => t.Text)));
            text.ShouldContain("Pat Lee (Acme Labs): program officer, organizer");
            text.ShouldContain("Decide the renewal's aims");
            text.ShouldContain("Whether the budget includes travel");
            text.ShouldContain("Brief by Claude");
            Descendants<Button>(window).ShouldContain(b => Equals(b.Content, "Prep now") && b.IsEnabled);
            var join = Descendants<Button>(window).Single(b => Equals(b.Content, "Join"));
            join.IsVisible.ShouldBeTrue();
            join.CommandParameter.ShouldBe("https://example.zoom.us/j/123");
            // The location is the link: shown as a link that joins, with the same command.
            Descendants<TextBlock>(window).ShouldContain(t => t.IsVisible && t.Inlines.OfType<System.Windows.Documents.Hyperlink>().Any(h => h.CommandParameter as string == "https://example.zoom.us/j/123"));
            join.Command.ShouldNotBeNull();

            // A reload (every sync step and outbox change makes one) leaves an open Brief open.
            var vm = (MainViewModel)window.DataContext;
            ShowsUnanswered(window).ShouldBeTrue();
            vm.Reload();
            Pump();
            Descendants<Expander>(window).ShouldHaveSingleItem().IsExpanded.ShouldBeTrue();

            // Maybe clears "not answered" at once, before any calendar pull reads it back; the day
            // is redrawn for the new response and the Brief stays open.
            vm.TentativeEventCommand.Execute(vm.Days.SelectMany(d => d.Events).Single());
            Pump();
            ShowsUnanswered(window).ShouldBeFalse();
            Descendants<Expander>(window).ShouldHaveSingleItem().IsExpanded.ShouldBeTrue();

            // Minimizing hides the window to the notification area.
            window.WindowState = WindowState.Minimized;
            Pump();
            window.IsVisible.ShouldBeFalse();
            window.Reveal();
            Pump();
            window.IsVisible.ShouldBeTrue();

            window.Close();
            app.Shutdown();
        }

        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // A log file still closing; the temp folder is cleaned up eventually.
        }
    }

    private static bool ShowsUnanswered(Window window) =>
        Descendants<TextBlock>(window).Any(t => t.Text == "not answered" && t.IsVisible);

    private static void Pump()
    {
        for (var i = 0; i < 5; i++)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => frame.Continue = false);
            Dispatcher.PushFrame(frame);
        }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var d in Descendants<T>(child))
            {
                yield return d;
            }
        }
    }
}
