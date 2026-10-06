using RuBackend.Harness;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Prompting;
using Xunit;

namespace RuBackend.Tests.Harness;

/// <summary>
/// The scripted model, which lets the product be exercised without a provider key.
/// </summary>
/// <remarks>
/// This runner exists so the whole app can be driven end to end and watched, which is the
/// only way to check what the interface does with a real run. What is asserted here is its
/// parsing and its fail-closed behaviour; the driving itself is a manual harness.
/// </remarks>
public sealed class ScriptedModelStepRunnerTests
{
    [Fact]
    public void A_text_only_step_finishes_the_turn()
    {
        var steps = ScriptedModelStepRunner.Parse("""[{"text":"done"}]""");

        var step = Assert.Single(steps);
        Assert.Equal("done", step.Text);
        Assert.Empty(step.ToolCalls);
        Assert.Equal(RuAgent.Harness.Core.AssistantFinishReason.Stop, step.FinishReason);
    }

    [Fact]
    public void A_step_with_a_tool_asks_the_runtime_to_run_it()
    {
        var steps = ScriptedModelStepRunner.Parse(
            """[{"text":"planning","tool":{"id":"todo_write","arguments":{"todos":[]}}}]""");

        var step = Assert.Single(steps);
        var call = Assert.Single(step.ToolCalls);
        Assert.Equal("ru_call_script_1", call.ProviderCallId);
        Assert.Equal("todo_write", call.ToolId);

        // A version is required by the protocol, so an omitted one has to default rather
        // than produce a call the catalog cannot resolve.
        Assert.Equal("1.0.0", call.ToolVersion);
        Assert.Equal(RuAgent.Harness.Core.AssistantFinishReason.ToolCalls, step.FinishReason);
    }

    [Fact]
    public void A_step_with_bounded_ordered_tools_preserves_each_unique_call_identity()
    {
        var steps = ScriptedModelStepRunner.Parse(
            """[{"tools":[{"id":"read","arguments":{"path":"a.cs"}},{"id":"read","arguments":{"path":"b.cs"}}]}]""");

        var calls = Assert.Single(steps).ToolCalls;
        Assert.Equal(2, calls.Count);
        Assert.Equal("ru_call_script_1_1", calls[0].ProviderCallId);
        Assert.Equal("ru_call_script_1_2", calls[1].ProviderCallId);
        Assert.Equal("a.cs", calls[0].Arguments.GetProperty("path").GetString());
        Assert.Equal("b.cs", calls[1].Arguments.GetProperty("path").GetString());
        Assert.Equal(RuAgent.Harness.Core.AssistantFinishReason.ToolCalls, steps[0].FinishReason);
    }

    [Fact]
    public void A_scripted_multi_tool_step_cannot_exceed_scheduler_batch_bound()
    {
        var tools = string.Join(",", Enumerable.Range(0, 9).Select(index => $"{{\"id\":\"read\",\"arguments\":{{\"path\":\"{index}.cs\"}}}}"));
        Assert.Throws<InvalidOperationException>(() => ScriptedModelStepRunner.Parse($"[{{\"tools\":[{tools}]}}]"));
    }

    [Fact]
    public async Task A_context_oracle_checks_the_actual_typed_model_context_source()
    {
        var runner = ScriptedModelStepRunner.FromJsonForTests(
            """[{"text":"done","context_oracle":{"source_id":"base:session-work-state","contains":"ANCHOR-17"}}]""");
        var context = new ModelContextSnapshot(
            [ModelContextEntry.Text("base:session-work-state", 1, ModelContextRole.System, "Earlier user intent: ANCHOR-17")],
            ReadOnlyMemory<byte>.Empty);
        var request = CreateRequest(context);

        var result = await runner.RunStepAsync(request, _ => ValueTask.CompletedTask, CancellationToken.None);

        Assert.Equal("done", result.Step.Text);
    }

