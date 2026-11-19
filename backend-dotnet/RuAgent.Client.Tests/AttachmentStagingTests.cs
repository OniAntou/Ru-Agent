using System.Text;
using RuAgent.Client.Attachments;
using Xunit;

namespace RuAgent.Client.Tests;

/// <summary>ADR 0053: an outside attachment is copied into the workspace's .ru/attachments.</summary>
public sealed class AttachmentStagingTests : IDisposable
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "ru-staging-" + Guid.NewGuid().ToString("N"));

    public AttachmentStagingTests() => Directory.CreateDirectory(_workspace);

    [Fact]
    public async Task A_copy_lands_in_an_ignored_folder_by_its_relative_path()
    {
        var staged = await StageAsync("report.docx", "MARKER");

        Assert.Equal(".ru/attachments/report.docx", staged.RelativePath);
        Assert.Equal(6, staged.ByteSize);
        Assert.Equal("MARKER", File.ReadAllText(Path.Combine(_workspace, ".ru", "attachments", "report.docx")));
        Assert.Contains("*", File.ReadAllText(Path.Combine(_workspace, ".ru", "attachments", ".gitignore")), StringComparison.Ordinal);
        Assert.True(AttachmentStaging.IsStagedPath(staged.RelativePath));
        Assert.Empty(Directory.GetFiles(Path.Combine(_workspace, ".ru", "attachments"), "*.partial"));
    }

    [Fact]
    public async Task The_same_bytes_reuse_the_copy_and_different_bytes_get_a_numbered_name()
    {
        var first = await StageAsync("notes.txt", "one");
        var again = await StageAsync("notes.txt", "one");
        var other = await StageAsync("notes.txt", "two");

        Assert.Equal(".ru/attachments/notes.txt", first.RelativePath);
        Assert.Equal(first.RelativePath, again.RelativePath);
        Assert.Equal(".ru/attachments/notes (2).txt", other.RelativePath);
    }

    [Fact]
    public async Task A_copy_past_the_bound_fails_by_name_and_leaves_nothing_behind()
    {
        using var source = new BoundlessStream(AttachmentStaging.MaxBytes + 1);

        var error = await Assert.ThrowsAsync<AttachmentStagingException>(() =>
            AttachmentStaging.StageAsync(source, "huge.bin", _workspace, CancellationToken.None));

        Assert.Equal(AttachmentStaging.TooLargeCode, error.Code);
        Assert.Equal([".gitignore"], Directory.GetFiles(Path.Combine(_workspace, ".ru", "attachments")).Select(Path.GetFileName));
    }

    [Theory]
    [InlineData("..\\..\\evil.txt", "evil.txt")]
    [InlineData("a<b>c.txt", "a_b_c.txt")]
    [InlineData("trailing. ", "trailing")]
    [InlineData(".env", "attachment.env")]
    [InlineData("broker.json", "attached-broker.json")]
    [InlineData("agent_harness.db", "attached-agent_harness.db")]
    public void Names_are_made_safe_for_the_staging_folder(string name, string expected)
    {
        Assert.Equal(expected, AttachmentStaging.SafeFileName(name));
    }

    [Fact]
    public async Task Removing_a_staged_copy_deletes_only_inside_the_staging_folder()
    {
        var staged = await StageAsync("drop.txt", "x");
        File.WriteAllText(Path.Combine(_workspace, "keep.txt"), "keep");

        AttachmentStaging.TryRemove(staged.RelativePath, _workspace);
        AttachmentStaging.TryRemove("keep.txt", _workspace);
        AttachmentStaging.TryRemove(".ru/attachments/../../keep.txt", _workspace);

        Assert.False(File.Exists(Path.Combine(_workspace, ".ru", "attachments", "drop.txt")));
        Assert.True(File.Exists(Path.Combine(_workspace, "keep.txt")));
    }

    [Fact]
    public async Task Old_copies_are_pruned_when_another_file_is_staged()
    {
        var old = await StageAsync("old.txt", "old");
        var oldPath = Path.Combine(_workspace, ".ru", "attachments", "old.txt");
        File.SetLastWriteTimeUtc(oldPath, DateTime.UtcNow.AddDays(-(AttachmentStaging.RetentionDays + 1)));

        await StageAsync("new.txt", "new");

        Assert.Equal(".ru/attachments/old.txt", old.RelativePath);
        Assert.False(File.Exists(oldPath));
    }

    private async Task<StagedAttachment> StageAsync(string name, string content)
    {
        using var source = new MemoryStream(Encoding.UTF8.GetBytes(content));
        return await AttachmentStaging.StageAsync(source, name, _workspace, CancellationToken.None);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_workspace, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Yields zero bytes up to a length without holding them in memory.</summary>
    private sealed class BoundlessStream(long length) : Stream
    {
        private long _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = (int)Math.Min(count, length - _position);
            Array.Clear(buffer, offset, read);
            _position += read;
            return read;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
