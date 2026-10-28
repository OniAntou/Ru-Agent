using System.Text.Json.Nodes;
using RuAgent.Protocol;

namespace RuAgent.Broker.Tools.Computer;

/// <summary>
/// Executes <c>computer</c> actions (ADR 0031): session app grants, one controlling
/// run, screenshot-relative coordinates, target checks before every input event,
/// and the emergency stop. Policy decided whether the action may run at all; this
/// class decides which window it may reach.
/// </summary>
public sealed class ComputerUseService
{
    public static readonly TimeSpan OwnerIdleTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(250);

    private readonly IDesktopController _desktop;
    private readonly IComputerStopHotkey _hotkey;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _actionGate = new(1, 1);
    private readonly object _gate = new();
    private readonly Dictionary<string, HashSet<string>> _grants = new(StringComparer.Ordinal);
    private readonly Dictionary<int, Shot> _shots = [];
    private string? _ownerRunId;
    private DateTimeOffset _ownerLastUse;
    private string? _stoppedRunId;
    private CancellationTokenSource? _inFlight;

    private sealed record Shot(PixelRect Monitor, int Width, int Height);

    public ComputerUseService(IDesktopController desktop, IComputerStopHotkey hotkey, TimeProvider? time = null)
    {
        _desktop = desktop ?? throw new ArgumentNullException(nameof(desktop));
        _hotkey = hotkey ?? throw new ArgumentNullException(nameof(hotkey));
        _time = time ?? TimeProvider.System;
        _hotkey.Pressed += OnStopPressed;
    }

    /// <summary>The run that currently controls the desktop, if any.</summary>
    public string? OwnerRunId
    {
        get { lock (_gate) return _ownerRunId; }
    }

    /// <summary>Raised with the controlling run's id when the user presses the stop hotkey.</summary>
    public event Action<string>? StopRequested;

    public IReadOnlySet<string> Grants(string sessionId)
    {
        lock (_gate)
        {
            return _grants.TryGetValue(sessionId, out var apps)
                ? new HashSet<string>(apps, StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);
        }
    }

    public void RemoveSession(string sessionId)
    {
        lock (_gate) _grants.Remove(sessionId);
    }

    /// <summary>Ends a run's control so another run can take the desktop at once.</summary>
    public void ReleaseRun(string runId)
    {
        lock (_gate)
        {
            if (_ownerRunId == runId) ClearOwner();
            if (_stoppedRunId == runId) _stoppedRunId = null;
        }
    }

