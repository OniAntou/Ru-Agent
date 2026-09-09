using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using RuBackend.Business.Activity;
using RuBackend.Business.Sessions;
using RuBackend.Business.Settings;
using RuBackend.Conversation;
using RuBackend.Harness;

namespace RuBackend.Business;

public static class BusinessEndpoints
{
    private const string DefaultSessionTitle = "Cuộc trò chuyện mới";

    public static void Map(WebApplication app)
    {
        app.MapGet("/settings", GetSettingsAsync);
        app.MapPost("/settings", SaveSettingsAsync);
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
            var hasOpenCode = await HasCredentialAsync(
                credentials,
                "api_key",
                cancellationToken).ConfigureAwait(false);
            var hasKilo = await HasCredentialAsync(
                credentials,
                "kilo_gateway_api_key",
                cancellationToken).ConfigureAwait(false);
            var hasGemini = await HasCredentialAsync(
                credentials,
                "gemini_vision_api_key",
                cancellationToken).ConfigureAwait(false);
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
                hasOpenCode,
                hasKilo,
                hasGemini,
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

    private static async Task<IResult> ListSessionsAsync(
        ISessionStore sessions,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        try
        {
            var values = await sessions.ListAsync(cancellationToken).ConfigureAwait(false);
            return Results.Ok(new
            {
                status = "ok",
                sessions = values.Select(value => new
                {
                    id = value.Id,
                    title = value.Title,
                    created_at = value.CreatedAt.ToUniversalTime().ToString("O"),
                    updated_at = value.UpdatedAt.ToUniversalTime().ToString("O"),
                    message_count = value.MessageCount,
                    parent_session_id = value.ParentSessionId,
                    forked_from_message_id = value.ForkedFromMessageId,
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
        bool hasOpenCode,
        bool hasKilo,
        bool hasGemini,
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
        result["opencode_model"] = value.OpenCodeModel;
        result["kilo_model"] = value.KiloModel;
        result["ollama_model"] = value.OllamaModel;
        result["lmstudio_model"] = value.LmStudioModel;
        result["custom_model"] = value.CustomModel;
        result["ollama_url"] = value.OllamaUrl;
        result["lmstudio_url"] = value.LmStudioUrl;
        result["custom_url"] = value.CustomUrl;
        result["openai_model"] = value.OpenAiModel;
        result["anthropic_model"] = value.AnthropicModel;
        result["api_variant"] = value.ApiVariant;
        result["kilo_reasoning_enabled"] = value.KiloReasoningEnabled;
        result["enable_self_learning"] = value.EnableSelfLearning;
        result["self_learning_enabled"] = value.EnableSelfLearning;
        result["share_memory_with_cloud"] = value.ShareMemoryWithCloud;
        result["cloud_memory_consent"] = value.ShareMemoryWithCloud;
        result.Remove("checkpoint_path");
        result["voice_label"] = value.VoiceLabel;
        result["voice_style_id"] = value.VoiceStyleId;
        result["voice_speed"] = value.VoiceSpeed;
        result["auto_pace_enabled"] = value.AutoPaceEnabled;
        result["auto_read_replies"] = value.AutoReadReplies;
        result["show_subtitles"] = value.ShowSubtitles;
        result["subtitle_color"] = value.SubtitleColor;
        result["subtitle_size"] = value.SubtitleSize;
        result["subtitle_duration"] = value.SubtitleDuration;
        result["subtitle_pos_mode"] = value.SubtitlePosMode;
        result["subtitle_pos_x"] = value.SubtitlePosX;
        result["subtitle_pos_y"] = value.SubtitlePosY;
        result["opencode_api_key"] = string.Empty;
        result["kilo_api_key"] = string.Empty;
        result["gemini_vision_api_key"] = string.Empty;
        result["custom_api_key"] = string.Empty;
        result["openai_api_key"] = string.Empty;
        result["anthropic_api_key"] = string.Empty;
        result["has_opencode_key"] = hasOpenCode;
        result["has_kilo_key"] = hasKilo;
        result["has_gemini_vision_key"] = hasGemini;
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
        "OpenCode Zen" => "opencode",
        "Kilo Gateway" => "kilo",
        "Ollama" => "ollama",
        "LM Studio" => "lmstudio",
        "Custom OpenAI" => "custom",
        "OpenAI" => "openai",
        "Anthropic" => "anthropic",
        "llama.cpp" => "gguf",
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
