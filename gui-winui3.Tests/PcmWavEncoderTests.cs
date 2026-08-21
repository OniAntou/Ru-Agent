using System.Buffers.Binary;
using System.Text;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class PcmWavEncoderTests
{
    [Fact]
    public void EncodesPcm16MonoWavWithCorrectHeaderAndPayload()
    {
        short[] samples = { 0x1234, -0x1234, 0, short.MaxValue };

        byte[] wav = PcmWavEncoder.Encode(samples, 16_000, 1);

        Assert.Equal(44 + samples.Length * sizeof(short), wav.Length);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(wav, 8, 4));
        Assert.Equal("fmt ", Encoding.ASCII.GetString(wav, 12, 4));
        Assert.Equal("data", Encoding.ASCII.GetString(wav, 36, 4));
        Assert.Equal((uint)(36 + samples.Length * sizeof(short)), BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(4, 4)));
        Assert.Equal((ushort)1, BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(20, 2)));
        Assert.Equal((ushort)1, BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(22, 2)));
        Assert.Equal((uint)16_000, BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(24, 4)));
        Assert.Equal((uint)32_000, BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(28, 4)));
        Assert.Equal((ushort)2, BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(32, 2)));
        Assert.Equal((ushort)16, BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(34, 2)));
        Assert.Equal((uint)(samples.Length * sizeof(short)), BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(40, 4)));
        Assert.Equal((short)0x1234, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(44, 2)));
        Assert.Equal((short)-0x1234, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(46, 2)));
    }

    [Fact]
    public void RejectsEmptySamples()
    {
        Assert.Throws<ArgumentException>(() => PcmWavEncoder.Encode(Array.Empty<short>(), 16_000, 1));
    }

    [Theory]
    [InlineData(7_999)]
    [InlineData(192_001)]
    public void RejectsUnsupportedSampleRate(int sampleRate)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PcmWavEncoder.Encode(new short[] { 1 }, sampleRate, 1));
    }

    [Fact]
    public void RejectsNonMonoInput()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PcmWavEncoder.Encode(new short[] { 1 }, 16_000, 2));
    }
}
