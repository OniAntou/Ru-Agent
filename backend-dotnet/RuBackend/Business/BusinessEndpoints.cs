using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using RuAgent.Harness.Core;
using RuAgent.Harness.Persistence;
using RuAgent.Harness.Persistence.Workspace;
using RuBackend.Business.Activity;
using RuBackend.Business.Sessions;
using RuBackend.Business.Settings;
using RuBackend.Conversation;
using RuBackend.Harness;
using RuBackend.Harness.Workspace;
using RuBackend.Providers;

namespace RuBackend.Business;

public static class BusinessEndpoints
{
    private const string DefaultSessionTitle = "Cuộc trò chuyện mới";

    public static void Map(WebApplication app)
    {
        app.MapGet("/settings", GetSettingsAsync);
        app.MapPost("/settings", SaveSettingsAsync);
        app.MapPost("/settings/provider/remove", RemoveProviderAsync);
        app.MapGet("/settings/models-dev-keys", GetModelsDevKeysAsync);
        app.MapGet("/sessions", ListSessionsAsync);
        app.MapPost("/sessions/new", CreateSessionAsync);
        app.MapPost("/sessions/{sessionId}/activate", ActivateSessionAsync);
        app.MapPost("/sessions/{sessionId}/fork", ForkSessionAsync);
        app.MapPost("/sessions/{sessionId}/rewind", RewindSessionAsync);
        app.MapDelete("/sessions/{sessionId}", DeleteSessionAsync);
        app.MapGet("/activity-log", GetActivityAsync);
        app.MapDelete("/activity-log", ClearActivityAsync);
    }

