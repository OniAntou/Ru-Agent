using RuAgent.Client;
using RuAgent.Protocol.Harness;
using RuApp.Services;
using RuApp.ViewModels;
using Xunit;

namespace RuApp.Tests;

public sealed class SessionPresetViewModelTests
{
    private readonly FakeBackend _backend = new();
    private readonly SessionPresetViewModel _presets;

    public SessionPresetViewModelTests() => _presets = new SessionPresetViewModel(_backend);

    [Fact]
    public void Standard_is_the_default()
    {
        Assert.Equal(AgentPermissionPreset.Standard, _presets.Preset);
    }

    [Theory]
    [InlineData(AgentPermissionPreset.Plan, "plan")]
    [InlineData(AgentPermissionPreset.Standard, "standard")]
    [InlineData(AgentPermissionPreset.FullAccess, "full_access")]
    public void Wire_names_round_trip(AgentPermissionPreset preset, string wire)
    {
        Assert.Equal(wire, SessionPresetViewModel.WireName(preset));
        Assert.Equal(preset, SessionPresetViewModel.FromWire(wire));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("FULL_ACCESS")]
    [InlineData("admin")]
    public void An_unknown_stored_value_reads_as_standard_never_as_an_elevation(string? wire)
    {
        Assert.Equal(AgentPermissionPreset.Standard, SessionPresetViewModel.FromWire(wire));
    }

    [Fact]
    public void Full_access_does_not_follow_the_user_into_another_session()
    {
        _presets.Choose(AgentPermissionPreset.FullAccess);

        Assert.True(_presets.NarrowForOpening("sess-b"));
        Assert.Equal(AgentPermissionPreset.Standard, _presets.Preset);
    }

    [Theory]
    [InlineData(AgentPermissionPreset.Plan)]
    [InlineData(AgentPermissionPreset.Standard)]
    public void Plan_and_standard_are_preferences_and_follow_the_user(AgentPermissionPreset preset)
    {
        _presets.Choose(preset);

        Assert.False(_presets.NarrowForOpening("sess-b"));
        Assert.Equal(preset, _presets.Preset);
    }

    [Fact]
    public async Task Full_access_stored_for_a_session_is_kept_only_for_that_session()
    {
        _presets.Choose(AgentPermissionPreset.FullAccess);
        await _presets.EnsureStoredAsync(AgentPermissionPreset.FullAccess, "sess-a", () => true, CancellationToken.None);

        Assert.False(_presets.NarrowForOpening("sess-a"));
        Assert.True(_presets.NarrowForOpening("sess-b"));
    }

    [Fact]
    public async Task Reopening_after_a_reset_narrows_full_access_until_the_stored_choice_is_adopted()
    {
        // The open path resets the turn (forgetting the stored version) before it
        // narrows, so Full Access drops even for the same session; adoption then
        // restores it from the Host's own record, which is the authority.
        _presets.Choose(AgentPermissionPreset.FullAccess);
        await _presets.EnsureStoredAsync(AgentPermissionPreset.FullAccess, "sess-a", () => true, CancellationToken.None);
        _presets.ForgetStoredVersion();

        Assert.True(_presets.NarrowForOpening("sess-a"));
        _backend.Stored["sess-a"] = new ApiService.SessionPresetRead("full_access", 1);
        Assert.Equal(
            AgentPermissionPreset.FullAccess,
            await _presets.AdoptStoredAsync("sess-a", () => "sess-a", CancellationToken.None));
    }

    [Fact]
    public void The_full_access_notice_follows_the_preset()
    {
        var changes = new List<string?>();
        _presets.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        _presets.Choose(AgentPermissionPreset.FullAccess);
        Assert.True(_presets.IsFullAccess);
        Assert.Contains(nameof(SessionPresetViewModel.IsFullAccess), changes);

        _presets.DeclineFullAccess();
        Assert.False(_presets.IsFullAccess);
    }

    [Fact]
    public void A_declined_full_access_falls_back_to_standard()
    {
        _presets.Choose(AgentPermissionPreset.FullAccess);

        _presets.DeclineFullAccess();

        Assert.Equal(AgentPermissionPreset.Standard, _presets.Preset);
    }

    [Fact]
    public async Task A_reset_forgets_the_stored_version_but_not_the_users_choice()
    {
        _presets.Choose(AgentPermissionPreset.Plan);
        await _presets.EnsureStoredAsync(AgentPermissionPreset.Plan, "sess-a", () => true, CancellationToken.None);

        _presets.ForgetStoredVersion();

        Assert.Equal(AgentPermissionPreset.Plan, _presets.Preset);
        await _presets.EnsureStoredAsync(AgentPermissionPreset.Plan, "sess-a", () => true, CancellationToken.None);
        Assert.Equal(2, _backend.Writes.Count);
    }

