using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RuApp.Services;

public enum SandboxRenderKind
{
    Mermaid,
    Math,
    Svg,
}

/// <summary>How a picture should look: the reader's theme, text color and size.</summary>
public readonly record struct SandboxRenderStyle(bool Dark, string Ink, double FontSize, bool Display = false);

/// <summary>A decoded PNG and its size in device-independent pixels; Depth is how far math sits below the baseline.</summary>
public sealed record SandboxPicture(byte[] Png, double Width, double Height, double Depth);

/// <summary>
/// The rules around the picture renderer (ADR 0057), kept apart from WebView2 so they
/// are testable. The renderer page is the only document the web view may show, the
/// only origin it may fetch from, and the only sender whose reply is read. A reply is
/// untrusted data: it must name the request it answers and carry a bounded PNG.
/// </summary>
public static class SandboxRenderPolicy
{
    public const string HostName = "ru-render.local";
    public const string Origin = "https://" + HostName + "/";
    public const string PageUrl = Origin + "render.html";

    /// <summary>One render may take this long before the request fails and the source shows.</summary>
    public static readonly TimeSpan RenderTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The page and its vendored libraries must say they are ready within this time.</summary>
    public static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(20);

    public const int MaxSide = 4096;
    public const int MaxPngBase64Chars = 24 * 1024 * 1024;
    public const int MaxCachedPictures = 64;

    private const string PngPrefix = "data:image/png;base64,";
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static int MaxSourceChars(SandboxRenderKind kind) => kind switch
    {
        SandboxRenderKind.Mermaid => 50_000,
        SandboxRenderKind.Math => 4_000,
        _ => 1024 * 1024,
    };

    public static string WireKind(SandboxRenderKind kind) => kind switch
    {
        SandboxRenderKind.Mermaid => "mermaid",
        SandboxRenderKind.Math => "math",
        _ => "svg",
    };

    /// <summary>True only for a resource on the renderer's own virtual host.</summary>
    public static bool IsRendererResource(string? uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var parsed)
        && parsed.Scheme == Uri.UriSchemeHttps
        && string.Equals(parsed.Host, HostName, StringComparison.OrdinalIgnoreCase)
        && parsed.IsDefaultPort
        && string.IsNullOrEmpty(parsed.UserInfo);

    /// <summary>
    /// What the web view may load: its own host, plus the in-memory <c>data:</c> images
    /// the page draws through. A data URL reaches no network and names no file.
    /// </summary>
    public static bool MayLoad(string? uri) =>
        IsRendererResource(uri) || (uri is not null && uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase));

    /// <summary>The page itself, not another file on the host: the only navigation allowed.</summary>
    public static bool IsRendererPage(string? uri) =>
        IsRendererResource(uri)
        && Uri.TryCreate(uri, UriKind.Absolute, out var parsed)
        && string.Equals(parsed.AbsolutePath, "/render.html", StringComparison.OrdinalIgnoreCase);

    public static bool Accepts(SandboxRenderKind kind, string? source) =>
        !string.IsNullOrWhiteSpace(source) && source.Length <= MaxSourceChars(kind);

    public static string CacheKey(SandboxRenderKind kind, string source, SandboxRenderStyle style)
    {
        string material = string.Join('\n',
            WireKind(kind),
            style.Dark ? "dark" : "light",
            style.Ink,
            style.FontSize.ToString("0.##", CultureInfo.InvariantCulture),
            style.Display ? "display" : "inline",
            source);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    /// <summary>The request the page receives: the source travels as a JSON string, never as markup.</summary>
    public static string Request(long id, SandboxRenderKind kind, string source, SandboxRenderStyle style) =>
        JsonSerializer.Serialize(new
        {
            id,
            kind = WireKind(kind),
            source,
            dark = style.Dark,
            ink = NormalizeInk(style.Ink),
            fontSize = Math.Clamp(style.FontSize, 8, 48),
            display = style.Display,
        });

    /// <summary>A "#rrggbb" color, or black when the value is anything else.</summary>
    public static string NormalizeInk(string? ink) =>
        ink is { Length: 7 } && ink[0] == '#' && ink.Skip(1).All(Uri.IsHexDigit) ? ink : "#000000";

    public static bool IsReady(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("type", out var type)
                && type.ValueKind == JsonValueKind.String
                && type.GetString() == "ready";
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>The request id a result answers, or null when the message is not a result.</summary>
    public static long? ResultId(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "result"
                && root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out long value)
                ? value
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads a result for <paramref name="expectedId"/>. Anything malformed, oversized,
    /// for another request or not a PNG is a failure; the caller then shows the source.
    /// </summary>
    public static SandboxPicture? ReadResult(string json, long expectedId)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            if (ResultId(json) != expectedId
                || !root.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True
                || !root.TryGetProperty("png", out var pngProperty) || pngProperty.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            string png = pngProperty.GetString() ?? string.Empty;
            if (!png.StartsWith(PngPrefix, StringComparison.Ordinal) || png.Length - PngPrefix.Length > MaxPngBase64Chars)
            {
                return null;
            }

            double width = Dimension(root, "width"), height = Dimension(root, "height");
            if (width <= 0 || height <= 0) return null;
            double depth = root.TryGetProperty("depth", out var depthProperty) && depthProperty.ValueKind == JsonValueKind.Number
                ? depthProperty.GetDouble()
                : 0;
            if (!double.IsFinite(depth) || Math.Abs(depth) > height) depth = 0;

            byte[] bytes = Convert.FromBase64String(png[PngPrefix.Length..]);
            if (bytes.Length < PngSignature.Length || !bytes.AsSpan(0, PngSignature.Length).SequenceEqual(PngSignature))
            {
                return null;
            }

            return new SandboxPicture(bytes, width, height, depth);
        }
        catch (Exception error) when (error is JsonException or FormatException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// The page's failure code for the trace, when it is a plain bounded identifier;
    /// anything else reads as "render_failed", so page text never reaches the log.
    /// </summary>
    public static string ErrorCode(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 4 });
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.String
                && error.GetString() is { Length: > 0 and <= 40 } code
                && code.All(character => character is (>= 'a' and <= 'z') or '_')
                    ? code
                    : "render_failed";
        }
        catch (JsonException)
        {
            return "render_failed";
        }
    }

    private static double Dimension(JsonElement root, string name) =>
        root.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number
        && property.GetDouble() is var value && double.IsFinite(value) && value > 0 && value <= MaxSide
            ? value
            : 0;
}
