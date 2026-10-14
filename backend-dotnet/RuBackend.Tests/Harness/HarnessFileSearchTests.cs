extern alias Broker;
using System;
using System.IO;
using System.Text;
using System.Threading;
using FilesAdapter = Broker::RuAgent.Broker.Tools.Files.FileSearchAdapter;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed class HarnessFileSearchTests
{
    [Fact]
    public void ReadSearchText_reads_valid_utf8_content()
    {
        var bytes = Encoding.UTF8.GetBytes("hello world from search test");
        using var stream = new MemoryStream(bytes);

        var result = FilesAdapter.ReadSearchText(stream, 1024 * 1024, 8000, CancellationToken.None);

        Assert.Equal("hello world from search test", result);
    }

    [Fact]
    public void ReadSearchText_detects_binary_probe_and_returns_null()
    {
        var bytes = new byte[] { 0x68, 0x65, 0x6C, 0x6C, 0x6F, 0x00, 0x77, 0x6F };
        using var stream = new MemoryStream(bytes);

        var result = FilesAdapter.ReadSearchText(stream, 1024, 8000, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public void ReadSearchText_throws_on_invalid_utf8()
    {
        var bytes = new byte[] { 0xC3, 0x28, 0x68, 0x65 }; // Invalid UTF-8 sequence
        using var stream = new MemoryStream(bytes);

        var ex = Assert.Throws<ArgumentException>(() =>
            FilesAdapter.ReadSearchText(stream, 1024, 8000, CancellationToken.None));

        Assert.Equal("invalid_utf8", ex.Message);
    }

    [Fact]
    public void ReadSearchText_throws_on_file_too_large()
    {
        var bytes = Encoding.UTF8.GetBytes(new string('x', 200));
        using var stream = new MemoryStream(bytes);

        var ex = Assert.Throws<ArgumentException>(() =>
            FilesAdapter.ReadSearchText(stream, 100, 50, CancellationToken.None));

        Assert.Equal("file_too_large", ex.Message);
    }

    [Fact]
    public void ReadSearchText_handles_empty_stream()
    {
        using var stream = new MemoryStream(Array.Empty<byte>());

        var result = FilesAdapter.ReadSearchText(stream, 1024, 8000, CancellationToken.None);

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void ReadSearchText_honors_cancellation()
    {
        var bytes = Encoding.UTF8.GetBytes("test");
        using var stream = new MemoryStream(bytes);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            FilesAdapter.ReadSearchText(stream, 1024, 8000, cts.Token));
    }
}
