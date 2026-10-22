using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using RuAgent.Harness.Core;

namespace RuBackend.Harness.Hooks;

/// <summary>
/// Run-level hooks: <c>UserPromptSubmit</c> before a run is created and <c>Stop</c> once a
/// run has finished. Tool hooks live in <see cref="HookToolGateway"/>.
/// </summary>
/// <remarks>
/// A prompt hook that exits with <see cref="HookResult.BlockExitCode"/> refuses the prompt
/// before any run is written; a timeout, another exit code or a start failure never blocks.
/// A workspace's own hooks run only once the workspace is trusted. Until then only the
/// user's hooks run, and they start in the user's profile folder rather than the
/// workspace, where <c>cmd.exe</c> would resolve a bare program name against workspace
/// files first. Stop hooks observe the finished run and cannot change it.
/// </remarks>
public sealed class RunLifecycleHooks
{
    /// <summary>A prompt larger than this is left out of the hook's input.</summary>
    public const int MaxPromptBytes = 32 * 1024;

    private const int MaxBlockMessageChars = 200;
    private const int MaxRemembered = 4096;

    private readonly IHookRunner _runner;
    private readonly ILogger<RunLifecycleHooks> _logger;
    private readonly string? _userHome;
    private readonly CancellationToken _stopping;
    private readonly ConcurrentDictionary<string, byte> _stopped = new(StringComparer.Ordinal);

    public RunLifecycleHooks(IHookRunner runner, ILogger<RunLifecycleHooks> logger, IHostApplicationLifetime lifetime)
        : this(runner, logger, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), lifetime.ApplicationStopping)
    {
    }

    internal RunLifecycleHooks(IHookRunner runner, ILogger<RunLifecycleHooks> logger, string? userHome, CancellationToken stopping)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _userHome = string.IsNullOrWhiteSpace(userHome) ? null : userHome;
        _stopping = stopping;
    }

    /// <summary>
    /// Runs the prompt hooks and returns the refusal message of the first that blocks,
    /// or null when the prompt may proceed.
    /// </summary>
    public async Task<string?> SubmitPromptAsync(
        string prompt,
        string sessionId,
        string workspaceRoot,
        bool workspaceTrusted,
        CancellationToken cancellationToken)
    {
        var hooks = HookConfiguration.Load(workspaceTrusted ? workspaceRoot : null, _userHome)
            .ForEvent(HookEvent.UserPromptSubmit);
        if (hooks.Count == 0) return null;

        var includePrompt = Encoding.UTF8.GetByteCount(prompt) <= MaxPromptBytes;
        var input = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["event"] = nameof(HookEvent.UserPromptSubmit),
            ["session_id"] = sessionId,
            ["workspace"] = workspaceRoot,
            ["workspace_trusted"] = workspaceTrusted,
            ["prompt"] = includePrompt ? prompt : null,
            ["prompt_omitted"] = !includePrompt,
        });
        var workingDirectory = workspaceTrusted ? workspaceRoot : _userHome ?? workspaceRoot;
        foreach (var hook in hooks)
        {
            HookResult result;
            try
            {
                result = await _runner.RunAsync(hook, input, workingDirectory, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                _logger.LogWarning(error, "UserPromptSubmit hook could not start");
                continue;
            }

            if (result.Blocks)
            {
                var message = string.IsNullOrWhiteSpace(result.Message)
                    ? "The prompt was blocked by a UserPromptSubmit hook."
                    : result.Message.Trim();
                return message.Length <= MaxBlockMessageChars ? message : message[..MaxBlockMessageChars];
            }
        }

        return null;
    }

    /// <summary>Starts the Stop hooks of a finished run once, without waiting for them.</summary>
    public void RunFinished(AgentRun run, string status, string workspaceRoot)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (!run.State.IsTerminal() || !_stopped.TryAdd(run.RunId.Value, 0)) return;
        if (_stopped.Count > MaxRemembered) _stopped.Clear();

        var hooks = HookConfiguration.Load(workspaceRoot, _userHome).ForEvent(HookEvent.Stop);
        if (hooks.Count == 0) return;
        var input = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["event"] = nameof(HookEvent.Stop),
            ["run_id"] = run.RunId.Value,
            ["session_id"] = run.SessionId.Value,
            ["workspace"] = workspaceRoot,
            ["status"] = status,
            ["terminal_error_code"] = run.TerminalErrorCode,
        });
        _ = Task.Run(async () =>
        {
            foreach (var hook in hooks)
            {
                try
                {
                    await _runner.RunAsync(hook, input, workspaceRoot, _stopping).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception error)
                {
                    _logger.LogWarning(error, "Stop hook failed for run {RunId}", run.RunId.Value);
                }
            }
        }, CancellationToken.None);
    }
}
