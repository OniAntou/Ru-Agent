using System.Net;
using RuAgent.Runtime.Adapters.Bootstrap;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RuBackend.Business.Settings;

namespace RuBackend.Providers;

/// <summary>One model a user listed for a custom provider.</summary>
public sealed record CustomProviderModel(string Id, int? ContextWindow);

/// <summary>A user-described OpenAI- or Anthropic-compatible endpoint (ADR 0034).</summary>
public sealed record CustomProviderDefinition(
    string Id,
    string DisplayName,
    string BaseUrl,
    string Protocol,
    IReadOnlyList<CustomProviderModel> Models)
{
    public string Backend => CustomProviders.BackendPrefix + Id;
    public string Account => CustomProviders.CredentialAccount(Id);
}

/// <summary>
/// Named custom providers (ADR 0034): their settings shape, identity and the
/// rule that keeps a key off plain HTTP to another host.
/// </summary>
/// <remarks>
/// Backend and alias share the <c>custom:</c> prefix so a user id such as
/// <c>anthropic</c> never matches a first-party branch, and the credential
/// account has its own namespace so it cannot collide with a catalog provider's
/// <c>&lt;id&gt;_api_key</c> or the legacy <c>custom_api_key</c>.
/// </remarks>
public static partial class CustomProviders
{
    public const string SettingField = "custom_providers";
    public const string BackendPrefix = "custom:";
    public const string InsecureEndpointCode = "provider_insecure_endpoint";
    public const int MaximumProviders = 32;
    public const int MaximumModels = 200;
    public const int MinimumContextWindow = 1_024;
    public const int MaximumContextWindow = 10_000_000;

    private static readonly string[] EndpointSuffixes = ["/chat/completions", "/completions", "/messages", "/models"];

    public static string CredentialAccount(string id) => "custom_provider_" + id + "_key";

    public static bool IsValidId(string? id) => id is not null && IdPattern().IsMatch(id);

    /// <summary>The canonical <c>custom:&lt;id&gt;</c> backend, or null when the value is not one.</summary>
    public static string? NormalizeBackend(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        if (!trimmed.StartsWith(BackendPrefix, StringComparison.OrdinalIgnoreCase)) return null;
        var id = trimmed[BackendPrefix.Length..].Trim().ToLowerInvariant();
        return IsValidId(id) ? BackendPrefix + id : null;
    }

    public static bool IsCustomBackend(string? value) => NormalizeBackend(value) is not null;

    public static string IdOf(string backend) =>
        NormalizeBackend(backend)?[BackendPrefix.Length..] ?? throw new ArgumentException("Not a custom provider backend.");

