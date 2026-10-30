using System.Xml.Linq;
using Xunit;

namespace RuApp.Tests;

public sealed class UserMessageAttachmentLayoutTests
{
    [Fact]
    public void Attachment_only_messages_show_no_empty_text_bubble_and_caption_changes_notify_the_view()
    {
        var message = new RuApp.Models.ChatMessageItem();
        message.Attachments.Add(new RuApp.Models.ImageAttachmentItem { FileName = "photo.png" });
        Assert.True(message.HasAttachments);
        Assert.False(message.HasText);
        var changed = new List<string?>();
        message.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        message.Content = "Here is the image";

        Assert.True(message.HasText);
        Assert.Contains(nameof(message.HasText), changed);
        message.Content = " \n ";
        Assert.False(message.HasText);
    }

    [Fact]
    public void Images_and_files_are_outside_the_text_bubble()
    {
        var template = GuiSource.Xml("Views", "ChatView.xaml").Descendants()
            .Single(element => (string?)element.Attribute(GuiSource.Xaml + "Key") == "UserMessageTemplate");
        var text = template.Descendants().Single(element =>
            (string?)element.Attribute(GuiSource.Xaml + "Name") == "AgentLegacyTextBlock");
        var bubble = text.Ancestors().First(element => element.Name.LocalName == "Border");
        var attachments = template.Descendants().Single(element => element.Name.LocalName == "ItemsControl"
            && ((string?)element.Attribute("ItemsSource"))?.Contains("Attachments", StringComparison.Ordinal) == true);

        Assert.False(GuiSource.IsDescendantOf(attachments, bubble),
            "Attachments must be separate from the user's text bubble.");
        Assert.Contains(attachments.Ancestors(), element => ReferenceEquals(element, bubble.Parent));
        Assert.Contains(attachments.Descendants(), element => element.Name.LocalName == "HyperlinkButton"
            && (string?)element.Attribute("Click") == "OnImageAttachmentClick");
        Assert.Contains(bubble.Parent!.Descendants(), element => element.Name.LocalName == "MenuFlyoutItem"
            && (string?)element.Attribute("Click") == "OnCopyMessageContentClick");
        Assert.Contains("HasText", GuiSource.AttributeValue(bubble, "Visibility"));
    }
}
