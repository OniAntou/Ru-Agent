using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Policy;
using Xunit;

namespace RuAgent.Harness.Core.Tests.Conformance;

/// <summary>
/// Shared conformance contract for <see cref="IToolGateway"/> adapters. Every
/// production adapter (current in-process gateway, future Broker-backed
/// adapter) subclasses this with its own gateway configuration so the same
/// seam contract runs against each of them. Read-only tool semantics only;
/// mutation and terminal facts are added when their adapters migrate.
/// </summary>
public abstract class ToolGatewayConformance : IDisposable
{
    private readonly string _workspaceRoot;
    private readonly List<IToolGateway> _extraGateways = [];

    protected ToolGatewayConformance()
    {
        _workspaceRoot = CreateWorkspace();
        Gateway = CreateGatewayAsync(_workspaceRoot, StandardPreset).GetAwaiter().GetResult();
    }

    protected IToolGateway Gateway { get; }

    protected abstract Task<IToolGateway> CreateGatewayAsync(string workspaceRoot, string preset);

    protected const string StandardPreset = "standard";

    protected const string FullAccessPreset = "full_access";

    protected virtual IReadOnlyList<string> AdvertisedCapabilities { get; } =
    [
        "help.docs",
        "files.list",
        "files.glob",
        "files.search",
        "files.read",
        "files.write",
        "files.edit",
        "files.delete",
        "files.restore",
        "terminal.run",
        "terminal.shell",
    ];

    protected virtual string ToolVersion => "1.0.0";

    protected virtual bool StreamsToolOutputDeltas => true;

    protected virtual void DisposeGateway()
    {
    }

