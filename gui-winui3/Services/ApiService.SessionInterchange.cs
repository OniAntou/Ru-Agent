using System.Net.Http.Json;
using System.Text.Json;
using RuAgent.SessionInterchange;

namespace RuApp.Services;

/// <summary>
/// Handing a session over to another coding agent, from the GUI.
/// </summary>
/// <remarks>
/// The files land where that agent keeps its sessions — the same place the CLI puts
/// them, resolved by the same <see cref="SessionFileLayout"/> — so the two surfaces
/// cannot drift into writing the same session to two different directories.
/// </remarks>
public partial class ApiService
{
    /// <summary>Where an export wrote, or why it wrote nothing.</summary>
    public sealed record SessionExportOutcome(bool Ok, IReadOnlyList<string> Paths, string? Error);

    /// <summary>The session an import produced, or why it produced none.</summary>
    public sealed record SessionImportOutcome(
        bool Ok,
        string? SessionId,
        int Messages,
        int DroppedEntries,
        string? Error);

    /// <summary>
    /// Writes a session out in another agent's format.
    /// </summary>
    public async Task<SessionExportOutcome> ExportSessionAsync(
        string sessionId,
        string harness,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _httpClient
                .GetAsync($"{_baseUrl}/sessions/{sessionId}/export?harness={Uri.EscapeDataString(harness)}", cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new SessionExportOutcome(false, [], $"HTTP {(int)response.StatusCode}");
            }

            var result = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken).ConfigureAwait(false);
            if (!result.TryGetProperty("status", out var status) || status.GetString() != "ok")
            {
                var message = result.TryGetProperty("message", out var text) ? text.GetString() : null;
                return new SessionExportOutcome(false, [], message ?? "The host refused the export.");
            }

            var fileNames = new List<string>();
            var contents = new List<string>();
            if (result.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array)
            {
                foreach (var file in files.EnumerateArray())
                {
                    fileNames.Add(file.GetProperty("name").GetString() ?? string.Empty);
                    contents.Add(file.GetProperty("content").GetString() ?? string.Empty);
                }
            }

            if (fileNames.Count == 0)
            {
                return new SessionExportOutcome(false, [], "The host returned an export with no files.");
            }

            var session = result.GetProperty("session");
            var workingDirectory = session.TryGetProperty("working_directory", out var directory)
                ? directory.GetString()
                : null;
            var createdAt = session.TryGetProperty("created_at", out var createdAtValue)
                && createdAtValue.ValueKind == JsonValueKind.String
                && createdAtValue.TryGetDateTimeOffset(out var stamp)
                    ? stamp
                    : default;

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(home))
            {
                return new SessionExportOutcome(false, [], "Cannot locate the user profile directory.");
            }

            IReadOnlyList<SessionFileTarget> targets;
            try
            {
                targets = SessionFileLayout.TargetsFor(
                    result.TryGetProperty("harness", out var resolved) ? resolved.GetString() ?? harness : harness,
                    fileNames,
                    workingDirectory,
                    createdAt,
                    home);
            }
            catch (ArgumentException error)
            {
                // A format whose home is unknown is refused before anything is written,
                // rather than dropped into a directory that merely looks plausible.
                return new SessionExportOutcome(false, [], error.Message);
            }

            var written = new List<string>(targets.Count);
            for (var index = 0; index < targets.Count && index < contents.Count; index++)
            {
                var target = targets[index];
                var parent = Path.GetDirectoryName(target.Path);
                if (!string.IsNullOrEmpty(parent))
                {
                    Directory.CreateDirectory(parent);
                }

                if (target.Append && File.Exists(target.Path))
                {
                    await File.AppendAllTextAsync(target.Path, contents[index], cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await File.WriteAllTextAsync(target.Path, contents[index], cancellationToken).ConfigureAwait(false);
                }

                written.Add(target.Path);
            }

            return new SessionExportOutcome(true, written, null);
        }
        catch (Exception ex)
        {
            LogApiError("ExportSessionAsync", ex);
            return new SessionExportOutcome(false, [], ex.Message);
        }
    }

    /// <summary>
    /// Turns another agent's session file into a new Ru session.
    /// </summary>
    /// <remarks>
    /// What the host could not store comes back as a count rather than being hidden: Ru's
    /// session log holds turns, so another agent's tool traffic has nowhere to land and
    /// the person who asked for the import gets told how much stayed behind.
    /// </remarks>
    public async Task<SessionImportOutcome> ImportSessionAsync(
        string harness,
        string content,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _httpClient
                .PostAsJsonAsync($"{_baseUrl}/sessions/import", new { harness, content }, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new SessionImportOutcome(false, null, 0, 0, $"HTTP {(int)response.StatusCode}");
            }

            var result = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken).ConfigureAwait(false);
            if (!result.TryGetProperty("status", out var status) || status.GetString() != "ok")
            {
                var message = result.TryGetProperty("message", out var text) ? text.GetString() : null;
                return new SessionImportOutcome(false, null, 0, 0, message ?? "The host refused the import.");
            }

            var session = result.GetProperty("session");
            return new SessionImportOutcome(
                true,
                session.TryGetProperty("id", out var id) ? id.GetString() : null,
                session.TryGetProperty("messages", out var messages) ? messages.GetInt32() : 0,
                result.TryGetProperty("dropped_entries", out var dropped) ? dropped.GetInt32() : 0,
                null);
        }
        catch (Exception ex)
        {
            LogApiError("ImportSessionAsync", ex);
            return new SessionImportOutcome(false, null, 0, 0, ex.Message);
        }
    }
}
