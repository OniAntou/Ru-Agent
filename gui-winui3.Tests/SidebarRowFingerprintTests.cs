using System.Collections.Generic;
using RuApp.Models;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class SidebarRowFingerprintTests
{
    [Fact]
    public void Identical_lists_share_a_fingerprint()
    {
        var first = new List<SessionInfo> { Row("a", "Hello", "2026-09-17T18:00:00Z"), Row("b", "World", "2026-09-17T17:00:00Z") };
        var second = new List<SessionInfo> { Row("a", "Hello", "2026-09-17T18:00:00Z"), Row("b", "World", "2026-09-17T17:00:00Z") };

        Assert.Equal(SidebarRowFingerprint.ForSessions(first), SidebarRowFingerprint.ForSessions(second));
    }

    [Fact]
    public void Title_or_time_change_breaks_the_fingerprint()
    {
        var before = new List<SessionInfo> { Row("a", "Hello", "2026-09-17T18:00:00Z") };

        Assert.NotEqual(
            SidebarRowFingerprint.ForSessions(before),
            SidebarRowFingerprint.ForSessions(new List<SessionInfo> { Row("a", "Hello!", "2026-09-17T18:00:00Z") }));
        Assert.NotEqual(
            SidebarRowFingerprint.ForSessions(before),
            SidebarRowFingerprint.ForSessions(new List<SessionInfo> { Row("a", "Hello", "2026-09-17T18:01:00Z") }));
    }

    [Fact]
    public void Reorder_or_add_or_remove_breaks_the_fingerprint()
    {
        var first = new List<SessionInfo> { Row("a", "Hello", "t"), Row("b", "World", "t") };
        var reordered = new List<SessionInfo> { Row("b", "World", "t"), Row("a", "Hello", "t") };
        var added = new List<SessionInfo> { Row("a", "Hello", "t"), Row("b", "World", "t"), Row("c", "!", "t") };
        var removed = new List<SessionInfo> { Row("a", "Hello", "t") };

        Assert.NotEqual(SidebarRowFingerprint.ForSessions(first), SidebarRowFingerprint.ForSessions(reordered));
        Assert.NotEqual(SidebarRowFingerprint.ForSessions(first), SidebarRowFingerprint.ForSessions(added));
        Assert.NotEqual(SidebarRowFingerprint.ForSessions(first), SidebarRowFingerprint.ForSessions(removed));
    }

    private static SessionInfo Row(string id, string title, string updatedAt) =>
        new() { Id = id, Title = title, UpdatedAt = updatedAt };
}
