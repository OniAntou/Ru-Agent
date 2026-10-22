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
    private readonly Func<AgentRun, CancellationToken, Task<bool>>? _isWorkspaceTrusted;
    private readonly ConcurrentDictionary<string, byte> _stopped = new(StringComparer.Ordinal);

    /// <param name="isWorkspaceTrusted">Whether the run's workspace is trusted now. A finished
    /// run may never have been trusted (the user declined, or the challenge expired), so its
    /// workspace's own Stop hooks run only when this answers true.</param>
    public RunLifecycleHooks(
        IHookRunner runner,
        ILogger<RunLifecycleHooks> logger,
        IHostApplicationLifetime lifetime,
        Func<AgentRun, CancellationToken, Task<bool>> isWorkspaceTrusted)
        : this(runner, logger, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), lifetime.ApplicationStopping,
            isWorkspaceTrusted ?? throw new ArgumentNullException(nameof(isWorkspaceTrusted)))
    {
    }

    internal RunLifecycleHooks(
        IHookRunner runner,
        ILogger<RunLifecycleHooks> logger,
        string? userHome,
        CancellationToken stopping,
        Func<AgentRun, CancellationToken, Task<bool>>? isWorkspaceTrusted = null)
    {
        _isWorkspaceTrusted = isWorkspaceTrusted;
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

        _ = Task.Run(async () =>
        {
            // A declined or expired trust challenge also finishes a run, so a cloned
            // repository's Stop hook would otherwise run exactly when the user refused it.
            // Fail closed: an unknown answer counts as untrusted.
            var trusted = false;
            try
            {
                trusted = _isWorkspaceTrusted is not null
                    && await _isWorkspaceTrusted(run, _stopping).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                _logger.LogWarning(error, "workspace trust lookup failed for run {RunId}; running only the user's Stop hooks", run.RunId.Value);
            }

            var hooks = HookConfiguration.Load(trusted ? workspaceRoot : null, _userHome).ForEvent(HookEvent.Stop);
            if (hooks.Count == 0) return;
            var workingDirectory = trusted ? workspaceRoot : _userHome ?? workspaceRoot;
            var input = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["event"] = nameof(HookEvent.Stop),
                ["run_id"] = run.RunId.Value,
                ["session_id"] = run.SessionId.Value,
                ["workspace"] = workspaceRoot,
                ["workspace_trusted"] = trusted,
                ["status"] = status,
                ["terminal_error_code"] = run.TerminalErrorCode,
            });
            foreach (var hook in hooks)
            {
                try
                {
                    await _runner.RunAsync(hook, input, workingDirectory, _stopping).ConfigureAwait(false);
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
