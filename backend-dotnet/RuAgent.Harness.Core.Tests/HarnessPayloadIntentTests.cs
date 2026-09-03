using System.Text;
using RuAgent.Harness.Core.Persistence;
using Xunit;

namespace RuAgent.Harness.Core.Tests;

public sealed class HarnessPayloadIntentTests
{
    [Fact]
    public void UserImage_payload_validates_with_dual_audience()
    {
        var id = new PayloadId("payload_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var dataUrl = "data:image/png;base64,iVBORw0KGgo=";
        var bytes = Encoding.UTF8.GetBytes(dataUrl);
        var payload = new HarnessPayloadIntent(
            id,
            HarnessPayloadKind.UserImage,
            HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
            HarnessPayloadEncoding.Utf8Text,
            bytes.Length,
            bytes);

        Assert.Equal(HarnessPayloadKind.UserImage, payload.Kind);
        Assert.Equal((HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext), payload.Audience);
        Assert.Equal(HarnessPayloadEncoding.Utf8Text, payload.Encoding);
    }

    [Fact]
    public void UserImage_value_is_11()
    {
        Assert.Equal(11, (int)HarnessPayloadKind.UserImage);
    }

    [Fact]
    public void UserImage_rejects_internal_only_audience()
    {
        var id = new PayloadId("payload_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        var bytes = new byte[10];
        Assert.Throws<ArgumentException>(() => new HarnessPayloadIntent(
            id,
            HarnessPayloadKind.UserImage,
            HarnessPayloadAudience.InternalOnly,
            HarnessPayloadEncoding.Utf8Text,
            10,
            bytes));
    }

    [Fact]
    public void UserImage_rejects_internal_only_combined_audience()
    {
        var id = new PayloadId("payload_cccccccccccccccccccccccccccccccc");
        var bytes = new byte[10];
        Assert.Throws<ArgumentException>(() => new HarnessPayloadIntent(
            id,
            HarnessPayloadKind.UserImage,
            HarnessPayloadAudience.InternalOnly | HarnessPayloadAudience.AuthenticatedClient,
            HarnessPayloadEncoding.Utf8Text,
            10,
            bytes));
    }

    [Theory]
    [InlineData(HarnessPayloadAudience.AuthenticatedClient)]
    [InlineData(HarnessPayloadAudience.ModelContext)]
    public void UserImage_rejects_single_audience(HarnessPayloadAudience audience)
    {
        var id = new PayloadId("payload_dddddddddddddddddddddddddddddddd");
        var bytes = new byte[10];
        Assert.Throws<ArgumentException>(() => new HarnessPayloadIntent(
            id,
            HarnessPayloadKind.UserImage,
            audience,
            HarnessPayloadEncoding.Utf8Text,
            10,
            bytes));
    }

    [Theory]
    [InlineData(HarnessPayloadEncoding.Json)]
    [InlineData(HarnessPayloadEncoding.Opaque)]
    public void UserImage_rejects_non_utf8_encoding(HarnessPayloadEncoding encoding)
    {
        var id = new PayloadId("payload_eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee");
        var bytes = new byte[10];
        Assert.Throws<ArgumentException>(() => new HarnessPayloadIntent(
            id,
            HarnessPayloadKind.UserImage,
            HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
            encoding,
            10,
            bytes));
    }

    [Fact]
    public void UserImage_enforces_byte_cap_and_maximum()
    {
        var id = new PayloadId("payload_ffffffffffffffffffffffffffffffff");
        var dataUrl = "data:image/png;base64,iVBORw0KGgo=";
        var bytes = Encoding.UTF8.GetBytes(dataUrl);
        // valid cap
        var payload = new HarnessPayloadIntent(
            id,
            HarnessPayloadKind.UserImage,
            HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
            HarnessPayloadEncoding.Utf8Text,
            bytes.Length,
            bytes);
        Assert.Equal(bytes.Length, payload.ByteCap);

        // cap zero rejected
        Assert.Throws<ArgumentOutOfRangeException>(() => new HarnessPayloadIntent(
            id,
            HarnessPayloadKind.UserImage,
            HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
            HarnessPayloadEncoding.Utf8Text,
            0,
            bytes));

        // cap > MaximumByteCap rejected
        Assert.Throws<ArgumentOutOfRangeException>(() => new HarnessPayloadIntent(
            id,
            HarnessPayloadKind.UserImage,
            HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
            HarnessPayloadEncoding.Utf8Text,
            HarnessPayloadIntent.MaximumByteCap + 1,
            bytes));

        // plaintext exceeds cap rejected
        Assert.Throws<ArgumentException>(() => new HarnessPayloadIntent(
            id,
            HarnessPayloadKind.UserImage,
            HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
            HarnessPayloadEncoding.Utf8Text,
            1,
            bytes));
    }

    [Fact]
    public void UserImage_defensively_copies_plaintext()
    {
        var id = new PayloadId("payload_11111111111111111111111111111111");
        var bytes = Encoding.UTF8.GetBytes("data:image/png;base64,abcd");
        var payload = new HarnessPayloadIntent(
            id,
            HarnessPayloadKind.UserImage,
            HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
            HarnessPayloadEncoding.Utf8Text,
            bytes.Length,
            bytes);
        bytes[0] = (byte)'X';
        Assert.NotEqual((byte)'X', payload.Plaintext.Span[0]);
    }
}