    public async ValueTask<ToolExecutionOutcome> ExecuteAsync(
        string runId,
        string sessionId,
        bool fullAccess,
        bool imageInputAccepted,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(arguments);
        await _actionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var inFlight = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Acquire(runId, inFlight);
            try
            {
                // One action stays on one thread: the controller sets a per-thread DPI context.
                return await Task.Run(
                    () => Execute(sessionId, fullAccess, imageInputAccepted, arguments, inFlight.Token),
                    inFlight.Token).ConfigureAwait(false);
            }
            catch (ComputerUseException error)
            {
                return ToolExecutionOutcome.Failed(error.Code, error.Detail);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && IsStopped(runId))
            {
                return ToolExecutionOutcome.Failed("computer_stopped_by_user",
                    $"The user pressed {ComputerStopHotkey.Description} to stop computer use.");
            }
            finally
            {
                lock (_gate)
                {
                    if (_inFlight == inFlight) _inFlight = null;
                    if (_ownerRunId == runId) _ownerLastUse = _time.GetUtcNow();
                }
            }
        }
        catch (ComputerUseException error)
        {
            return ToolExecutionOutcome.Failed(error.Code, error.Detail);
        }
        finally
        {
            _actionGate.Release();
        }
    }

    private void Acquire(string runId, CancellationTokenSource inFlight)
    {
        if (!_hotkey.EnsureRegistered())
            throw new ComputerUseException("computer_stop_unavailable",
                $"The {ComputerStopHotkey.Description} stop hotkey could not be registered, so computer use stays off.");
        lock (_gate)
        {
            if (_stoppedRunId == runId)
                throw new ComputerUseException("computer_stopped_by_user",
                    $"The user pressed {ComputerStopHotkey.Description} to stop computer use for this run.");
            var now = _time.GetUtcNow();
            if (_ownerRunId is not null && _ownerRunId != runId && now - _ownerLastUse < OwnerIdleTimeout)
                throw new ComputerUseException("computer_busy", "Another run is controlling the desktop.");
            if (_ownerRunId != runId)
            {
                ClearOwner();
                _ownerRunId = runId;
            }

            _ownerLastUse = now;
            _inFlight = inFlight;
        }
    }

    private void ClearOwner()
    {
        _ownerRunId = null;
        _shots.Clear();
    }

    private bool IsStopped(string runId)
    {
        lock (_gate) return _stoppedRunId == runId;
    }

    private void OnStopPressed()
    {
        string? owner;
        lock (_gate)
        {
            owner = _ownerRunId;
            if (owner is null) return;
            _stoppedRunId = owner;
            _inFlight?.Cancel();
        }

        StopRequested?.Invoke(owner);
    }

    private ToolExecutionOutcome Execute(
        string sessionId, bool fullAccess, bool imageInputAccepted, JsonObject arguments, CancellationToken cancellationToken)
    {
        if (!_desktop.IsInputDesktopAvailable())
            throw new ComputerUseException("computer_desktop_unavailable", "The desktop is locked or a secure prompt is showing.");
        var action = Text(arguments, "action");
        return action switch
        {
            ComputerActions.List => List(sessionId, fullAccess),
            ComputerActions.RequestAccess => RequestAccess(sessionId, arguments),
            ComputerActions.Focus => Focus(sessionId, fullAccess, arguments),
            ComputerActions.Screenshot => Screenshot(sessionId, fullAccess, imageInputAccepted, arguments),
            ComputerActions.Click or ComputerActions.Move or ComputerActions.Scroll or ComputerActions.Drag =>
                Pointer(action, sessionId, fullAccess, arguments, cancellationToken),
            ComputerActions.Type or ComputerActions.Key => Keyboard(action, sessionId, fullAccess, arguments, cancellationToken),
            _ => throw new ComputerUseException("computer_action_invalid", "Unknown computer action."),
        };
    }

    private ToolExecutionOutcome List(string sessionId, bool fullAccess)
    {
        var grants = Grants(sessionId);
        var ruHosts = _desktop.RuHostProcessIds();
        var monitors = new JsonArray(_desktop.Monitors().Select(monitor => (JsonNode)new JsonObject
        {
            ["monitor"] = monitor.Index,
            ["primary"] = monitor.Primary,
            ["width"] = monitor.Bounds.Width,
            ["height"] = monitor.Bounds.Height,
        }).ToArray());
        var apps = new JsonArray(_desktop.Windows()
            .GroupBy(window => window.AppName, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group =>
            {
                var granted = grants.Contains(group.Key);
                var isProtected = ComputerProtectedApps.IsProtected(group.Key) || group.Any(window => ruHosts.Contains(window.ProcessId));
                var entry = new JsonObject
                {
                    ["app"] = group.Key,
                    ["access"] = isProtected ? "protected"
                        : granted ? "granted" : fullAccess ? "full_access" : "not_granted",
                    ["windows"] = group.Count(),
                };
                // Titles carry content, so they appear only where the user granted access.
                if ((granted || fullAccess) && !isProtected)
                    entry["titles"] = new JsonArray(group.Select(window => (JsonNode)Bound(window.Title, 120)).Take(8).ToArray());
                return (JsonNode)entry;
            })
            .ToArray());
        return ToolExecutionOutcome.Succeeded(
            new JsonObject
            {
                ["monitors"] = monitors,
                ["apps"] = apps,
                ["granted"] = new JsonArray(grants.Order(StringComparer.Ordinal).Select(app => (JsonNode)app).ToArray()),
                ["stop_hotkey"] = ComputerStopHotkey.Description,
            },
            ToolVerificationStates.NotRequired,
            "Monitors and apps listed.");
    }

    private ToolExecutionOutcome RequestAccess(string sessionId, JsonObject arguments)
    {
        var requested = (arguments["apps"] as JsonArray ?? [])
            .Select(item => ComputerProtectedApps.Normalize(item?.GetValue<string>()))
            .ToArray();
        if (requested.Length == 0 || requested.Any(app => app is null))
            throw new ComputerUseException("computer_apps_invalid", "apps must name executables such as notepad.exe.");
        if (requested.FirstOrDefault(ComputerProtectedApps.IsProtected) is { } protectedApp)
            throw new ComputerUseException("computer_app_protected", $"{protectedApp} can never be controlled.");
        lock (_gate)
        {
            if (!_grants.TryGetValue(sessionId, out var apps))
                _grants[sessionId] = apps = new HashSet<string>(StringComparer.Ordinal);
            foreach (var app in requested) apps.Add(app!);
        }

        var running = _desktop.Windows().Select(window => window.AppName).ToHashSet(StringComparer.Ordinal);
        return ToolExecutionOutcome.Succeeded(
            new JsonObject
            {
                ["granted"] = new JsonArray(requested.Select(app => (JsonNode)app!).ToArray()),
                ["not_running"] = new JsonArray(requested.Where(app => !running.Contains(app!)).Select(app => (JsonNode)app!).ToArray()),
            },
            ToolVerificationStates.NotRequired,
            "Computer access granted for this session.");
    }

    private ToolExecutionOutcome Focus(string sessionId, bool fullAccess, JsonObject arguments)
    {
        var app = ComputerProtectedApps.Normalize(Text(arguments, "app"))
            ?? throw new ComputerUseException("computer_arguments_invalid", "focus needs app, an executable name such as notepad.exe.");
        if (ComputerProtectedApps.IsProtected(app))
            throw new ComputerUseException("computer_app_protected", $"{app} can never be controlled.");
        if (!fullAccess && !Grants(sessionId).Contains(app))
            throw new ComputerUseException("computer_app_not_granted", $"{app} has no access in this session; call request_access with it first.");
        var window = _desktop.TopWindowOf(app)
            ?? throw new ComputerUseException("computer_app_not_running", $"{app} has no window to bring to the front.");
        var target = RequireTarget(window, Grants(sessionId), fullAccess);
        if (!_desktop.BringToForeground(target.Handle))
            throw new ComputerUseException("computer_focus_refused", $"Windows did not bring {app} to the front; ask the user to switch to it.");
        Thread.Sleep(SettleDelay);
        return ToolExecutionOutcome.Succeeded(
            new JsonObject { ["action"] = ComputerActions.Focus, ["app"] = app, ["title"] = Bound(target.Title, 120) },
            ToolVerificationStates.NotRequired,
            $"Brought {app} to the front.");
    }

    private ToolExecutionOutcome Screenshot(string sessionId, bool fullAccess, bool imageInputAccepted, JsonObject arguments)
    {
        if (!imageInputAccepted)
            throw new ComputerUseException("image_unsupported_by_model", "The pinned model does not accept images.");
        var monitor = FindMonitor(arguments);
        // A window that moves, opens or closes during the capture must not show through:
        // keep only pixels the window lists before and after the capture both allow.
        var before = fullAccess ? null : _desktop.Windows();
        var pixels = _desktop.Capture(monitor.Bounds);
        var masked = false;
        if (!fullAccess)
        {
            var after = _desktop.Windows();
            var grants = Grants(sessionId);
            var ruHosts = _desktop.RuHostProcessIds();
            bool Allowed(DesktopWindow window) => grants.Contains(window.AppName)
                && !ComputerProtectedApps.IsProtected(window.AppName) && !ruHosts.Contains(window.ProcessId);
            ComputerGeometry.MaskOutside(pixels, monitor.Bounds, ComputerGeometry.VisibleAllowedRegions(before!, Allowed, monitor.Bounds));
            ComputerGeometry.MaskOutside(pixels, monitor.Bounds, ComputerGeometry.VisibleAllowedRegions(after, Allowed, monitor.Bounds));
            masked = true;
        }

        var (width, height) = ComputerGeometry.ScreenshotSize(monitor.Bounds, ComputerLimits.MaxScreenshotEdge);
        var png = ScreenshotEncoder.EncodePng(pixels, monitor.Bounds.Width, monitor.Bounds.Height, width, height);
        while (png.Length > ToolOutputImage.MaximumBytes && width > 320)
        {
            (width, height) = (width * 3 / 4, Math.Max(1, height * 3 / 4));
            png = ScreenshotEncoder.EncodePng(pixels, monitor.Bounds.Width, monitor.Bounds.Height, width, height);
        }

        lock (_gate) _shots[monitor.Index] = new Shot(monitor.Bounds, width, height);
        var output = new JsonObject
        {
            ["monitor"] = monitor.Index,
            ["width"] = width,
            ["height"] = height,
            ["masked"] = masked,
            ["media_type"] = "image/png",
        };
        return new ToolExecutionOutcome(ToolResultStatuses.Succeeded, output, null, "Screenshot captured.",
            ToolVerificationStates.NotRequired, Image: new ToolOutputImage("image/png", png));
    }

    private ToolExecutionOutcome Pointer(
        string action, string sessionId, bool fullAccess, JsonObject arguments, CancellationToken cancellationToken)
    {
        var monitor = FindMonitor(arguments);
        Shot shot;
        lock (_gate)
        {
            if (!_shots.TryGetValue(monitor.Index, out shot!))
                throw new ComputerUseException("computer_screenshot_required",
                    $"Take a screenshot of monitor {monitor.Index} first; coordinates are pixels of that screenshot.");
        }

        if (shot.Monitor != monitor.Bounds)
            throw new ComputerUseException("computer_display_changed", "The monitor layout changed; take a new screenshot.");
        var (x, y) = ComputerGeometry.ToPhysical(shot.Monitor, shot.Width, shot.Height, Int(arguments, "x"), Int(arguments, "y"));
        var grants = Grants(sessionId);
        var target = RequireTarget(_desktop.RootWindowAt(x, y), grants, fullAccess);
        cancellationToken.ThrowIfCancellationRequested();
        switch (action)
        {
            case ComputerActions.Click:
                _desktop.Click(x, y, Button(arguments), OptionalInt(arguments, "count") ?? 1, target.Handle);
                break;
            case ComputerActions.Move:
                _desktop.MovePointer(x, y);
                break;
            case ComputerActions.Scroll:
                var amount = (OptionalInt(arguments, "amount") ?? 3) * 120;
                var (deltaX, deltaY) = Text(arguments, "direction") switch
                {
                    "up" => (0, amount),
                    "down" => (0, -amount),
                    "left" => (-amount, 0),
                    "right" => (amount, 0),
                    _ => throw new ComputerUseException("computer_arguments_invalid", "scroll needs direction up, down, left or right."),
                };
                _desktop.Scroll(x, y, deltaX, deltaY);
                break;
            default:
                var (toX, toY) = ComputerGeometry.ToPhysical(shot.Monitor, shot.Width, shot.Height,
                    Int(arguments, "to_x"), Int(arguments, "to_y"));
                RequireTarget(_desktop.RootWindowAt(toX, toY), grants, fullAccess);
                _desktop.Drag(x, y, toX, toY, cancellationToken);
                break;
        }

        return Done(action, target);
    }

    private ToolExecutionOutcome Keyboard(
        string action, string sessionId, bool fullAccess, JsonObject arguments, CancellationToken cancellationToken)
    {
        var grants = Grants(sessionId);
        var target = RequireTarget(_desktop.ForegroundWindow(), grants, fullAccess);
        if (action == ComputerActions.Type)
        {
            var text = Text(arguments, "text");
            if (string.IsNullOrEmpty(text) || text.Length > ComputerLimits.MaxTypeChars)
                throw new ComputerUseException("computer_arguments_invalid", $"type needs text of 1 to {ComputerLimits.MaxTypeChars} characters.");
            _desktop.TypeText(text, target.Handle, cancellationToken);
        }
        else
        {
            var chord = KeyChord.Parse(Text(arguments, "keys"));
            // The Windows key opens the shell, which belongs to Explorer.
            if (chord.UsesWindowsKey && !fullAccess && !grants.Contains("explorer.exe"))
                throw new ComputerUseException("computer_key_not_allowed", "The Windows key needs access to explorer.exe.");
            _desktop.PressChord(chord, target.Handle);
        }

        return Done(action, target);
    }

    private DesktopWindow RequireTarget(DesktopWindow? window, IReadOnlySet<string> grants, bool fullAccess)
    {
        if (window is null)
            throw new ComputerUseException("computer_target_unknown", "No window is at that point or in the foreground.");
        if (ComputerProtectedApps.IsProtected(window.AppName) || _desktop.RuHostProcessIds().Contains(window.ProcessId))
            throw new ComputerUseException("computer_app_protected", $"{window.AppName} can never be controlled.");
        if (!fullAccess && !grants.Contains(window.AppName))
            throw new ComputerUseException("computer_app_not_granted",
                $"{window.AppName} has no access in this session; call request_access with it first.");
        if (_desktop.IsShieldedFromInput(window.ProcessId))
            throw new ComputerUseException("computer_target_elevated",
                $"{window.AppName} runs with higher rights than Ru, so Windows would ignore the input.");
        return window;
    }

    private static ToolExecutionOutcome Done(string action, DesktopWindow target)
    {
        Thread.Sleep(SettleDelay);
        return ToolExecutionOutcome.Succeeded(
            new JsonObject { ["action"] = action, ["app"] = target.AppName },
            ToolVerificationStates.NotRequired,
            $"Computer {action} sent to {target.AppName}.");
    }

    private DesktopMonitor FindMonitor(JsonObject arguments)
    {
        var index = OptionalInt(arguments, "monitor") ?? 0;
        return _desktop.Monitors().FirstOrDefault(monitor => monitor.Index == index)
            ?? throw new ComputerUseException("computer_monitor_invalid", $"There is no monitor {index}; call list to see monitors.");
    }

    private static PointerButton Button(JsonObject arguments) => Text(arguments, "button") switch
    {
        null or "left" => PointerButton.Left,
        "right" => PointerButton.Right,
        "middle" => PointerButton.Middle,
        _ => throw new ComputerUseException("computer_arguments_invalid", "button must be left, right or middle."),
    };

    private static string? Text(JsonObject arguments, string name) =>
        arguments[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static int? OptionalInt(JsonObject arguments, string name) =>
        arguments[name] is JsonValue value && value.TryGetValue<int>(out var number) ? number : null;

    private static int Int(JsonObject arguments, string name) =>
        OptionalInt(arguments, name) ?? throw new ComputerUseException("computer_arguments_invalid", $"{name} is required.");

    private static string Bound(string text, int max) => text.Length <= max ? text : text[..max];
}