    [Fact]
    public async Task A_context_oracle_rejects_a_marker_from_the_wrong_context_source()
    {
        var runner = ScriptedModelStepRunner.FromJsonForTests(
            """[{"text":"done","context_oracle":{"source_id":"base:session-work-state","contains":"ANCHOR-17"}}]""");
        var context = new ModelContextSnapshot(
            [ModelContextEntry.Text("run:user-input", 1, ModelContextRole.User, "ANCHOR-17")],
            ReadOnlyMemory<byte>.Empty);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await runner.RunStepAsync(CreateRequest(context), _ => ValueTask.CompletedTask, CancellationToken.None));

        Assert.Equal("scripted_context_oracle_failed", error.Message);
    }

    [Fact]
    public async Task A_context_oracle_requires_the_exact_current_user_prompt_entry()
    {
        var runner = ScriptedModelStepRunner.FromJsonForTests(
            """[{"text":"done","context_oracle":{"source_id":"base:session-work-state","contains":"ANCHOR-17","also_contains":"current request","current_prompt":"current request"}}]""");
        var context = new ModelContextSnapshot(
        [
            ModelContextEntry.Text("base:session-work-state", 1, ModelContextRole.System, "Earlier intent ANCHOR-17; current request"),
            ModelContextEntry.Text("run:user-input", 2, ModelContextRole.User, "current request"),
        ],
        ReadOnlyMemory<byte>.Empty);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await runner.RunStepAsync(CreateRequest(context), _ => ValueTask.CompletedTask, CancellationToken.None));

        Assert.Equal("scripted_context_oracle_failed", error.Message);
    }

    [Fact]
    public async Task Paging_cursor_uses_the_exact_preceding_call_reference()
    {
        var runner = ScriptedModelStepRunner.FromJsonForTests(
            """[{"tool":{"id":"read","arguments":{"path":"first.txt","offset":0}}},{"tool":{"id":"read","arguments":{"path":"second.txt","offset":"$projection_next_offset"}}}]""");
        var context = new ModelContextSnapshot(
        [ModelContextEntry.ToolResult("tool:previous", 1, Receipt("ru_call_script_1", "call_11111111111111111111111111111111"), "{}")],
        ReadOnlyMemory<byte>.Empty);
        var references = new PromptRuntimeContext(null, null,
        [
            new PromptReference("tool_result", "older", "{\"projection_tool_call_id\":\"call_22222222222222222222222222222222\",\"projection_provider_call_id\":\"ru_call_script_old\",\"projection_next_offset\":111,\"projection_has_more\":true}"),
            new PromptReference("tool_result", "previous", "{\"projection_tool_call_id\":\"call_11111111111111111111111111111111\",\"projection_provider_call_id\":\"ru_call_script_1\",\"projection_next_offset\":222,\"projection_has_more\":true}"),
        ]);
        var promptContext = new SystemPromptContext(
            SystemPromptContext.PromptContextSchemaVersion,
            SystemPromptContext.RequiredAgentProfile,
            "standard",
            SystemPromptContext.Empty.Environment,
            Array.Empty<PromptInstructionBlock>(),
            Array.Empty<PromptCapability>(),
            references);
        var request = CreateRequest(context, promptContext);

        await runner.RunStepAsync(request, _ => ValueTask.CompletedTask, CancellationToken.None);
        var result = await runner.RunStepAsync(request, _ => ValueTask.CompletedTask, CancellationToken.None);

        Assert.Equal(222, Assert.Single(result.Step.ToolCalls).Arguments.GetProperty("offset").GetInt32());
    }

    [Fact]
    public async Task Restarted_runner_positions_same_script_from_durable_assistant_steps()
    {
        var runner = ScriptedModelStepRunner.FromJsonForTests(
            """[{"text":"first"},{"text":"second"}]""");
        var context = new ModelContextSnapshot(
        [ModelContextEntry.Assistant(
            "event:evt_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            1,
            new AssistantStep("first", Array.Empty<ModelToolCall>(), AssistantFinishReason.Stop, 1))],
        ReadOnlyMemory<byte>.Empty);

        var result = await runner.RunStepAsync(CreateRequest(context), _ => ValueTask.CompletedTask, CancellationToken.None);

        Assert.Equal("second", result.Step.Text);
    }

    [Fact]
    public async Task Context_oracle_rejects_actual_paging_receipt_with_offset_after_projected_cursor()
    {
        const string firstProviderCall = "ru_call_script_1";
        const string nextProviderCall = "ru_call_script_2";
        const string projected = "{\"projection_provider_call_id\":\"ru_call_script_1\",\"projection_tool_call_id\":\"call_11111111111111111111111111111111\",\"content\":\"VISIBLE-\",\"projection_next_offset\":18,\"projection_has_more\":true}";
        const string source = "{\"path\":\"x.txt\",\"offset\":10,\"content\":\"VISIBLE-END\"}";
        var runner = ScriptedModelStepRunner.FromJsonForTests(
            """[{"tool":{"id":"read","arguments":{"path":"x.txt","offset":10}}},{"cursor_from_call_id":"ru_call_script_1","context_oracle":{"projection_source_call_id":"ru_call_script_1","projection_field":"content","projection_excludes":"END","projection_cursor_matches_visible_bytes":true},"tool":{"id":"read","arguments":{"path":"x.txt","offset":"$projection_next_offset"}}},{"text":"done","context_oracle":{"projection_source_call_id":"ru_call_script_1","projection_result_call_id":"ru_call_script_2","projection_field":"content","projection_excludes":"END","projection_cursor_matches_visible_bytes":true}}]""");
        var firstReceipt = Receipt(firstProviderCall, "call_11111111111111111111111111111111");
        var firstContext = new ModelContextSnapshot(
            [ModelContextEntry.ToolResult("tool:first", 1, firstReceipt, source)],
            ReadOnlyMemory<byte>.Empty);
        var firstPrompt = PromptContextWithReference(projected);
        var wrongNextReceipt = Receipt(nextProviderCall, "call_22222222222222222222222222222222");
        var contextAfterWrongNextCall = new ModelContextSnapshot(
        [
            ModelContextEntry.ToolResult("tool:first", 1, firstReceipt, source),
            ModelContextEntry.ToolResult("tool:next", 2, wrongNextReceipt, "{\"path\":\"x.txt\",\"offset\":19,\"content\":\"END\"}"),
        ],
        ReadOnlyMemory<byte>.Empty);

        await runner.RunStepAsync(CreateRequest(new ModelContextSnapshot([], ReadOnlyMemory<byte>.Empty)), _ => ValueTask.CompletedTask, CancellationToken.None);
        var secondStep = await runner.RunStepAsync(CreateRequest(firstContext, firstPrompt), _ => ValueTask.CompletedTask, CancellationToken.None);
        Assert.Equal(18, Assert.Single(secondStep.Step.ToolCalls).Arguments.GetProperty("offset").GetInt32());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await runner.RunStepAsync(CreateRequest(contextAfterWrongNextCall, firstPrompt), _ => ValueTask.CompletedTask, CancellationToken.None));

        Assert.Equal("scripted_context_oracle_failed", error.Message);
    }

    [Fact]
    public async Task Paging_cursor_rejects_a_reference_with_mismatched_persisted_call_identity()
    {
        var runner = ScriptedModelStepRunner.FromJsonForTests(
            """[{"tool":{"id":"read","arguments":{"path":"first.txt","offset":0}}},{"tool":{"id":"read","arguments":{"path":"second.txt","offset":"$projection_next_offset"}}}]""");
        var context = new ModelContextSnapshot(
        [ModelContextEntry.ToolResult("tool:previous", 1, Receipt("ru_call_script_1", "call_11111111111111111111111111111111"), "{}")],
        ReadOnlyMemory<byte>.Empty);
        var promptContext = PromptContextWithReference(
            "{\"projection_tool_call_id\":\"call_22222222222222222222222222222222\",\"projection_provider_call_id\":\"ru_call_script_1\",\"projection_next_offset\":222,\"projection_has_more\":true}");
        var request = CreateRequest(context, promptContext);

        await runner.RunStepAsync(request, _ => ValueTask.CompletedTask, CancellationToken.None);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await runner.RunStepAsync(request, _ => ValueTask.CompletedTask, CancellationToken.None));

        Assert.Equal("scripted_projection_cursor_source_unavailable", error.Message);
    }

    [Fact]
    public async Task Paging_cursor_rejects_a_reference_with_mismatched_provider_call_identity()
    {
        var runner = ScriptedModelStepRunner.FromJsonForTests(
            """[{"tool":{"id":"read","arguments":{"path":"first.txt","offset":0}}},{"tool":{"id":"read","arguments":{"path":"second.txt","offset":"$projection_next_offset"}}}]""");
        var context = new ModelContextSnapshot(
        [ModelContextEntry.ToolResult("tool:previous", 1, Receipt("ru_call_script_1", "call_11111111111111111111111111111111"), "{}")],
        ReadOnlyMemory<byte>.Empty);
        var promptContext = PromptContextWithReference(
            "{\"projection_tool_call_id\":\"call_11111111111111111111111111111111\",\"projection_provider_call_id\":\"ru_call_script_other\",\"projection_next_offset\":222,\"projection_has_more\":true}");
        var request = CreateRequest(context, promptContext);

        await runner.RunStepAsync(request, _ => ValueTask.CompletedTask, CancellationToken.None);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await runner.RunStepAsync(request, _ => ValueTask.CompletedTask, CancellationToken.None));

        Assert.Equal("scripted_projection_cursor_source_unavailable", error.Message);
    }

    [Fact]
    public async Task An_any_entry_marker_assertion_requires_the_diagnostic_in_context()
    {
        const string script = """[{"text":"done","context_oracle":{"also_contains":"DIAGNOSTIC-MARKER"}}]""";
        var runner = ScriptedModelStepRunner.FromJsonForTests(script);
        var missing = new ModelContextSnapshot(
            [ModelContextEntry.Text("base:session-work-state", 1, ModelContextRole.System, "unrelated content")],
            ReadOnlyMemory<byte>.Empty);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await runner.RunStepAsync(CreateRequest(missing), _ => ValueTask.CompletedTask, CancellationToken.None));

        Assert.Equal("scripted_context_oracle_failed", error.Message);

        var present = new ModelContextSnapshot(
            [ModelContextEntry.Text("run:tool-result", 1, ModelContextRole.System, "stderr: DIAGNOSTIC-MARKER")],
            ReadOnlyMemory<byte>.Empty);
        var result = await ScriptedModelStepRunner.FromJsonForTests(script)
            .RunStepAsync(CreateRequest(present), _ => ValueTask.CompletedTask, CancellationToken.None);

        Assert.Equal("done", result.Step.Text);
    }

    [Fact]
    public async Task A_not_contains_assertion_rejects_a_leaked_marker()
    {
        const string script = """[{"text":"done","context_oracle":{"not_contains":"LEAK-MARKER"}}]""";
        var runner = ScriptedModelStepRunner.FromJsonForTests(script);
        var leaked = new ModelContextSnapshot(
            [ModelContextEntry.Text("run:user-input", 1, ModelContextRole.User, "prompt with LEAK-MARKER inside")],
            ReadOnlyMemory<byte>.Empty);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await runner.RunStepAsync(CreateRequest(leaked), _ => ValueTask.CompletedTask, CancellationToken.None));

        Assert.Equal("scripted_context_oracle_failed", error.Message);

        var clean = new ModelContextSnapshot(
            [ModelContextEntry.Text("run:user-input", 1, ModelContextRole.User, "prompt without the marker")],
            ReadOnlyMemory<byte>.Empty);
        var result = await ScriptedModelStepRunner.FromJsonForTests(script)
            .RunStepAsync(CreateRequest(clean), _ => ValueTask.CompletedTask, CancellationToken.None);

        Assert.Equal("done", result.Step.Text);
    }

    private static ModelStepRequest CreateRequest(ModelContextSnapshot context) => new(
        new RunId("run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"),
        new ModelStepId("step_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"),
        context,
        SystemPromptContext.Empty,
        AgentPermissionPreset.Standard,
        1,
        new string('0', 64));

    private static ModelStepRequest CreateRequest(ModelContextSnapshot context, SystemPromptContext promptContext) => new(
        new RunId("run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"),
        new ModelStepId("step_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"),
        context,
        promptContext,
        AgentPermissionPreset.Standard,
        1,
        new string('0', 64));

    private static ToolExecutionReceipt Receipt(string providerCallId, string toolCallId) => new(
        new ExecutionId("exec_11111111111111111111111111111111"),
        new ToolCallId(toolCallId),
        ToolExecutionOutcome.Succeeded,
        "ok",
        null,
        sideEffectsPossible: false,
        providerCallId: providerCallId);

    private static SystemPromptContext PromptContextWithReference(string content) => new(
        SystemPromptContext.PromptContextSchemaVersion,
        SystemPromptContext.RequiredAgentProfile,
        "standard",
        SystemPromptContext.Empty.Environment,
        Array.Empty<PromptInstructionBlock>(),
        Array.Empty<PromptCapability>(),
        new PromptRuntimeContext(null, null, [new PromptReference("tool_result", "previous", content)]));

    [Fact]
    public void The_arguments_survive_parsing()
    {
        var steps = ScriptedModelStepRunner.Parse(
            """
            [{"tool":{"id":"todo_write","arguments":{"todos":[
              {"content":"One","status":"pending"},{"content":"Two","status":"completed"}]}}}]
            """);

        var arguments = Assert.Single(steps).ToolCalls[0].Arguments;
        var todos = arguments.GetProperty("todos");
        Assert.Equal(2, todos.GetArrayLength());
        Assert.Equal("One", todos[0].GetProperty("content").GetString());
        Assert.Equal("completed", todos[1].GetProperty("status").GetString());
    }

    [Fact]
    public void A_step_without_arguments_gets_an_empty_object()
    {
        // The protocol always carries an arguments object, so an omitted one must not
        // produce a malformed call.
        var steps = ScriptedModelStepRunner.Parse("""[{"tool":{"id":"list"}}]""");

        Assert.Equal(
            System.Text.Json.JsonValueKind.Object,
            Assert.Single(steps).ToolCalls[0].Arguments.ValueKind);
    }

    [Fact]
    public void Malformed_json_is_refused_rather_than_ignored()
    {
        // A script that does not parse must not silently become "no scripted model", which
        // would fall through to a real provider and quietly spend the user's credit.
        Assert.Throws<InvalidOperationException>(() => ScriptedModelStepRunner.Parse("{not json"));
    }

    [Fact]
    public void A_script_that_is_not_an_array_is_refused()
    {
        Assert.Throws<InvalidOperationException>(() => ScriptedModelStepRunner.Parse("""{"text":"one"}"""));
    }

    [Fact]
    public void A_tool_step_without_an_id_is_refused()
    {
        Assert.Throws<InvalidOperationException>(
            () => ScriptedModelStepRunner.Parse("""[{"tool":{"arguments":{}}}]"""));
    }

    [Fact]
    public void A_step_that_is_not_an_object_is_refused()
    {
        Assert.Throws<InvalidOperationException>(() => ScriptedModelStepRunner.Parse("""["text"]"""));
    }

    [Fact]
    public void No_script_in_the_environment_means_no_runner()
    {
        // The normal path. Nothing about this feature may engage on its own.
        string? previous = Environment.GetEnvironmentVariable(ScriptedModelStepRunner.EnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(ScriptedModelStepRunner.EnvironmentVariable, null);
            Assert.Null(ScriptedModelStepRunner.FromEnvironment());
        }
        finally
        {
            Environment.SetEnvironmentVariable(ScriptedModelStepRunner.EnvironmentVariable, previous);
        }
    }

    [Fact]
    public void A_script_path_that_does_not_exist_is_refused_at_startup()
    {
        // Fail closed and loudly: a typo in the path must not start an app that looks
        // scripted but is talking to a real provider.
        string? previous = Environment.GetEnvironmentVariable(ScriptedModelStepRunner.EnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(
                ScriptedModelStepRunner.EnvironmentVariable,
                Path.Combine(Path.GetTempPath(), "ru-no-such-script-" + Guid.NewGuid().ToString("N") + ".json"));

            var error = Assert.Throws<InvalidOperationException>(
                () => ScriptedModelStepRunner.FromEnvironment());
            Assert.Contains(ScriptedModelStepRunner.EnvironmentVariable, error.Message, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ScriptedModelStepRunner.EnvironmentVariable, previous);
        }
    }
}
