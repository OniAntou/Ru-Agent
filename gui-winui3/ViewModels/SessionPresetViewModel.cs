using CommunityToolkit.Mvvm.ComponentModel;
using RuAgent.Client;
using RuAgent.Protocol.Harness;
using RuApp.Services;

namespace RuApp.ViewModels;

/// <summary>The Host's stored permission preset for a session.</summary>
public interface ISessionPresetBackend
{
    /// <summary>The stored selection, or null when it cannot be read (best effort).</summary>
    Task<ApiService.SessionPresetRead?> ReadAsync(string sessionId, CancellationToken cancellationToken);

    /// <summary>Stores a selection, negotiating the version; returns the stored version.</summary>
    Task<long> WriteAsync(string sessionId, string wirePreset, CancellationToken cancellationToken);
}

/// <summary>Reads through the API service and writes through the run controller in hand.</summary>
internal sealed class HostSessionPresetBackend(
    Func<ApiService?> apiService,
    Func<AgentRunController?> controller) : ISessionPresetBackend
{
    public async Task<ApiService.SessionPresetRead?> ReadAsync(string sessionId, CancellationToken cancellationToken) =>
        apiService() is { } api ? await api.GetSessionPresetAsync(sessionId, cancellationToken) : null;

    public Task<long> WriteAsync(string sessionId, string wirePreset, CancellationToken cancellationToken)
    {
        var runController = controller() ?? throw new InvalidOperationException("Agent controller is unavailable.");
        return SessionPresetVersionNegotiator.ExecuteAsync(
            sessionId,
            expectedVersion => runController.SetSessionPresetAsync(
                sessionId,
                wirePreset,
                expectedVersion,
                interactive: true,
                cancellationToken),
            cancellationToken);
    }
}

/// <summary>
/// The chat's permission preset and what is known about the Host's stored copy of it.
/// </summary>
/// <remarks>
/// A preset is session-scoped and owned by the user: nothing here narrows it except
/// the two rules that protect an elevation. Full Access never follows the user into a
/// session it was not set for, and a declined Full Access confirmation falls back to
/// Standard. The stored version is cached per session so a send whose preset already
/// matches the Host's selection does not rewrite it.
/// </remarks>
public sealed partial class SessionPresetViewModel : ObservableObject
{
    private readonly ISessionPresetBackend _backend;
    private string? _storedSessionId;
    private long? _storedVersion;

    public SessionPresetViewModel(ISessionPresetBackend backend)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
    }

    /// <summary>Standard until the user or a stored selection says otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFullAccess))]
    public partial AgentPermissionPreset Preset { get; private set; } = AgentPermissionPreset.Standard;

    /// <summary>Drives the composer's read-only Full Access notice.</summary>
    public bool IsFullAccess => Preset == AgentPermissionPreset.FullAccess;

    public static string WireName(AgentPermissionPreset preset) => preset switch
    {
        AgentPermissionPreset.Plan => "plan",
        AgentPermissionPreset.FullAccess => "full_access",
        _ => "standard",
    };

    /// <summary>An unknown stored value reads as Standard, never as an elevation.</summary>
    public static AgentPermissionPreset FromWire(string? wire) => wire switch
    {
        "plan" => AgentPermissionPreset.Plan,
        "full_access" => AgentPermissionPreset.FullAccess,
        _ => AgentPermissionPreset.Standard,
    };

    /// <summary>The user's choice, from the composer or Settings.</summary>
    internal void Choose(AgentPermissionPreset preset) => Preset = preset;

    /// <summary>Full Access was declined at send time.</summary>
    internal void DeclineFullAccess() => Preset = AgentPermissionPreset.Standard;

    /// <summary>The session in hand was reset or replaced; its stored version is no longer known.</summary>
    internal void ForgetStoredVersion()
    {
        _storedSessionId = null;
        _storedVersion = null;
    }

    /// <summary>
    /// Opening a session. The send path pushes the preset onto whichever session is
    /// open, so carrying Full Access across would silently elevate a session the user
    /// never chose it for. Plan and Standard are preferences and may follow the user;
    /// an elevation may not. Returns whether the preset changed.
    /// </summary>
    internal bool NarrowForOpening(string sessionId)
    {
        if (Preset != AgentPermissionPreset.FullAccess
            || string.Equals(_storedSessionId, sessionId, StringComparison.Ordinal))
        {
            return false;
        }

        Preset = AgentPermissionPreset.Standard;
        return true;
    }

    /// <summary>
    /// Adopts the selection the Host stored for a session, so a reopened session shows
    /// its own choice instead of the previous session's. Best effort: a read failure, a
    /// session that is no longer open or a cancelled open leaves the current choice
    /// untouched. Returns the adopted preset, or null when nothing was adopted.
    /// </summary>
    /// <remarks>
    /// Adoption seeds the version cache with the stored selection, so the next send
    /// skips the preset write instead of re-writing the value this just read.
    /// </remarks>
    internal async Task<AgentPermissionPreset?> AdoptStoredAsync(
        string sessionId,
        Func<string> activeSessionId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionId)
            || !string.Equals(sessionId, activeSessionId(), StringComparison.Ordinal))
        {
            return null;
        }

        var stored = await _backend.ReadAsync(sessionId, cancellationToken);
        if (stored is null
            || !string.Equals(sessionId, activeSessionId(), StringComparison.Ordinal)
            || cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        Preset = FromWire(stored.Preset);
        _storedSessionId = sessionId;
        _storedVersion = stored.Version;
        return Preset;
    }

    /// <summary>
    /// Makes the Host's stored selection for the session equal <paramref name="preset"/>
    /// and returns its version. A write that finishes after the user switched sessions
    /// fails as <c>session_changed</c> rather than being credited to the new session.
    /// </summary>
    internal async Task<long> EnsureStoredAsync(
        AgentPermissionPreset preset,
        string sessionId,
        Func<bool> isStillCurrent,
        CancellationToken cancellationToken)
    {
        if (string.Equals(_storedSessionId, sessionId, StringComparison.Ordinal)
            && Preset == preset
            && _storedVersion is long knownVersion)
        {
            return knownVersion;
        }

        long version = await _backend.WriteAsync(sessionId, WireName(preset), cancellationToken);
        if (!isStillCurrent())
        {
            throw new AgentApiException("session_changed", "Active session changed during permission update.");
        }

        _storedSessionId = sessionId;
        Preset = preset;
        _storedVersion = version;
        return version;
    }
}
