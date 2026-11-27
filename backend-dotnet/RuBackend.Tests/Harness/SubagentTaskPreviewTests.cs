using RuAgent.Harness.Core;
using RuAgent.Protocol.Subagents;
using RuBackend.Harness.Subagents;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed class SubagentTaskPreviewTests
{
    [Fact]
    public void A_long_brief_is_cut_at_the_preview_bound_without_splitting_unicode()
    {
        var longTask = new string('a', SubagentLimits.MaxTaskPreviewCharacters + 100);
        var preview = SubagentPublicService.TaskPreview(longTask);

        Assert.NotNull(preview);
        Assert.EndsWith("…", preview, StringComparison.Ordinal);
        Assert.True(preview.Length <= SubagentLimits.MaxTaskPreviewCharacters + 1);
        Assert.Equal("short brief", SubagentPublicService.TaskPreview("short brief"));
        Assert.Null(SubagentPublicService.TaskPreview(null));

        var emoji = string.Concat(Enumerable.Repeat("😀", SubagentLimits.MaxTaskPreviewCharacters + 10));
        var emojiPreview = SubagentPublicService.TaskPreview(emoji);
        Assert.NotNull(emojiPreview);
        // Every astral character is two UTF-16 code units; the cut (before the ellipsis)
        // must keep whole pairs.
        Assert.EndsWith("…", emojiPreview, StringComparison.Ordinal);
        Assert.Equal(0, emojiPreview[..^1].Length % 2);
        Assert.DoesNotContain('\ufffd', emojiPreview);
    }
}
