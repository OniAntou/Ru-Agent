using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace RuAgent.Client;

/// <summary>One file an export produced.</summary>
public sealed record SessionExportedFile(string Name, string Content);

/// <summary>The files a session leaves as, in the format that was asked for.</summary>
public sealed record SessionExportResult(
    string Harness,
    string DisplayName,
    string? WorkingDirectory,
    DateTimeOffset CreatedAt,
    IReadOnlyList<SessionExportedFile> Files);

/// <summary>What an import produced, including what could not be stored.</summary>
public sealed record SessionImportResult(
    string SessionId,
    string? Title,
    int Messages,
    int DroppedEntries);

/// <summary>
/// Moves a session between Ru and another coding agent.
/// </summary>
/// <remarks>
/// The format knowledge lives on the host and in <c>RuAgent.SessionInterchange</c>; this
/// client only carries the request and reads the envelope back. A refusal the host made —
/// unknown format, a file that is not the format it claims, a session that does not exist —
/// arrives as a normal <c>{"status":"error"}</c> body and is raised here as
/// <see cref="AgentApiException"/> with the host's own message, because that message is
/// the useful part.
/// </remarks>
public sealed class SessionInterchangeApiClient
{
    private readonly HttpClient _httpClient;
    private readonly PublicHostSession _session;

    public SessionInterchangeApiClient(PublicHostSession session, HttpClient? httpClient = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _httpClient = httpClient ?? new HttpClient();
    }

    /// <summary>Asks the host to write a session out in another agent's format.</summary>
    public async Task<SessionExportResult> ExportAsync(
        string sessionId,
        string harness,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(harness);

        var url = new Uri(
            _session.BaseUri,
            $"sessions/{Uri.EscapeDataString(sessionId)}/export?harness={Uri.EscapeDataString(harness)}");
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _session.BearerToken);

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
        using var document = Parse(body);
        var root = document.RootElement;
        ThrowIfError(root, "session_export_failed");

        var files = new List<SessionExportedFile>();
        if (root.TryGetProperty("files", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var file in array.EnumerateArray())
            {
                files.Add(new SessionExportedFile(
                    file.GetProperty("name").GetString() ?? string.Empty,
                    file.GetProperty("content").GetString() ?? string.Empty));
            }
        }

        if (files.Count == 0)
        {
            throw new AgentApiException("session_export_empty", "The host returned an export with no files.");
        }

        var session = root.GetProperty("session");
        return new SessionExportResult(
            root.GetProperty("harness").GetString() ?? harness,
            root.TryGetProperty("display_name", out var displayName) ? displayName.GetString() ?? harness : harness,
            session.TryGetProperty("working_directory", out var directory) ? directory.GetString() : null,
            session.TryGetProperty("created_at", out var createdAt)
                && createdAt.ValueKind == JsonValueKind.String
                && createdAt.TryGetDateTimeOffset(out var stamp)
                    ? stamp
                    : default,
            files);
    }

    /// <summary>Hands the host a session file from another agent.</summary>
    public async Task<SessionImportResult> ImportAsync(
        string harness,
        string content,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(harness);
        ArgumentException.ThrowIfNullOrWhiteSpace(content);

        var url = new Uri(_session.BaseUri, "sessions/import");
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { harness, content }),
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _session.BearerToken);

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
        using var document = Parse(body);
        var root = document.RootElement;
        ThrowIfError(root, "session_import_failed");

        var session = root.GetProperty("session");
        return new SessionImportResult(
            session.GetProperty("id").GetString() ?? string.Empty,
            session.TryGetProperty("title", out var title) ? title.GetString() : null,
            session.TryGetProperty("messages", out var messages) ? messages.GetInt32() : 0,
            root.TryGetProperty("dropped_entries", out var dropped) ? dropped.GetInt32() : 0);
    }

    private static async Task<string> ReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new AgentApiException("unauthorized", "Bearer rejected.", (int)response.StatusCode);
        }

        if (!response.IsSuccessStatusCode)
        {
            // A 413 or a bounded validation failure carries the useful sentence in its
            // body; flattening it to "HTTP 413" would throw that away.
            throw new AgentApiException(
                "session_interchange_failed",
                await ReadRefusalAsync(response, cancellationToken).ConfigureAwait(false),
                (int)response.StatusCode);
        }

        return await BoundedHttpResponseReader.ReadAsync(response.Content, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The host's own words for a non-success response, read through the bounded reader
    /// and reduced to one line; the status is the fallback when the body has none.
    /// </summary>
    private static async Task<string> ReadRefusalAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        const int MaxRefusalChars = 200;
        try
        {
            var body = await BoundedHttpResponseReader.ReadAsync(response.Content, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(body))
            {
                using var document = JsonDocument.Parse(body);
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in new[] { "message", "error_code", "detail" })
                    {
                        if (document.RootElement.TryGetProperty(property, out var value)
                            && value.ValueKind == JsonValueKind.String
                            && !string.IsNullOrWhiteSpace(value.GetString()))
                        {
                            var text = value.GetString()!;
                            return text.Length <= MaxRefusalChars ? text : text[..(MaxRefusalChars - 1)] + "…";
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // No usable body; the status below is still bounded.
        }

        return $"HTTP {(int)response.StatusCode}";
    }

    private static JsonDocument Parse(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            throw new AgentApiException("invalid_response", "Empty response");
        }

        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException error)
        {
            throw new AgentApiException("invalid_response", error.Message);
        }
    }

    /// <summary>
    /// Raises the host's refusal as an error carrying the host's own words.
    /// </summary>
    private static void ThrowIfError(JsonElement root, string fallbackCode)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new AgentApiException("invalid_response", "Root must be object");
        }

        if (!root.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.String)
        {
            throw new AgentApiException("invalid_response", "Missing status");
        }

        if (!string.Equals(status.GetString(), "ok", StringComparison.Ordinal))
        {
            var message = root.TryGetProperty("message", out var text) ? text.GetString() : null;
            throw new AgentApiException(fallbackCode, message ?? "The host refused the request.");
        }
    }
}