    [Fact]
    public async Task Ensuring_writes_once_per_session_and_preset_then_reuses_the_version()
    {
        long first = await _presets.EnsureStoredAsync(AgentPermissionPreset.Plan, "sess-a", () => true, CancellationToken.None);
        long again = await _presets.EnsureStoredAsync(AgentPermissionPreset.Plan, "sess-a", () => true, CancellationToken.None);

        Assert.Equal(first, again);
        Assert.Equal([("sess-a", "plan")], _backend.Writes);
        Assert.Equal(AgentPermissionPreset.Plan, _presets.Preset);

        await _presets.EnsureStoredAsync(AgentPermissionPreset.Standard, "sess-a", () => true, CancellationToken.None);
        await _presets.EnsureStoredAsync(AgentPermissionPreset.Standard, "sess-b", () => true, CancellationToken.None);
        Assert.Equal(3, _backend.Writes.Count);
    }

    [Fact]
    public async Task A_write_that_finishes_after_a_session_switch_is_not_credited()
    {
        var error = await Assert.ThrowsAsync<AgentApiException>(() =>
            _presets.EnsureStoredAsync(AgentPermissionPreset.Plan, "sess-a", () => false, CancellationToken.None));

        Assert.Equal("session_changed", error.ErrorCode);
        Assert.Equal(AgentPermissionPreset.Standard, _presets.Preset);
        await _presets.EnsureStoredAsync(AgentPermissionPreset.Plan, "sess-a", () => true, CancellationToken.None);
        Assert.Equal(2, _backend.Writes.Count);
    }

    [Fact]
    public async Task Adoption_reads_without_writing_and_seeds_the_version_cache()
    {
        _backend.Stored["sess-a"] = new ApiService.SessionPresetRead("plan", 7);

        var adopted = await _presets.AdoptStoredAsync("sess-a", () => "sess-a", CancellationToken.None);
        long version = await _presets.EnsureStoredAsync(AgentPermissionPreset.Plan, "sess-a", () => true, CancellationToken.None);

        Assert.Equal(AgentPermissionPreset.Plan, adopted);
        Assert.Equal(7, version);
        Assert.Empty(_backend.Writes);
    }

    [Fact]
    public async Task A_missing_stored_selection_leaves_the_current_choice()
    {
        _presets.Choose(AgentPermissionPreset.Plan);

        Assert.Null(await _presets.AdoptStoredAsync("sess-a", () => "sess-a", CancellationToken.None));
        Assert.Equal(AgentPermissionPreset.Plan, _presets.Preset);
    }

    [Fact]
    public async Task A_read_for_a_session_that_is_no_longer_open_is_dropped()
    {
        string active = "sess-a";
        _backend.Stored["sess-a"] = new ApiService.SessionPresetRead("full_access", 3);
        _backend.OnRead = () => active = "sess-b";

        Assert.Null(await _presets.AdoptStoredAsync("sess-a", () => active, CancellationToken.None));
        Assert.Equal(AgentPermissionPreset.Standard, _presets.Preset);

        Assert.Null(await _presets.AdoptStoredAsync("sess-a", () => "sess-b", CancellationToken.None));
        Assert.Equal(1, _backend.Reads);
    }

    [Fact]
    public async Task A_cancelled_open_does_not_adopt()
    {
        _backend.Stored["sess-a"] = new ApiService.SessionPresetRead("full_access", 3);
        using var cancelled = new CancellationTokenSource();
        _backend.OnRead = cancelled.Cancel;

        Assert.Null(await _presets.AdoptStoredAsync("sess-a", () => "sess-a", cancelled.Token));
        Assert.Equal(AgentPermissionPreset.Standard, _presets.Preset);
    }

    private sealed class FakeBackend : ISessionPresetBackend
    {
        private long _version;

        public Dictionary<string, ApiService.SessionPresetRead> Stored { get; } = new(StringComparer.Ordinal);

        public List<(string SessionId, string Preset)> Writes { get; } = new();

        public int Reads { get; private set; }

        public Action? OnRead { get; set; }

        public Task<ApiService.SessionPresetRead?> ReadAsync(string sessionId, CancellationToken cancellationToken)
        {
            Reads++;
            OnRead?.Invoke();
            return Task.FromResult(Stored.TryGetValue(sessionId, out var stored) ? stored : null);
        }

        public Task<long> WriteAsync(string sessionId, string wirePreset, CancellationToken cancellationToken)
        {
            Writes.Add((sessionId, wirePreset));
            return Task.FromResult(++_version);
        }
    }
}
