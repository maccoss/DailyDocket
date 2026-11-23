using DailyDocket.Core.Claude;

namespace DailyDocket.Tests.Claude;

public sealed class ClaudeSessionOptionsTests
{
    [Fact]
    public void A_settings_file_is_passed_for_the_runs_hooks()
    {
        var args = new ClaudeSessionOptions { ClaudePath = "claude.exe", WorkingDirectory = ".", SettingsPath = @"C:\runs\r1\settings.json" }
            .BuildArguments();

        var i = args.ToList().IndexOf("--settings");
        i.ShouldBeGreaterThanOrEqualTo(0);
        args[i + 1].ShouldBe(@"C:\runs\r1\settings.json");
    }

    [Fact]
    public void Without_one_no_settings_flag_is_added()
    {
        new ClaudeSessionOptions { ClaudePath = "claude.exe", WorkingDirectory = "." }.BuildArguments().ShouldNotContain("--settings");
    }
}
