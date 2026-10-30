using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using RuApp.Models;

namespace RuApp.Services
{
    /// <summary>One model row as the editor holds it, before validation.</summary>
    public readonly record struct CustomProviderModelDraft(string Id, string ContextWindow);

    /// <summary>The editor's fields, before validation.</summary>
    public sealed record CustomProviderDraft(
        string Id,
        string DisplayName,
        string BaseUrl,
        string Protocol,
        IReadOnlyList<CustomProviderModelDraft> Models);

    /// <summary>
    /// The GUI side of named custom providers (ADR 0034). It mirrors the Host's
    /// rules so the editor can explain a problem before saving; the Host still
    /// validates every save and has the final word.
    /// </summary>
    public static partial class CustomProviderPolicy
    {
        public const string BackendPrefix = "custom:";
        public const string ChatCompletions = "chat_completions";
        public const string AnthropicMessages = "anthropic_messages";
        public const int MinimumContextWindow = 1_024;
        public const int MaximumContextWindow = 10_000_000;
        public const int MaximumModels = 200;
        public const int MaximumProviders = 32;

        private static readonly string[] EndpointSuffixes = ["/chat/completions", "/completions", "/messages", "/models"];

        public static bool IsValidId(string? id) => id is not null && IdPattern().IsMatch(id);

        public static string Backend(string id) => BackendPrefix + id;

        public static bool IsCustomChoice(string? choice) =>
            choice is not null
            && choice.StartsWith(BackendPrefix, StringComparison.OrdinalIgnoreCase)
            && IsValidId(choice[BackendPrefix.Length..]);

        public static string IdOf(string choice) => choice[BackendPrefix.Length..];

        public static string ProtocolLabel(string protocol) =>
            protocol == AnthropicMessages ? "Anthropic Messages" : "OpenAI Chat Completions";

        /// <summary>The base URL the Host will store, or null when it cannot be an endpoint.</summary>
        public static string? NormalizeBaseUrl(string? value)
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
                return null;
            }

            return uri.AbsolutePath is "" or "/" ? text + "/v1" : text;
        }

        /// <summary>Whether a key may go to this URL: https, or http to this computer only.</summary>
        public static bool AllowsKey(string? url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return true;
            if (uri.Scheme == "https") return true;
            if (uri.Scheme != "http") return false;
            var host = uri.IdnHost.Trim('[', ']');
            if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);
        }

        /// <summary>
        /// The first problem with a draft, in the order the form reads, or null when
        /// it can be saved. <paramref name="otherIds"/> are the ids already taken.
        /// </summary>
        public static string? Validate(CustomProviderDraft draft, IEnumerable<string> otherIds)
        {
            ArgumentNullException.ThrowIfNull(draft);
            var id = draft.Id.Trim();
            if (!IsValidId(id))
                return "Provider ID: start with a lowercase letter; use only lowercase letters, digits and hyphens (up to 32).";
            if (otherIds.Contains(id, StringComparer.Ordinal))
                return $"Provider ID: \"{id}\" is already used by another provider.";
            var name = draft.DisplayName.Trim();
            if (name.Length is 0 or > 64 || name.Any(char.IsControl))
                return "Display name: enter 1 to 64 characters.";
            if (NormalizeBaseUrl(draft.BaseUrl) is null)
                return "Base URL: enter an http:// or https:// address, for example http://localhost:11434/v1.";
            if (draft.Protocol is not (ChatCompletions or AnthropicMessages))
                return "API protocol: choose one.";
            if (draft.Models.Count > MaximumModels)
                return $"Models: at most {MaximumModels}.";
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var model in draft.Models)
            {
                var modelId = model.Id.Trim();
                if (modelId.Length is 0 or > 256 || modelId.Any(char.IsControl))
                    return "Models: every model needs an ID of up to 256 characters.";
                if (!seen.Add(modelId))
                    return $"Models: \"{modelId}\" is listed twice.";
                if (ParseContextWindow(model.ContextWindow) is { Valid: false })
                    return $"Models: the context window of \"{modelId}\" must be a whole number from {MinimumContextWindow:N0} to {MaximumContextWindow:N0}, or empty.";
            }

            return null;
        }

        /// <summary>An empty field is no override; anything else must be a whole number in range.</summary>
        public static (bool Valid, int? Tokens) ParseContextWindow(string? text)
        {
            var trimmed = (text ?? string.Empty).Trim().Replace(",", string.Empty).Replace(".", string.Empty);
            if (trimmed.Length == 0) return (true, null);
            return int.TryParse(trimmed, out var tokens) && tokens is >= MinimumContextWindow and <= MaximumContextWindow
                ? (true, tokens)
                : (false, null);
        }

        /// <summary>The stored form of a valid draft.</summary>
        public static CustomProviderDto ToDto(CustomProviderDraft draft) => new()
        {
            Id = draft.Id.Trim(),
            DisplayName = draft.DisplayName.Trim(),
            BaseUrl = NormalizeBaseUrl(draft.BaseUrl) ?? draft.BaseUrl.Trim(),
            Protocol = draft.Protocol,
            Models = draft.Models.Select(model => new CustomProviderModelDto
            {
                Id = model.Id.Trim(),
                ContextWindow = ParseContextWindow(model.ContextWindow).Tokens,
            }).ToList(),
        };

        private static readonly Dictionary<string, string> FailureMessages =
            new(StringComparer.Ordinal)
            {
                ["provider_insecure_endpoint"] =
                    "The key was not sent: this address uses plain http:// to another computer. Use https://, run the server on this computer, or remove the key.",
                ["catalog_unauthorized"] = "The server rejected the key. Check the API key.",
                ["catalog_http_error"] =
                    "The server answered with an error. It may not list its models at /models; add model IDs by hand instead.",
                ["catalog_timeout"] =
                    "The server did not answer within 15 seconds. Check that it is running and the URL is right.",
                ["catalog_unavailable"] =
                    "Could not reach the server. Check that it is running and the URL is right.",
                ["catalog_malformed"] =
                    "The server's model list was not in the expected format. Add model IDs by hand instead.",
                ["catalog_too_large"] =
                    "The server's model list is too large to read. Add model IDs by hand instead.",
                ["catalog_rate_limited"] = "The server is rate limiting requests. Try again shortly.",
                ["provider_not_active"] = "Save the provider before fetching its models.",
            };

        /// <summary>
        /// The codes this editor phrases; a parity test keeps them cataloged in
        /// <see cref="RuAgent.Client.AgentFailureCopy"/> so the run-level table and the
        /// editing surfaces cannot drift apart.
        /// </summary>
        internal static IReadOnlyCollection<string> PhrasedCodes => FailureMessages.Keys;

        /// <summary>What a Host failure code means for someone editing a custom provider.</summary>
        public static string FailureMessage(string? code) =>
            code is not null && FailureMessages.TryGetValue(code.Trim(), out var message)
                ? message
                : "Could not save. Check the fields and try again.";

        [GeneratedRegex("^[a-z][a-z0-9-]{0,31}$")]
        private static partial Regex IdPattern();
    }
}
