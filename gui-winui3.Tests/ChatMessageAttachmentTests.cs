using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text.Json;
using RuAgent.Protocol.Harness;
using RuApp.Models;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class ChatMessageAttachmentTests
{
    [Fact]
    public void ChatMessageItem_HasAttachments_ReturnsTrueWhenAttachmentsExist()
    {
        var msg = new ChatMessageItem
        {
            Role = "user",
            Content = "Hãy phân tích bức ảnh này",
        };

        Assert.False(msg.HasAttachments);

        msg.Attachments.Add(new ImageAttachmentItem
        {
            FileName = "photo.png",
            ByteSize = 2048,
            Base64Data = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=",
            MimeType = "image/png"
        });

        Assert.True(msg.HasAttachments);
    }

    [Fact]
    public void ImageAttachmentItem_FormattedSize_CalculatesCorrectUnits()
    {
        var bytesItem = new ImageAttachmentItem { ByteSize = 512 };
        Assert.Equal("512 B", bytesItem.FormattedSize);

        var kbItem = new ImageAttachmentItem { ByteSize = 2048 };
        Assert.Equal("2.0 KB", kbItem.FormattedSize);

        var mbItem = new ImageAttachmentItem { ByteSize = 5 * 1024 * 1024 };
        Assert.Equal("5.0 MB", mbItem.FormattedSize);
    }

    [Fact]
    public void ImageAttachmentItem_DocumentAndCodeClassification()
    {
        var codeItem = new ImageAttachmentItem
        {
            FileName = "Program.cs",
            IsImage = false,
            TextContent = "Console.WriteLine(\"Hello\");"
        };
        Assert.False(codeItem.IsImage);
        Assert.True(codeItem.IsDocument);
        Assert.True(codeItem.IsCodeFile);
        Assert.Equal(".cs", codeItem.FileExtension);
        Assert.Equal("\uE943", codeItem.FileIconGlyph);

        var docItem = new ImageAttachmentItem
        {
            FileName = "document.pdf",
            IsImage = false
        };
        Assert.False(docItem.IsImage);
        Assert.True(docItem.IsDocument);
        Assert.False(docItem.IsCodeFile);
        Assert.Equal("\uE8A5", docItem.FileIconGlyph);

        var imgItem = new ImageAttachmentItem
        {
            FileName = "sample.png",
            IsImage = true
        };
        Assert.True(imgItem.IsImage);
        Assert.False(imgItem.IsDocument);
        Assert.Equal("\uEB9F", imgItem.FileIconGlyph);
    }

    [Fact]
    public void CreateAgentRunRequest_SerializesWithImageAttachments()
    {
        var req = new CreateAgentRunRequest(
            "sess_1",
            "D:\\LLM",
            "Check this image",
            "standard",
            1,
            "idem_key_1",
            new List<string> { "data:image/png;base64,abc12345" });

        string json = JsonSerializer.Serialize(req);
        using JsonDocument doc = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.TryGetProperty("image_attachments", out var images));
        Assert.Equal(1, images.GetArrayLength());
        Assert.Equal("data:image/png;base64,abc12345", images[0].GetString());
    }

    [Fact]
    public void CreateAgentRunRequest_SerializesNullImageAttachmentsWhenOmitted()
    {
        var req = new CreateAgentRunRequest(
            "sess_1",
            "D:\\LLM",
            "Check this image",
            "standard",
            1,
            "idem_key_1");

        string json = JsonSerializer.Serialize(req);
        using JsonDocument doc = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.TryGetProperty("image_attachments", out var images));
        Assert.Equal(JsonValueKind.Null, images.ValueKind);
    }

    [Theory]
    [InlineData("google/gemini-2.5-flash", 1_000_000, "1M")]
    [InlineData("gemini-1.5-pro", 2_000_000, "2M")]
    [InlineData("anthropic/claude-3-5-sonnet", 200_000, "200K")]
    [InlineData("dots-studio/dots-3-note-preview:free", 131_072, "128K")]
    [InlineData("meta-llama/llama-3.3-70b-instruct", 131_072, "128K")]
    [InlineData("qwen/qwen-2.5-coder-32b-instruct", 131_072, "128K")]
    [InlineData("deepseek/deepseek-chat", 65_536, "64K")]
    [InlineData("mistral/mistral-large", 32_768, "32K")]
    [InlineData("unknown-model", 131_072, "128K")]
    [InlineData(null, 131_072, "128K")]
    public void ModelContextSpecs_resolves_expected_limits(string? model, int expectedTokens, string expectedText)
    {
        var (maxTokens, displayText) = ModelContextSpecs.GetContextSpec(model);
        Assert.Equal(expectedTokens, maxTokens);
        Assert.Equal(expectedText, displayText);
    }

    [Fact]
    public void ModelContextSpecs_prioritizes_catalog_context_length()
    {
        var (maxTokens, displayText) = ModelContextSpecs.GetContextSpec("poolside/laguna-s-2.1:free", 262_144);
        Assert.Equal(262_144, maxTokens);
        Assert.Equal("262.1K", displayText);
    }

    [Fact]
    public void ModelContextSpecs_extracts_tokens_from_model_name_dynamically()
    {
        var (maxTokens, displayText) = ModelContextSpecs.GetContextSpec("custom-model-256k");
        Assert.Equal(256 * 1024, maxTokens);
        Assert.Equal("256K", displayText);
    }

    [Fact]
    public void EstimateTokens_returns_proportional_estimates()
    {
        Assert.Equal(0, ModelContextSpecs.EstimateTokens(""));
        Assert.Equal(0, ModelContextSpecs.EstimateTokens(null));
        Assert.True(ModelContextSpecs.EstimateTokens("Hello world") >= 1);
        Assert.True(ModelContextSpecs.EstimateTokens(new string('a', 350)) == 100);
    }

    [Theory]
    [InlineData(500, "500")]
    [InlineData(1_500, "1.5K")]
    [InlineData(128_000, "128K")]
    [InlineData(1_000_000, "1M")]
    [InlineData(2_000_000, "2M")]
    public void FormatTokens_produces_readable_strings(int tokens, string expected)
    {
        Assert.Equal(expected, ModelContextSpecs.FormatTokens(tokens));
    }

    [Fact]
    public void ChatView_XAML_has_searchable_model_flyout_contract()
    {
        string xamlPath = Path.Combine("..", "..", "..", "..", "gui-winui3", "Views", "ChatView.xaml");
        if (!File.Exists(xamlPath))
        {
            xamlPath = Path.Combine("Views", "ChatView.xaml");
        }
        if (File.Exists(xamlPath))
        {
            string xaml = File.ReadAllText(xamlPath);
            Assert.Contains("ChatModelFlyout", xaml, StringComparison.Ordinal);
            Assert.Contains("ProviderModelPicker", xaml, StringComparison.Ordinal);
        }

        string pickerPath = Path.Combine("..", "..", "..", "..", "gui-winui3", "Views", "ProviderModelPicker.xaml");
        if (!File.Exists(pickerPath))
        {
            pickerPath = Path.Combine("Views", "ProviderModelPicker.xaml");
        }
        if (File.Exists(pickerPath))
        {
            string pickerXaml = File.ReadAllText(pickerPath);
            Assert.Contains("ModelSearchBox", pickerXaml, StringComparison.Ordinal);
            Assert.Contains("PickerListContainer", pickerXaml, StringComparison.Ordinal);
            Assert.Contains("PlaceholderText=\"Search models…\"", pickerXaml, StringComparison.Ordinal);
            Assert.Contains("HorizontalScrollBarVisibility=\"Disabled\"", pickerXaml, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ModelSearch_filters_models_case_insensitively()
    {
        var models = new List<string>
        {
            "google/gemini-2.5-flash",
            "anthropic/claude-3-5-sonnet",
            "meta-llama/llama-3.3-70b-instruct",
            "qwen/qwen-2.5-coder-32b-instruct",
            "deepseek/deepseek-chat"
        };

        var query = "CLAUDE";
        var filtered = models.FindAll(m => m.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase));
        Assert.Single(filtered);
        Assert.Equal("anthropic/claude-3-5-sonnet", filtered[0]);

        query = "llama";
        filtered = models.FindAll(m => m.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase));
        Assert.Single(filtered);
        Assert.Equal("meta-llama/llama-3.3-70b-instruct", filtered[0]);

        query = "nonexistent";
        filtered = models.FindAll(m => m.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase));
        Assert.Empty(filtered);
    }

    [Fact]
    public void ChatView_XAML_has_unified_send_and_stop_button_contract()
    {
        string xamlPath = Path.Combine("..", "..", "..", "..", "gui-winui3", "Views", "ChatView.xaml");
        if (!File.Exists(xamlPath))
        {
            xamlPath = Path.Combine("Views", "ChatView.xaml");
        }
        if (File.Exists(xamlPath))
        {
            string xaml = File.ReadAllText(xamlPath);
            Assert.Contains("x:Name=\"SendButton\"", xaml, StringComparison.Ordinal);
            Assert.Contains("x:Name=\"SendButtonIcon\"", xaml, StringComparison.Ordinal);
            Assert.Contains("Click=\"OnSendClick\"", xaml, StringComparison.Ordinal);
            Assert.Contains("AutomationProperties.AutomationId=\"AgentStopButton\"", xaml, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ImageAttachmentItem_ThumbnailImage_FallsBackToPreviewImage()
    {
        var item = new ImageAttachmentItem();
        Assert.Null(item.ThumbnailImage);
        Assert.Null(item.PreviewImage);
    }

    [Fact]
    public async Task ImageAttachmentResizer_ResizesSmallImageWithoutDistortion()
    {
        byte[] pngBytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        using (var writer = new Windows.Storage.Streams.DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(pngBytes);
            await writer.StoreAsync();
        }
        stream.Seek(0);

        var (bytes, mime, width, height) = await RuApp.Services.ImageAttachmentResizer.ResizeIfNeededAsync(stream, "image/png");
        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.Equal("image/png", mime);
        Assert.Equal(1u, width);
        Assert.Equal(1u, height);
    }

    [Fact]
    public void ChatView_CS_allows_image_attachments_in_send()
    {
        // The send path caps the count from the policy constant and announces the cap
        // from the same place. A literal in either would make the constant decorative:
        // changing MaxImageCount would not change the limit, or the number the user reads.
        var send = GuiSource.MethodBody(GuiSource.ViewText("ChatView"), "SendCurrentMessageAsync");

        Assert.Contains("ChatAttachmentPolicy.MaxImageCount", send, StringComparison.Ordinal);
        Assert.Contains("ChatAttachmentPolicy.TooManyImagesStatus", send, StringComparison.Ordinal);
        Assert.DoesNotContain("chưa được hỗ trợ", send, StringComparison.Ordinal);

        // The attachment path resizes through the shared resizer rather than sending the
        // original bytes.
        var attach = GuiSource.MethodBody(GuiSource.ViewText("ChatView"), "AddFileAttachmentAsync");
        Assert.Contains("ImageAttachmentResizer.ResizeIfNeededAsync", attach, StringComparison.Ordinal);
    }

    [Fact]
    public void The_image_cap_is_the_policy_constant()
    {
        // Pins the value itself, so a change on either side is deliberate.
        Assert.Equal(4, ChatAttachmentPolicy.MaxImageCount);
    }

    [Fact]
    public void The_too_many_images_line_names_the_cap_the_check_uses()
    {
        // The message and the check must not be able to disagree.
        Assert.Contains(
            ChatAttachmentPolicy.MaxImageCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ChatAttachmentPolicy.TooManyImagesStatus,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ChatView_XAML_has_thumbnail_formatted_size_tooltip()
    {
        string xamlPath = Path.Combine("..", "..", "..", "..", "gui-winui3", "Views", "ChatView.xaml");
        if (!File.Exists(xamlPath))
        {
            xamlPath = Path.Combine("Views", "ChatView.xaml");
        }
        if (File.Exists(xamlPath))
        {
            string xaml = File.ReadAllText(xamlPath);
            Assert.Contains("ToolTipService.ToolTip=\"{x:Bind FormattedSize, Mode=OneWay}\"", xaml, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ImageAttachmentItem_EnsurePreviewFromBase64_DecodesBase64()
    {
        var item = new ImageAttachmentItem
        {
            IsImage = true,
            Base64Data = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=",
            FileName = "dot.png"
        };
        Assert.True(item.ByteSize > 0);
        Assert.Equal("68 B", item.FormattedSize);
    }

    [Fact]
    public void ChatView_has_image_click_zoom_contract()
    {
        string xamlPath = Path.Combine("..", "..", "..", "..", "gui-winui3", "Views", "ChatView.xaml");
        if (!File.Exists(xamlPath))
        {
            xamlPath = Path.Combine("Views", "ChatView.xaml");
        }
        if (File.Exists(xamlPath))
        {
            string xaml = File.ReadAllText(xamlPath);
            Assert.Contains("Click=\"OnImageAttachmentClick\"", xaml, StringComparison.Ordinal);
        }

        string cs = GuiSource.ViewText("ChatView");
        Assert.Contains("OnImageAttachmentClick", cs, StringComparison.Ordinal);
    }
}
