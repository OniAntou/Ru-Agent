using RuAgent.Harness.Core.Persistence;
using Xunit;

namespace RuAgent.Harness.Persistence.Tests;

/// <summary>Native tool calling is the only calling mode a new provider pin may record.</summary>
public sealed class ProviderSelectionCallingModeTests
{
    [Theory]
    [InlineData("text_selection")]
    [InlineData("fallback")]
    public async Task A_new_pin_rejects_any_calling_mode_but_native(string callingMode)
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var selections = await CreateRunAsync(fixture);
        var runId = fixture.NewRun(1, 1, 1, 'a', 'b').Run.RunId;

        var error = await Assert.ThrowsAsync<HarnessStoreException>(() =>
            selections.PinAsync(runId, Selection("openai_responses", callingMode), default));

        Assert.Equal("provider_selection_invalid", error.Code);
        Assert.Null((await selections.GetAsync(runId, default))!.Selection);
    }

    [Theory]
    [InlineData("chat_completions")]
    [InlineData("openai_responses")]
    [InlineData("anthropic_messages")]
    public async Task A_pin_without_a_calling_mode_records_native_for_every_protocol(string protocol)
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var selections = await CreateRunAsync(fixture);
        var runId = fixture.NewRun(1, 1, 1, 'a', 'b').Run.RunId;

        var pinned = await selections.PinAsync(runId, Selection(protocol, callingMode: null), default);

        Assert.Equal("native", pinned.Selection!.CallingMode);
        Assert.Equal("native", (await selections.GetAsync(runId, default))!.Selection!.CallingMode);
    }

    private static async Task<SqliteAgentRunProviderSelectionStore> CreateRunAsync(HarnessStoreTestFixture fixture)
    {
        await fixture.RegisterWorkspaceAsync(1);
        var source = fixture.NewRun(1, 1, 1, 'a', 'b');
        var request = new NewAgentRun(source.Run, source.Events, source.Payloads, source.ConversationOutbox,
            source.CreationIdentity);
        Assert.Equal(CreateRunOutcome.Created, (await fixture.CreateEventStore().TryCreateRunAsync(request, default)).Outcome);
        return new SqliteAgentRunProviderSelectionStore(fixture.Options);
    }

    private static AgentRunProviderSelection Selection(string protocol, string? callingMode) =>
        new("Custom OpenAI", "custom", protocol, "https://provider.invalid/v1", "model-a", "custom_api_key", "{}",
            protocol == "anthropic_messages" ? 4096 : null, ModelLabel: "model-a", CallingMode: callingMode);
}