    private static async Task<IResult> GetSettingsAsync(
        ISettingsStore settings,
        ICredentialStore credentials,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = await settings.ReadAsync(cancellationToken).ConfigureAwait(false);
            var hasCustom = await HasCredentialAsync(
                credentials,
                "custom_api_key",
                cancellationToken).ConfigureAwait(false);
            var hasOpenAi = await HasCredentialAsync(
                credentials,
                "openai_api_key",
                cancellationToken).ConfigureAwait(false);
            var hasAnthropic = await HasCredentialAsync(
                credentials,
                "anthropic_api_key",
                cancellationToken).ConfigureAwait(false);
            return Results.Json(ToSettingsResponse(
                snapshot,
                hasCustom,
                hasOpenAi,
                hasAnthropic));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            LogFailure(loggers, "settings read", error);
            return Unavailable("Settings are temporarily unavailable.");
        }
    }

    private static async Task<IResult> SaveSettingsAsync(
        JsonObject payload,
        ISettingsTransitionService transition,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await transition.ApplyAsync(payload, cancellationToken)
                .ConfigureAwait(false);
            return Results.Ok(new
            {
                status = result.Success ? "success" : "error",
                message = result.Message,
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            LogFailure(loggers, "settings save", error);
            return Results.Ok(new
            {
                status = "error",
                message = "Runtime settings could not be applied.",
            });
        }
    }

    /// <summary>
    /// Models.dev provider ids with a stored key (`&lt;id&gt;_api_key`). The
    /// credential store has no enumeration, so each catalog id is probed; the
    /// catalog itself is cached for 24h, keeping this a single cheap call the
    /// GUI makes when Settings loads rather than per keystroke.
    /// </summary>
    private static async Task<IResult> GetModelsDevKeysAsync(
        ProviderCatalogService catalog,
        ICredentialStore credentials,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        try
        {
            var providers = await catalog.GetModelsDevAsync(cancellationToken).ConfigureAwait(false);
            var withKeys = new List<string>();
            foreach (var provider in providers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var account = provider.Id.ToLowerInvariant() + "_api_key";
                if (await HasCredentialAsync(credentials, account, cancellationToken).ConfigureAwait(false))
                {
                    withKeys.Add(provider.Id.ToLowerInvariant());
                }
            }

            return Results.Json(new
            {
                status = "ok",
                provider_ids = withKeys.ToArray(),
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            LogFailure(loggers, "models-dev keys read", error);
            return Unavailable("Settings are temporarily unavailable.");
        }
    }

    private static async Task<IResult> RemoveProviderAsync(
        JsonObject payload,
        ISettingsTransitionService transition,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        try
        {
            var alias = payload["backend_choice"]?.GetValue<string>()?.Trim();
            if (string.IsNullOrEmpty(alias))
            {
                return Results.Ok(new
                {
                    status = "error",
                    message = "Provider alias is required.",
                });
            }

            var result = await transition.RemoveProviderAsync(alias, cancellationToken)
                .ConfigureAwait(false);
            return Results.Ok(new
            {
                status = result.Success ? "success" : "error",
                message = result.Message,
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            LogFailure(loggers, "provider remove", error);
            return Results.Ok(new
            {
                status = "error",
                message = "Provider could not be removed.",
            });
        }
    }

    private static async Task<IResult> ListSessionsAsync(
        ISessionStore sessions,
        HarnessSqliteOptions sqliteOptions,
        SqliteWorkspaceStore workspaceStore,
        WorkspaceTrustService trustService,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        try
        {
            var values = await sessions.ListAsync(cancellationToken).ConfigureAwait(false);
            var workspaces = await ResolveSessionWorkspacesAsync(
                    sqliteOptions,
                    workspaceStore,
                    trustService,
                    cancellationToken).ConfigureAwait(false);
            return Results.Ok(new
            {
                status = "ok",
                sessions = values.Select(value =>
                {
                    if (workspaces.TryGetValue(value.Id, out var workspace))
                    {
                        value = value with
                        {
                            WorkspaceId = workspace.WorkspaceId,
                            WorkspaceLabel = workspace.Label,
                            WorkspacePath = workspace.Path,
                        };
                    }

                    return new
                    {
                        id = value.Id,
                        title = value.Title,
                        created_at = value.CreatedAt.ToUniversalTime().ToString("O"),
                        updated_at = value.UpdatedAt.ToUniversalTime().ToString("O"),
                        last_agent_activity_at = value.LastAgentActivityAt?.ToUniversalTime().ToString("O"),
                        message_count = value.MessageCount,
                        parent_session_id = value.ParentSessionId,
                        forked_from_message_id = value.ForkedFromMessageId,
                        workspace_id = value.WorkspaceId,
                        workspace_label = value.WorkspaceLabel,
                        workspace_path = value.WorkspacePath,
                    };
                }).ToArray(),
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            LogFailure(loggers, "session list", error);
            return Unavailable("Sessions are temporarily unavailable.");
        }
    }

    private static async Task<IReadOnlyDictionary<string, (string WorkspaceId, string Label, string Path)>>
        ResolveSessionWorkspacesAsync(
            HarnessSqliteOptions sqliteOptions,
            SqliteWorkspaceStore workspaceStore,
            WorkspaceTrustService trustService,
            CancellationToken cancellationToken)
    {
        var empty = new Dictionary<string, (string WorkspaceId, string Label, string Path)>(StringComparer.Ordinal);
        try
        {
            var latestBySession = new Dictionary<string, string>(StringComparer.Ordinal);
            await using var connection = await OpenHarnessConnectionAsync(sqliteOptions, cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = SessionWorkspaceResolver.LatestWorkspaceSql;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                latestBySession[reader.GetString(0)] = reader.GetString(1);
            }

            var roots = new Dictionary<string, (string Label, string Path)>(StringComparer.Ordinal);
            foreach (var workspaceId in latestBySession.Values.Distinct(StringComparer.Ordinal))
            {
                try
                {
                    var stored = await workspaceStore.GetAsync(new WorkspaceId(workspaceId), cancellationToken).ConfigureAwait(false);
                    if (stored is null)
                    {
                        continue;
                    }

                    var root = await trustService.DecryptCanonicalRootAsync(stored, cancellationToken).ConfigureAwait(false);
                    roots[workspaceId] = (SessionWorkspaceResolver.LabelForRoot(root), root);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // Unknown or moved workspace: the session stays ungrouped.
                }
            }

            var result = new Dictionary<string, (string WorkspaceId, string Label, string Path)>(StringComparer.Ordinal);
            foreach (var (sessionId, workspaceId) in latestBySession)
            {
                if (roots.TryGetValue(workspaceId, out var root))
                {
                    result[sessionId] = (workspaceId, root.Label, root.Path);
                }
            }

            return result;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return empty;
        }
    }

    private static async Task<SqliteConnection> OpenHarnessConnectionAsync(
        HarnessSqliteOptions sqliteOptions,
        CancellationToken cancellationToken)
    {
        // No pooling: the handle must be fully closed on dispose so test and
        // host data directories can be cleaned up deterministically.
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = sqliteOptions.DatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString();
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"PRAGMA foreign_keys = ON; PRAGMA busy_timeout = {sqliteOptions.BusyTimeoutMilliseconds};";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return connection;
    }

    private static async Task<IResult> CreateSessionAsync(
        ISessionStore sessions,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        try
        {
            var sessionId = await sessions.CreateAsync(cancellationToken).ConfigureAwait(false);
            if (sessionId is null)
            {
                return Results.Ok(new { status = "error", message = "Không tạo được session." });
            }

            var document = await sessions.GetAsync(sessionId, cancellationToken)
                .ConfigureAwait(false);
            return Results.Ok(new
            {
                status = "ok",
                session_id = sessionId,
                session = new
                {
                    id = sessionId,
                    title = document?.Title ?? DefaultSessionTitle,
                },
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            LogFailure(loggers, "session create", error);
            return Results.Ok(new { status = "error", message = "Không tạo được session." });
        }
    }

    private static async Task<IResult> ActivateSessionAsync(
        string sessionId,
        ISessionStore sessions,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        try
        {
            var document = await sessions.GetAsync(sessionId, cancellationToken)
                .ConfigureAwait(false);
            return document is null
                ? Results.Ok(new { status = "error", message = "Session không tồn tại." })
                : Results.Ok(new
                {
                    status = "ok",
                    session = new
                    {
                        id = document.Id,
                        title = document.Title,
                        messages = document.Messages,
                    },
                });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            LogFailure(loggers, "session activate", error);
            return Results.Ok(new { status = "error", message = "Session không tồn tại." });
        }
    }

    private static async Task<IResult> DeleteSessionAsync(
        string sessionId,
        IHarnessSessionDeletionCoordinator deletion,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        try
        {
            var outcome = await deletion.DeleteAsync(sessionId, cancellationToken)
                .ConfigureAwait(false);
            return outcome switch
            {
                HarnessSessionDeletionOutcome.Deleted => Results.Ok(new { status = "ok" }),
                HarnessSessionDeletionOutcome.ArtifactsPending => Results.Ok(new { status = "error", message = "Session đã xoá nhưng artifacts còn sót, hãy thử xoá lại." }),
                _ => Results.Ok(new { status = "error", message = "Session không tồn tại." }),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            LogFailure(loggers, "session delete", error);
            return Results.Ok(new { status = "error", message = "Không xoá được session." });
        }
    }

    public sealed record ForkSessionRequest(
        [property: JsonPropertyName("up_to_message_index")] int UpToMessageIndex,
        [property: JsonPropertyName("title")] string? Title = null,
        [property: JsonPropertyName("new_title")] string? NewTitle = null)
    {
        public string? ResolvedTitle => !string.IsNullOrWhiteSpace(Title) ? Title : NewTitle;
    }

    public sealed record RewindSessionRequest(
        [property: JsonPropertyName("to_message_index")] int ToMessageIndex);

    private static async Task<IResult> ForkSessionAsync(
        string sessionId,
        ForkSessionRequest? request,
        ISessionStore sessions,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        try
        {
            var upTo = request?.UpToMessageIndex ?? int.MaxValue;
            var doc = await sessions.ForkAsync(sessionId, upTo, request?.ResolvedTitle, cancellationToken).ConfigureAwait(false);
            return doc is null
                ? Results.Ok(new { status = "error", message = "Không tìm thấy session để fork." })
                : Results.Ok(new { status = "ok", session = doc });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            LogFailure(loggers, "session fork", error);
            return Results.Ok(new { status = "error", message = "Lỗi khi fork session." });
        }
    }

    private static async Task<IResult> RewindSessionAsync(
        string sessionId,
        RewindSessionRequest? request,
        ISessionStore sessions,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        try
        {
            var toIndex = request?.ToMessageIndex ?? 0;
            var doc = await sessions.RewindAsync(sessionId, toIndex, cancellationToken).ConfigureAwait(false);
            return doc is null
                ? Results.Ok(new { status = "error", message = "Không tìm thấy session để rewind." })
                : Results.Ok(new { status = "ok", session = doc });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            LogFailure(loggers, "session rewind", error);
            return Results.Ok(new { status = "error", message = "Lỗi khi rewind session." });
        }
    }

    private static async Task<IResult> GetActivityAsync(
        HttpContext context,
        IActivityLogStore activity,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        try
        {
            var events = await activity.ReadAsync(ReadLimit(context), cancellationToken)
                .ConfigureAwait(false);
            return Results.Ok(new { status = "ok", events });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            LogFailure(loggers, "activity read", error);
            return Unavailable("Activity log is temporarily unavailable.");
        }
    }

    private static async Task<IResult> ClearActivityAsync(
        IActivityLogStore activity,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        try
        {
            await activity.ClearAsync(cancellationToken).ConfigureAwait(false);
            return Results.Ok(new { status = "ok" });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            LogFailure(loggers, "activity clear", error);
            return Unavailable("Activity log could not be cleared.");
        }
    }

    private static JsonObject ToSettingsResponse(
        SettingsSnapshot value,
        bool hasCustom,
        bool hasOpenAi,
        bool hasAnthropic)
    {
        var result = value.Raw.DeepClone().AsObject();
        result.Remove("reasoning_preferences");
        result["reasoning_effort"] = RuBackend.Providers.ProviderReasoningPreferences.Read(value);
        foreach (var key in new[]
                 {
                     "api_key",
                     "opencode_api_key",
                     "kilo_api_key",
                     "gemini_vision_api_key",
                     "custom_api_key",
                     "openai_api_key",
                     "anthropic_api_key",
                     "allow_web_search",
                     "allow_online_search",
                 })
        {
            result.Remove(key);
        }

        result["status"] = "ok";
        result["backend"] = value.Backend;
        result["backend_choice"] = BackendAlias(value.Backend);
        result["model"] = value.Model;
        result["custom_model"] = value.CustomModel;
        result["custom_url"] = value.CustomUrl;
        result["openai_model"] = value.OpenAiModel;
        result["anthropic_model"] = value.AnthropicModel;
        result.Remove("checkpoint_path");
        result["custom_api_key"] = string.Empty;
        result["openai_api_key"] = string.Empty;
        result["anthropic_api_key"] = string.Empty;
        result["has_custom_api_key"] = hasCustom;
        result["has_openai_api_key"] = hasOpenAi;
        result["has_anthropic_api_key"] = hasAnthropic;
        return result;
    }

    private static async Task<bool> HasCredentialAsync(
        ICredentialStore credentials,
        string account,
        CancellationToken cancellationToken)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(await credentials.GetAsync(
                "Ru",
                account,
                cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private static string BackendAlias(string backend) => backend switch
    {
        "Custom OpenAI" => "custom",
        "OpenAI" => "openai",
        "Anthropic" => "anthropic",
        "llama.cpp" => "gguf",
        _ when RuBackend.Providers.ProviderSelection.IsModelsDevBackend(backend) => backend,
        _ => string.Empty,
    };

    private static int ReadLimit(HttpContext context) =>
        int.TryParse(context.Request.Query["limit"], out var value)
            ? Math.Clamp(value, 1, 500)
            : 200;

    private static IResult Unavailable(string message) => Results.Json(
        new { status = "error", message },
        statusCode: StatusCodes.Status503ServiceUnavailable);

    private static void LogFailure(
        ILoggerFactory loggers,
        string operation,
        Exception error)
    {
        loggers.CreateLogger("RuBackend.Business").LogWarning(
            "Business operation failed: operation={Operation}, exception_type={ExceptionType}.",
            operation,
            error.GetType().Name);
    }
}
