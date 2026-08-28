using RuBackend.Conversation;
using Xunit;

namespace RuBackend.Tests.Conversation;

public sealed class ConversationIntentResolverTests
{
    private readonly ConversationIntentResolver _resolver = new();

    [Theory]
    [InlineData("Xin chào em")]
    [InlineData("hello Ru")]
    [InlineData("Em ơi")]
    public void High_confidence_greetings_are_local(string prompt)
    {
        var result = _resolver.Resolve(prompt);

        Assert.Equal(ConversationIntentKind.Greeting, result.Kind);
        Assert.True(result.IsLocalReply);
        Assert.False(string.IsNullOrWhiteSpace(result.LocalReply));
        Assert.True(result.Confidence >= 0.95);
    }

    [Theory]
    [InlineData("ok")]
    [InlineData("được rồi")]
    [InlineData("duyệt")]
    public void High_confidence_acknowledgements_are_local(string prompt)
    {
        var result = _resolver.Resolve(prompt);

        Assert.Equal(ConversationIntentKind.Acknowledgement, result.Kind);
        Assert.True(result.IsLocalReply);
        Assert.Equal("Vâng, em nghe đây.", result.LocalReply);
    }

    [Theory]
    [InlineData("Không, ý anh là sửa câu trước")]
    [InlineData("Sửa lại giúp anh")]
    public void Corrections_keep_full_model_context(string prompt)
    {
        var result = _resolver.Resolve(prompt);

        Assert.Equal(ConversationIntentKind.Correction, result.Kind);
        Assert.False(result.IsLocalReply);
        Assert.True(result.RequiresModelContext);
    }

    [Theory]
    [InlineData("Truy cập vào folder D:\\Test giúp anh")]
    [InlineData("Tìm kiếm tài liệu về WinUI")]
    [InlineData("Bây giờ là mấy giờ?")]
    public void Path_search_and_current_fact_requests_are_not_local(string prompt)
    {
        var result = _resolver.Resolve(prompt);

        Assert.False(result.IsLocalReply);
        Assert.True(result.RequiresModelContext);
    }

    [Fact]
    public void Unrelated_text_is_a_no_match()
    {
        var result = _resolver.Resolve("Kể cho anh một câu chuyện dài hơn nhé");

        Assert.Equal(ConversationIntentKind.NoMatch, result.Kind);
        Assert.False(result.IsLocalReply);
        Assert.True(result.RequiresModelContext);
    }
}
