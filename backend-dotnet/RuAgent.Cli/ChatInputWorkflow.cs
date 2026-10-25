using System.Text.Json;
using RuAgent.Client;
using RuAgent.Protocol.Harness;

namespace RuAgent.Cli;

internal sealed record ChatInputQuestion(string RunId, string SessionId, string InputId, string Question, IReadOnlyList<string> Options);
internal enum ChatInputOutcome { Answered, Deferred, AlreadyAnswered }

/// <summary>One explicit answer to one pending run/input; the Host remains the consume-once authority.</summary>
internal sealed class ChatInputWorkflow(AgentApiClient client)
{
    internal const int MaxAnswerCharacters = 8192;
    private readonly HashSet<(string Run, string Input)> _answered = [];

    internal async Task<ChatInputOutcome> AnswerAsync(ChatInputQuestion question,
        Func<ChatInputQuestion, CancellationToken, Task<string?>> ask, CancellationToken cancellationToken)
    {
        if (question.InputId.Length != 36 || !question.InputId.StartsWith("inp_", StringComparison.Ordinal)
            || question.InputId.AsSpan(4).ContainsAnyExcept("0123456789abcdef")
            || string.IsNullOrWhiteSpace(question.Question) || question.Question.Length > 2048
            || question.Options.Count > 8 || question.Options.Any(option => option.Length > 512))
            throw new AgentApiException("input_request_invalid", "The pending question is invalid.");
        if (_answered.Contains((question.RunId, question.InputId))) return ChatInputOutcome.AlreadyAnswered;
        var before = await client.GetRunAsync(question.RunId, cancellationToken).ConfigureAwait(false);
        ValidateRun(before, question);
        var answer = await ask(question, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (answer is null) return ChatInputOutcome.Deferred;
        if (!IsValidAnswer(answer)) throw new AgentApiException("invalid_answer", "Enter an answer of 1..8192 characters.");
        var after = await client.GetRunAsync(question.RunId, cancellationToken).ConfigureAwait(false);
        ValidateRun(after, question);
        if (!ChatRunWorkflow.SameSnapshot(before, after))
            throw new AgentApiException("input_stale", "The run changed while the question was open. Use /follow to refresh it.");
        var result = await client.AnswerAsync(question.RunId, question.InputId, answer, cancellationToken).ConfigureAwait(false);
        if (result.ValueKind != JsonValueKind.Object
            || !result.TryGetProperty("input_id", out var id) || id.GetString() != question.InputId
            || !result.TryGetProperty("status", out var status) || status.GetString() != "answered")
            throw new AgentApiException("invalid_response", "The Host did not acknowledge this answer. Use /follow to check the run.");
        _answered.Add((question.RunId, question.InputId));
        if (_answered.Count > 256) _answered.Remove(_answered.First());
        return ChatInputOutcome.Answered;
    }

    internal static bool IsValidAnswer(string answer) =>
        !string.IsNullOrWhiteSpace(answer) && answer.Length <= MaxAnswerCharacters && !answer.Contains('\0');

    private static void ValidateRun(GetAgentRunResponse run, ChatInputQuestion question)
    {
        if (run.RunId != question.RunId || run.SessionId != question.SessionId)
            throw new AgentApiException("run_session_mismatch", "The question no longer belongs to this session.");
        if (run.TerminalAt is not null || !string.Equals(run.Status, "waiting_input", StringComparison.OrdinalIgnoreCase))
            throw new AgentApiException("input_stale", "The run is no longer waiting for this answer. Use /follow to refresh it.");
    }
}
