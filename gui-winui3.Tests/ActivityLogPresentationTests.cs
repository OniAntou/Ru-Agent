using System.Reflection;
using System.Text.Json;
using RuApp.Models;
using RuApp.Views;
using Xunit;

namespace RuApp.Tests;

public sealed class ActivityLogPresentationTests
{
    [Fact]
    public void Observation_reaction_is_presented_as_a_bounded_activity_row()
    {
        using var document = JsonDocument.Parse("""
            {"preview":"Anh thấy một chuyển động mới","scene":"phòng khách","event":"person_arrived","observation_id":"obs-1"}
            """);
        var activity = new ActivityLogEventDto
        {
            Type = "observation_reaction",
            Ts = 100,
            Iso = "2026-08-20T00:00:00Z",
            SessionId = "s1",
            Data = document.RootElement.Clone(),
        };

        var formatter = typeof(LogView).GetMethod("FormatObservationReaction", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(formatter);
        var content = (string)formatter!.Invoke(null, new object[] { activity.Data })!;

        Assert.Equal("Quan sát: Anh thấy một chuyển động mới", content);

        var badgeFormatter = typeof(LogView).GetMethod("GetActivityBadge", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(badgeFormatter);
        Assert.Equal("QUAN SÁT", badgeFormatter!.Invoke(null, new object[] { activity.Type }));
    }
}
