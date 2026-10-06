using System.Reflection;
using System.Text;
using RuAgent.Harness.Core;
using RuAgent.Runtime.Adapters.Bootstrap;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Persistence;
using RuBackend.Tests.Harness;
using RuBackend.Providers;
using Xunit;

namespace RuBackend.Tests.Providers;

public sealed class SqliteProviderContinuationReaderTests
{
    [Fact]
    public void Exact_association_rejects_balanced_duplicate_and_missing_retained_steps()
    {
        var first = Event(1);
        var second = Event(2);
        var references = new[]
        {
            Reference(first, 1),
            Reference(first, 2),
        };

        Assert.False(SqliteProviderContinuationReader.HasExactAssociations(references, [first, second]));
    }

    [Fact]
    public void Exact_association_rejects_a_materialized_row_beyond_the_query_cap()
    {
        var retained = Enumerable.Range(1, 257).Select(Event).ToArray();
        var references = retained.Select((eventId, index) => Reference(eventId, index + 1)).ToArray();

        Assert.False(SqliteProviderContinuationReader.HasExactAssociations(references, retained));
    }

    [Fact]
    public void Parse_maps_malformed_persisted_output_member_to_named_continuation_error()
    {
        var bootstrap = new ProviderBootstrapData(
            "OpenAI", "openai_responses", new Uri("https://provider.invalid/v1/responses"),
            "gpt-5.6-luna", "synthetic", toolCallingMode: ProviderToolCallingMode.Native, allowNativeResponses: true);
        var parse = typeof(SqliteProviderContinuationReader).GetMethod("Parse", BindingFlags.NonPublic | BindingFlags.Static)!;

        var thrown = Assert.Throws<TargetInvocationException>(() => parse.Invoke(null,
            [new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes("{\"output_items\":[null]}")), bootstrap, "openai"]));

        var failure = Assert.IsType<ProviderCatalogException>(thrown.InnerException);
        Assert.Equal("provider_continuation_invalid", failure.Code);
    }

    [Fact]
    public async Task Reader_does_not_decrypt_continuations_for_compacted_assistant_steps()
    {
        await using var fixture = await BackendHarnessTestFixture.CreateAsync();
        var created = await fixture.SeedRunAsync(new HarnessRunStatus(HarnessRunState.Running));
        var run = created.Run;
        var oldStep = new ModelStepId("step_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var keptStep = new ModelStepId("step_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        var oldAssistantEvent = new EventId(Event(10));
        var keptAssistantEvent = new EventId(Event(11));
        var oldContinuationPayload = Payload(10);
        var keptContinuationPayload = Payload(11);
        var oldAssistantPayload = Payload(12);
        var keptAssistantPayload = Payload(13);
        var payloads = new[]
        {
            AssistantPayload(oldAssistantPayload), AssistantPayload(keptAssistantPayload),
            ContinuationPayload(oldContinuationPayload, oldStep), ContinuationPayload(keptContinuationPayload, keptStep),
        };
        var events = new[]
        {
            new HarnessEventIntent(oldAssistantEvent, HarnessEventKind.ModelStepCompleted,
                modelStepId: oldStep, payloadId: oldAssistantPayload),
            new HarnessEventIntent(new EventId(Event(12)), HarnessEventKind.ProviderContinuationRecorded,
                modelStepId: oldStep, payloadId: oldContinuationPayload, safeCode: "openai_responses_v1"),
            new HarnessEventIntent(keptAssistantEvent, HarnessEventKind.ModelStepCompleted,
                modelStepId: keptStep, payloadId: keptAssistantPayload),
            new HarnessEventIntent(new EventId(Event(13)), HarnessEventKind.ProviderContinuationRecorded,
                modelStepId: keptStep, payloadId: keptContinuationPayload, safeCode: "openai_responses_v1"),
        };
        var next = new AgentRun(run.RunId, run.SessionId, run.WorkspaceId,
            new HarnessRunStatus(HarnessRunState.Running), 1, run.LastSequence + events.Length,
            run.BudgetWindow, run.ModelStepsInWindow, run.ToolCallsInWindow,
            run.Preset, run.PolicySnapshotVersion, run.PolicySnapshotSha256, run.CreatedAt, run.UpdatedAt.AddSeconds(1));
        var committed = await fixture.EventStore.CommitAsync(new HarnessCommit(run.RunId, 0, next,
            events, payloads, []), CancellationToken.None);
        Assert.Equal(HarnessCommitOutcome.Committed, committed.Outcome);

        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(
                         $"Data Source={fixture.Options.DatabasePath};Mode=ReadWrite;Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE agent_payloads SET protected_content = zeroblob(length(protected_content)), nonce = zeroblob(12), authentication_tag = zeroblob(16) WHERE payload_id = $payload_id";
            command.Parameters.AddWithValue("$payload_id", oldContinuationPayload.Value);
            await command.ExecuteNonQueryAsync();
        }

        var bootstrap = new ProviderBootstrapData("OpenAI", "openai_responses",
            new Uri("https://provider.invalid/v1/responses"), "gpt-5.6-luna", "synthetic",
            toolCallingMode: ProviderToolCallingMode.Native, allowNativeResponses: true);
        var reader = new SqliteProviderContinuationReader(fixture.Options, fixture.PayloadStore);
        var continuations = await reader.ReadAsync(run.RunId, bootstrap, [keptAssistantEvent.Value], CancellationToken.None);

        Assert.Single(continuations);
        Assert.Equal("read", continuations[0].OutputItems[0]["name"]!.GetValue<string>());
    }

    private static HarnessPayloadIntent AssistantPayload(PayloadId payloadId) => new(
        payloadId, HarnessPayloadKind.AssistantStep,
        HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
        HarnessPayloadEncoding.Json, 1024,
        AssistantStepJsonCodec.Serialize(new RuAgent.Harness.Core.AssistantStep(null, [],
            RuAgent.Harness.Core.AssistantFinishReason.Stop, 1)));

    private static HarnessPayloadIntent ContinuationPayload(PayloadId payloadId, ModelStepId stepId)
    {
        var text = System.Text.Json.JsonSerializer.Serialize(new
        {
            model_step_id = stepId.Value,
            Provider = "openai",
            Protocol = "openai_responses",
            Model = "gpt-5.6-luna",
            Version = 1,
            output_items = new[]
            {
                new { type = "function_call", id = "item-" + stepId.Value[^1], call_id = "call-" + stepId.Value[^1], name = "read", arguments = "{}" },
            },
        });
        return new HarnessPayloadIntent(payloadId, HarnessPayloadKind.ProviderContinuation,
            HarnessPayloadAudience.InternalOnly, HarnessPayloadEncoding.Opaque,
            RuAgent.Harness.Core.Prompting.ProviderContinuation.MaximumEnvelopeBytes, Encoding.UTF8.GetBytes(text));
    }

    private static PayloadId Payload(int number) => new("payload_" + number.ToString("x32"));

    private static string Event(int number) => "evt_" + number.ToString("x32");

    private static SqliteProviderContinuationReader.ContinuationReference Reference(string eventId, int number) =>
        new(eventId, new ModelStepId("step_" + number.ToString("x32")), new PayloadId("payload_" + number.ToString("x32")));
}
