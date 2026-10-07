using RuAgent.Broker.Tools.Terminal;
using RuAgent.Protocol;
using Xunit;

namespace RuAgent.Broker.Tests;

/// <summary>
/// One terminal task's spill writer: the head stays in memory for receipts,
/// everything past it streams to the session spool file, and the retention
/// ceiling cuts the tail without failing the command.
/// </summary>
public sealed class SpoolFileWriterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ru-spool-writer-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Small_output_stays_in_the_window_without_a_file()
    {
        var writer = new SpoolFileWriter(Path.Combine(_root, "small.log"));
        writer.Append("hello ");
        writer.Append("world");
        writer.Complete();

        Assert.Equal("hello world", writer.Window);
        Assert.Equal(11, writer.TotalChars);
        Assert.Equal(11, writer.TotalBytes);
        Assert.Null(writer.FilePath);
        Assert.False(writer.Truncated);
        Assert.False(File.Exists(Path.Combine(_root, "small.log")));
    }

    [Fact]
    public void Output_past_the_window_spills_and_keeps_every_char()
    {
        var writer = new SpoolFileWriter(Path.Combine(_root, "spill.log"));
        writer.Append(new string('a', TerminalLimits.MaxSpoolChars));
        writer.Append(new string('b', 5000));
        writer.Complete();

        Assert.Equal(TerminalLimits.MaxSpoolChars, writer.Window.Length);
        Assert.Equal(TerminalLimits.MaxSpoolChars + 5000, writer.TotalChars);
        Assert.Equal(TerminalLimits.MaxSpoolChars + 5000, writer.TotalBytes);
        Assert.NotNull(writer.FilePath);
        Assert.False(writer.Truncated);
        Assert.Equal(new string('b', 5000), File.ReadAllText(writer.FilePath!));
    }

    [Fact]
    public void Crossing_frame_splits_between_window_and_file()
    {
        var writer = new SpoolFileWriter(Path.Combine(_root, "cross.log"));
        writer.Append(new string('x', TerminalLimits.MaxSpoolChars - 10));
        writer.Append(new string('y', 30));
        writer.Complete();

        Assert.Equal(TerminalLimits.MaxSpoolChars, writer.Window.Length);
        Assert.Equal(10, writer.Window.Count(character => character == 'y'));
        Assert.Equal(new string('y', 20), File.ReadAllText(writer.FilePath!));
        Assert.Equal(TerminalLimits.MaxSpoolChars + 20, writer.TotalChars);
    }

    [Fact]
    public void Ceiling_cuts_the_spill_file_tail()
    {
        var writer = new SpoolFileWriter(
            Path.Combine(_root, "filecap.log"),
            maxBytes: TerminalLimits.MaxSpoolChars + 100);
        writer.Append(new string('a', TerminalLimits.MaxSpoolChars));
        writer.Append(new string('b', 500));
        writer.Complete();

        Assert.Equal(TerminalLimits.MaxSpoolChars + 100, writer.TotalChars);
        Assert.Equal(TerminalLimits.MaxSpoolChars + 100, writer.TotalBytes);
        Assert.True(writer.Truncated);
        Assert.Equal(new string('b', 100), File.ReadAllText(writer.FilePath!));
    }

    [Fact]
    public void Byte_ceiling_cuts_multi_byte_text_without_splitting_it()
    {
        var writer = new SpoolFileWriter(
            Path.Combine(_root, "utf8cap.log"),
            maxBytes: TerminalLimits.MaxSpoolChars + 4);
        writer.Append(new string('a', TerminalLimits.MaxSpoolChars));
        writer.Append("ééé");
        writer.Complete();

        Assert.Equal(TerminalLimits.MaxSpoolChars + 2, writer.TotalChars);
        Assert.Equal(TerminalLimits.MaxSpoolChars + 4, writer.TotalBytes);
        Assert.True(writer.Truncated);
        Assert.Equal("éé", File.ReadAllText(writer.FilePath!));
        Assert.Equal(4, new FileInfo(writer.FilePath!).Length);
    }

    [Fact]
    public void Byte_ceiling_never_splits_a_surrogate_pair()
    {
        var writer = new SpoolFileWriter(
            Path.Combine(_root, "paircap.log"),
            maxBytes: TerminalLimits.MaxSpoolChars + 2);
        writer.Append(new string('a', TerminalLimits.MaxSpoolChars));
        writer.Append("😀");
        writer.Complete();

        Assert.Equal(TerminalLimits.MaxSpoolChars, writer.TotalChars);
        Assert.Equal(TerminalLimits.MaxSpoolChars, writer.TotalBytes);
        Assert.True(writer.Truncated);
        Assert.Null(writer.FilePath);
    }
}
