using System.Text;
using Xunit;

namespace RuAgent.Harness.Persistence.Tests;

public sealed class WorkspaceMergeEngineTests
{
    [Fact]
    public void Non_overlapping_text_changes_are_merged_from_the_common_base()
    {
        var result = WorkspaceMergeEngine.MergeText(
            "one\ntwo\nthree\n",
            "ONE\ntwo\nthree\n",
            "one\ntwo\nTHREE\n",
            localSequence: 1,
            externalSequence: 2);

        Assert.Equal(WorkspaceMergeOutcome.Merged, result.Outcome);
        Assert.Equal("ONE\ntwo\nTHREE\n", result.Content);
    }

    [Fact]
    public void Overlapping_text_change_uses_the_newer_coordinator_sequence()
    {
        var result = WorkspaceMergeEngine.MergeText(
            "value\n",
            "local\n",
            "external\n",
            localSequence: 3,
            externalSequence: 2);

        Assert.Equal(WorkspaceMergeOutcome.LastWriterWins, result.Outcome);
        Assert.Equal("local\n", result.Content);
    }

    [Fact]
    public void Binary_conflict_is_rejected_without_content()
    {
        var result = WorkspaceMergeEngine.RejectBinaryConflict();

        Assert.Equal(WorkspaceMergeOutcome.BinaryConflict, result.Outcome);
        Assert.Null(result.Content);
        Assert.Equal("concurrent_binary_conflict", result.ErrorCode);
    }

    [Fact]
    public void Binary_bytes_are_fail_closed_and_leave_content_unmerged()
    {
        var result = WorkspaceMergeEngine.MergeBytes(
            [0x00, 0x01],
            [0x00, 0x02],
            [0x00, 0x03],
            localSequence: 1,
            externalSequence: 2,
            extension: ".bin");

        Assert.Equal(WorkspaceMergeOutcome.BinaryConflict, result.Outcome);
        Assert.Equal("concurrent_binary_conflict", result.ErrorCode);
        Assert.Null(result.Content);
    }

    [Fact]
    public void Utf8_bom_and_line_endings_are_preserved_for_text_bytes()
    {
        var result = WorkspaceMergeEngine.MergeBytes(
            Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("one\r\ntwo\r\n")).ToArray(),
            Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("ONE\r\ntwo\r\n")).ToArray(),
            Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("one\r\nTWO\r\n")).ToArray(),
            localSequence: 1,
            externalSequence: 2,
            extension: ".cs");

        Assert.Equal(WorkspaceMergeOutcome.Merged, result.Outcome);
        Assert.StartsWith("\uFEFF", result.Content);
        Assert.Contains("ONE\r\nTWO\r\n", result.Content);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Utf16_bom_is_preserved_for_text_bytes(bool bigEndian)
    {
        var encoding = bigEndian ? Encoding.BigEndianUnicode : Encoding.Unicode;
        var preamble = encoding.GetPreamble();
        var result = WorkspaceMergeEngine.MergeBytes(
            preamble.Concat(encoding.GetBytes("one\r\ntwo\r\n")).ToArray(),
            preamble.Concat(encoding.GetBytes("ONE\r\ntwo\r\n")).ToArray(),
            preamble.Concat(encoding.GetBytes("one\r\nTWO\r\n")).ToArray(),
            localSequence: 1,
            externalSequence: 2,
            extension: ".cs");

        Assert.Equal(WorkspaceMergeOutcome.Merged, result.Outcome);
        Assert.StartsWith("\uFEFF", result.Content);
        Assert.Contains("ONE\r\nTWO\r\n", result.Content);
    }
}
