using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;
using RuAgent.Harness.Core;
using RuBackend.Business.Sessions;
using RuBackend.Business.Settings;
using RuBackend.Memory;
using HarnessEventId = RuAgent.Harness.Core.EventId;

namespace RuBackend.Harness;

public interface IAgentMemoryContextProvider
{
    Task<string?> BuildAsync(
        SessionDocument session,
        CancellationToken cancellationToken);
}

public sealed class AgentMemoryContextProvider : IAgentMemoryContextProvider
{
    private const int MaximumRenderedCharacters = 100_000;
    private readonly IMemoryService _memory;
    private readonly ISettingsStore _settings;

    public AgentMemoryContextProvider(
        IMemoryService memory,
        ISettingsStore settings)
    {
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public async Task<string?> BuildAsync(
        SessionDocument session,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        var settings = await _settings.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (!ShouldShareMemory(settings))
        {
            return null;
        }

        var prompt = session.Messages
            .LastOrDefault(message => string.Equals(message.Role, "user", StringComparison.Ordinal))
            ?.Content
            .Trim();
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return null;
        }

        IReadOnlyList<MemoryRecord> records;
        try
        {
            records = await _memory.RetrieveAsync(prompt, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }

        var rendered = RenderMemory(records);
        return string.IsNullOrWhiteSpace(rendered)
            ? null
            : $"Relevant memory:\n{rendered}";
    }

    internal static bool ShouldShareMemory(SettingsSnapshot settings) =>
        string.Equals(settings.Backend, "llama.cpp", StringComparison.Ordinal)
        || settings.ShareMemoryWithCloud;

    internal static string RenderMemory(IEnumerable<MemoryRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var builder = new StringBuilder();
        foreach (var record in records)
        {
            var content = record.Content.Trim();
            if (content.Length == 0)
            {
                continue;
            }

            var line = $"{record.Kind.ToString().ToLowerInvariant()}: {content}";
            if (builder.Length > 0)
            {
                if (builder.Length + Environment.NewLine.Length >= MaximumRenderedCharacters)
                {
                    break;
                }

                builder.Append(Environment.NewLine);
            }

            var remaining = MaximumRenderedCharacters - builder.Length;
            if (line.Length <= remaining)
            {
                builder.Append(line);
                continue;
            }

            builder.Append(line.AsSpan(0, remaining));
            break;
        }

        return builder.ToString();
    }
}

public sealed class AgentConversationMemoryProjector
{
    private readonly IMemoryService _memory;
    private readonly ISettingsStore _settings;
    private readonly ISessionStore _sessions;
    private readonly ILogger<AgentConversationMemoryProjector>? _logger;
    private readonly ConcurrentDictionary<string, byte> _projected = new(StringComparer.Ordinal);

    public AgentConversationMemoryProjector(
        IMemoryService memory,
        ISettingsStore settings,
        ISessionStore sessions,
        ILogger<AgentConversationMemoryProjector>? logger = null)
    {
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _logger = logger;
    }

    public async Task ProjectAsync(
        HarnessEventId intentId,
        string sessionId,
        CancellationToken cancellationToken)
    {
        _ = intentId.Value;
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var key = intentId.Value;
        if (!_projected.TryAdd(key, 0))
        {
            return;
        }

        try
        {
            var settings = await _settings.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (!settings.EnableSelfLearning)
            {
                return;
            }

            var turns = await _sessions.GetContextTurnsAsync(
                    sessionId,
                    1,
                    cancellationToken)
                .ConfigureAwait(false);
            var turn = turns.LastOrDefault();
            if (turn is null
                || string.IsNullOrWhiteSpace(turn.Prompt)
                || string.IsNullOrWhiteSpace(turn.Response))
            {
                return;
            }

            try
            {
                await _memory.RememberTurnAsync(
                        turn.Prompt,
                        turn.Response,
                        cancellationToken)
                    .ConfigureAwait(false);
                _memory.EnqueueLearning(turn.Prompt, turn.Response);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Memory is a best-effort projection. History and run state remain
                // authoritative when the memory store is unavailable.
                _logger?.LogWarning("memory_projection_failed");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _projected.TryRemove(key, out _);
            throw;
        }
        catch
        {
            _logger?.LogWarning("memory_projection_failed");
            _projected.TryRemove(key, out _);
        }
    }
}
