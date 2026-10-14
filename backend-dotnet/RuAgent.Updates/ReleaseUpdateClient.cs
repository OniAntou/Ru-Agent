using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace RuAgent.Updates;

public sealed record ReleaseUpdate(string Version, string AssetName, Uri DownloadUri, string Sha256, long Size, Uri ReleaseUri);
public sealed record DownloadedUpdate(ReleaseUpdate Release, string InstallerPath);

/// <summary>Reads only Ru releases. No provider credentials or model output enter this client.</summary>
public sealed class ReleaseUpdateClient : IDisposable
{
    public const string Repository = "OniAntou/Ru-Agent";
    public const long MaximumInstallerBytes = 1024L * 1024 * 1024;
    private readonly HttpClient _http;
    public ReleaseUpdateClient(HttpMessageHandler? handler = null)
    {
        _http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(10) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Ru-Update/1.0");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public async Task<ReleaseUpdate?> CheckAsync(InstalledRelease installed, UpdateChannel channel, CancellationToken ct)
    {
        var current = ReleaseVersion.Parse(installed.Version) ?? throw new UpdateException("update_installation_invalid");
        using var response = await _http.GetAsync($"https://api.github.com/repos/{Repository}/releases?per_page=100", HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new UpdateException(response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests ? "update_rate_limited" : "update_check_failed");
        var bytes = await ReadBoundedAsync(response.Content, 2 * 1024 * 1024, ct).ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(bytes);
            return Select(document.RootElement, installed.ProductKind, current, channel);
        }
        catch (JsonException) { throw new UpdateException("update_metadata_invalid"); }
    }

    public static ReleaseUpdate? Select(JsonElement releases, UpdateProduct product, ReleaseVersion current, UpdateChannel channel)
    {
        if (releases.ValueKind != JsonValueKind.Array) throw new UpdateException("update_metadata_invalid");
        ReleaseUpdate? selected = null;
        ReleaseVersion? selectedVersion = null;
        foreach (var release in releases.EnumerateArray())
        {
            if (release.ValueKind != JsonValueKind.Object || !release.TryGetProperty("draft", out var draft) || draft.ValueKind != JsonValueKind.False
                || !release.TryGetProperty("prerelease", out var prerelease) || prerelease.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) continue;
            var version = ReleaseVersion.Parse(Text(release, "tag_name"));
            if (version is null || version.CompareTo(current) <= 0
                || channel == UpdateChannel.Stable && (prerelease.GetBoolean() || version.Prerelease is not null)) continue;
            if (selectedVersion is not null && version.CompareTo(selectedVersion) <= 0) continue;
            var tag = Text(release, "tag_name");
            var name = $"Ru-{(product == UpdateProduct.Desktop ? "Desktop" : "CLI")}-{version}-win-x64-Setup.exe";
            if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) continue;
            var matches = assets.EnumerateArray().Where(asset => Text(asset, "name") == name).ToArray();
            if (matches.Length != 1) continue;
            var asset = matches[0];
            var digest = Text(asset, "digest");
            if (digest is null || !digest.StartsWith("sha256:", StringComparison.Ordinal) || digest.Length != 71
                || !digest[7..].All(Uri.IsHexDigit)) throw new UpdateException("update_digest_missing");
            if (!asset.TryGetProperty("size", out var size) || size.ValueKind != JsonValueKind.Number || !size.TryGetInt64(out var length) || length is <= 0 or > MaximumInstallerBytes)
                throw new UpdateException("update_asset_size_invalid");
            var expected = $"https://github.com/{Repository}/releases/download/{tag}/{name}";
            if (!Uri.TryCreate(Text(asset, "browser_download_url"), UriKind.Absolute, out var url)
                || url.AbsoluteUri != expected) throw new UpdateException("update_asset_origin_invalid");
            selected = new(version.ToString(), name, url, digest[7..].ToLowerInvariant(), length, new Uri($"https://github.com/{Repository}/releases/tag/{tag}"));
            selectedVersion = version;
        }
        return selected;
    }

    public async Task<DownloadedUpdate> DownloadAsync(ReleaseUpdate update, string stagingDirectory, IProgress<double>? progress, CancellationToken ct)
    {
        // Candidates are untrusted even when passed by a caller rather than Select.
        var version = ReleaseVersion.Parse(update.Version);
        if (version is null || !IsTrustedDownloadUri(update.DownloadUri) || update.DownloadUri.Host != "github.com"
            || !update.DownloadUri.AbsolutePath.EndsWith("/" + update.AssetName, StringComparison.Ordinal)
            || update.AssetName != $"Ru-CLI-{version}-win-x64-Setup.exe" && update.AssetName != $"Ru-Desktop-{version}-win-x64-Setup.exe"
            || update.Size is <= 0 or > MaximumInstallerBytes
            || update.Sha256.Length != 64 || !update.Sha256.All(Uri.IsHexDigit)
            || Path.GetFileName(update.AssetName) != update.AssetName || !update.AssetName.EndsWith("-Setup.exe", StringComparison.Ordinal))
            throw new UpdateException("update_metadata_invalid");
        Directory.CreateDirectory(stagingDirectory);
        var path = Path.Combine(stagingDirectory, update.AssetName);
        var partial = path + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            using var response = await OpenDownloadAsync(update.DownloadUri, ct).ConfigureAwait(false);
            if (response.Content.Headers.ContentLength is { } declared && declared != update.Size) throw new UpdateException("update_download_size_mismatch");
            await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                long written = 0;
                int count;
                while ((count = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    written += count;
                    if (written > update.Size) throw new UpdateException("update_download_size_mismatch");
                    await output.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
                    progress?.Report((double)written / update.Size);
                }
                if (written != update.Size) throw new UpdateException("update_download_size_mismatch");
            }
            await using var downloaded = File.OpenRead(partial);
            var digest = Convert.ToHexString(await SHA256.HashDataAsync(downloaded, ct).ConfigureAwait(false));
            if (!digest.Equals(update.Sha256, StringComparison.OrdinalIgnoreCase)) throw new UpdateException("update_digest_mismatch");
            downloaded.Close();
            File.Move(partial, path, overwrite: true);
            return new(update, path);
        }
        finally { if (File.Exists(partial)) File.Delete(partial); }
    }

    private async Task<HttpResponseMessage> OpenDownloadAsync(Uri uri, CancellationToken ct)
    {
        for (int redirects = 0; redirects <= 4; redirects++)
        {
            if (!IsTrustedDownloadUri(uri)) throw new UpdateException("update_asset_origin_invalid");
            var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                var location = response.Headers.Location;
                response.Dispose();
                if (location is null) throw new UpdateException("update_download_failed");
                uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                continue;
            }
            if (!response.IsSuccessStatusCode) { response.Dispose(); throw new UpdateException("update_download_failed"); }
            return response;
        }
        throw new UpdateException("update_redirect_limit");
    }

    public static bool IsTrustedDownloadUri(Uri uri) => uri.IsAbsoluteUri && uri.Scheme == "https" && uri.Port == 443 && uri.UserInfo.Length == 0
        && (uri.Host == "github.com" && uri.AbsolutePath.StartsWith($"/{Repository}/releases/download/", StringComparison.Ordinal)
            || uri.Host is "release-assets.githubusercontent.com" or "objects.githubusercontent.com");

    private static string? Text(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() : null;

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, int limit, CancellationToken ct)
    {
        if (content.Headers.ContentLength > limit) throw new UpdateException("update_metadata_too_large");
        await using var input = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            if (output.Length + count > limit) throw new UpdateException("update_metadata_too_large");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }
    public void Dispose() => _http.Dispose();
}
