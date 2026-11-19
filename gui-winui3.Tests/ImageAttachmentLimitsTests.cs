using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

/// <summary>
/// The source-pixel budget that stops a small, highly compressed image from making the
/// decoder allocate an enormous surface before the resize transform runs.
/// </summary>
public sealed class ImageAttachmentLimitsTests
{
    [Fact]
    public void The_single_image_input_cap_grows_without_growing_the_wire_image()
    {
        Assert.Equal(100 * 1024 * 1024, ImageAttachmentResizer.DefaultMaxInputBytes);
        Assert.Equal(ImageAttachmentResizer.DefaultMaxInputBytes, ChatAttachmentPolicy.MaxImageAttachmentBytes);
        // The Host, payload store and provider adapters keep the 4 MiB wire bound; only the
        // client normalization long edge grows into Anthropic's high-resolution tier.
        Assert.Equal(4 * 1024 * 1024, ImageAttachmentResizer.DefaultMaxBytes);
        Assert.Equal(2576, ImageAttachmentResizer.DefaultMaxDimension);
    }

    [Theory]
    [InlineData(1u, 1u, false)]
    [InlineData(8000u, 8000u, false)] // exactly the 64 MP bound
    [InlineData(9000u, 9000u, true)] // 81 MP
    [InlineData(65535u, 65535u, true)] // theoretical header maximum
    public void The_source_pixel_budget_bounds_the_decode_surface(uint width, uint height, bool exceeds)
    {
        Assert.Equal(exceeds, ImageAttachmentResizer.ExceedsPixelBudget(width, height));
    }
}
