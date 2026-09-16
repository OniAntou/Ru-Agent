using System.Text.Json.Nodes;
using RuBackend.Business.Activity;
using Xunit;

namespace RuBackend.Tests.Business;

public sealed class ActivityPreviewTests
{
    [Fact]
    public void FormatPreview_normalizes_whitespace_and_truncates_to_160_chars()
    {
        var longText = "  Hello   world!  " + new string('a', 200);
        var (preview, origLen, privacyRedacted, truncated) = ActivityPreview.FormatPreview(longText);

        Assert.True(preview.Length <= 160);
        Assert.EndsWith("...", preview);
        Assert.Equal(longText.Length, origLen);
        Assert.False(privacyRedacted);
        Assert.True(truncated);
        Assert.StartsWith("Hello world! aaaa", preview);
    }

    [Fact]
    public void FormatPreview_removes_private_control_metadata_and_secrets()
    {
        var text = "Check this: [[SEARCH: google]] with key sk-1234567890abcdef and Bearer token123 at https://openai.com and C:\\secret\\file.txt";
        var (preview, _, privacyRedacted, truncated) = ActivityPreview.FormatPreview(text);

        Assert.True(privacyRedacted);
        Assert.False(truncated);
        Assert.DoesNotContain("[[SEARCH", preview);
        Assert.DoesNotContain("sk-1234567890abcdef", preview);
        Assert.DoesNotContain("Bearer token123", preview);
        Assert.DoesNotContain("https://openai.com", preview);
        Assert.DoesNotContain("C:\\secret", preview);
        Assert.Contains("[REDACTED_KEY]", preview);
        Assert.Contains("[REDACTED_TOKEN]", preview);
        Assert.Contains("[URL]", preview);
        Assert.Contains("[PATH]", preview);
    }

    [Fact]
    public void FormatPreview_bounds_the_sanitization_input_and_requested_output()
    {
        var (preview, originalLength, privacyRedacted, truncated) = ActivityPreview.FormatPreview(
            new string('x', 60_000),
            int.MaxValue);

        Assert.Equal(60_000, originalLength);
        Assert.False(privacyRedacted);
        Assert.True(truncated);
        Assert.Equal(50_000, preview.Length);

        Assert.Equal(string.Empty, ActivityPreview.FormatPreview("hello", 0).Preview);
        Assert.Equal("..", ActivityPreview.FormatPreview("hello", 2).Preview);
    }

    [Fact]
    public void SanitizeEvent_converts_raw_chat_content_to_preview()
    {
        var rawEvent = new ActivityEvent(
            EventId: null,
            Ts: 100,
            Iso: "2026-08-20T00:00:00Z",
            Type: "chat",
            SessionId: "s1",
            Data: new JsonObject
            {
                ["role"] = "user",
                ["content"] = "Hello Ru!",
            });

        var sanitized = ActivityPreview.SanitizeEvent(rawEvent);

        Assert.NotNull(sanitized.EventId);
        Assert.StartsWith("evt_", sanitized.EventId);
        Assert.False(sanitized.Data.ContainsKey("content"));
        Assert.Equal("Hello Ru!", sanitized.Data["preview"]!.GetValue<string>());
        Assert.Equal(9, sanitized.Data["original_length"]!.GetValue<int>());
        Assert.False(sanitized.Data["redacted"]!.GetValue<bool>());
        Assert.False(sanitized.Data["truncated"]!.GetValue<bool>());
    }

    [Fact]
    public void SanitizeEvent_flags_truncated_without_confusing_it_with_redacted()
    {
        var rawEvent = new ActivityEvent(null, 100, "2026-08-20T00:00:00Z", "chat", "s1", new JsonObject
        {
            ["role"] = "user",
            ["content"] = new string('a', 500),
        });

        var sanitized = ActivityPreview.SanitizeEvent(rawEvent);

        Assert.False(sanitized.Data["redacted"]!.GetValue<bool>());
        Assert.True(sanitized.Data["truncated"]!.GetValue<bool>());
    }

    [Fact]
    public void SanitizeEvent_converts_agent_reply_to_the_same_safe_preview_shape()
    {
        var sanitized = ActivityPreview.SanitizeEvent(
            new ActivityEvent(
                "agent:run:step:assistant",
                100,
                "2026-08-20T00:00:00Z",
                "agent",
                "s1",
                new JsonObject
                {
                    ["role"] = "assistant",
                    ["content"] = "Visible reply [[VTS:smile]]",
                    ["emotion"] = "joy",
                }));

        Assert.Equal("agent", sanitized.Type);
        Assert.Equal("assistant", sanitized.Data["role"]!.GetValue<string>());
        Assert.Equal("Visible reply", sanitized.Data["preview"]!.GetValue<string>());
        Assert.False(sanitized.Data.ContainsKey("content"));
    }

