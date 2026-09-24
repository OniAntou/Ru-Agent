using RuBackend.Harness;
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
