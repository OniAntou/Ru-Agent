using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class ChatAttachmentIntakePresenterTests
{
    [Theory]
    [InlineData("image.PNG", 100, 0, true, null)]
    [InlineData("source.cs", 100, 0, true, null)]
    [InlineData("report.docx", 100, 0, false, "unsupported")]
    [InlineData("large.txt", 5242881, 0, false, "too_large")]
    [InlineData("large.png", 100, 4, false, "image_limit")]
    public void Intake_classifies_type_size_and_image_count(
        string fileName,
        long size,
        int imageCount,
        bool accepted,
        string? reason)
    {
        var result = ChatAttachmentIntakePresenter.Evaluate(fileName, size, imageCount);

        Assert.Equal(accepted, result.Accepted);
        Assert.Equal(reason, result.Reason);
    }

    [Fact]
    public void Completed_intake_can_only_join_the_session_generation_that_started_it()
    {
        Assert.True(ChatAttachmentIntakePresenter.OwnsSession("s1", 4, "s1", 4));
        Assert.False(ChatAttachmentIntakePresenter.OwnsSession("s1", 4, "s2", 5));
        Assert.False(ChatAttachmentIntakePresenter.OwnsSession("s1", 4, "s1", 5));
    }

    [Fact]
    public void A_restored_turn_over_the_image_cap_is_rejected_before_submission()
    {
        Assert.Null(ChatAttachmentIntakePresenter.TurnAdmissionStatus(4));
        Assert.Equal(ChatAttachmentPolicy.TooManyImagesStatus, ChatAttachmentIntakePresenter.TurnAdmissionStatus(5));
    }
}
