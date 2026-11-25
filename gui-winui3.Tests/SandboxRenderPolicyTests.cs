using System.Text.Json;
using RuApp.Controls;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

/// <summary>
/// The picture renderer's rules (ADR 0057): it may only reach its own page, its reply
/// is untrusted data, and model text travels to it only as a JSON string.
/// </summary>
public sealed class SandboxRenderPolicyTests
{
    private static readonly byte[] TinyPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=");

    private static string Result(long id, object? png = null, double width = 10, double height = 5, double depth = 0, bool ok = true) =>
        JsonSerializer.Serialize(new
        {
            type = "result",
            id,
            ok,
            png = png ?? "data:image/png;base64," + Convert.ToBase64String(TinyPng),
            width,
            height,
            depth,
        });

    [Theory]
    [InlineData("https://ru-render.local/render.html", true)]
    [InlineData("https://ru-render.local/mermaid.min.js", true)]
    [InlineData("https://RU-RENDER.local/tex-svg.js", true)]
    [InlineData("http://ru-render.local/render.html", false)]
    [InlineData("https://ru-render.local:8443/render.html", false)]
    [InlineData("https://ru-render.local.evil.example/render.html", false)]
    [InlineData("https://user@ru-render.local/render.html", false)]
    [InlineData("https://example.com/x.png", false)]
    [InlineData("data:image/png;base64,AAAA", false)]
    [InlineData("file:///C:/Windows/win.ini", false)]
    [InlineData(null, false)]
    public void Only_the_renderer_host_may_be_fetched(string? uri, bool allowed) =>
        Assert.Equal(allowed, SandboxRenderPolicy.IsRendererResource(uri));

    [Theory]
    [InlineData("data:image/svg+xml;charset=utf-8,%3Csvg%3E", true)]
    [InlineData("https://ru-render.local/tex-svg.js", true)]
    [InlineData("https://example.com/pixel.png", false)]
    [InlineData("blob:https://ru-render.local/1", false)]
    [InlineData("file:///C:/secret.txt", false)]
    public void The_web_view_loads_its_host_and_in_memory_images_only(string uri, bool allowed) =>
        Assert.Equal(allowed, SandboxRenderPolicy.MayLoad(uri));

    [Theory]
    [InlineData("https://ru-render.local/render.html", true)]
    [InlineData("https://ru-render.local/mermaid.min.js", false)]
    [InlineData("https://example.com/render.html", false)]
    public void Only_the_renderer_page_may_be_navigated_to(string uri, bool allowed) =>
        Assert.Equal(allowed, SandboxRenderPolicy.IsRendererPage(uri));

    [Fact]
    public void Source_travels_as_a_json_string()
    {
        const string source = "graph TD\n A[\"</script><script>alert(1)</script>\"] --> B";
        string request = SandboxRenderPolicy.Request(7, SandboxRenderKind.Mermaid, source,
            new SandboxRenderStyle(true, "#ABCDEF", 14));

        using var document = JsonDocument.Parse(request);
        var root = document.RootElement;
        Assert.Equal(7, root.GetProperty("id").GetInt64());
        Assert.Equal("mermaid", root.GetProperty("kind").GetString());
        Assert.Equal(source, root.GetProperty("source").GetString());
        Assert.True(root.GetProperty("dark").GetBoolean());
        Assert.Equal("#ABCDEF", root.GetProperty("ink").GetString());
    }

    [Theory]
    [InlineData("#0f172a", "#0f172a")]
    [InlineData("red", "#000000")]
    [InlineData("#12345g", "#000000")]
    [InlineData("#123456;background:url(x)", "#000000")]
    [InlineData(null, "#000000")]
    public void Ink_is_a_plain_hex_color(string? ink, string expected) =>
        Assert.Equal(expected, SandboxRenderPolicy.NormalizeInk(ink));

    [Theory]
    [InlineData(SandboxRenderKind.Mermaid, 50_000, true)]
    [InlineData(SandboxRenderKind.Mermaid, 50_001, false)]
    [InlineData(SandboxRenderKind.Math, 4_001, false)]
    [InlineData(SandboxRenderKind.Svg, 1024 * 1024, true)]
    public void Sources_are_bounded(SandboxRenderKind kind, int length, bool accepted) =>
        Assert.Equal(accepted, SandboxRenderPolicy.Accepts(kind, new string('x', length)));

    [Fact]
    public void A_blank_source_is_not_rendered() =>
        Assert.False(SandboxRenderPolicy.Accepts(SandboxRenderKind.Math, "  "));

    [Fact]
    public void A_well_formed_result_becomes_a_picture()
    {
        var picture = SandboxRenderPolicy.ReadResult(Result(3, width: 120, height: 40, depth: 4), 3);

        Assert.NotNull(picture);
        Assert.Equal(TinyPng, picture!.Png);
        Assert.Equal(120, picture.Width);
        Assert.Equal(40, picture.Height);
        Assert.Equal(4, picture.Depth);
    }

    [Fact]
    public void A_result_for_another_request_is_ignored() =>
        Assert.Null(SandboxRenderPolicy.ReadResult(Result(4), 3));

    [Fact]
    public void A_failed_result_is_no_picture() =>
        Assert.Null(SandboxRenderPolicy.ReadResult(Result(3, ok: false), 3));

    [Theory]
    [InlineData("data:image/svg+xml;base64,PHN2Zz4=")]
    [InlineData("https://example.com/a.png")]
    [InlineData("data:image/png;base64,not base64!")]
    [InlineData("data:image/png;base64,R0lGODlhAQABAAAAACw=")]
    public void Only_a_png_data_url_is_accepted(string png) =>
        Assert.Null(SandboxRenderPolicy.ReadResult(Result(3, png: png), 3));

