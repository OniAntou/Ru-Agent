using System.Text;
using RuAgent.Broker.Tools.LanguageServers;
using Xunit;

namespace RuAgent.Broker.Tests.LanguageServers;

public sealed class LspFramingTests
{
    [Fact]
    public async Task ReadsFragmentedUtf8FramesAndLeavesFollowingFrame()
    {
        var first = Encoding.UTF8.GetBytes("{\"name\":\"Zürich\"}");
        var second = Encoding.UTF8.GetBytes("{}");
        var input = new FragmentedReadStream(Encoding.ASCII.GetBytes($"Content-Length: {first.Length}\r\n\r\n")
            .Concat(first).Concat(Encoding.ASCII.GetBytes($"Content-Length: {second.Length}\r\n\r\n")).Concat(second).ToArray());

        Assert.Equal(first, await LspFraming.ReadAsync(input, CancellationToken.None));
        Assert.Equal(second, await LspFraming.ReadAsync(input, CancellationToken.None));
    }

    [Theory]
    [InlineData("Content-Length: nope\r\n\r\n")]
    [InlineData("Content-Length: 4194305\r\n\r\n")]
    [InlineData("Content-Length: 1\r\nContent-Length: 1\r\n\r\n")]
    public async Task RejectsInvalidOrOversizedLengths(string header)
    {
        var stream = new MemoryStream(Encoding.ASCII.GetBytes(header));
        await Assert.ThrowsAsync<InvalidDataException>(() => LspFraming.ReadAsync(stream, CancellationToken.None));
    }

    private sealed class FragmentedReadStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);
    }
}
