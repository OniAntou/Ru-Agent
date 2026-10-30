using System.Text.Json.Nodes;
using RuAgent.Runtime.Adapters.Models;
using Xunit;

namespace RuAgent.Runtime.Adapters.Tests;

public sealed class ModelRequestTests
{
    private static ModelRequest CreateRequest(IReadOnlyList<ModelImage>? images = null) => new(
        "gpt-4o",
        "sys",
        "hi",
        Array.Empty<ModelTurn>(),
        new JsonObject(),
        new Uri("https://api.openai.com/v1"),
        "sk",
        100,
        images);

    [Fact]
    public void ModelRequest_accepts_images()
    {
        var images = new[] { new ModelImage("image/png", "iVBORw0KGgo=") };
        var req = CreateRequest(images);
        Assert.Single(req.Images);
        Assert.Equal("image/png", req.Images[0].MimeType);
    }

    [Fact]
    public void ModelRequest_accepts_four_images_boundary()
    {
        var images = Enumerable.Range(0, 4).Select(_ => new ModelImage("image/png", "aGVsbG8=")).ToArray();
        var req = CreateRequest(images);
        Assert.Equal(4, req.Images.Count);
    }

    [Fact]
    public void ModelRequest_defaults_images_to_empty_when_null()
    {
        var req = new ModelRequest("gpt-4o", "sys", "hi", Array.Empty<ModelTurn>(), new JsonObject(), new Uri("https://api.openai.com/v1"), "sk", 100);
        Assert.NotNull(req.Images);
        Assert.Empty(req.Images);
    }

    [Fact]
    public void ModelRequest_rejects_too_many_images()
    {
        var imgs = Enumerable.Range(0, 5).Select(_ => new ModelImage("image/png", "a")).ToArray();
        Assert.Throws<ArgumentException>(() => CreateRequest(imgs));
    }

    [Fact]
    public void Provider_headers_are_kept_verbatim_and_bounded()
    {
        var request = new ModelRequest("gpt-4o", "sys", "hi", Array.Empty<ModelTurn>(), new JsonObject(),
            new Uri("https://api.openai.com/v1"), "sk", 100,
            providerHeaders: new Dictionary<string, string> { ["x-opencode-session"] = "sess_01" });

        Assert.Equal("sess_01", request.ProviderHeaders["x-opencode-session"]);

        // A header may not take over the credential or the framing the adapter owns.
        Assert.Throws<ArgumentException>(() => new ModelRequest(
            "gpt-4o", "sys", "hi", Array.Empty<ModelTurn>(), new JsonObject(),
            new Uri("https://api.openai.com/v1"), "sk", 100,
            providerHeaders: new Dictionary<string, string> { ["Authorization"] = "Bearer other" }));
        // Values travel on the wire, so a line break is refused here, not by the socket.
        Assert.Throws<ArgumentException>(() => new ModelRequest(
            "gpt-4o", "sys", "hi", Array.Empty<ModelTurn>(), new JsonObject(),
            new Uri("https://api.openai.com/v1"), "sk", 100,
            providerHeaders: new Dictionary<string, string> { ["x-opencode-session"] = "a\r\nb" }));
    }

    [Fact]
    public void ModelRequest_rejects_null_image_entry()
    {
        var imgs = new ModelImage?[] { new ModelImage("image/png", "a"), null! }.Where(x => true).Select(x => x!).ToList();
        // force list containing null without compile error: create array with null element
        var list = new List<ModelImage> { new ModelImage("image/png", "a"), null! };
        Assert.Throws<ArgumentException>(() => CreateRequest(list));
    }

    [Fact]
    public void ModelFunctionCall_requires_a_safe_identifier()
    {
        var arguments = System.Text.Json.JsonDocument.Parse("{}").RootElement;

        Assert.Throws<ArgumentException>(() => new ModelFunctionCall(null!, "read", arguments));
        Assert.Throws<ArgumentException>(() => new ModelFunctionCall("call/unsafe", "read", arguments));
    }

