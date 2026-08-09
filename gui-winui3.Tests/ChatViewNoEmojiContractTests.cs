using System.Text;
using Xunit;

namespace RuApp.Tests;

public sealed class ChatViewNoEmojiContractTests
{
    [Fact]
    public void ChatViewPresentationSourcesContainNoEmoji()
    {
        foreach (var sourceName in new[] { "ChatView.xaml", "ChatView.xaml.cs" })
        {
            foreach (var rune in File.ReadAllText(FindChatViewSource(sourceName)).EnumerateRunes())
            {
                Assert.False(
                    IsEmojiRune(rune),
                    $"{sourceName} contains prohibited emoji U+{rune.Value:X4}.");
            }
        }
    }

    [Theory]
    [InlineData(0x00A9)]
    [InlineData(0x203C)]
    [InlineData(0x2194)]
    [InlineData(0x231A)]
    [InlineData(0x2600)]
    [InlineData(0x2B50)]
    [InlineData(0x1F642)]
    [InlineData(0x1FAE0)]
    [InlineData(0xFE0F)]
    [InlineData(0x20E3)]
    [InlineData(0xE007F)]
    public void EmojiPolicyCoversEmojiPresentationAndCompositionRunes(int scalar)
    {
        Assert.True(IsEmojiRune(new Rune(scalar)));
    }

    private static bool IsEmojiRune(Rune rune)
    {
        var value = rune.Value;
        return value is 0x00A9 or 0x00AE or 0x203C or 0x2049 or 0x2122 or 0x2139
            or 0x3030 or 0x303D or 0x3297 or 0x3299 or 0x20E3 or 0xFE0F
            or >= 0x2194 and <= 0x21FF
            or >= 0x2300 and <= 0x23FF
            or >= 0x2600 and <= 0x27BF
            or >= 0x2934 and <= 0x2935
            or >= 0x2B00 and <= 0x2BFF
            or >= 0x1F000 and <= 0x1FAFF
            or >= 0xE0020 and <= 0xE007F;
    }

    private static string FindChatViewSource(string sourceName)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var sourcePath = Path.Combine(
                directory.FullName,
                "gui-winui3",
                "Views",
                sourceName);
            if (File.Exists(sourcePath))
            {
                return sourcePath;
            }
        }

        throw new FileNotFoundException($"Could not locate Views/{sourceName}.");
    }
}