    public void Dispose()
    {
        DisposeGateway();
        foreach (var gateway in _extraGateways)
        {
            if (gateway is IAsyncDisposable asyncDisposable)
            {
                asyncDisposable.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            else if (gateway is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }

        try
        {
            Directory.Delete(_workspaceRoot, recursive: true);
        }
        catch (Exception)
        {
            // best-effort cleanup: a leaked temp dir must not fail the test
        }
    }

    // ---- capability boundary ----

    [Fact]
    public async Task UnadvertisedToolId_is_denied_as_phase_not_available()
    {
        var decision = await Gateway.EvaluateAsync(Proposal("web.search", []), CancellationToken.None);

        Assert.Equal(PolicyDecision.Deny, decision.Decision);
        Assert.Equal("phase_not_available", decision.SafeCode);
    }

    [Fact]
    public async Task UnknownToolId_is_denied_as_phase_not_available()
    {
        var decision = await Gateway.EvaluateAsync(Proposal("totally.unknown", []), CancellationToken.None);

        Assert.Equal(PolicyDecision.Deny, decision.Decision);
        Assert.Equal("phase_not_available", decision.SafeCode);
    }

    // ---- canonical proposal invariants (migrated from the Host gateway) ----

    [Fact]
    public async Task Delete_rejects_preimage_hash_in_model_arguments()
    {
        var decision = await Gateway.EvaluateAsync(
            Proposal("files.delete", new JsonObject
            {
                ["path"] = "notes/a.txt",
                ["expected_sha256"] = new string('a', 64),
            }),
            CancellationToken.None);

        Assert.Equal(PolicyDecision.Deny, decision.Decision);
        Assert.Equal("invalid_arguments", decision.SafeCode);
    }

    [Fact]
    public async Task Search_rejects_the_retired_regex_argument_name()
    {
        var decision = await Gateway.EvaluateAsync(
            Proposal("files.search", new JsonObject
            {
                ["query"] = "needle",
                ["regex"] = false,
            }),
            CancellationToken.None);

        Assert.Equal(PolicyDecision.Deny, decision.Decision);
        Assert.Equal("invalid_arguments", decision.SafeCode);
    }

    [Fact]
    public async Task HelpDocs_executes_read_only_within_bounds()
    {
        var proposal = Proposal("help.docs", new JsonObject
        {
            ["query"] = "approval",
            ["max_results"] = 1,
        });

        var decision = await Gateway.EvaluateAsync(proposal, CancellationToken.None);
        Assert.Equal(PolicyDecision.Allow, decision.Decision);
        Assert.Equal(ToolAccessKind.ReadOnly, decision.AccessKind);

        var prepared = await Gateway.PrepareAsync(
            new ApprovedToolExecution(proposal, NextExecutionId(), decision.CanonicalIntentToken!),
            CancellationToken.None);
        Assert.Empty(prepared.PreparationPayloads);

        var receipt = await Gateway.ExecuteAsync(
            prepared,
            _ => ValueTask.CompletedTask,
            CancellationToken.None);
        Assert.True(
            receipt.Outcome == ToolExecutionOutcome.Succeeded,
            $"help.docs execution failed: code={receipt.Code}");
        var output = ParseModelContent(receipt);
        Assert.Equal("approval", output["query"]!.GetValue<string>());
        Assert.True(output["results"]!.AsArray().Count <= 1);
    }

    // ---- evaluate: path boundary ----

    [Fact]
    public async Task RelativeTraversal_outside_root_is_denied_as_invalid_arguments()
    {
        var decision = await Gateway.EvaluateAsync(
            Proposal("files.read", new JsonObject { ["path"] = "notes/../../escape.txt" }),
            CancellationToken.None);

        Assert.Equal(PolicyDecision.Deny, decision.Decision);
        Assert.Equal("invalid_arguments", decision.SafeCode);
    }

    [Fact]
    public async Task AbsolutePath_outside_root_asks_external_directory_required()
    {
        var outsidePath = Path.Combine(Path.GetTempPath(), "conformance-outside-target.txt");

        var decision = await Gateway.EvaluateAsync(
            Proposal("files.read", new JsonObject { ["path"] = outsidePath }),
            CancellationToken.None);

        Assert.Equal(PolicyDecision.Ask, decision.Decision);
        Assert.Equal("external_directory_required", decision.SafeCode);
    }

    // ---- execute: files.read ----

    [Fact]
    public async Task FilesRead_returns_exact_content_with_metadata()
    {
        var receipt = await ExecuteAllowedAsync("files.read", new JsonObject { ["path"] = "notes/a.txt" });

        Assert.Equal(ToolExecutionOutcome.Succeeded, receipt.Outcome);
        Assert.Equal("ok", receipt.Code);
        Assert.False(receipt.SideEffectsPossible);

        var output = ParseModelContent(receipt);
        Assert.Equal("notes/a.txt", output["path"]!.GetValue<string>());
        Assert.Equal("alpha", output["content"]!.GetValue<string>());
        Assert.False(output["truncated"]!.GetValue<bool>());
        Assert.Equal(5, output["bytes_read"]!.GetValue<int>());
        Assert.Equal(5, output["total_bytes"]!.GetValue<long>());
    }

    [Fact]
    public async Task FilesRead_missing_file_fails_with_file_not_found()
    {
        var receipt = await ExecuteAllowedAsync("files.read", new JsonObject { ["path"] = "notes/missing.txt" });

        Assert.Equal(ToolExecutionOutcome.Failed, receipt.Outcome);
        Assert.Equal("file_not_found", receipt.Code);
        Assert.False(receipt.SideEffectsPossible);
        Assert.Equal("file_not_found", ParseModelContent(receipt)["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task FilesRead_invalid_utf8_fails_as_invalid_utf8()
    {
        var receipt = await ExecuteAllowedAsync("files.read", new JsonObject { ["path"] = "notes/data.bin" });

        Assert.Equal(ToolExecutionOutcome.Failed, receipt.Outcome);
        Assert.Equal("invalid_utf8", receipt.Code);
        Assert.False(receipt.SideEffectsPossible);
        Assert.Equal("invalid_utf8", ParseModelContent(receipt)["error"]!.GetValue<string>());
    }

    // ---- execute: discovery tools ----

    [Fact]
    public async Task FilesList_returns_entries_with_relative_paths()
    {
        var receipt = await ExecuteAllowedAsync("files.list", new JsonObject { ["path"] = "notes" });

        Assert.Equal(ToolExecutionOutcome.Succeeded, receipt.Outcome);
        var output = ParseModelContent(receipt);
        Assert.Equal(3, output["count"]!.GetValue<int>());

        var paths = output["entries"]!
            .AsArray()
            .Select(entry => entry!["path"]!.GetValue<string>())
            .ToArray();
        Assert.Equal(["notes/a.txt", "notes/b.txt", "notes/data.bin"], paths);
        Assert.All(
            output["entries"]!.AsArray(),
            entry => Assert.Equal("file", entry!["kind"]!.GetValue<string>()));
    }

    [Fact]
    public async Task FilesGlob_returns_sorted_relative_matches()
    {
        var receipt = await ExecuteAllowedAsync(
            "files.glob",
            new JsonObject { ["path"] = ".", ["pattern"] = "notes/*.txt" });

        Assert.Equal(ToolExecutionOutcome.Succeeded, receipt.Outcome);
        var output = ParseModelContent(receipt);
        Assert.Equal(2, output["count"]!.GetValue<int>());

        var results = output["results"]!.AsArray().Select(item => item!.GetValue<string>()).ToArray();
        Assert.Equal(["notes/a.txt", "notes/b.txt"], results);
    }

    [Fact]
    public async Task FilesSearch_finds_substring_matches()
    {
        var receipt = await ExecuteAllowedAsync(
            "files.search",
            new JsonObject { ["query"] = "beta" });

        Assert.Equal(ToolExecutionOutcome.Succeeded, receipt.Outcome);
        var output = ParseModelContent(receipt);
        Assert.True(
            output["results"]!.AsArray().Any(),
            "expected at least one search match for 'beta'");
    }

    // ---- execute lifecycle contract ----

    [Fact]
    public async Task ForgedPreparedExecution_fails_as_decision_stale()
    {
        var forged = new PreparedToolExecution(
            new ExecutionId(Identifier("exec_")),
            new ToolCallId(Identifier("call_")),
            modelOrder: 1,
            ToolAccessKind.ReadOnly,
            preparationToken: "forged-token",
            []);

        var receipt = await Gateway.ExecuteAsync(forged, _ => ValueTask.CompletedTask, CancellationToken.None);

        Assert.Equal(ToolExecutionOutcome.Failed, receipt.Outcome);
        Assert.Equal("decision_stale", receipt.Code);
    }

    [Fact]
    public async Task Cancel_unknown_execution_is_tolerated()
    {
        await Gateway.CancelAsync(new ExecutionId(Identifier("exec_")), CancellationToken.None);
    }

    [Fact]
    public async Task Execute_streams_sequenced_stdout_chunks_matching_model_content()
    {
        if (!StreamsToolOutputDeltas)
        {
            return;
        }

        var proposal = Proposal("files.read", new JsonObject { ["path"] = "notes/b.txt" });
        var decision = await Gateway.EvaluateAsync(proposal, CancellationToken.None);
        Assert.Equal(PolicyDecision.Allow, decision.Decision);

        var prepared = await Gateway.PrepareAsync(
            new ApprovedToolExecution(proposal, new ExecutionId(Identifier("exec_")), decision.CanonicalIntentToken!),
            CancellationToken.None);

        var chunks = new List<ToolOutputChunk>();
        var receipt = await Gateway.ExecuteAsync(
            prepared,
            chunk =>
            {
                chunks.Add(chunk);
                return ValueTask.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal(ToolExecutionOutcome.Succeeded, receipt.Outcome);
        Assert.NotEmpty(chunks);
        Assert.True(
            chunks.Select(chunk => chunk.Sequence)
                .SequenceEqual(Enumerable.Range(1, chunks.Count).Select(index => (long)index)));
        Assert.All(chunks, chunk => Assert.Equal(ToolOutputStream.Stdout, chunk.Stream));

        var streamed = string.Concat(chunks.Select(chunk => chunk.Content));
        Assert.Equal(receipt.ModelContent.ToArray(), Encoding.UTF8.GetBytes(streamed));
    }

    // ---- execute: mutations ----

    [Fact]
    public async Task FilesWrite_creates_file_with_preimage_and_after_hash()
    {
        var receipt = await ExecuteAllowedAsync("files.write", new JsonObject
        {
            ["path"] = "notes/created.txt",
            ["content"] = "fresh",
        });

        Assert.Equal(ToolExecutionOutcome.Succeeded, receipt.Outcome);
        Assert.Equal("ok", receipt.Code);
        Assert.True(receipt.SideEffectsPossible);

        var output = ParseModelContent(receipt);
        Assert.Equal("notes/created.txt", output["path"]!.GetValue<string>());
        Assert.Equal("missing", output["before_sha256"]!.GetValue<string>());
        Assert.Equal(Sha256("fresh"), output["after_sha256"]!.GetValue<string>());

        var readBack = await ExecuteAllowedAsync("files.read", new JsonObject { ["path"] = "notes/created.txt" });
        Assert.Equal("fresh", ParseModelContent(readBack)["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task FilesWrite_existing_without_overwrite_fails_file_exists()
    {
        var receipt = await ExecuteAllowedAsync("files.write", new JsonObject
        {
            ["path"] = "notes/a.txt",
            ["content"] = "clash",
        });

        Assert.Equal(ToolExecutionOutcome.Failed, receipt.Outcome);
        Assert.Equal("file_exists", receipt.Code);
        Assert.False(receipt.SideEffectsPossible);
    }

    [Fact]
    public async Task FilesEdit_replaces_expected_occurrences()
    {
        var receipt = await ExecuteAllowedAsync("files.edit", new JsonObject
        {
            ["path"] = "notes/b.txt",
            ["old_string"] = "beta",
            ["new_string"] = "gamma",
            ["expected_occurrences"] = 2,
        });

        Assert.Equal(ToolExecutionOutcome.Succeeded, receipt.Outcome);
        Assert.True(receipt.SideEffectsPossible);

        var readBack = await ExecuteAllowedAsync("files.read", new JsonObject { ["path"] = "notes/b.txt" });
        Assert.Equal("gamma\nsecond line gamma\n", ParseModelContent(readBack)["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task FilesDelete_allows_inside_workspace_in_standard()
    {
        var decision = await Gateway.EvaluateAsync(
            Proposal("files.delete", new JsonObject { ["path"] = "notes/a.txt" }),
            CancellationToken.None);

        Assert.Equal(PolicyDecision.Allow, decision.Decision);
    }

    [Fact]
    public async Task FullAccess_delete_restore_roundtrip_is_one_time()
    {
        var gateway = await CreatePresetGatewayAsync(FullAccessPreset);

        var created = await ExecuteOnAsync(
            gateway,
            "files.write",
            new JsonObject { ["path"] = "doomed.txt", ["content"] = "doomed" });
        Assert.Equal(ToolExecutionOutcome.Succeeded, created.Outcome);

        var deleted = await ExecuteOnAsync(
            gateway,
            "files.delete",
            new JsonObject { ["path"] = "doomed.txt" });
        Assert.Equal(ToolExecutionOutcome.Succeeded, deleted.Outcome);
        var deleteOutput = ParseModelContent(deleted);
        Assert.Equal("doomed.txt", deleteOutput["path"]!.GetValue<string>());
        Assert.Equal(Sha256("doomed"), deleteOutput["before_sha256"]!.GetValue<string>());
        var recoveryId = deleteOutput["recovery_id"]!.GetValue<string>();

        var gone = await ExecuteOnAsync(gateway, "files.read", new JsonObject { ["path"] = "doomed.txt" });
        Assert.Equal(ToolExecutionOutcome.Failed, gone.Outcome);
        Assert.Equal("file_not_found", gone.Code);

        var restored = await ExecuteOnAsync(
            gateway,
            "files.restore",
            new JsonObject { ["recovery_id"] = recoveryId });
        Assert.Equal(ToolExecutionOutcome.Succeeded, restored.Outcome);
        Assert.Equal("doomed.txt", ParseModelContent(restored)["path"]!.GetValue<string>());

        var readBack = await ExecuteOnAsync(gateway, "files.read", new JsonObject { ["path"] = "doomed.txt" });
        Assert.Equal("doomed", ParseModelContent(readBack)["content"]!.GetValue<string>());

        var replay = await ExecuteOnAsync(
            gateway,
            "files.restore",
            new JsonObject { ["recovery_id"] = recoveryId });
        Assert.Equal(ToolExecutionOutcome.Failed, replay.Outcome);
        Assert.Equal("recovery_unavailable", replay.Code);
    }

    // ---- execute: terminal ----

    [Fact]
    public async Task Terminal_run_standard_analyzer_safe_form_is_allowed_and_executes()
    {
        var receipt = await ExecuteAllowedAsync("terminal.run", new JsonObject
        {
            ["executable"] = "dotnet",
            ["arguments"] = new JsonArray("--version"),
        });

        Assert.Equal(ToolExecutionOutcome.Succeeded, receipt.Outcome);
        Assert.Equal("ok", receipt.Code);
        Assert.False(receipt.ModelContent.IsEmpty);
    }

    [Fact]
    public async Task Terminal_unknown_executable_requires_approval_in_standard()
    {
        var decision = await Gateway.EvaluateAsync(
            Proposal("terminal.run", new JsonObject
            {
                ["executable"] = "cmd.exe",
                ["arguments"] = new JsonArray("/c", "echo", "conformance"),
            }),
            CancellationToken.None);

        Assert.Equal(PolicyDecision.Ask, decision.Decision);
        Assert.Equal("terminal_unknown_executable", decision.SafeCode);
    }

    // ---- helpers ----

    protected ToolProposal Proposal(string toolId, JsonObject arguments)
    {
        return new ToolProposal(
            new RunId(Identifier("run_")),
            new ModelStepId(Identifier("step_")),
            new ToolCallId(Identifier("call_")),
            modelOrder: 1,
            toolId,
            ToolVersion,
            JsonSerializer.SerializeToElement(arguments));
    }

    protected async Task<ToolExecutionReceipt> ExecuteAllowedAsync(string toolId, JsonObject arguments)
    {
        return await ExecuteOnAsync(Gateway, toolId, arguments);
    }

    protected async Task<IToolGateway> CreatePresetGatewayAsync(string preset)
    {
        var gateway = await CreateGatewayAsync(_workspaceRoot, preset);
        _extraGateways.Add(gateway);
        return gateway;
    }

    protected async Task<ToolExecutionReceipt> ExecuteOnAsync(
        IToolGateway gateway,
        string toolId,
        JsonObject arguments)
    {
        var proposal = Proposal(toolId, arguments);
        var decision = await gateway.EvaluateAsync(proposal, CancellationToken.None);
        Assert.True(
            decision.Decision == PolicyDecision.Allow,
            $"expected Allow for {toolId}, got {decision.Decision}: {decision.SafeCode}");

        var prepared = await gateway.PrepareAsync(
            new ApprovedToolExecution(proposal, NextExecutionId(), decision.CanonicalIntentToken!),
            CancellationToken.None);

        return await gateway.ExecuteAsync(prepared, _ => ValueTask.CompletedTask, CancellationToken.None);
    }

    protected static JsonNode ParseModelContent(ToolExecutionReceipt receipt)
    {
        return JsonNode.Parse(Encoding.UTF8.GetString(receipt.ModelContent.Span))
            ?? throw new InvalidOperationException("receipt model content is empty");
    }

    private static string Identifier(string prefix) => prefix + new string('0', 32);

    private int _executionSequence;

    private ExecutionId NextExecutionId()
    {
        _executionSequence++;
        return new ExecutionId("exec_" + new string('0', 30) + _executionSequence.ToString("D2", System.Globalization.CultureInfo.InvariantCulture));
    }

    protected static string Sha256(string content) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    private static string CreateWorkspace()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-conformance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "notes"));
        File.WriteAllText(Path.Combine(root, "notes", "a.txt"), "alpha");
        File.WriteAllText(Path.Combine(root, "notes", "b.txt"), "beta\nsecond line beta\n");
        File.WriteAllBytes(Path.Combine(root, "notes", "data.bin"), [0x68, 0x69, 0xFF, 0x21]);
        return root;
    }
}
