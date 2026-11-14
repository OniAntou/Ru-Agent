using System.Net;
using System.Text;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Protocol;
using RuAgent.Protocol.Harness;
using RuAgent.Runtime.Adapters.Bootstrap;
using RuAgent.Runtime.Adapters.Models;
using RuAgent.Runtime.Model;
using RuBackend.Harness.ModelRunner;
using RuBackend.Providers;
using Xunit;
using Xunit.Abstractions;

namespace RuBackend.Tests.ModelRunner;

/// <summary>
/// ADR 0047 regression: the consultation must fit through the real chat-completions projection
/// with a large-context models.dev advisor (the live OpenCode Go run reported a false
/// advisor_context_too_large on a tiny transcript).
/// </summary>
public sealed class AdvisorRealProjectionTests(ITestOutputHelper output)
{
    private static readonly SessionId Session = new("6f3ab1998d584a93bf178972341e8fd7");

    [Fact]
    public async Task A_small_transcript_reaches_a_large_context_advisor_that_requires_a_session()
    {
        // The live OpenCode Go run failed here: the provider requires its session header and the
        // consultation did not carry the run's session, which was then misreported as a size failure.
        var handler = new Handler();
        using var http = new HttpClient(handler);
        var runner = new InProcessModelStepRunner(new ModelStepService(new OpenAiCompatibleModelProvider(http)))
        {
            ResolveAdvisorAsync = (_, _, _, _) => Task.FromResult(new AdvisorTarget(AdvisorBootstrap(), "deepseek-v4.1-flash")),
        };

        var result = await runner.ConsultAsync(Consultation(Session), default);

        Assert.Null(result.ErrorCode);
        Assert.Equal("Apply the percentage.", result.Advice);
        Assert.Equal(Session.Value, Assert.Single(handler.Sessions));
    }

    [Fact]
    public async Task A_request_the_provider_cannot_prepare_is_a_provider_failure_with_its_reason()
    {
        var handler = new Handler();
        using var http = new HttpClient(handler);
        var runner = new InProcessModelStepRunner(new ModelStepService(new OpenAiCompatibleModelProvider(http)))
        {
            ResolveAdvisorAsync = (_, _, _, _) => Task.FromResult(new AdvisorTarget(AdvisorBootstrap(), "deepseek-v4.1-flash")),
        };

        var result = await runner.ConsultAsync(Consultation(null), default);

        Assert.Equal(AdvisorCodes.ProviderFailed, result.ErrorCode);
        Assert.Equal("prepare_failed:provider_session_missing", result.Detail);
        Assert.Empty(handler.Bodies);
        output.WriteLine(result.Detail);
    }

    [Fact]
    public void The_advisor_reads_the_agent_instructions_without_tool_descriptions()
    {
        var main = FullPromptContext();
        var full = SystemPromptComposer.Compose(main).Text;

        var instructions = AdvisorTranscript.Instructions(main);

        Assert.Contains("as model longcat-2.5-free with the standard permission preset", instructions, StringComparison.Ordinal);
        // Every tool keeps the purpose the agent's own prompt gives it.
        var read = main.Capabilities.Single(capability => capability.Id == "read");
        Assert.Contains($"\n - read: {read.Description[..(read.Description.IndexOf(". ", StringComparison.Ordinal) + 1)]}",
            instructions, StringComparison.Ordinal);
        Assert.All(main.Capabilities, capability => Assert.Contains($"\n - {capability.Id}: ", instructions, StringComparison.Ordinal));
        // Ru's base contract is the advisor request's own system prompt; it is not quoted twice.
        Assert.Contains("## ru.persona", full, StringComparison.Ordinal);
        Assert.DoesNotContain("## ru.persona", instructions, StringComparison.Ordinal);
        output.WriteLine($"full={full.Length} chars, advisor={instructions.Length} chars");
        Assert.True(instructions.Length < full.Length);
    }

    /// <summary>A prompt context advertising the whole current tool profile, as a real root run does.</summary>
    private static SystemPromptContext FullPromptContext()
    {
        var capabilities = AgentToolDefinitionCatalog.Get()
            .Where(definition => AgentToolProfiles.Includes(AgentToolProfiles.Current, definition.ToolId))
            .Select(definition => new PromptCapability(definition.ToolId, definition.ToolVersion, definition.Description,
                definition.Scope, AgentToolDefinitionCatalog.CanonicalSchemaJson(definition.InputSchema),
                AgentToolDefinitionCatalog.ComputeSchemaDigest(definition.InputSchema), null, "enabled"))
            .ToArray();
        return new SystemPromptContext(2, "ru-agent", "standard",
            new PromptEnvironment("longcat-2.5-free", "windows", "vi-VN", "UTC", "2026-11-14", "fixture"),
            [], capabilities, new PromptRuntimeContext([]));
    }

