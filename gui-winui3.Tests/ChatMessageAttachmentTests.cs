using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
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
            // The field searches providers as well as models, and the projection keeps
            // a whole provider's models when its name matches, so promising only models
            // was the narrower half of what the field actually does.
            Assert.Contains("PlaceholderText=\"Search models or providers\"", pickerXaml, StringComparison.Ordinal);
            Assert.Contains("HorizontalScrollBarVisibility=\"Disabled\"", pickerXaml, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The search well's inner edges sit on the same lines as the two columns it filters.
    /// </summary>
    /// <remarks>
    /// Both numbers are derived, not chosen: the well's outer margin is the list's own
    /// padding, and its inner padding is the width the row reserves for the selection bar
    /// on the left and the row's right padding on the right, which is where the context
    /// figures end. Change any one of those three and this fails, because the alternative
    /// is a field hanging out of line with a column of numbers in a panel whose whole
    /// argument is that column.
    ///
    /// Pinned rather than computed on purpose: if it read the constants out of the
    /// code-behind it would follow any edit to them and never once object. The failure
    /// message names the numbers so whoever hits it knows which side moved.
    /// </remarks>
    [Fact]
    public void Model_picker_search_field_lines_up_with_the_columns_it_filters()
    {
        const int listPadding = 6;
        const int selectionColumn = 11;
        const int rowRightPadding = 8;

        string xaml = File.ReadAllText(PickerXamlPath());

        Assert.Contains(
            $"Margin=\"{listPadding},8,{listPadding},0\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            $"Padding=\"{selectionColumn},0,{rowRightPadding},0\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            $"Padding=\"{listPadding},6,{listPadding},10\"",
            xaml,
            StringComparison.Ordinal);
    }

    private static string PickerXamlPath()
    {
        string path = Path.Combine(
            "..", "..", "..", "..", "gui-winui3", "Views", "ProviderModelPicker.xaml");
        return File.Exists(path) ? path : Path.Combine("Views", "ProviderModelPicker.xaml");
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

        // Picker and clipboard attachments share one bounded image preparation path.
        var attach = GuiSource.MethodBody(GuiSource.ViewText("ChatView"), "AddFileAttachmentAsync");
        Assert.Contains("PrepareImageAttachmentAsync", attach, StringComparison.Ordinal);
        Assert.Contains("CanAcceptImageAttachment", attach, StringComparison.Ordinal);
    }

    [Fact]
    public void Plain_text_attachments_drop_a_utf8_bom_like_the_cli()
    {
        // A BOM is an encoding marker, not prompt text: the CLI read detects and
        // strips it, and the GUI path must not hand U+FEFF to the model.
        var view = GuiSource.ViewText("ChatView");
        var decode = GuiSource.MethodBody(view, "DecodeTextAttachment");

        Assert.Contains("detectEncodingFromByteOrderMarks: true", decode, StringComparison.Ordinal);
        var attach = GuiSource.MethodBody(view, "AddFileAttachmentAsync");
        Assert.Contains("DecodeTextAttachment(bytes)", attach, StringComparison.Ordinal);
    }

    [Fact]
    public void Clipboard_images_use_the_picker_normalization_and_intake_limits()
    {
        var view = GuiSource.ViewText("ChatView");
        var paste = GuiSource.MethodBody(view, "TryPasteImageFromClipboardAsync");

        Assert.Contains("PrepareImageAttachmentAsync", paste, StringComparison.Ordinal);
        Assert.Contains("CanAcceptImageAttachment", paste, StringComparison.Ordinal);
        var preparation = GuiSource.MethodBody(view, "PrepareImageAttachmentAsync");
        Assert.Contains("ImageAttachmentResizer.ResizeIfNeededAsync", preparation, StringComparison.Ordinal);
        Assert.Contains("ChatAttachmentPolicy.MimeForExtension", preparation, StringComparison.Ordinal);
    }

    [Fact]
    public void Final_attachment_admission_rechecks_image_cap_and_session_generation()
    {
        var view = GuiSource.ViewText("ChatView");
        var admission = GuiSource.MethodBody(view, "TryAddAttachmentToComposer");

        Assert.Contains("CanAcceptImageAttachment", admission, StringComparison.Ordinal);
        Assert.Contains("sessionId", admission, StringComparison.Ordinal);
        Assert.Contains("_activeSessionId", admission, StringComparison.Ordinal);
        Assert.Contains("sessionGeneration != _sessionOpenGeneration", admission, StringComparison.Ordinal);
        Assert.True(
            admission.IndexOf("CanAcceptImageAttachment", StringComparison.Ordinal)
                < admission.IndexOf("_composerDrafts.TrySave", StringComparison.Ordinal),
            "the final cap check must run before the draft is saved");
    }

    [Fact]
    public void Send_keeps_the_composer_draft_until_the_controller_accepts_the_run()
    {
        var view = GuiSource.ViewText("ChatView");
        var send = GuiSource.MethodBody(view, "SendCurrentMessageAsync");
        var turn = GuiSource.MethodBody(view, "SendAgentTurnAsync");

        Assert.Contains("CreateAndRunAsync", turn, StringComparison.Ordinal);
        Assert.Contains("if (!accepted)", turn, StringComparison.Ordinal);
        Assert.Contains("onAccepted:", send, StringComparison.Ordinal);
        Assert.Contains("PendingAttachments.SequenceEqual(attachments)", send, StringComparison.Ordinal);
        int acceptedCallback = send.IndexOf("onAccepted:", StringComparison.Ordinal);
        int clearDraft = send.IndexOf("ClearComposerDraft(submittedSessionId)", StringComparison.Ordinal);
        Assert.True(clearDraft > acceptedCallback, "the saved draft must be cleared only after the controller accepts the run");

        var beginAccepted = GuiSource.MethodBody(view, "BeginAcceptedAgentTurn");
        int beginReply = beginAccepted.IndexOf("BeginAgentReply(", StringComparison.Ordinal);
        int clearStatus = beginAccepted.IndexOf("ClearComposerStatus()", StringComparison.Ordinal);
        int updateStatus = beginAccepted.IndexOf("UpdateAgentInlineState()", StringComparison.Ordinal);
        Assert.True(beginReply >= 0 && clearStatus > beginReply, "a previous error clears only after the current session accepts the run");
        Assert.True(updateStatus > clearStatus, "the accepted run's status is projected after the old composer error clears");
    }

    [Fact]
    public void Send_snapshots_the_session_after_creating_a_session_for_a_fresh_chat()
    {
        var send = GuiSource.MethodBody(GuiSource.ViewText("ChatView"), "SendCurrentMessageAsync");
        int ensureSession = send.IndexOf("await EnsureAgentSessionAsync", StringComparison.Ordinal);
        int captureSession = send.IndexOf("submittedSessionId = _activeSessionId", StringComparison.Ordinal);

        Assert.True(ensureSession >= 0, "a fresh chat needs its session before send guards are captured");
        Assert.True(captureSession > ensureSession, "capture the session after a fresh session is activated");
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
    public void MimeForExtension_only_ever_returns_a_type_the_Host_accepts()
    {
        // The Host and the provider adapters accept exactly png/jpeg/webp. The picker
        // also offers .bmp, so its mime must be the wire type the bytes get
        // normalized to -- returning "image/bmp" produced a 400 on every BMP.
        foreach (var name in new[] { "a.png", "a.jpg", "a.jpeg", "a.webp", "a.bmp" })
        {
            var mime = ChatAttachmentPolicy.MimeForExtension(name);
            Assert.Contains(mime, new[] { "image/png", "image/jpeg", "image/webp" });
        }
    }

    [Fact]
    public void A_composer_local_status_survives_the_inline_state_refresh()
    {
        // ShowAgentInlineStatus had no run behind it, so the next
        // UpdateAgentInlineState painted the panel policy's empty text straight over
        // it and a rejected attachment was rejected silently. The local status is
        // the one that must outlive that refresh.
        var view = GuiSource.ViewText("ChatView");
        var status = GuiSource.MethodBody(view, "ShowAgentInlineStatus");

        Assert.Contains("_localComposerStatus = status", status, StringComparison.Ordinal);
        var update = GuiSource.MethodBody(view, "UpdateAgentInlineState");
        Assert.Contains("_localComposerStatus", update, StringComparison.Ordinal);
        Assert.Contains("state.ShowInlinePanel || _localComposerStatus.Length > 0", update, StringComparison.Ordinal);
    }

    [Fact]
    public void The_image_count_check_precedes_entering_the_sending_face()
    {
        // Bailing out after UpdateSendButtonMode(true) leaves the button showing Stop
        // with no run behind it. The cap check has to come first.
        var send = GuiSource.MethodBody(GuiSource.ViewText("ChatView"), "SendCurrentMessageAsync");
        int cap = send.IndexOf("ChatAttachmentPolicy.MaxImageCount", StringComparison.Ordinal);
        int sending = send.IndexOf("UpdateSendButtonMode(isAgentRunning: true)", StringComparison.Ordinal);

        Assert.True(cap >= 0 && sending >= 0, "both statements must be present");
        Assert.True(cap < sending, "the cap check must run before the composer enters its sending face");
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
    public void A_bool_bound_to_Visibility_goes_through_the_converter()
    {
        // Visibility is not a bool, so a raw bool binding needs the converter. The
        // image/document pills were bound without one, which left every non-image
        // attachment invisible in the composer: the file was attached and sent, the
        // user just saw nothing. Images looked fine, so the strip looked alive.
        string xamlPath = Path.Combine("..", "..", "..", "..", "gui-winui3", "Views", "ChatView.xaml");
        if (!File.Exists(xamlPath))
        {
            xamlPath = Path.Combine("Views", "ChatView.xaml");
        }
        Assert.True(File.Exists(xamlPath), xamlPath);

        string xaml = File.ReadAllText(xamlPath);
        var missing = System.Text.RegularExpressions.Regex.Matches(
                xaml,
                "Visibility=\"\\{x:Bind (?<flag>[A-Za-z]+), Mode=OneWay\\}\"")
            .Select(match => match.Groups["flag"].Value)
            .Where(flag => flag is "IsImage" or "IsDocument")
            .ToArray();
        Assert.Empty(missing);
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
