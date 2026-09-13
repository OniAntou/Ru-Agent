using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Harness.Core.Prompting;
using Xunit;

namespace RuAgent.Harness.Core.Tests.Prompting;

/// <summary>
/// Golden parity against the deterministic Python reference captured in
/// tests/fixtures/python-csharp-phase1-baseline.json. No live model, network,
/// audio or device state participates in these assertions.
/// </summary>
public sealed class Phase1GoldenParityTests
{
    [Fact]
    public void Prompt_composition_remains_deterministic_with_canonical_capabilities()
    {
        var fixture = LoadFixture();
        var promptCase = FindCase(fixture, "prompt.basic");
        var context = LoadPromptContext(promptCase["input"]!);

        var snapshot = SystemPromptComposer.Compose(context);
        var second = SystemPromptComposer.Compose(context);
        Assert.Equal(snapshot.Text, second.Text);
        Assert.Equal(snapshot.Receipt.PromptSha256, Sha256(snapshot.Text));
        Assert.Equal(
            promptCase["expected"]!["prompt_sha256"]!.GetValue<string>(),
            snapshot.Receipt.PromptSha256);
        Assert.Equal(
            ((JsonArray)promptCase["expected"]!["block_digests"]!).Select(item =>
                (item!["block_id"]!.GetValue<string>(), item["sha256"]!.GetValue<string>())),
            snapshot.Receipt.BlockDigests.Select(item => (item.BlockId, item.Sha256)));
        Assert.Contains("TOOL SELECTION CONTRACT:", snapshot.Text, StringComparison.Ordinal);

        // Instruction digests stay bound to the context that declared them.
        foreach (var instruction in context.InstructionBlocks)
        {
            var digest = snapshot.Receipt.BlockDigests.Single(
                item => item.BlockId == $"instruction.{instruction.SourceId}");
            Assert.Equal(instruction.ContentSha256, digest.Sha256);
        }

        var marker = promptCase["expected"]!["private_marker"]!.GetValue<string>();
        Assert.DoesNotContain(marker, snapshot.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(
            marker,
            JsonSerializer.Serialize(snapshot.Receipt),
            StringComparison.Ordinal);

        snapshot.Receipt.ValidateAgainst(context, snapshot.Text);
    }

    [Fact]
    public void Tool_selection_parser_matches_the_python_golden_fixture()
    {
        var fixture = LoadFixture();
        var toolCase = FindCase(fixture, "tool_selection.valid");
        var input = toolCase["input"]!;
        var advertised = ((JsonArray)input["advertised"]!)
            .Select(item => item!.GetValue<string>())
            .ToHashSet(StringComparer.Ordinal);

        var selection = ModelToolSelectionParser.Parse(
            input["text"]!.GetValue<string>(),
            advertised);

        Assert.Equal(input["tool_id"]!.GetValue<string>(), selection.ToolId);
        Assert.Equal(input["tool_version"]!.GetValue<string>(), selection.ToolVersion);
        Assert.Equal("README.md", selection.Arguments.GetProperty("path").GetString());
        Assert.Equal(64, selection.Arguments.GetProperty("limit").GetInt32());
        Assert.Contains(selection.ToolId, advertised);
    }

    [Fact]
    public void Fixture_keeps_all_phase_one_groups_and_the_measurement_contract()
    {
        var fixture = LoadFixture();

        Assert.Equal(
            new HashSet<string>
            {
                "prompt", "compaction", "tool_selection", "protocol", "cancellation", "privacy",
            },
            ((JsonArray)fixture["cases"]!).Select(item => item!["group"]!.GetValue<string>()).ToHashSet());
        Assert.Equal(
            ((JsonArray)fixture["cases"]!).Count,
            ((JsonArray)fixture["cases"]!).Select(item => item!["id"]!.GetValue<string>()).Distinct().Count());

        var measurement = fixture["measurement"]!;
        Assert.True(measurement["min_samples"]!.GetValue<int>() >= 20);
        Assert.Equal(
            new HashSet<string> { "prompt.compose", "tool_selection.parse" },
            ((JsonObject)measurement["baseline"]!).Select(item => item.Key).ToHashSet(StringComparer.Ordinal));
    }

    private static JsonNode LoadFixture()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "tests",
                "fixtures",
                "python-csharp-phase1-baseline.json");
            if (File.Exists(candidate))
            {
                return JsonNode.Parse(File.ReadAllText(candidate))!;
            }

            directory = directory.Parent!;
        }

        throw new InvalidOperationException(
            "The Phase 1 baseline fixture was not found above the test output directory.");
    }

    private static JsonNode FindCase(JsonNode fixture, string caseId) =>
        ((JsonArray)fixture["cases"]!).Single(item => item!["id"]!.GetValue<string>() == caseId)!;

    private static SystemPromptContext LoadPromptContext(JsonNode input)
    {
        var environment = input["environment"]!;
        var instructions = new List<PromptInstructionBlock>();
        foreach (var block in (JsonArray)input["instruction_blocks"]!)
        {
            instructions.Add(new PromptInstructionBlock(
                block!["source_id"]!.GetValue<string>(),
                block["display_name"]!.GetValue<string>(),
                block["content"]!.GetValue<string>(),
                block["content_sha256"]!.GetValue<string>()));
        }

        var capabilities = new List<PromptCapability>();
        foreach (var capability in (JsonArray)input["capabilities"]!)
        {
            capabilities.Add(new PromptCapability(
                capability!["id"]!.GetValue<string>(),
                capability["version"]!.GetValue<string>(),
                capability["description"]!.GetValue<string>(),
                capability["scope"]!.GetValue<string>(),
                capability["input_schema_json"]!.GetValue<string>(),
                capability["input_schema_digest"]!.GetValue<string>(),
                OptionalString(capability["example_arguments_json"]),
                capability["availability"]!.GetValue<string>()));
        }

        var runtime = input["runtime_context"]!;
        var references = new List<PromptReference>();
        foreach (var reference in (JsonArray)(runtime["references"] ?? new JsonArray())!)
        {
            references.Add(new PromptReference(
                reference!["kind"]!.GetValue<string>(),
                reference["label"]!.GetValue<string>(),
                reference["content"]!.GetValue<string>()));
        }

        return new SystemPromptContext(
            input["schema_version"]!.GetValue<int>(),
            input["agent_profile"]!.GetValue<string>(),
            input["preset"]!.GetValue<string>(),
            new PromptEnvironment(
                environment["model"]!.GetValue<string>(),
                environment["platform"]!.GetValue<string>(),
                environment["locale"]!.GetValue<string>(),
                environment["timezone"]!.GetValue<string>(),
                environment["date"]!.GetValue<string>(),
                OptionalString(environment["workspace_label"])),
            instructions,
            capabilities,
            new PromptRuntimeContext(
                OptionalString(runtime["mood"]),
                OptionalString(runtime["hardware"]),
                references));
    }

    private static string? OptionalString(JsonNode? node) => node is null ? null : node.GetValue<string>();

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(new UTF8Encoding(false).GetBytes(value))).ToLowerInvariant();
}
