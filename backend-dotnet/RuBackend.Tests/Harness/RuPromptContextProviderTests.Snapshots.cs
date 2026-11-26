extern alias Broker;

using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Protocol;
using RuAgent.Protocol.Harness;
using RuBackend.Business.Settings;
using RuBackend.Harness.Prompting;
using WorkspaceGitSnapshot = Broker::RuAgent.Broker.Tools.Git.WorkspaceGitSnapshot;
using Xunit;

namespace RuBackend.Tests.Harness;

/// <summary>
/// The snapshot references: opt-in, bounded and fail-soft. They add orientation to
/// the prompt but may never fail a run or displace the fixed instruction surface.
/// </summary>
public sealed partial class RuPromptContextProviderTests
{
    [Fact]
    public async Task Workspace_snapshot_reports_repository_and_layout_with_detected_commands()
    {
        using var fixture = RuInstructionLoaderTestsRoot.Create();
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "build.bat"), "echo build");
        Directory.CreateDirectory(Path.Combine(fixture.Root, "scripts"));
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "scripts", "test_dotnet.ps1"), string.Empty);
        var provider = new RuPromptContextProvider(
            fixture.Root,
            fixture.CreateLoader(),
            new StubSettingsStore(Settings()),
            new FixedTimeProvider(new DateTimeOffset(2026, 11, 27, 0, 0, 0, TimeSpan.Zero)))
        {
            WorkspaceGitSnapshot = _ => Task.FromResult<WorkspaceGitSnapshot?>(new WorkspaceGitSnapshot(
                true, fixture.Root, "dev", "abc123", "fix the build", 1, 0, 2, 0, 1, 0,
                ["src/a.cs"], ["abc123\tfix the build"], Truncated: false, StatusUnavailable: false)),
        };

        var context = await provider.BuildAsync(
            TestRun(),
            new ModelContextSnapshot(Array.Empty<ModelContextEntry>(), Array.Empty<byte>()),
            CancellationToken.None);

        var reference = Assert.Single(context.RuntimeContext.References, item => item.Kind == "workspace_snapshot");
        Assert.Contains("branch: dev", reference.Content, StringComparison.Ordinal);
        Assert.Contains("head: abc123 fix the build", reference.Content, StringComparison.Ordinal);
        Assert.Contains("1 modified, 0 staged, 2 untracked, 0 conflicted", reference.Content, StringComparison.Ordinal);
        Assert.Contains("top-level:", reference.Content, StringComparison.Ordinal);
        Assert.Contains("build.bat", reference.Content, StringComparison.Ordinal);
        Assert.Contains("scripts/", reference.Content, StringComparison.Ordinal);
        Assert.Contains("candidate commands", reference.Content, StringComparison.Ordinal);
        Assert.Contains(".\\build.bat", reference.Content, StringComparison.Ordinal);
        Assert.Contains("test_dotnet.ps1", reference.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failed_repository_probe_keeps_the_layout_section()
    {
        using var fixture = RuInstructionLoaderTestsRoot.Create();
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "README.md"), "readme");
        var provider = new RuPromptContextProvider(
            fixture.Root,
            fixture.CreateLoader(),
            new StubSettingsStore(Settings()),
            new FixedTimeProvider(DateTimeOffset.UtcNow))
        {
            WorkspaceGitSnapshot = _ => throw new InvalidOperationException("probe failed"),
        };

        var context = await provider.BuildAsync(
            TestRun(),
            new ModelContextSnapshot(Array.Empty<ModelContextEntry>(), Array.Empty<byte>()),
            CancellationToken.None);

        var reference = Assert.Single(context.RuntimeContext.References, item => item.Kind == "workspace_snapshot");
        Assert.Contains("workspace:", reference.Content, StringComparison.Ordinal);
        Assert.Contains("README.md", reference.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("repository:", reference.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_the_git_probe_no_workspace_snapshot_is_assembled()
    {
        using var fixture = RuInstructionLoaderTestsRoot.Create();
        var provider = new RuPromptContextProvider(
            fixture.Root,
            fixture.CreateLoader(),
            new StubSettingsStore(Settings()),
            new FixedTimeProvider(DateTimeOffset.UtcNow));

        var context = await provider.BuildAsync(
            TestRun(),
            new ModelContextSnapshot(Array.Empty<ModelContextEntry>(), Array.Empty<byte>()),
            CancellationToken.None);

        Assert.DoesNotContain(context.RuntimeContext.References, item => item.Kind == "workspace_snapshot");
    }

    [Fact]
    public async Task The_repository_probe_runs_once_inside_its_refresh_interval()
    {
        using var fixture = RuInstructionLoaderTestsRoot.Create();
        var calls = 0;
        var provider = new RuPromptContextProvider(
            fixture.Root,
            fixture.CreateLoader(),
            new StubSettingsStore(Settings()),
            new FixedTimeProvider(DateTimeOffset.UtcNow))
        {
            WorkspaceGitSnapshot = _ =>
            {
                calls++;
                return Task.FromResult<WorkspaceGitSnapshot?>(WorkspaceGitSnapshot.NotRepository);
            },
        };
        var snapshot = new ModelContextSnapshot(Array.Empty<ModelContextEntry>(), Array.Empty<byte>());

        var first = await provider.BuildAsync(TestRun(), snapshot, CancellationToken.None);
        var second = await provider.BuildAsync(TestRun(), snapshot, CancellationToken.None);

        Assert.Equal(1, calls);
        Assert.Single(first.RuntimeContext.References, item => item.Kind == "workspace_snapshot");
        Assert.Single(second.RuntimeContext.References, item => item.Kind == "workspace_snapshot");
    }

    [Fact]
    public async Task Machine_snapshot_is_opt_in_and_bounded()
    {
        using var fixture = RuInstructionLoaderTestsRoot.Create();
        var provider = new RuPromptContextProvider(
            fixture.Root,
            fixture.CreateLoader(),
            new StubSettingsStore(Settings()),
            new FixedTimeProvider(DateTimeOffset.UtcNow))
        {
            MachineSnapshot = () => "machine:\n  os: test (X64)",
        };

        var context = await provider.BuildAsync(
            TestRun(),
            new ModelContextSnapshot(Array.Empty<ModelContextEntry>(), Array.Empty<byte>()),
            CancellationToken.None);

        var reference = Assert.Single(context.RuntimeContext.References, item => item.Kind == "machine_snapshot");
        Assert.Contains("os: test (X64)", reference.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Machine_snapshot_is_absent_unless_the_shell_supplies_it()
    {
        using var fixture = RuInstructionLoaderTestsRoot.Create();
        var provider = new RuPromptContextProvider(
            fixture.Root,
            fixture.CreateLoader(),
            new StubSettingsStore(Settings()),
            new FixedTimeProvider(DateTimeOffset.UtcNow));

        var context = await provider.BuildAsync(
            TestRun(),
            new ModelContextSnapshot(Array.Empty<ModelContextEntry>(), Array.Empty<byte>()),
            CancellationToken.None);

        Assert.DoesNotContain(context.RuntimeContext.References, item => item.Kind == "machine_snapshot");
    }

    [Fact]
    public async Task Run_state_reports_counters_and_optional_session_notes()
    {
        using var fixture = RuInstructionLoaderTestsRoot.Create();
        var provider = new RuPromptContextProvider(
            fixture.Root,
            fixture.CreateLoader(),
            new StubSettingsStore(Settings()),
            new FixedTimeProvider(DateTimeOffset.UtcNow))
        {
            IncludeRunState = true,
            ActiveSubagents = _ => Task.FromResult<string?>("2 subagent(s): a (Review)"),
            OpenTasks = _ => Task.FromResult<string?>("1 open, 0 completed; open: fix the build"),
        };

        var context = await provider.BuildAsync(
            TestRun(),
            new ModelContextSnapshot(Array.Empty<ModelContextEntry>(), Array.Empty<byte>()),
            CancellationToken.None);

        var reference = Assert.Single(context.RuntimeContext.References, item => item.Kind == "run_state");
        Assert.Contains("model step: 1 of 128 in budget window 100", reference.Content, StringComparison.Ordinal);
        Assert.Contains("tool calls this window: 0 of 256", reference.Content, StringComparison.Ordinal);
        Assert.Contains("visible transcript: 0 assistant step(s), 0 tool result(s)", reference.Content, StringComparison.Ordinal);
        Assert.Contains("subagents: 2 subagent(s): a (Review)", reference.Content, StringComparison.Ordinal);
        Assert.Contains("work items: 1 open, 0 completed", reference.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_state_reports_the_window_ordinal_and_the_step_limit()
    {
        using var fixture = RuInstructionLoaderTestsRoot.Create();
        var provider = new RuPromptContextProvider(
            fixture.Root,
            fixture.CreateLoader(),
            new StubSettingsStore(Settings()),
            new FixedTimeProvider(DateTimeOffset.UtcNow))
        {
            IncludeRunState = true,
        };

        var context = await provider.BuildAsync(
            RunWithBudget(budgetWindow: 3, modelStepsInWindow: 5),
            new ModelContextSnapshot(Array.Empty<ModelContextEntry>(), Array.Empty<byte>()),
            CancellationToken.None);

        var reference = Assert.Single(context.RuntimeContext.References, item => item.Kind == "run_state");
        Assert.Contains("model step: 6 of 128 in budget window 3", reference.Content, StringComparison.Ordinal);
        Assert.Contains("tool calls this window: 0 of 256", reference.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_state_is_absent_unless_the_shell_asks_for_it()
    {
        using var fixture = RuInstructionLoaderTestsRoot.Create();
        var provider = new RuPromptContextProvider(
            fixture.Root,
            fixture.CreateLoader(),
            new StubSettingsStore(Settings()),
            new FixedTimeProvider(DateTimeOffset.UtcNow));

        var context = await provider.BuildAsync(
            TestRun(),
            new ModelContextSnapshot(Array.Empty<ModelContextEntry>(), Array.Empty<byte>()),
            CancellationToken.None);

        Assert.DoesNotContain(context.RuntimeContext.References, item => item.Kind == "run_state");
    }

    [Fact]
    public async Task Run_state_session_note_failures_stay_silent()
    {
        using var fixture = RuInstructionLoaderTestsRoot.Create();
        var provider = new RuPromptContextProvider(
            fixture.Root,
            fixture.CreateLoader(),
            new StubSettingsStore(Settings()),
            new FixedTimeProvider(DateTimeOffset.UtcNow))
        {
            IncludeRunState = true,
            ActiveSubagents = _ => throw new InvalidOperationException("subagent store failed"),
            OpenTasks = _ => throw new InvalidOperationException("work state failed"),
        };

        var context = await provider.BuildAsync(
            TestRun(),
            new ModelContextSnapshot(Array.Empty<ModelContextEntry>(), Array.Empty<byte>()),
            CancellationToken.None);

        var reference = Assert.Single(context.RuntimeContext.References, item => item.Kind == "run_state");
        Assert.DoesNotContain("subagents:", reference.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("work items:", reference.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Snapshot_references_fit_the_runtime_reference_budget()
    {
        using var fixture = RuInstructionLoaderTestsRoot.Create();
        var provider = new RuPromptContextProvider(
            fixture.Root,
            fixture.CreateLoader(),
            new StubSettingsStore(Settings()),
            new FixedTimeProvider(DateTimeOffset.UtcNow))
        {
            IncludeRunState = true,
            WorkspaceGitSnapshot = _ => Task.FromResult<WorkspaceGitSnapshot?>(WorkspaceGitSnapshot.NotRepository),
            MachineSnapshot = () => "machine:\n  os: test",
            ActiveSubagents = _ => Task.FromResult<string?>("1 subagent(s): a (Review)"),
            OpenTasks = _ => Task.FromResult<string?>("0 open, 1 completed"),
        };
        var entries = Enumerable.Range(1, 32)
            .Select(index => ModelContextEntry.ToolResult(
                "result-" + index, index, ToolReceipt(index), new string('é', 4_000)))
            .ToArray();

        var context = await provider.BuildAsync(
            TestRun(),
            new ModelContextSnapshot(entries, Array.Empty<byte>()),
            CancellationToken.None);

        Assert.InRange(
            context.RuntimeContext.References.Count,
            1,
            PromptRuntimeContext.MaxRuntimeReferences);
        Assert.Contains(context.RuntimeContext.References, item => item.Kind == "workspace_snapshot");
        Assert.Contains(context.RuntimeContext.References, item => item.Kind == "machine_snapshot");
        Assert.Contains(context.RuntimeContext.References, item => item.Kind == "run_state");
    }

    [Fact]
    public async Task The_workspace_snapshot_is_reprobed_after_its_refresh_interval()
    {
        using var fixture = RuInstructionLoaderTestsRoot.Create();
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var calls = 0;
        var provider = new RuPromptContextProvider(
            fixture.Root,
            fixture.CreateLoader(),
            new StubSettingsStore(Settings()),
            clock)
        {
            WorkspaceGitSnapshot = _ =>
            {
                calls++;
                return Task.FromResult<WorkspaceGitSnapshot?>(WorkspaceGitSnapshot.NotRepository);
            },
        };
        var snapshot = new ModelContextSnapshot(Array.Empty<ModelContextEntry>(), Array.Empty<byte>());

        await provider.BuildAsync(TestRun(), snapshot, CancellationToken.None);
        clock.UtcNow = clock.UtcNow.AddSeconds(11);
        await provider.BuildAsync(TestRun(), snapshot, CancellationToken.None);

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Reference_text_is_sanitized_instead_of_failing_the_prompt()
    {
        using var fixture = RuInstructionLoaderTestsRoot.Create();
        var provider = new RuPromptContextProvider(
            fixture.Root,
            fixture.CreateLoader(),
            new StubSettingsStore(Settings()),
            new FixedTimeProvider(DateTimeOffset.UtcNow))
        {
            IncludeRunState = true,
            MachineSnapshot = () => "machine:\n\0  os: \uD800",
            OpenTasks = _ => Task.FromResult<string?>("\0open: x"),
        };

        var context = await provider.BuildAsync(
            TestRun(),
            new ModelContextSnapshot(Array.Empty<ModelContextEntry>(), Array.Empty<byte>()),
            CancellationToken.None);

        var machine = Assert.Single(context.RuntimeContext.References, item => item.Kind == "machine_snapshot");
        Assert.DoesNotContain('\0', machine.Content);
        Assert.DoesNotContain('\uD800', machine.Content);
        var runState = Assert.Single(context.RuntimeContext.References, item => item.Kind == "run_state");
        Assert.DoesNotContain('\0', runState.Content);
    }

    private static AgentRun RunWithBudget(int budgetWindow, int modelStepsInWindow) => new(
        new RunId("run_11111111111111111111111111111111"),
        new SessionId("11111111111111111111111111111111"),
        new WorkspaceId("ws_11111111111111111111111111111111"),
        new HarnessRunStatus(HarnessRunState.Running),
        1,
        0,
        budgetWindow,
        modelStepsInWindow,
        0,
        AgentPermissionPreset.Standard,
        1,
        new string('a', 64),
        new DateTimeOffset(2026, 8, 29, 0, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 8, 29, 0, 0, 0, TimeSpan.Zero));

    private sealed class MutableTimeProvider(DateTimeOffset now) : ITimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = now;
    }
}