    [Theory]
    [InlineData(0, 10)]
    [InlineData(10, -1)]
    [InlineData(5000, 10)]
    public void Picture_sizes_are_bounded(double width, double height) =>
        Assert.Null(SandboxRenderPolicy.ReadResult(Result(3, width: width, height: height), 3));

    [Fact]
    public void A_depth_larger_than_the_picture_is_dropped() =>
        Assert.Equal(0, SandboxRenderPolicy.ReadResult(Result(3, height: 10, depth: 50), 3)!.Depth);

    [Theory]
    [InlineData("{\"type\":\"result\",\"ok\":false,\"error\":\"render_image_timeout\"}", "render_image_timeout")]
    [InlineData("{\"type\":\"result\",\"ok\":false,\"error\":\"<b>Parse error on line 2</b>\"}", "render_failed")]
    [InlineData("{\"type\":\"result\",\"ok\":false}", "render_failed")]
    [InlineData("nope", "render_failed")]
    public void Only_a_plain_error_code_reaches_the_trace(string json, string code) =>
        Assert.Equal(code, SandboxRenderPolicy.ErrorCode(json));

    [Theory]
    [InlineData("{\"type\":\"ready\"}", true)]
    [InlineData("{\"type\":\"result\",\"id\":1}", false)]
    [InlineData("not json", false)]
    [InlineData("\"ready\"", false)]
    public void Ready_is_a_typed_message(string json, bool ready) =>
        Assert.Equal(ready, SandboxRenderPolicy.IsReady(json));

    [Fact]
    public void The_cache_key_covers_every_input()
    {
        var style = new SandboxRenderStyle(false, "#000000", 14);
        string key = SandboxRenderPolicy.CacheKey(SandboxRenderKind.Math, "x", style);

        Assert.Equal(key, SandboxRenderPolicy.CacheKey(SandboxRenderKind.Math, "x", style));
        Assert.NotEqual(key, SandboxRenderPolicy.CacheKey(SandboxRenderKind.Math, "y", style));
        Assert.NotEqual(key, SandboxRenderPolicy.CacheKey(SandboxRenderKind.Mermaid, "x", style));
        Assert.NotEqual(key, SandboxRenderPolicy.CacheKey(SandboxRenderKind.Math, "x", style with { Dark = true }));
        Assert.NotEqual(key, SandboxRenderPolicy.CacheKey(SandboxRenderKind.Math, "x", style with { Ink = "#ffffff" }));
        Assert.NotEqual(key, SandboxRenderPolicy.CacheKey(SandboxRenderKind.Math, "x", style with { FontSize = 16 }));
        Assert.NotEqual(key, SandboxRenderPolicy.CacheKey(SandboxRenderKind.Math, "x", style with { Display = true }));
    }

    [Theory]
    [InlineData("mermaid", true)]
    [InlineData("Mermaid", true)]
    [InlineData("mermaid {theme=dark}", true)]
    [InlineData("mermaidjs", false)]
    [InlineData("python", false)]
    [InlineData(null, false)]
    public void Only_a_mermaid_fence_is_drawn(string? info, bool drawn) =>
        Assert.Equal(drawn, MarkdownPictures.IsMermaid(info));

    [Theory]
    [InlineData(0, 3, true, true)]
    [InlineData(2, 3, true, false)]
    [InlineData(2, 3, false, true)]
    public void The_streaming_tail_waits_until_the_reply_ends(int index, int count, bool streaming, bool settled) =>
        Assert.Equal(settled, MarkdownPictures.IsSettled(index, count, streaming));

    [Fact]
    public void A_formula_written_mid_reply_is_drawn_once_the_next_block_arrives()
    {
        // "$x$" was the streaming tail when built; a second block arrived and kept its key.
        var built = new[] { (Settled: false, Source: "Area is $x^2$.") };

        int retained = MarkdownPictures.Retained(1, built, index => MarkdownPictures.IsSettled(index, 2, streaming: true));

        Assert.Equal(0, retained);
    }

    [Fact]
    public void Settled_blocks_and_plain_text_are_kept()
    {
        var built = new[] { (Settled: true, Source: "$x$"), (Settled: false, Source: "plain prose") };

        Assert.Equal(2, MarkdownPictures.Retained(2, built, _ => true));
    }

    [Fact]
    public void The_streaming_tail_itself_is_not_rebuilt_while_it_streams()
    {
        var built = new[] { (Settled: true, Source: "intro"), (Settled: false, Source: "```mermaid\ngraph TD\n```") };

        Assert.Equal(2, MarkdownPictures.Retained(2, built, index => MarkdownPictures.IsSettled(index, 2, streaming: true)));
    }

    [Theory]
    [InlineData(300, 500, 300)]
    [InlineData(600, 500, 500)]
    [InlineData(600, 300, 450)]
    [InlineData(600, 0, 600)]
    public void A_diagram_fits_its_column_but_keeps_its_labels_readable(double natural, double available, double width) =>
        Assert.Equal(width, MarkdownPictures.DiagramWidth(natural, available));

    [Fact]
    public void The_renderer_has_its_own_unthrottled_profile()
    {
        Assert.Equal(Path.Combine("C:\\Local", "Ru", "WebView2Renderer"), SandboxRenderPolicy.UserDataFolder("C:\\Local"));
        Assert.Contains("--disable-background-timer-throttling", SandboxRenderPolicy.BrowserArguments, StringComparison.Ordinal);
        Assert.Contains("--disable-renderer-backgrounding", SandboxRenderPolicy.BrowserArguments, StringComparison.Ordinal);
    }

    [Fact]
    public void Ink_comes_from_the_reader_color() =>
        Assert.Equal("#0f172a", MarkdownPictures.Ink(15, 23, 42));
}
