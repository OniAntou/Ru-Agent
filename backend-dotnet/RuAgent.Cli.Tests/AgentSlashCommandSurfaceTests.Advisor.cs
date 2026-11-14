using System.Text.Json.Nodes;
using RuAgent.Cli;
using RuAgent.Cli.Tui;
using Xunit;

namespace RuAgent.Cli.Tests;

/// <summary>ADR 0047: <c>/advisor</c> saves the advisor as a pair and applies to new runs.</summary>
public sealed partial class AgentSlashCommandSurfaceTests
{
    [Theory]
    [InlineData("claude-opus-5-5", "openai", "claude-opus-5-5")]
    [InlineData("anthropic claude-opus-5-5", "Anthropic", "claude-opus-5-5")]
    [InlineData("openrouter anthropic/claude-opus-5-5", "models.dev:openrouter", "anthropic/claude-opus-5-5")]
    public async Task Advisor_WithAnArgument_PostsTheBackendAndModelTogether(string arg, string backend, string model)
    {
        var output = new StringWriter();
        var posted = new List<string>();
        var commands = Create(output, request => Router(request, ReasoningCatalog, posted));

        Assert.True(await commands.TryHandleAsync("/advisor", arg, "sess_1", "standard", "D:\\LLM", CancellationToken.None));

        var body = JsonNode.Parse(Assert.Single(posted))!.AsObject();
        Assert.Equal(2, body.Count);
        Assert.Equal(backend, body["advisor_backend"]!.GetValue<string>());
        Assert.Equal(model, body["advisor_model"]!.GetValue<string>());
        Assert.Contains("for new runs", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Advisor_Off_ClearsBothFields()
    {
        var output = new StringWriter();
        var posted = new List<string>();
        var commands = Create(output, request => Router(request, ReasoningCatalog, posted));

        await commands.TryHandleAsync("/advisor", "off", "sess_1", "standard", "D:\\LLM", CancellationToken.None);

        var body = JsonNode.Parse(Assert.Single(posted))!.AsObject();
        Assert.Equal(2, body.Count);
        Assert.Null(body["advisor_backend"]);
        Assert.Null(body["advisor_model"]);
        Assert.Contains("advisor: off", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Advisor_RejectsAModelWithSpacesWithoutSaving()
    {
        var output = new StringWriter();
        var posted = new List<string>();
        var commands = Create(output, request => Router(request, ReasoningCatalog, posted));

        await commands.TryHandleAsync("/advisor", "anthropic claude opus", "sess_1", "standard", "D:\\LLM", CancellationToken.None);

        Assert.Empty(posted);
    }

    [Fact]
    public async Task Advisor_BareFormUnderJson_RefusesInsteadOfPicking()
    {
        var output = new StringWriter();
        var posted = new List<string>();
        var commands = Create(output, request => Router(request, ReasoningCatalog, posted));
        commands.Json = true;
        commands.Picker = (_, _) => throw new InvalidOperationException("--json must never open a picker");

        await commands.TryHandleAsync("/advisor", null, "sess_1", "standard", "D:\\LLM", CancellationToken.None);

        Assert.Empty(posted);
        Assert.Empty(output.ToString());
    }

    [Fact]
    public async Task Advisor_WithoutAPicker_ShowsTheCurrentAdvisorAndSavesNothing()
    {
        var output = new StringWriter();
        var posted = new List<string>();
        var commands = Create(output, request => Router(request, ReasoningCatalog, posted));

        await commands.TryHandleAsync("/advisor", null, "sess_1", "standard", "D:\\LLM", CancellationToken.None);

        var text = output.ToString();
        Assert.Contains("╭─ advisor", text, StringComparison.Ordinal);
        Assert.Contains("off", text, StringComparison.Ordinal);
        Assert.Empty(posted);
    }

    [Fact]
    public async Task Advisor_PickerChoosesAProviderThenAModel()
    {
        var output = new StringWriter();
        var posted = new List<string>();
        var commands = Create(output, request => Router(request, ReasoningCatalog, posted));
        var opened = new List<ChoiceList>();
        var answers = new Queue<string?>(["openai", "gpt-5"]);
        commands.Picker = (list, _) =>
        {
            opened.Add(list);
            return Task.FromResult(answers.Dequeue());
        };

        await commands.TryHandleAsync("/advisor", null, "sess_1", "standard", "D:\\LLM", CancellationToken.None);

        Assert.Equal(2, opened.Count);
        var body = JsonNode.Parse(Assert.Single(posted))!.AsObject();
        Assert.Equal("openai", body["advisor_backend"]!.GetValue<string>());
        Assert.Equal("gpt-5", body["advisor_model"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Advisor_CancellingEitherPickerSavesNothing(int cancelAt)
    {
        var output = new StringWriter();
        var posted = new List<string>();
        var commands = Create(output, request => Router(request, ReasoningCatalog, posted));
        var step = 0;
        commands.Picker = (_, _) => Task.FromResult(step++ == cancelAt ? null : "openai");

        await commands.TryHandleAsync("/advisor", null, "sess_1", "standard", "D:\\LLM", CancellationToken.None);

        Assert.Empty(posted);
    }

    [Fact]
    public async Task Advisor_PickerOffChoiceClearsTheAdvisor()
    {
        var output = new StringWriter();
        var posted = new List<string>();
        var commands = Create(output, request => Router(request, ReasoningCatalog, posted));
        commands.Picker = (_, _) => Task.FromResult<string?>("off");

        await commands.TryHandleAsync("/advisor", null, "sess_1", "standard", "D:\\LLM", CancellationToken.None);

        var body = JsonNode.Parse(Assert.Single(posted))!.AsObject();
        Assert.Null(body["advisor_model"]);
    }

    [Fact]
    public void Advisor_IsListedAndPausedLikeTheOtherSettingsCommands()
    {
        Assert.Contains(ChatCommandCatalog.KnownCommands, c => c.Command == "/advisor");
        Assert.True(ChatSubmissionSnapshot.IsPausedControlCommand("/advisor off"));
        Assert.Contains("/advisor", string.Join('\n', TuiSurfaces.Help(80)), StringComparison.Ordinal);
    }
}