    [Fact]
    public void The_users_and_workspaces_own_instructions_reach_the_advisor()
    {
        const string guidance = "Always run the tests before finishing.";
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(guidance))).ToLowerInvariant();
        var block = new PromptInstructionBlock("workspace:AGENTS.md", "AGENTS.md", guidance, hash);
        var reference = new PromptReference("project_memory", "project memory", "The billing module uses integer cents.");
        var run = new SystemPromptContext(2, "ru-agent", "standard",
            new PromptEnvironment("longcat-2.5-free", "windows", "vi-VN", "UTC", "2026-11-14", "fixture"),
            [block], [], new PromptRuntimeContext([reference]));

        var instructions = AdvisorTranscript.Instructions(run);

        Assert.Contains("[AGENTS.md (workspace:AGENTS.md)]\n" + guidance, instructions, StringComparison.Ordinal);
        Assert.Contains("[runtime reference: project_memory; project memory]\nThe billing module uses integer cents.",
            instructions, StringComparison.Ordinal);
    }

    [Fact]
    public void The_transcript_keeps_the_runtime_snapshot_the_agent_saw()
    {
        var user = ModelContextEntry.Text("user", 1, ModelContextRole.User, "Fix the bug.");
        var snapshot = ModelContextEntry.Text(RuntimeContextSnapshot.SourcePrefix + "one", 2, ModelContextRole.User,
            "background task build_1 is still running");
        var context = new ModelContextSnapshot([user, snapshot], ReadOnlyMemory<byte>.Empty);

        var entries = AdvisorTranscript.Entries(new AdvisorConsultationSource(context, MainPromptContext(), null));

        Assert.Contains(entries, entry => entry.Contains("background task build_1 is still running", StringComparison.Ordinal));
        Assert.Contains(entries, entry => entry.Contains("Fix the bug.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_directive_asks_for_complete_prioritized_advice_without_a_length_cap()
    {
        var handler = new Handler();
        using var http = new HttpClient(handler);
        var runner = new InProcessModelStepRunner(new ModelStepService(new OpenAiCompatibleModelProvider(http)))
        {
            ResolveAdvisorAsync = (_, _, _, _) => Task.FromResult(new AdvisorTarget(AdvisorBootstrap(), "deepseek-v4.1-flash")),
        };

        await runner.ConsultAsync(Consultation(Session), default);

        var messages = System.Text.Json.Nodes.JsonNode.Parse(Assert.Single(handler.Bodies))!["messages"]!.AsArray();
        var request = messages.Last(message => message!["role"]!.GetValue<string>() == "user")!["content"]!.GetValue<string>();
        var directive = request[..request.IndexOf("<transcript>", StringComparison.Ordinal)];
        Assert.Contains("include everything that does", directive, StringComparison.Ordinal);
        Assert.DoesNotContain("words", directive, StringComparison.Ordinal);
    }

    private static AdvisorConsultationRequest Consultation(SessionId? session) => new(
        new RunId("run_" + new string('a', 32)), new ModelStepId("step_" + new string('b', 32)), Source(), null, null, session);

    private static ProviderBootstrapData AdvisorBootstrap() => new(
        "models.dev:opencode-go", "chat_completions",
        new Uri("https://opencode.ai/inference/go/openai/v1/chat/completions"), "deepseek-v4.1-flash", "synthetic",
        // What ModelOutputBudget pins for a 1M window that publishes a 384K output limit.
        maximumOutputTokens: 128_000, contextWindowTokens: 1_000_000, outputTokenReserve: 128_000,
        sessionHeaderName: "x-opencode-session");

    private static SystemPromptContext MainPromptContext()
    {
        var definition = AgentToolDefinitionCatalog.Find("read", "1.0.0")!;
        var capability = new PromptCapability(definition.ToolId, definition.ToolVersion, definition.Description, definition.Scope,
            AgentToolDefinitionCatalog.CanonicalSchemaJson(definition.InputSchema),
            AgentToolDefinitionCatalog.ComputeSchemaDigest(definition.InputSchema), null, "enabled");
        return new SystemPromptContext(2, "ru-agent", "standard",
            new PromptEnvironment("longcat-2.5-free", "windows", "vi-VN", "UTC", "2026-11-14", "fixture"),
            [], [capability], new PromptRuntimeContext([]));
    }

    private static AdvisorConsultationSource Source()
    {
        var user = ModelContextEntry.Text("user", 2, ModelContextRole.User, "Fix pricing.py, consult the advisor first.");
        var state = ModelContextEntry.Text(SessionWorkState.ContextSourceId, 1, ModelContextRole.User, "workflow state");
        using var arguments = System.Text.Json.JsonDocument.Parse("{\"path\":\"pricing.py\"}");
        var step = new AssistantStep("Reading the file.",
            [new ModelToolCall("read_1", "read", "1.0.0", arguments.RootElement.Clone())], AssistantFinishReason.ToolCalls, 16);
        var assistant = ModelContextEntry.Assistant("assistant", 3, step);
        var receipt = new ToolExecutionReceipt(new ExecutionId("exec_" + new string('d', 32)),
            new ToolCallId("call_" + new string('e', 32)), ToolExecutionOutcome.Succeeded, "ok", null, false, providerCallId: "read_1");
        var tool = ModelContextEntry.ToolResult("result", 4, receipt, "def total_price(items, discount_percent): ...");
        return new AdvisorConsultationSource(
            new ModelContextSnapshot([state, user, assistant, tool], ReadOnlyMemory<byte>.Empty),
            MainPromptContext(),
            "I will ask the advisor.");
    }

    private sealed class Handler : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        public List<string> Sessions { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            if (request.Headers.TryGetValues("x-opencode-session", out var values)) Sessions.AddRange(values);
            const string json = """{"choices":[{"delta":{"content":"Apply the percentage."},"finish_reason":"stop"}]}""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("data: " + json + "\n\ndata: [DONE]\n\n", Encoding.UTF8, "text/event-stream"),
            };
        }
    }
}