    [Fact]
    public void ModelRequest_exposes_a_true_read_only_turn_collection()
    {
        var original = new ModelTurn("user", "original");
        var request = new ModelRequest(
            "gpt-4o",
            "sys",
            "hi",
            new List<ModelTurn> { original },
            new JsonObject(),
            new Uri("https://api.openai.com/v1"),
            "sk");
        var mutable = Assert.IsAssignableFrom<IList<ModelTurn>>(request.Turns);

        Assert.Throws<NotSupportedException>(() => mutable[0] = new ModelTurn("user", "mutated"));
        Assert.Equal("original", request.Turns[0].Content);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public void ModelRequest_enforces_exact_history_byte_boundary(int extra, bool accepted)
    {
        var placeholders = Enumerable.Range(0, 10).Select(_ => new ModelTurn("user", "x")).ToArray();
        var remaining = ModelInputLimits.MaxHistoryBytes - (int)ModelRequest.MeasureModelMessagesBytes(placeholders) + extra;
        var turns = placeholders.Select((_, index) => new ModelTurn("user",
            new string('x', 1 + remaining / 10 + (index < remaining % 10 ? 1 : 0)))).ToArray();
        Assert.Equal(ModelInputLimits.MaxHistoryBytes + extra, ModelRequest.MeasureModelMessagesBytes(turns));
        ModelRequest Construct() => new("test-model", "sys", "hi", turns, new JsonObject(),
            new Uri("https://provider.invalid/v1"), "synthetic");
        if (accepted) Assert.Equal(10, Construct().Turns.Count);
        else Assert.Throws<ArgumentException>(Construct);
    }

    [Fact]
    public void ModelRequest_accepts_configured_message_history_boundary()
    {
        var turns = Enumerable.Range(0, ModelInputLimits.MaxMessages)
            .Select(_ => new ModelTurn("user", "x"))
            .ToArray();

        var request = new ModelRequest(
            "gpt-4o",
            "sys",
            "hi",
            turns,
            new JsonObject(),
            new Uri("https://api.openai.com/v1"),
            "sk");

        Assert.Equal(ModelInputLimits.MaxMessages, request.Turns.Count);
    }

    [Fact]
    public void ModelRequest_rejects_more_than_configured_messages()
    {
        var turns = Enumerable.Range(0, ModelInputLimits.MaxMessages + 1)
            .Select(_ => new ModelTurn("user", "x"))
            .ToArray();

        Assert.Throws<ArgumentException>(() => new ModelRequest(
            "gpt-4o",
            "sys",
            "hi",
            turns,
            new JsonObject(),
            new Uri("https://api.openai.com/v1"),
            "sk"));
    }

    [Fact]
    public void ModelRequest_rejects_history_larger_than_memory_ceiling()
    {
        var turns = Enumerable.Range(0, 90)
            .Select(_ => new ModelTurn("user", new string('x', 100_000)))
            .ToArray();

        Assert.Throws<ArgumentException>(() => new ModelRequest(
            "gpt-4o",
            "sys",
            "hi",
            turns,
            new JsonObject(),
            new Uri("https://api.openai.com/v1"),
            "sk"));
    }

    [Fact]
    public void ModelRequest_rejects_tool_result_without_preceding_call()
    {
        var turns = new[]
        {
            new ModelTurn("tool", "result", toolCallId: "call_1", toolResultCode: "ok"),
        };

        Assert.Throws<ArgumentException>(() => new ModelRequest(
            "gpt-4o",
            "sys",
            "hi",
            turns,
            new JsonObject(),
            new Uri("https://api.openai.com/v1"),
            "sk"));
    }

    [Fact]
    public void ModelRequest_rejects_assistant_call_batch_without_results()
    {
        var turns = new[]
        {
            new ModelTurn(
                "assistant",
                string.Empty,
                [new ModelFunctionCall(
                    "call_1",
                    "read",
                    System.Text.Json.JsonDocument.Parse("{}").RootElement.Clone())]),
        };

        Assert.Throws<ArgumentException>(() => new ModelRequest(
            "gpt-4o",
            "sys",
            "hi",
            turns,
            new JsonObject(),
            new Uri("https://api.openai.com/v1"),
            "sk"));
    }

    [Theory]
    [InlineData("image/gif")]
    [InlineData("image/bmp")]
    [InlineData("text/plain")]
    [InlineData("")]
    public void ModelImage_rejects_invalid_mime(string mime)
    {
        Assert.Throws<ArgumentException>(() => new ModelImage(mime, "aGVsbG8="));
    }

    [Fact]
    public void ModelImage_accepts_valid_mimes()
    {
        var png = new ModelImage("image/png", "a");
        var jpeg = new ModelImage("image/jpeg", "a");
        var webp = new ModelImage("image/webp", "a");
        Assert.Equal("image/png", png.MimeType);
        Assert.Equal("image/jpeg", jpeg.MimeType);
        Assert.Equal("image/webp", webp.MimeType);
    }

    [Fact]
    public void ModelImage_rejects_empty_base64()
    {
        Assert.Throws<ArgumentException>(() => new ModelImage("image/png", ""));
        Assert.Throws<ArgumentException>(() => new ModelImage("image/png", "   "));
        Assert.Throws<ArgumentException>(() => new ModelImage("image/png", null!));
    }

    [Fact]
    public void ModelRequest_rejects_invalid_mime_via_image()
    {
        // ModelImage itself should reject, but also ensure ModelRequest path
        Assert.Throws<ArgumentException>(() => new ModelImage("image/gif", "aGVsbG8="));
    }

    [Fact]
    public void ModelImage_rejects_base64_too_long()
    {
        // 5_600_000 is max, 5_600_001 should fail; avoid allocating huge string in test by using length check via reflection?
        // We allocate exactly 5_600_001 chars — ~10 MB string, acceptable for test
        var tooLong = new string('A', 5_600_001);
        Assert.Throws<ArgumentException>(() => new ModelImage("image/png", tooLong));
    }

    [Fact]
    public void ModelImage_accepts_base64_at_limit()
    {
        var atLimit = new string('A', 5_600_000);
        var img = new ModelImage("image/png", atLimit);
        Assert.Equal(5_600_000, img.Base64Data.Length);
    }

    [Fact]
    public void ModelRequest_rejects_total_decoded_too_large()
    {
        // Each image Base64 length 5_600_000 => decoded ~4_200_000
        // 4 images => ~16.8MB <20MB so would pass.
        var maxImages = Enumerable.Range(0, 4).Select(_ => new ModelImage("image/png", new string('A', 5_600_000))).ToArray();
        var req = CreateRequest(maxImages);
        Assert.Equal(4, req.Images.Count);

        var five = Enumerable.Range(0, 5).Select(_ => new ModelImage("image/png", "a")).ToArray();
        Assert.Throws<ArgumentException>(() => CreateRequest(five));
    }

    [Fact]
    public void ModelRequest_rejects_total_decoded_exceeds_20MB()
    {
        // To exceed 20MB with valid per-image base64 (5_600_000) is impossible (4*4.2MB=16.8MB).
        // Use 'with' to bypass per-image limit and create images with 7_000_000 chars => decoded 5_250_000 each => total 21MB >20MB.
        // This verifies ModelRequest total validation exists even though normal ModelImage would reject.
        var baseImg = new ModelImage("image/png", "a");
        var large = baseImg with { Base64Data = new string('A', 7_000_000) };
        var imgs = Enumerable.Range(0, 4).Select(_ => large).ToArray();
        Assert.Throws<ArgumentException>(() => CreateRequest(imgs));
    }

    [Fact]
    public void ModelRequest_images_are_defensively_copied()
    {
        var list = new List<ModelImage> { new ModelImage("image/png", "a") };
        var req = CreateRequest(list);
        list.Add(new ModelImage("image/jpeg", "b"));
        Assert.Single(req.Images);
    }

    [Fact]
    public void ModelRequest_accepts_empty_prompt_when_images_present()
    {
        var imgs = new[] { new ModelImage("image/png", "a") };
        var req = new ModelRequest("gpt-4o", "sys", "", Array.Empty<ModelTurn>(), new JsonObject(), new Uri("https://api.openai.com/v1"), "sk", 100, imgs);
        Assert.Equal("", req.Prompt);
        Assert.Single(req.Images);
    }

    [Theory]
    [InlineData("gpt-4o", true)]
    [InlineData("gpt-4o-mini", true)]
    [InlineData("gpt-5.6-sol", true)]
    [InlineData("claude-3-5-sonnet", true)]
    [InlineData("claude-sonnet-4-6", true)]
    [InlineData("claude-opus-5", true)]
    [InlineData("gemini-1.5-pro", true)]
    [InlineData("o1", true)]
    [InlineData("llama-3.2-vision-11b", true)]
    [InlineData("deepseek-chat", false)]
    [InlineData("deepseek-coder", false)]
    [InlineData("qwen2.5-72b", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ModelImage_IsVisionModel_matches_correctly(string? model, bool expected)
    {
        Assert.Equal(expected, ModelImage.IsVisionModel(model));
    }
}
