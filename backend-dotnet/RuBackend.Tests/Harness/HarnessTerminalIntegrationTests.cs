using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Policy;
using RuBackend.Harness.Broker;
using Xunit;

namespace RuBackend.Tests.Harness;

/// <summary>
/// These tests spawn real compilers and long-lived processes; running them beside the
/// timing-sensitive delivery/diagnostic tests breaks their retries, so the whole
/// collection is pushed into the non-parallel tail of the run.
/// </summary>
[CollectionDefinition(nameof(TerminalProcessHeavyCollection), DisableParallelization = true)]
public sealed class TerminalProcessHeavyCollection;

/// <summary>
/// Task 5 phase 3: terminal.run/terminal.shell execute through the durable Core boundary.
/// The Host gateway is the headless execution authority: it resolves canonical command
/// intents, classifies recognized developer commands, denies malformed forms before any
/// adapter work, asks for unknown/shell/extended-timeout forms, streams bounded output as
/// transient chunks, and records final receipts with side-effect semantics.
/// </summary>
[Collection(nameof(TerminalProcessHeavyCollection))]
public sealed class HarnessTerminalIntegrationTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 25, 8, 0, 0, TimeSpan.Zero);

    // ------------------------------------------------------------------
    // End-to-end allowed commands through AgentRunCoordinator + store.
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("rg", 0, false)]
    [InlineData("dotnet_build", 0, true)]
    [InlineData("dotnet_test", 0, true)]
    public async Task Allowed_recognized_commands_execute_through_durable_core(
        string scenario,
        int expectedExitCode,
        bool expectedSideEffects)
    {
        var root = CreateWorkspace(out var markerFile);
        try
        {
            if (scenario == "dotnet_build")
            {
                File.WriteAllText(Path.Combine(root, "tiny.csproj"), TinyProject);
                File.WriteAllText(Path.Combine(root, "Program.cs"), "System.Console.WriteLine(\"ru-ok\");");
            }

            var arguments = scenario switch
            {
                "rg" => new JsonObject { ["executable"] = "rg", ["arguments"] = new JsonArray("--files") },
                "dotnet_build" => new JsonObject
                {
                    ["executable"] = "dotnet",
                    // nodeReuse:false and a private compiler instance keep the test from
                    // leaving background MSBuild/compiler processes that perturb later runs.
                    ["arguments"] = new JsonArray(
                        "build", "tiny.csproj", "-nologo", "-v", "q",
                        "-nodeReuse:false", "-p:UseSharedCompilation=false"),
                    ["timeout_ms"] = 300_000,
                },
                _ => new JsonObject
                {
                    ["executable"] = "dotnet",
                    ["arguments"] = new JsonArray("test", "--help"),
                },
            };

            async Task<(CoordinatorEnvironment Env, ToolExecutionReceipt Receipt, JsonElement Output)> RunStepOnceAsync()
            {
                var environment = NewEnvironment(root, advertisedTerminal: true);
                var runner = new ScriptedModelRunner(
                    new AssistantStep(null, new[] { ModelToolCall(arguments) }, AssistantFinishReason.ToolCalls, 0),
                    new AssistantStep("done", Array.Empty<ModelToolCall>(), AssistantFinishReason.Stop, 0));
                var coordinator = environment.Coordinator(runner);
                var step = await coordinator.RunOneStepAsync(environment.RunId, CancellationToken.None);
                Assert.Null(step.ErrorCode);
                var stepReceipt = Assert.Single(step.Receipts);
                return (environment, stepReceipt, environment.SingleToolOutputPayload());
            }

            // The real dotnet CLI occasionally returns a nonzero exit under heavy machine
            // load even for `--help`; that is external CLI state, not gateway logic, so the
            // durable-boundary assertion retries once on a fresh run for this scenario only.
            var (env, receipt, output) = await RunStepOnceAsync();
            if (scenario == "dotnet_test"
                && (receipt.Outcome != ToolExecutionOutcome.Succeeded
                    || output.GetProperty("exit_code").GetInt32() != expectedExitCode))
            {
                (env, receipt, output) = await RunStepOnceAsync();
            }

            var actualExitCode = output.GetProperty("exit_code").GetInt32();
            Assert.True(
                receipt.Outcome == ToolExecutionOutcome.Succeeded && actualExitCode == expectedExitCode,
                $"scenario={scenario} outcome={receipt.Outcome} exit_code={actualExitCode} "
                + $"stdout_tail='{output.GetProperty("stdout_tail").GetString()}' "
                + $"stderr_head='{output.GetProperty("stderr_head").GetString()}' "
                + $"stderr_tail='{output.GetProperty("stderr_tail").GetString()}'");
            Assert.Equal(expectedSideEffects, receipt.SideEffectsPossible);
            if (scenario == "rg")
            {
                Assert.Contains(markerFile, output.GetProperty("stdout_head").GetString());
            }

            var events = await env.ReadEventsAsync();
            AssertEventOrder(events,
                ("tool.proposed", null),
                ("tool.execution_requested", receipt.ExecutionId.Value),
                ("tool.execution_completed", receipt.ExecutionId.Value));

            // Exactly one durable ToolOutput payload per executed call: transient deltas never persist.
            Assert.Equal(1, env.PayloadCount(HarnessPayloadKind.ToolOutput));

            // The next model step starts only after the final receipt is committed.
            var second = await env.Coordinator(
                new ScriptedModelRunner(
                    new AssistantStep("done", Array.Empty<ModelToolCall>(), AssistantFinishReason.Stop, 0)))
                .RunOneStepAsync(env.RunId, CancellationToken.None);
            Assert.Equal(HarnessRunState.WaitingHistory, second.Run.State);
            events = await env.ReadEventsAsync();
            Assert.True(
                SequenceOf(events, "tool.execution_completed") < SequenceOf(events, "model.step_started", occurrence: 2),
                "final receipt must be committed before the next model step starts");
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task Unknown_executable_and_shell_ask_in_standard_without_consuming_budget()
    {
        foreach (var arguments in new[]
                 {
                     new JsonObject { ["executable"] = "definitely-not-a-real-tool-xyz" },
                     new JsonObject { ["script"] = "Get-Date" },
                 })
        {
            var root = CreateWorkspace(out _);
            try
            {
                var environment = NewEnvironment(root, advertisedTerminal: true);
                var runner = new ScriptedModelRunner(
                    new AssistantStep(null, new[] { ModelToolCall(arguments) }, AssistantFinishReason.ToolCalls, 0));
                var coordinator = environment.Coordinator(runner);
                var before = await environment.RequireRunAsync();

                var result = await coordinator.RunOneStepAsync(environment.RunId, CancellationToken.None);

                Assert.Equal("approval_required", result.ErrorCode);
                Assert.Empty(result.Receipts);
                var after = await environment.RequireRunAsync();
                Assert.Equal(HarnessRunState.WaitingApproval, after.State);
                // The step's standard reservation is the only consumption: entering and
                // waiting in approval adds nothing beyond it.
                Assert.Equal(before.ToolCallsInWindow + 1, after.ToolCallsInWindow);
                Assert.Equal(before.ModelStepsInWindow + 1, after.ModelStepsInWindow);

                var events = await environment.ReadEventsAsync();
                Assert.Contains(events, e => e.Event.Kind == "approval.requested");
                Assert.DoesNotContain(events, e => e.Event.Kind == "tool.execution_requested");
                Assert.DoesNotContain(events, e => e.Event.Kind == "tool.execution_completed");

                // A blocked retry while waiting consumes no further budget.
                var blocked = await coordinator.RunOneStepAsync(environment.RunId, CancellationToken.None);
                Assert.Equal("run_not_runnable", blocked.ErrorCode);
                var afterBlocked = await environment.RequireRunAsync();
                Assert.Equal(after.ToolCallsInWindow, afterBlocked.ToolCallsInWindow);
                Assert.Equal(after.ModelStepsInWindow, afterBlocked.ModelStepsInWindow);
            }
            finally
            {
                TryDelete(root);
            }
        }
    }

    [Theory]
    [InlineData("nul_argument", "argument_nul_rejected")]
    [InlineData("interactive_flag", "interactive_flag_rejected")]
    [InlineData("background_daemon", "background_daemon_rejected")]
    [InlineData("hard_max_timeout", "timeout_exceeds_hard_max")]
    [InlineData("empty_executable", "executable_invalid")]
    public async Task Malformed_forms_are_denied_before_any_adapter_work(string scenario, string expectedCode)
    {
        var root = CreateWorkspace(out _);
        try
        {
            var arguments = scenario switch
            {
                "nul_argument" => new JsonObject
                {
                    ["executable"] = "rg",
                    ["arguments"] = new JsonArray("bad\0arg"),
                },
                "interactive_flag" => new JsonObject
                {
                    ["executable"] = "rg",
                    ["arguments"] = new JsonArray("--interactive", "pattern"),
                },
                "background_daemon" => new JsonObject
                {
                    ["executable"] = "rg",
                    ["arguments"] = new JsonArray("--daemon", "pattern"),
                },
                "hard_max_timeout" => new JsonObject
                {
                    ["executable"] = "rg",
                    ["arguments"] = new JsonArray("--files"),
                    ["timeout_ms"] = 1_800_001,
                },
                _ => new JsonObject { ["executable"] = " " },
            };

            var environment = NewEnvironment(root, advertisedTerminal: true);
            var runner = new ScriptedModelRunner(
                new AssistantStep(null, new[] { ModelToolCall(arguments) }, AssistantFinishReason.ToolCalls, 0));
            var coordinator = environment.Coordinator(runner);

            var result = await coordinator.RunOneStepAsync(environment.RunId, CancellationToken.None);

            Assert.Null(result.ErrorCode);
            var receipt = Assert.Single(result.Receipts);
            Assert.Equal(ToolExecutionOutcome.Failed, receipt.Outcome);
            Assert.False(receipt.SideEffectsPossible);
            Assert.Equal(expectedCode, receipt.Code);

            var events = await environment.ReadEventsAsync();
            Assert.DoesNotContain(events, e => e.Event.Kind == "tool.execution_requested");
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Theory]
    [InlineData(900_000, PolicyDecision.Allow)]
    [InlineData(901_000, PolicyDecision.Ask)]
    [InlineData(1_800_001, PolicyDecision.Deny)]
    public async Task Standard_timeout_matrix_follows_command_decision(
        int timeoutMs,
        PolicyDecision expected)
    {
        var root = CreateWorkspace(out _);
        try
        {
            var gateway = NewGateway(root, new[] { "terminal.run" });
            var decision = await gateway.EvaluateAsync(
                Proposal(new JsonObject
                {
                    ["executable"] = "rg",
                    ["arguments"] = new JsonArray("--files"),
                    ["timeout_ms"] = timeoutMs,
                }),
                CancellationToken.None);
            Assert.Equal(expected, decision.Decision);
            if (expected == PolicyDecision.Deny)
            {
                Assert.Equal("timeout_exceeds_hard_max", decision.SafeCode);
            }
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task Full_access_fallback_allows_well_formed_unknown_commands()
    {
        var root = CreateWorkspace(out _);
        try
        {
            var runId = FullAccessRunId();
            var gateway = NewGateway(root, new[] { "terminal.run" }, runId, AgentPermissionPreset.FullAccess);

            var allow = await gateway.EvaluateAsync(
                Proposal(new JsonObject
                {
                    ["executable"] = "ping",
                    ["arguments"] = new JsonArray("-n", "2", "127.0.0.1"),
                }, runId),
                CancellationToken.None);
            Assert.Equal(PolicyDecision.Allow, allow.Decision);

            var extended = await gateway.EvaluateAsync(
                Proposal(new JsonObject
                {
                    ["executable"] = "ping",
                    ["arguments"] = new JsonArray("-n", "2", "127.0.0.1"),
                    ["timeout_ms"] = 901_000,
                }, runId),
                CancellationToken.None);
            Assert.Equal(PolicyDecision.Ask, extended.Decision);

            var malformed = await gateway.EvaluateAsync(
                Proposal(new JsonObject
                {
                    ["executable"] = "ping",
                    ["arguments"] = new JsonArray("bad\0arg"),
                }, runId),
                CancellationToken.None);
            Assert.Equal(PolicyDecision.Deny, malformed.Decision);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task Full_access_unknown_command_executes_and_times_out_with_side_effects_marked()
    {
        var root = CreateWorkspace(out _);
        try
        {
            var environment = NewEnvironment(
                root,
                advertisedTerminal: true,
                preset: AgentPermissionPreset.FullAccess);
            var runner = new ScriptedModelRunner(
                new AssistantStep(
                    null,
                    new[]
                    {
                        ModelToolCall(new JsonObject
                        {
                            ["executable"] = "ping",
                            ["arguments"] = new JsonArray("-n", "30", "127.0.0.1"),
                            ["timeout_ms"] = 1_500,
                        }),
                    },
                    AssistantFinishReason.ToolCalls,
                    0));
            var coordinator = environment.Coordinator(runner);

            var result = await coordinator.RunOneStepAsync(environment.RunId, CancellationToken.None);

            Assert.Null(result.ErrorCode);
            var receipt = Assert.Single(result.Receipts);
            Assert.Equal(ToolExecutionOutcome.TimedOut, receipt.Outcome);
            Assert.Equal("process_timed_out", receipt.Code);
            Assert.True(receipt.SideEffectsPossible);
            Assert.Equal(0, environment.ActiveTerminalExecutions);

            var output = environment.SingleToolOutputPayload();
            Assert.True(output.GetProperty("duration_ms").GetInt64() >= 1_000);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task Cancelling_the_run_kills_the_process_tree_and_reports_cancelled_side_effects()
    {
        var root = CreateWorkspace(out _);
        try
        {
            var environment = NewEnvironment(
                root,
                advertisedTerminal: true,
                preset: AgentPermissionPreset.FullAccess);
            var runner = new ScriptedModelRunner(
                new AssistantStep(
                    null,
                    new[]
                    {
                        ModelToolCall(new JsonObject
                        {
                            ["executable"] = "ping",
                            ["arguments"] = new JsonArray("-n", "30", "127.0.0.1"),
                            ["timeout_ms"] = 60_000,
                        }),
                    },
                    AssistantFinishReason.ToolCalls,
                    0));
            var coordinator = environment.Coordinator(runner);

            var stepTask = coordinator.RunOneStepAsync(environment.RunId, CancellationToken.None);
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (environment.ActiveTerminalExecutions == 0)
            {
                if (DateTime.UtcNow > deadline)
                {
                    throw new InvalidOperationException("terminal execution never became cancellable");
                }

                await Task.Delay(50);
            }

            await coordinator.CancelRunningToolsAsync(environment.RunId, CancellationToken.None);
            var result = await stepTask.WaitAsync(TimeSpan.FromSeconds(20));

            var receipt = Assert.Single(result.Receipts);
            Assert.Equal(ToolExecutionOutcome.Cancelled, receipt.Outcome);
            Assert.Equal("process_cancelled", receipt.Code);
            Assert.True(receipt.SideEffectsPossible);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task Protected_and_outside_working_directories_are_denied()
    {
        var root = CreateWorkspace(out _);
        try
        {
            var gateway = NewGateway(root, new[] { "terminal.run" });

            var outsideParent = Directory.GetParent(root)!.FullName;
            var outsideDirectory = Path.Combine(outsideParent, "outside-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(outsideDirectory);
            try
            {
                var outside = await gateway.EvaluateAsync(
                    Proposal(new JsonObject
                    {
                        ["executable"] = "rg",
                        ["arguments"] = new JsonArray("--files"),
                        ["working_directory"] = outsideDirectory,
                    }),
                    CancellationToken.None);
                Assert.Equal(PolicyDecision.Deny, outside.Decision);
                Assert.Equal("cwd_outside_workspace", outside.SafeCode);
            }
            finally
            {
                TryDelete(outsideDirectory);
            }

            var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            Assert.False(string.IsNullOrWhiteSpace(windowsDirectory));
            var junction = Path.Combine(root, "windows-link");
            CreateJunction(junction, windowsDirectory);
            try
            {
                var protectedDecision = await gateway.EvaluateAsync(
                    Proposal(new JsonObject
                    {
                        ["executable"] = "rg",
                        ["arguments"] = new JsonArray("--files"),
                        ["working_directory"] = "windows-link",
                    }),
                    CancellationToken.None);
                Assert.Equal(PolicyDecision.Deny, protectedDecision.Decision);
                Assert.Equal("terminal_cwd_not_allowed", protectedDecision.SafeCode);
            }
            finally
            {
                TryRemoveLink(junction);
            }
        }
        finally
        {
            TryDelete(root);
        }
    }

    // ------------------------------------------------------------------
    // Canonical intent hash binding and stale preparation invalidation.
    // ------------------------------------------------------------------

    [Fact]
    public async Task Canonical_intent_token_binds_every_resolved_field_and_invalidates_stale_prepare()
    {
        var root = CreateWorkspace(out _);
        try
        {
            var gateway = NewGateway(root, new[] { "terminal.run" });
            var ids = new SequentialIdGenerator();

            static JsonObject Args(Action<JsonObject>? mutate = null)
            {
                var args = new JsonObject
                {
                    ["executable"] = "rg",
                    ["arguments"] = new JsonArray("--files"),
                };
                mutate?.Invoke(args);
                return args;
            }

            var baseline = await gateway.EvaluateAsync(Proposal(Args()), CancellationToken.None);
            Assert.Equal(PolicyDecision.Allow, baseline.Decision);
            var repeat = await gateway.EvaluateAsync(Proposal(Args()), CancellationToken.None);
            Assert.Equal(baseline.CanonicalIntentToken, repeat.CanonicalIntentToken);

            foreach (var mutated in new[]
                     {
                         Args(a => a["arguments"] = new JsonArray("--files", "-q")),
                         Args(a => a["network_intent"] = true),
                         Args(a => a["timeout_ms"] = 240_000),
                     })
            {
                var decision = await gateway.EvaluateAsync(Proposal(mutated), CancellationToken.None);
                Assert.NotEqual(baseline.CanonicalIntentToken, decision.CanonicalIntentToken);
            }

            // A different resolved working directory changes the canonical intent token.
            Directory.CreateDirectory(Path.Combine(root, "sub"));
            var movedCwd = await gateway.EvaluateAsync(
                Proposal(Args(a => a["working_directory"] = "sub")),
                CancellationToken.None);
            Assert.NotEqual(baseline.CanonicalIntentToken, movedCwd.CanonicalIntentToken);

            // A proposal prepared against the baseline token but carrying changed argv is stale.
            var staleApproved = new ApprovedToolExecution(
                Proposal(Args(a => a["arguments"] = new JsonArray("--files", "-q"))),
                ids.NewExecutionId(),
                baseline.CanonicalIntentToken!);
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                gateway.PrepareAsync(staleApproved, CancellationToken.None));
            Assert.Equal("canonical_token_mismatch", exception.Message);

            // Matching token prepares successfully and re-resolves at preparation time.
            var fresh = await gateway.EvaluateAsync(Proposal(Args()), CancellationToken.None);
            var valid = new ApprovedToolExecution(
                Proposal(Args()),
                ids.NewExecutionId(),
                fresh.CanonicalIntentToken!);
            var prepared = await gateway.PrepareAsync(valid, CancellationToken.None);
            Assert.Equal(ToolAccessKind.ReadOnly, prepared.AccessKind);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task Python_module_forms_follow_the_recognized_matrix()
    {
        var root = CreateWorkspace(out _);
        try
        {
            var gateway = NewGateway(root, new[] { "terminal.run" });

            var pytestModule = await gateway.EvaluateAsync(
                Proposal(new JsonObject
                {
                    ["executable"] = "python",
                    ["arguments"] = new JsonArray("-m", "pytest", "--version"),
                }),
                CancellationToken.None);
            Assert.Equal(PolicyDecision.Allow, pytestModule.Decision);

            var pipInstall = await gateway.EvaluateAsync(
                Proposal(new JsonObject
                {
                    ["executable"] = "python",
                    ["arguments"] = new JsonArray("-m", "pip", "install", "requests"),
                }),
                CancellationToken.None);
            Assert.Equal(PolicyDecision.Ask, pipInstall.Decision);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task Safe_events_never_carry_command_text_or_output_content()
    {
        var root = CreateWorkspace(out var marker);
        try
        {
            var environment = NewEnvironment(root, advertisedTerminal: true);
            var runner = new ScriptedModelRunner(
                new AssistantStep(
                    null,
                    new[]
                    {
                        ModelToolCall(new JsonObject
                        {
                            ["executable"] = "rg",
                            ["arguments"] = new JsonArray("--files", marker),
                        }),
                    },
                    AssistantFinishReason.ToolCalls,
                    0));
            var coordinator = environment.Coordinator(runner);
            var result = await coordinator.RunOneStepAsync(environment.RunId, CancellationToken.None);
            Assert.Null(result.ErrorCode);

            var events = await environment.ReadEventsAsync();
            foreach (var envelope in events)
            {
                var safeCode = envelope.Event.SafeCode ?? string.Empty;
                Assert.DoesNotContain(marker, safeCode);
                Assert.DoesNotContain('\n', safeCode);
            }
        }
        finally
        {
            TryDelete(root);
        }
    }

    // ------------------------------------------------------------------
    // Fixtures.
    // ------------------------------------------------------------------

    private static string CreateWorkspace(out string markerFile)
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-terminal-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        markerFile = "marker-" + Guid.NewGuid().ToString("N")[..8] + ".txt";
        File.WriteAllText(Path.Combine(root, markerFile), "ru terminal e2e\n");
        return root;
    }

    private static RunId StandardRunId() => new($"run_{Guid.NewGuid():N}");

    private static RunId FullAccessRunId() => new($"run_{new string('f', 32)}");

    private static HarnessToolGateway NewGateway(
        string root,
        IReadOnlyList<string> capabilities,
        RunId? fullAccessRun = null,
        AgentPermissionPreset fullAccessPreset = AgentPermissionPreset.Standard) =>
        new(
            root,
            capabilities,
            runId => runId == fullAccessRun ? fullAccessPreset : AgentPermissionPreset.Standard,
            new SequentialIdGenerator());

    private static CoordinatorEnvironment NewEnvironment(
        string root,
        bool advertisedTerminal,
        AgentPermissionPreset preset = AgentPermissionPreset.Standard)
    {
        var capabilities = advertisedTerminal
            ? new[] { "files.list", "files.read", "terminal.run", "terminal.shell" }
            : new[] { "files.list", "files.read" };
        var runId = preset == AgentPermissionPreset.FullAccess ? FullAccessRunId() : StandardRunId();
        var presets = new Dictionary<RunId, AgentPermissionPreset> { [runId] = preset };
        var ids = new SequentialIdGenerator();
        var gateway = new HarnessToolGateway(
            root,
            capabilities,
            runId => presets.TryGetValue(runId, out var resolved) ? resolved : AgentPermissionPreset.Standard,
            ids);
        var store = new RecordingStore();
        var frames = new TransientFrameBuffer(64 * 1024);
        var run = new AgentRun(
            runId,
            new SessionId(new string('b', 32)),
            new WorkspaceId("ws_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"),
            new HarnessRunStatus(HarnessRunState.Running),
            version: 1,
            lastSequence: 1,
            budgetWindow: 1,
            modelStepsInWindow: 0,
            toolCallsInWindow: 0,
            preset,
            policySnapshotVersion: 1,
            policySnapshotSha256: new string('c', 64),
            createdAt: FixedNow,
            updatedAt: FixedNow);
        store.TryCreateRunAsync(
            new NewAgentRun(
                run,
                new[]
                {
                    new HarnessEventIntent(ids.NewEventId(), HarnessEventKind.RunStateChanged, safeCode: "running"),
                },
                Array.Empty<HarnessPayloadIntent>(),
                Array.Empty<ConversationOutboxIntent>(),
                new RunCreationIdentity(new string('d', 64), new string('e', 64), 1)),
            CancellationToken.None).GetAwaiter().GetResult();
        return new CoordinatorEnvironment(
            root,
            store,
            frames,
            runId,
            gateway,
            ids);
    }

    private static ToolProposal Proposal(JsonObject arguments, RunId? runId = null)
    {
        var scriptMode = arguments.ContainsKey("script");
        return new ToolProposal(
            runId ?? StandardRunId(),
            new ModelStepId($"step_{Guid.NewGuid():N}"),
            new ToolCallId($"call_{Guid.NewGuid():N}"),
            1,
            scriptMode ? "terminal.shell" : "terminal.run",
            "1.0.0",
            JsonDocument.Parse(arguments.ToJsonString()).RootElement);
    }

    private static ModelToolCall ModelToolCall(JsonObject arguments)
    {
        var scriptMode = arguments.ContainsKey("script");
        return new ModelToolCall(
            null,
            scriptMode ? "terminal.shell" : "terminal.run",
            "1.0.0",
            JsonDocument.Parse(arguments.ToJsonString()).RootElement);
    }

    private static void AssertEventOrder(
        IReadOnlyList<HarnessEventEnvelope> events,
        params (string Kind, string? ExecutionId)[] expected)
    {
        long previous = 0;
        foreach (var (kind, executionId) in expected)
        {
            var match = events.Single(e =>
                e.Event.Kind == kind
                && (executionId is null
                    || string.Equals(e.Event.ExecutionId?.Value, executionId, StringComparison.Ordinal)));
            Assert.True(match.Event.Sequence > previous, $"{kind} must appear in order");
            previous = match.Event.Sequence;
        }
    }

    private static long SequenceOf(
        IReadOnlyList<HarnessEventEnvelope> events,
        string kind,
        int occurrence = 1)
    {
        var matches = events.Where(e => e.Event.Kind == kind).OrderBy(e => e.Event.Sequence).ToArray();
        Assert.True(matches.Length >= occurrence, $"expected at least {occurrence} {kind} events");
        return matches[occurrence - 1].Event.Sequence;
    }

    private static void CreateJunction(string junction, string target)
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            Arguments = $"/c mklink /J \"{junction}\" \"{target}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        Assert.True(process.WaitForExit(15_000));
        Assert.Equal(0, process.ExitCode);
    }

    private static void TryRemoveLink(string link)
    {
        try
        {
            new DirectoryInfo(link).Delete();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed record CoordinatorEnvironment(
        string Root,
        RecordingStore Store,
        TransientFrameBuffer Frames,
        RunId RunId,
        HarnessToolGateway Gateway,
        SequentialIdGenerator Ids)
    {
        public AgentRunCoordinator Coordinator(IModelStepRunner runner) => new(
            runner,
            Gateway,
            Store,
            new AgentContextAssembler(new StaticContextSource()),
            Ids,
            new FixedTime(FixedNow),
            new ToolCallScheduler(),
            Frames);

        public async Task<AgentRun> RequireRunAsync()
        {
            var run = await Store.GetRunAsync(RunId, CancellationToken.None);
            Assert.NotNull(run);
            return run!;
        }

        public async Task<List<HarnessEventEnvelope>> ReadEventsAsync()
        {
            var events = new List<HarnessEventEnvelope>();
            await foreach (var envelope in Store.ReadEventsAsync(RunId, 0, CancellationToken.None))
            {
                events.Add(envelope);
            }

            return events;
        }

        public int PayloadCount(HarnessPayloadKind kind) =>
            Store.Payloads.Values.Count(payload => payload.Kind == kind);

        public JsonElement SingleToolOutputPayload()
        {
            var payload = Store.Payloads.Values.Single(p => p.Kind == HarnessPayloadKind.ToolOutput);
            return JsonDocument.Parse(payload.Plaintext).RootElement;
        }

        public int ActiveTerminalExecutions => Gateway.ActiveTerminalExecutionCount;
    }

    private const string TinyProject =
        "<Project Sdk=\"Microsoft.NET.Sdk\">"
        + "<PropertyGroup><TargetFramework>net10.0</TargetFramework>"
        + "<OutputType>Exe</OutputType><Nullable>enable</Nullable></PropertyGroup></Project>";

    private sealed class FixedTime : ITimeProvider
    {
        public FixedTime(DateTimeOffset now) => UtcNow = now;
        public DateTimeOffset UtcNow { get; }
    }

    private sealed class SequentialIdGenerator : IIdGenerator
    {
        private int _counter;
        private string Next(string prefix) => prefix + (++_counter).ToString("x32");
        public RunId NewRunId() => new(Next("run_"));
        public ModelStepId NewModelStepId() => new(Next("step_"));
        public ToolCallId NewToolCallId() => new(Next("call_"));
        public ExecutionId NewExecutionId() => new(Next("exec_"));
        public ApprovalId NewApprovalId() => new(Next("apr_"));
        public TrustRequestId NewTrustRequestId() => new(Next("trust_"));
        public PolicyRuleId NewPolicyRuleId() => new(Next("rule_"));
        public EventId NewEventId() => new(Next("evt_"));
        public PayloadId NewPayloadId() => new(Next("payload_"));
    }

    private sealed class StaticContextSource : IModelContextSource
    {
        public Task<ModelContextSourceSnapshot> LoadAsync(RunId runId, CancellationToken cancellationToken)
        {
            var entry = ModelContextEntry.Text("src-1", 1, ModelContextRole.User, "prompt");
            return Task.FromResult(new ModelContextSourceSnapshot(new[] { entry }, Array.Empty<ModelContextEntry>()));
        }
    }

    private sealed class ScriptedModelRunner : IModelStepRunner
    {
        private readonly Queue<AssistantStep> _steps;

        public ScriptedModelRunner(params AssistantStep[] steps) => _steps = new Queue<AssistantStep>(steps);

        public Task<AssistantStep> RunStepAsync(
            ModelStepRequest request,
            Func<AssistantDelta, ValueTask> publishDelta,
            CancellationToken cancellationToken)
        {
            if (_steps.Count == 0)
            {
                throw new InvalidOperationException("no scripted step left");
            }

            return Task.FromResult(_steps.Dequeue());
        }
    }

    private sealed class RecordingStore : IHarnessEventStore
    {
        private readonly Dictionary<string, AgentRun> _runs = new();
        private readonly Dictionary<string, List<HarnessEventEnvelope>> _events = new();

        public Dictionary<string, HarnessPayloadIntent> Payloads { get; } = new();

        public Task<CreateRunResult> TryCreateRunAsync(NewAgentRun request, CancellationToken cancellationToken)
        {
            var key = request.Run.RunId.Value;
            if (_runs.ContainsKey(key))
            {
                return Task.FromResult(CreateRunResult.AlreadyExists(_runs[key]));
            }

            _runs[key] = request.Run;
            _events[key] = new List<HarnessEventEnvelope>();
            return Task.FromResult(CreateRunResult.Created(request.Run));
        }

        public Task<AgentRun?> GetRunAsync(RunId runId, CancellationToken cancellationToken)
        {
            _runs.TryGetValue(runId.Value, out var run);
            return Task.FromResult(run);
        }

        public Task<HarnessCommitResult> CommitAsync(HarnessCommit commit, CancellationToken cancellationToken)
        {
            if (!_runs.TryGetValue(commit.RunId.Value, out var current))
            {
                return Task.FromResult(HarnessCommitResult.RunNotFound());
            }

            if (current.Version != commit.ExpectedVersion)
            {
                return Task.FromResult(HarnessCommitResult.VersionConflict(current));
            }

            _runs[commit.RunId.Value] = commit.NextRun;
            if (!_events.TryGetValue(commit.RunId.Value, out var list))
            {
                list = new List<HarnessEventEnvelope>();
                _events[commit.RunId.Value] = list;
            }

            long sequence = current.LastSequence;
            foreach (var payload in commit.Payloads)
            {
                Payloads[payload.PayloadId.Value] = payload;
            }

            foreach (var intent in commit.Events)
            {
                sequence++;
                var kind = intent.Kind switch
                {
                    HarnessEventKind.RunStateChanged => "run.state_changed",
                    HarnessEventKind.RunFailed => "run.failed",
                    HarnessEventKind.ModelStepStarted => "model.step_started",
                    HarnessEventKind.ModelStepCompleted => "model.step_completed",
                    HarnessEventKind.ToolProposed => "tool.proposed",
                    HarnessEventKind.ToolExecutionRequested => "tool.execution_requested",
                    HarnessEventKind.ToolExecutionCompleted => "tool.execution_completed",
                    HarnessEventKind.ApprovalRequested => "approval.requested",
                    HarnessEventKind.ContinueRequested => "continue.requested",
                    HarnessEventKind.ReconciliationRequired => "reconciliation.required",
                    HarnessEventKind.TransientFramesDropped => "transient.frames_dropped",
                    _ => "unknown",
                };
                list.Add(new HarnessEventEnvelope(
                    1,
                    new HarnessEvent(
                        intent.EventId,
                        commit.RunId,
                        sequence,
                        kind,
                        DateTimeOffset.UtcNow,
                        intent.PayloadId,
                        intent.ModelStepId,
                        intent.ToolCallId,
                        intent.ExecutionId,
                        intent.ApprovalId,
                        intent.SafeCount,
                        intent.SafeCode,
                        intent.ToolOutcome,
                        intent.SideEffectsPossible)));
            }

            return Task.FromResult(HarnessCommitResult.Committed(commit.NextRun));
        }

        public async IAsyncEnumerable<HarnessEventEnvelope> ReadEventsAsync(
            RunId runId,
            long afterSequence,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (_events.TryGetValue(runId.Value, out var list))
            {
                foreach (var envelope in list.Where(x => x.Event.Sequence > afterSequence))
                {
                    yield return envelope;
                    await Task.Yield();
                }
            }
        }
    }
}
