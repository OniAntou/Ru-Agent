using RuAgent.Runtime.Adapters.Runtime;
using RuAgent.Runtime.Core.Contracts;
using Xunit;

namespace RuAgent.Runtime.Adapters.Tests;

public sealed class ManagedRuntimeSettingsServiceTests
{
    [Fact]
    public async Task Load_round_trips_backend_model_and_base_url()
    {
        var service = new ManagedRuntimeSettingsService(
            new RuntimeSettingsSnapshot(
                "OpenCode Zen",
                "opencode/free",
                null,
                "Default",
                false));

        var message = await service.LoadAsync(
            new RuntimeSettingsInput(
                "Custom OpenAI",
                "local-model",
                "http://127.0.0.1:9000/v1",
                "Default",
                false),
            CancellationToken.None);

        Assert.Equal("Runtime loaded.", message);
        Assert.Equal("Custom OpenAI", service.Current.Backend);
        Assert.Equal("local-model", service.Current.ModelName);
        Assert.Equal(new Uri("http://127.0.0.1:9000/v1"), service.Current.BaseUrl);
    }

    [Fact]
    public async Task Load_rolls_back_state_when_applier_fails()
    {
        var applier = new FailingApplier();
        var service = new ManagedRuntimeSettingsService(
            new RuntimeSettingsSnapshot(
                "OpenCode Zen",
                "opencode/free",
                null,
                "Default",
                false),
            applier);

        var error = await Assert.ThrowsAsync<ManagedRuntimeSettingsException>(() => service.LoadAsync(
            new RuntimeSettingsInput(
                "Custom OpenAI",
                "local-model",
                "http://127.0.0.1:9000/v1",
                "Default",
                false),
            CancellationToken.None));

        Assert.Equal("runtime_load_failed", error.Code);
        Assert.Equal("OpenCode Zen", service.Current.Backend);
        Assert.Equal("opencode/free", service.Current.ModelName);
        Assert.Equal(1, applier.Calls);
    }

    [Fact]
    public async Task Load_rejects_invalid_provider_endpoint_before_applier()
    {
        var applier = new FailingApplier();
        var service = new ManagedRuntimeSettingsService(
            new RuntimeSettingsSnapshot("OpenCode Zen", "opencode/free", null, "Default", false),
            applier);

        await Assert.ThrowsAsync<ArgumentException>(() => service.LoadAsync(
            new RuntimeSettingsInput(
                "Custom OpenAI",
                "custom",
                "file:///private",
                "Default",
                false),
            CancellationToken.None));

        Assert.Equal(0, applier.Calls);
    }

    [Fact]
    public async Task Load_updates_voice_settings_without_reapplying_the_model_provider()
    {
        var applier = new FailingApplier();
        var service = new ManagedRuntimeSettingsService(
            new RuntimeSettingsSnapshot("OpenCode Zen", "opencode/free", null, "Default", false),
            applier);

        var message = await service.LoadAsync(
            new RuntimeSettingsInput(
                "OpenCode Zen",
                "opencode/free",
                null,
                "Default",
                false,
                new VoiceSettingsSnapshot("Ái Vy (OmniVoice)", VoiceSpeed: 1.2)),
            CancellationToken.None);

        Assert.Equal("Runtime loaded.", message);
        Assert.Equal(0, applier.Calls);
        Assert.Equal("Ái Vy (OmniVoice)", service.Current.Voice.VoiceLabel);
        Assert.Equal(1.2, service.Current.Voice.VoiceSpeed);
    }

    [Fact]
    public void Constructor_rejects_null_voice_text_fields_in_untrusted_snapshots()
    {
        var snapshot = new RuntimeSettingsSnapshot(
            "OpenCode Zen",
            "opencode/free",
            null,
            "Default",
            false)
        {
            Voice = new VoiceSettingsSnapshot(VoiceLabel: null!),
        };

        Assert.Throws<ArgumentException>(() => new ManagedRuntimeSettingsService(snapshot));
    }

    private sealed class FailingApplier : IManagedRuntimeApplier
    {
        public int Calls { get; private set; }

        public Task ApplyAsync(RuntimeSettingsSnapshot settings, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromException(new InvalidOperationException("provider unavailable"));
        }
    }
}
