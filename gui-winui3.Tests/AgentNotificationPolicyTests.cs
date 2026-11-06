using System.IO;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

/// <summary>Notifications fire on live transitions only: replays, adoptions and the user's own Stop stay silent.</summary>
public sealed class AgentNotificationPolicyTests
{
    [Fact]
    public void A_live_run_that_completes_notifies_once()
    {
        var policy = new AgentNotificationPolicy();
        Assert.Null(policy.ObserveRun("s1", "Fix tests", "run_1", "running"));

        var finished = policy.ObserveRun("s1", "Fix tests", "run_1", "completed");

        Assert.Equal(AgentNotificationKind.Finished, finished!.Kind);
        Assert.Equal("Fix tests", finished.Body);
        Assert.Null(policy.ObserveRun("s1", "Fix tests", "run_1", "completed"));
    }

    [Fact]
    public void Adopting_an_already_finished_run_never_notifies()
    {
        var policy = new AgentNotificationPolicy();
        Assert.Null(policy.ObserveRun("s1", "Old", "run_old", "completed"));
        policy.ObserveRun("s2", "New", "run_new", "running");
        // Switching back replays the finished run under its own id.
        Assert.Null(policy.ObserveRun("s1", "Old", "run_old", "completed"));
    }

    [Theory]
    [InlineData("failed", AgentNotificationKind.Stopped)]
    [InlineData("interrupted", AgentNotificationKind.Stopped)]
    public void A_run_that_stops_on_an_error_says_so(string status, AgentNotificationKind kind)
    {
        var policy = new AgentNotificationPolicy();
        policy.ObserveRun("s1", "T", "run_1", "running");
        Assert.Equal(kind, policy.ObserveRun("s1", "T", "run_1", status)!.Kind);
    }

    [Fact]
    public void A_run_the_user_cancelled_is_not_announced()
    {
        var policy = new AgentNotificationPolicy();
        policy.ObserveRun("s1", "T", "run_1", "running");
        Assert.Null(policy.ObserveRun("s1", "T", "run_1", "cancelled"));
    }

    [Fact]
    public void Each_approval_and_question_notifies_once_even_when_replayed()
    {
        var policy = new AgentNotificationPolicy();
        Assert.Equal(AgentNotificationKind.NeedsApproval, policy.ObserveWait("s1", "T", "ap_1", null)!.Kind);
        Assert.Null(policy.ObserveWait("s1", "T", "ap_1", null));
        Assert.Equal(AgentNotificationKind.NeedsAnswer, policy.ObserveWait("s1", "T", null, "in_1")!.Kind);
        Assert.Null(policy.ObserveWait("s1", "T", null, "in_1"));
        Assert.Null(policy.ObserveWait("s1", "T", null, null));
    }

    [Fact]
    public void Waits_already_on_screen_when_a_session_opened_stay_silent()
    {
        var policy = new AgentNotificationPolicy();
        policy.MarkSeen("ap_open", "in_open");
        Assert.Null(policy.ObserveWait("s1", "T", "ap_open", null));
        Assert.Null(policy.ObserveWait("s1", "T", null, "in_open"));
    }

    [Fact]
    public void A_background_session_notifies_when_it_stops_running_but_not_on_first_sight()
    {
        var policy = new AgentNotificationPolicy();
        Assert.Null(policy.ObserveBackgroundSession("s2", "Build", isRunning: false));
        Assert.Null(policy.ObserveBackgroundSession("s3", "Deploy", isRunning: true));

        Assert.Equal("Build", Assert.IsType<AgentNotification>(
            ObserveCycle(policy, "s2", "Build")).Body);
        Assert.NotNull(policy.ObserveBackgroundSession("s3", "Deploy", isRunning: false));
        Assert.Null(policy.ObserveBackgroundSession("s3", "Deploy", isRunning: false));
    }

    [Fact]
    public void Long_titles_are_shortened_and_missing_ones_read_as_session()
    {
        var policy = new AgentNotificationPolicy();
        Assert.Equal("Session", policy.ObserveWait("s1", null, "ap_x", null)!.Body);
        var body = policy.ObserveWait("s1", new string('a', 200), "ap_y", null)!.Body;
        Assert.Equal(60, body.Length);
        Assert.EndsWith("…", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Preferences_default_to_flash_and_sound_without_windows_notifications()
    {
        var path = Path.Combine(Path.GetTempPath(), "ru-notify-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new NotificationPreferenceStore(path);
            var defaults = store.Load();
            Assert.True(defaults.OnFinished && defaults.OnNeedsAttention && defaults.FlashTaskbar && defaults.PlaySound);
            Assert.False(defaults.ShowToast);

            Assert.True(store.TrySave(defaults with { PlaySound = false, ShowToast = true }));
            var loaded = store.Load();
            Assert.False(loaded.PlaySound);
            Assert.True(loaded.ShowToast);

            File.WriteAllText(path, "{not json");
            Assert.Equal(NotificationPreferences.Default, store.Load());
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static AgentNotification? ObserveCycle(AgentNotificationPolicy policy, string id, string title)
    {
        policy.ObserveBackgroundSession(id, title, isRunning: true);
        return policy.ObserveBackgroundSession(id, title, isRunning: false);
    }
}