    [Fact]
    public void SanitizeEvent_allows_safe_emotion_vts_and_observation_fields()
    {
        var emotionEvt = new ActivityEvent(null, 100, "2026-08-20T00:00:00Z", "emotion", null, new JsonObject
        {
            ["emotion"] = "happy",
            ["status"] = "succeeded",
            ["secret_key"] = "bad",
        });
        var sanitizedEmotion = ActivityPreview.SanitizeEvent(emotionEvt);
        Assert.Equal("happy", sanitizedEmotion.Data["emotion"]!.GetValue<string>());
        Assert.False(sanitizedEmotion.Data.ContainsKey("secret_key"));

        var vtsEvt = new ActivityEvent(null, 100, "2026-08-20T00:00:00Z", "vts_action", null, new JsonObject
        {
            ["category"] = "hotkey",
            ["status"] = "succeeded",
            ["private_vts_code"] = "hidden",
        });
        var sanitizedVts = ActivityPreview.SanitizeEvent(vtsEvt);
        Assert.Equal("hotkey", sanitizedVts.Data["category"]!.GetValue<string>());
        Assert.False(sanitizedVts.Data.ContainsKey("private_vts_code"));

        var obsEvt = new ActivityEvent(null, 100, "2026-08-20T00:00:00Z", "observation", null, new JsonObject
        {
            ["status"] = "completed",
            ["duration_ms"] = 123.456,
            ["screenshot_b64"] = "data",
        });
        var sanitizedObs = ActivityPreview.SanitizeEvent(obsEvt);
        Assert.Equal("completed", sanitizedObs.Data["status"]!.GetValue<string>());
        Assert.Equal(123.46, sanitizedObs.Data["duration_ms"]!.GetValue<double>());
        Assert.False(sanitizedObs.Data.ContainsKey("screenshot_b64"));
    }

    [Fact]
    public void SanitizeEvent_preserves_bounded_observation_reaction_preview_without_private_markers()
    {
        var text = "React [[SCREENSHOT:private]] Bearer token123 and sk-1234567890abcdef";
        var rawEvent = new ActivityEvent(null, 100, "2026-08-20T00:00:00Z", "observation_reaction", "s1", new JsonObject
        {
            ["text"] = text,
            ["scene"] = new string('s', 150),
            ["event"] = new string('e', 150),
            ["observation_id"] = new string('o', 150),
            ["private_payload"] = "must not persist",
        });

        var sanitized = ActivityPreview.SanitizeEvent(rawEvent);
        var data = sanitized.Data;

        Assert.Equal("observation_reaction", sanitized.Type);
        Assert.Equal(text.Length, data["original_length"]!.GetValue<int>());
        Assert.True(data["redacted"]!.GetValue<bool>());
        Assert.DoesNotContain("[[SCREENSHOT", data["preview"]!.GetValue<string>());
        Assert.DoesNotContain("Bearer token123", data["preview"]!.GetValue<string>());
        Assert.DoesNotContain("sk-1234567890abcdef", data["preview"]!.GetValue<string>());
        Assert.True(data["preview"]!.GetValue<string>().Length <= 120);
        Assert.True(data["scene"]!.GetValue<string>().Length <= 100);
        Assert.True(data["event"]!.GetValue<string>().Length <= 100);
        Assert.True(data["observation_id"]!.GetValue<string>().Length <= 64);
        Assert.False(data.ContainsKey("private_payload"));
    }

    [Theory]
    [InlineData("string")]
    [InlineData("negative")]
    [InlineData("huge")]
    public void SanitizeEvent_uses_bounded_original_length_fallback_for_invalid_metadata(string metadataKind)
    {
        const string text = "A short activity";
        JsonNode metadata = metadataKind switch
        {
            "string" => JsonValue.Create("25")!,
            "negative" => JsonValue.Create(-1)!,
            "huge" => JsonValue.Create(50_001_000)!,
            _ => throw new ArgumentOutOfRangeException(nameof(metadataKind)),
        };

        foreach (var (type, textKey) in new[]
        {
            ("chat", "content"),
            ("agent", "content"),
            ("observation_reaction", "text"),
        })
        {
            var rawEvent = new ActivityEvent(null, 100, "2026-08-20T00:00:00Z", type, "s1", new JsonObject
            {
                [textKey] = text,
                ["original_length"] = metadata.DeepClone(),
            });

            var sanitized = ActivityPreview.SanitizeEvent(rawEvent);

            Assert.Equal(text.Length, sanitized.Data["original_length"]!.GetValue<int>());
            Assert.InRange(sanitized.Data["original_length"]!.GetValue<int>(), 0, 50_000);
        }
    }
}
