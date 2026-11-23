using DailyDocket.Core.Configuration;

namespace DailyDocket.Tests.Fixtures;

/// <summary>
/// Synthetic connector results in the real shapes (recorded with spike\run-spike.ps1 -Step shapes),
/// with invented people and ids. This repository is public: real captures never go in it.
/// </summary>
internal static class Fixture
{
    public static string Read(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    public static DocketConfig Config() => DocketConfig.Parse("""
        owner:
          name: Owner Person
          emails: [owner@example.org]
          slackUserId: U0000000001
        calendars:
          - key: work
            primary: true
          - key: dept
            id: dept@example.org
            tag: Department
            primary: false
        mail:
          systemSenders: ["@newsletter.example.com"]
        """);

    public static TimeZoneInfo Pacific => TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");
}