    /// <summary>The stored definitions. A stored list that no longer validates reads as empty: fail closed.</summary>
    public static IReadOnlyList<CustomProviderDefinition> Read(SettingsSnapshot settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Raw[SettingField] is not JsonArray stored) return [];
        try
        {
            return Parse(stored);
        }
        catch (ArgumentException)
        {
            return [];
        }
    }

    public static CustomProviderDefinition? Find(SettingsSnapshot settings, string backend)
    {
        if (NormalizeBackend(backend) is not { } normalized) return null;
        var id = normalized[BackendPrefix.Length..];
        return Read(settings).FirstOrDefault(definition => definition.Id == id);
    }

    /// <summary>Applies a settings save's list; any invalid entry rejects the whole payload.</summary>
    public static void Apply(JsonObject payload, JsonObject raw)
    {
        if (!payload.TryGetPropertyValue(SettingField, out var node)) return;
        if (node is not JsonArray list) throw new ArgumentException("custom_providers must be an array.");
        raw[SettingField] = ToJson(Parse(list));
    }

    /// <summary>
    /// Trims, strips a pasted endpoint path and adds <c>/v1</c> to a bare host, so
    /// a URL copied from a server's docs reaches both routes. Throws for a value
    /// that is not an absolute http(s) URL without user info, query or fragment.
    /// </summary>
    public static string NormalizeBaseUrl(string value)
    {
        var text = (value ?? string.Empty).Trim().TrimEnd('/');
        foreach (var suffix in EndpointSuffixes)
        {
            if (text.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                text = text[..^suffix.Length].TrimEnd('/');
                break;
            }
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || text.Length > 1024)
        {
            throw new ArgumentException("Invalid custom provider base URL.");
        }

        return uri.AbsolutePath is "" or "/" ? text + "/v1" : text;
    }

    /// <summary>
    /// The legacy slot keeps saving whatever is typed; only a URL that parses is
    /// normalised, so a half-typed value is not rejected mid-edit.
    /// </summary>
    public static string NormalizeLegacyUrl(string value)
    {
        try
        {
            return NormalizeBaseUrl(value);
        }
        catch (ArgumentException)
        {
            return value;
        }
    }

    /// <summary>A key may travel over https, or over http only to this machine.</summary>
    public static bool AllowsCredential(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (string.Equals(endpoint.Scheme, "https", StringComparison.OrdinalIgnoreCase)) return true;
        if (!string.Equals(endpoint.Scheme, "http", StringComparison.OrdinalIgnoreCase)) return false;
        var host = endpoint.IdnHost.Trim('[', ']');
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);
    }

    /// <summary>Fails before a key would leave the machine unencrypted.</summary>
    public static void RequireCredentialTransport(Uri endpoint, string? credential)
    {
        if (!string.IsNullOrEmpty(credential) && !AllowsCredential(endpoint))
            throw new ProviderCatalogException(InsecureEndpointCode);
    }

    private static IReadOnlyList<CustomProviderDefinition> Parse(JsonArray list)
    {
        if (list.Count > MaximumProviders) throw new ArgumentException("Too many custom providers.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<CustomProviderDefinition>(list.Count);
        foreach (var node in list)
        {
            if (node is not JsonObject item) throw new ArgumentException("Invalid custom provider.");
            var id = Text(item["id"]);
            if (!IsValidId(id) || !ids.Add(id!)) throw new ArgumentException("Invalid custom provider id.");
            var name = Text(item["display_name"])?.Trim();
            if (string.IsNullOrEmpty(name) || name.Length > 64 || name.Any(char.IsControl))
                throw new ArgumentException("Invalid custom provider name.");
            var baseUrl = NormalizeBaseUrl(Text(item["base_url"]) ?? string.Empty);
            var protocol = Text(item["protocol"]);
            if (protocol is not (ProviderProtocols.ChatCompletions or ProviderProtocols.AnthropicMessages))
                throw new ArgumentException("Invalid custom provider protocol.");
            result.Add(new CustomProviderDefinition(id!, name, baseUrl, protocol, ParseModels(item["models"])));
        }

        return result;
    }

    private static IReadOnlyList<CustomProviderModel> ParseModels(JsonNode? node)
    {
        if (node is null) return [];
        if (node is not JsonArray models || models.Count > MaximumModels)
            throw new ArgumentException("Invalid custom provider models.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<CustomProviderModel>(models.Count);
        foreach (var entry in models)
        {
            if (entry is not JsonObject model) throw new ArgumentException("Invalid custom provider model.");
            var id = Text(model["id"])?.Trim();
            if (string.IsNullOrEmpty(id) || id.Length > 256 || id.Any(char.IsControl) || !ids.Add(id))
                throw new ArgumentException("Invalid custom provider model id.");
            int? window = null;
            if (model["context_window"] is { } windowNode)
            {
                if (windowNode is not JsonValue value || !value.TryGetValue<int>(out var tokens)
                    || tokens is < MinimumContextWindow or > MaximumContextWindow)
                    throw new ArgumentException("Invalid custom provider context window.");
                window = tokens;
            }

            result.Add(new CustomProviderModel(id, window));
        }

        return result;
    }

    private static JsonArray ToJson(IReadOnlyList<CustomProviderDefinition> definitions)
    {
        var array = new JsonArray();
        foreach (var definition in definitions)
        {
            var models = new JsonArray();
            foreach (var model in definition.Models)
            {
                var entry = new JsonObject { ["id"] = model.Id };
                if (model.ContextWindow is { } window) entry["context_window"] = window;
                models.Add(entry);
            }

            array.Add(new JsonObject
            {
                ["id"] = definition.Id,
                ["display_name"] = definition.DisplayName,
                ["base_url"] = definition.BaseUrl,
                ["protocol"] = definition.Protocol,
                ["models"] = models,
            });
        }

        return array;
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    [GeneratedRegex("^[a-z][a-z0-9-]{0,31}$")]
    private static partial Regex IdPattern();
}
