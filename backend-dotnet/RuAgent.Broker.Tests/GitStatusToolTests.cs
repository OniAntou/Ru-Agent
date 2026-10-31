using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using RuAgent.Broker.Policy;
using RuAgent.Broker.Runtime;
using RuAgent.Broker.Tools;
using RuAgent.Broker.Tools.Git;
using RuAgent.Protocol;
using RuAgent.Protocol.Harness;
using Xunit;
using BrokerRunLease = RuAgent.Broker.Runtime.BrokerRunLease;

namespace RuAgent.Broker.Tests;

/// <summary>
/// The model-visible read of the workspace's Git state. The parser is shared with the
/// Host's prompt probe; these tests pin the report shape, the workspace-subtree bound,
/// the fixed read-only invocation and the policy class. Real git processes share the
/// nonparallel collection with the other Git fixtures.
/// </summary>
[Collection(nameof(GitProcessHeavyCollection))]
public sealed class GitStatusToolTests
{
    private const string Oid = "0123456789abcdef0123456789abcdef01234567";

    [Fact]
    public void Report_separates_staged_unstaged_untracked_and_unmerged_entries()
    {
        var parsed = GitStatusParser.Parse(string.Join('\n',
            $"# branch.oid {Oid}",
            "# branch.head main",
            "# branch.upstream origin/main",
            "# branch.ab +2 -1",
            "1 M. N... 100644 100644 100644 aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb staged.txt",
            "1 .M N... 100644 100644 100644 aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb work.txt",
            "u UU N... 100644 100644 100644 100644 aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb cccccccccccccccccccccccccccccccccccccccc conflict.txt",
            "? new.txt"));

        var report = GitStatusAdapter.BuildReport(parsed);

        Assert.True(report["is_repository"]!.GetValue<bool>());
        Assert.Equal("main", report["branch"]!.GetValue<string>());
        Assert.False(report["detached"]!.GetValue<bool>());
        Assert.Equal(Oid, report["head_oid"]!.GetValue<string>());
        Assert.Equal("origin/main", report["upstream"]!.GetValue<string>());
        Assert.Equal(2, report["ahead"]!.GetValue<long>());
        Assert.Equal(1, report["behind"]!.GetValue<long>());
        Assert.Equal(new[] { "staged.txt" }, ChangePaths(report, "staged"));
        Assert.Equal("M", ChangeAt(report, "staged", 0)["code"]!.GetValue<string>());
        Assert.Equal(new[] { "work.txt" }, ChangePaths(report, "unstaged"));
        Assert.Equal("M", ChangeAt(report, "unstaged", 0)["code"]!.GetValue<string>());
        Assert.Equal(new[] { "new.txt" }, StringPaths(report, "untracked"));
        Assert.Equal(new[] { "conflict.txt" }, ChangePaths(report, "unmerged"));
        Assert.False(report["truncated"]!.GetValue<bool>());
        Assert.Equal(0, report["unparsed_lines"]!.GetValue<int>());
        Assert.False(report["clean"]!.GetValue<bool>());
        Assert.False(report.ContainsKey("notice"));
    }

    [Fact]
    public void Rename_reports_the_original_path_on_the_side_it_changed()
    {
        var parsed = GitStatusParser.Parse(string.Join('\n',
            "# branch.head dev",
            "2 R. N... 100644 100644 100644 aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb R100 new.txt\told.txt"));

        var report = GitStatusAdapter.BuildReport(parsed);

        var staged = Assert.Single(report["staged"]!.AsArray());
        Assert.Equal("new.txt", staged!["path"]!.GetValue<string>());
        Assert.Equal("R", staged["code"]!.GetValue<string>());
        Assert.Equal("old.txt", staged["original_path"]!.GetValue<string>());
        Assert.Empty(report["unstaged"]!.AsArray());
    }

    [Fact]
    public void Detached_head_has_no_branch_name_but_keeps_the_head_commit()
    {
        var parsed = GitStatusParser.Parse($"# branch.oid {Oid}\n# branch.head (detached)");

        var report = GitStatusAdapter.BuildReport(parsed);

        Assert.True(report["detached"]!.GetValue<bool>());
        Assert.Null(report["branch"]);
        Assert.Equal(Oid, report["head_oid"]!.GetValue<string>());
        Assert.Null(report["upstream"]);
        Assert.Null(report["ahead"]);
        Assert.Null(report["behind"]);
    }

    [Fact]
    public void Clean_report_says_so_instead_of_returning_bare_empty_lists()
    {
        var parsed = GitStatusParser.Parse($"# branch.oid {Oid}\n# branch.head main");

        var report = GitStatusAdapter.BuildReport(parsed);

        Assert.True(report["clean"]!.GetValue<bool>());
        Assert.Empty(report["staged"]!.AsArray());
        Assert.Empty(report["unstaged"]!.AsArray());
        Assert.Empty(report["untracked"]!.AsArray());
        Assert.Empty(report["unmerged"]!.AsArray());
        Assert.Equal(GitStatusAdapter.CleanNotice, report["notice"]!.GetValue<string>());
    }

    [Fact]
    public void Entry_cap_marks_the_lists_incomplete()
    {
        var text = new StringBuilder("# branch.head main\n");
        for (var index = 0; index <= GitStatusParser.MaxEntries; index++)
        {
            text.Append("1 M. N... 100644 100644 100644 aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb f")
                .Append(index.ToString("D4"))
                .Append(".txt\n");
        }

        var report = GitStatusAdapter.BuildReport(GitStatusParser.Parse(text.ToString()));

        Assert.True(report["truncated"]!.GetValue<bool>());
        Assert.Equal(GitStatusParser.MaxEntries, report["staged"]!.AsArray().Count);
        Assert.False(report["clean"]!.GetValue<bool>());
        Assert.Equal(GitStatusAdapter.TruncatedNotice, report["notice"]!.GetValue<string>());
    }

    [Fact]
    public void Unparsed_lines_are_counted_and_never_reported_as_clean()
    {
        var report = GitStatusAdapter.BuildReport(GitStatusParser.Parse("# branch.head main\nnot porcelain v2"));

        Assert.Equal(1, report["unparsed_lines"]!.GetValue<int>());
        Assert.False(report["clean"]!.GetValue<bool>());
        Assert.Equal(GitStatusAdapter.UnparsedNotice, report["notice"]!.GetValue<string>());
    }

    [Fact]
    public void Schema_is_argument_free_and_rejects_a_caller_selected_path()
    {
        var definition = AgentToolDefinitionCatalog.Find("git_status", AgentToolDefinitionCatalog.CurrentVersion)!;

        ToolSchemaValidator.Validate(definition.InputSchema, new JsonObject());
        Assert.Throws<ToolSchemaException>(() =>
            ToolSchemaValidator.Validate(definition.InputSchema, new JsonObject { ["path"] = "." }));
    }

    [Fact]
    public void Profile_and_descriptor_keep_the_tool_read_only_and_workspace_scoped()
    {
        var row = RuAgent.Protocol.Tools.AgentToolDescriptorTable.Find("git_status")!;

        Assert.Equal(AgentToolScopes.WorkspaceRead, row.Scope);
        Assert.Equal(RuAgent.Protocol.Tools.AgentToolAccess.ReadOnly, row.Access);
        Assert.Equal(RuAgent.Protocol.Tools.AgentToolRisk.Read, row.Risk);
        Assert.False(row.RequiresApproval);
        Assert.True(AgentToolProfiles.Includes(AgentToolProfiles.Minimal, "git_status"));
        Assert.True(RuAgent.Protocol.Harness.BrokerPolicyContracts.IsReadOnlyTool("git_status"));
    }

    [Fact]
    public async Task Adapter_answers_not_a_repository_instead_of_failing()
    {
        var cli = new RecordingGitCli(new GitCliResult(
            false,
            128,
            string.Empty,
            "fatal: not a git repository (or any of the parent directories): .git",
            "git_exit_128"));

        var report = await new GitStatusAdapter(cli).ExecuteAsync(@"C:\workspace", CancellationToken.None);

        Assert.True(report.Succeeded);
        Assert.False(report.Output!["is_repository"]!.GetValue<bool>());
        Assert.Equal(GitStatusAdapter.NotARepositoryNotice, report.Output["notice"]!.GetValue<string>());
    }

    [Fact]
    public async Task Adapter_pins_one_read_only_status_invocation()
    {
        var cli = new RecordingGitCli(new GitCliResult(
            true, 0, $"# branch.oid {Oid}\n# branch.head main\n", string.Empty, string.Empty));

        _ = await new GitStatusAdapter(cli).ExecuteAsync(@"C:\workspace", CancellationToken.None);

        Assert.Equal(new[] { "-c", "status.relativePaths=true", "status", "--porcelain=v2", "--branch", "--ahead-behind", "--untracked-files=normal", "--", "." }, cli.Arguments);
        Assert.Equal(@"C:\workspace", cli.WorkingDirectory);
        Assert.Equal("C", cli.Environment!["LC_ALL"]);
    }

    [Theory]
    [InlineData("git_exit_128", 128, "git_status_failed")]
    [InlineData("git_output_truncated", -1, "git_status_too_large")]
    [InlineData("git_timed_out", -1, "git_status_timed_out")]
    [InlineData("", -1, "git_unavailable")]
    public async Task Adapter_failures_have_a_named_code_and_a_next_step(
        string failureReason,
        int exitCode,
        string expectedCode)
    {
        var cli = new RecordingGitCli(new GitCliResult(
            false,
            exitCode,
            string.Empty,
            failureReason.Length == 0 ? string.Empty : "fatal: detected dubious ownership in repository",
            failureReason));

        var report = await new GitStatusAdapter(cli).ExecuteAsync(@"C:\workspace", CancellationToken.None);

        Assert.False(report.Succeeded);
        Assert.Equal(expectedCode, report.ErrorCode);
        Assert.False(string.IsNullOrWhiteSpace(report.ErrorHint));
        Assert.Null(report.Output);
    }

    [Fact]
    public async Task Service_reports_the_real_workspace_change_set()
    {
        var root = CreateRepo();
        var sessionRoot = Path.Combine(root, "..", "session-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllText(Path.Combine(root, "seed.txt"), "changed\n");
            File.WriteAllText(Path.Combine(root, "staged.txt"), "staged\n");
            RunGit(root, "add", "staged.txt");
            File.WriteAllText(Path.Combine(root, "untracked.txt"), "untracked\n");

            var result = await ExecuteAsync(root, sessionRoot);

            Assert.Equal(ToolResultStatuses.Succeeded, result.Status);
            Assert.True(result.Output["is_repository"]!.GetValue<bool>());
            Assert.Equal("seed.txt", Assert.Single(ChangePaths(result.Output, "unstaged")));
            Assert.Equal("staged.txt", Assert.Single(ChangePaths(result.Output, "staged")));
            Assert.Equal(new[] { "untracked.txt" }, StringPaths(result.Output, "untracked"));
            Assert.False(result.Output["clean"]!.GetValue<bool>());
            Assert.False(string.IsNullOrWhiteSpace(result.Output["branch"]!.GetValue<string>()));
        }
        finally
        {
            TryDelete(sessionRoot);
            TryDelete(root);
        }
    }

    [Fact]
    public async Task Service_bounds_the_report_to_a_workspace_subtree()
    {
        var root = CreateRepo();
        var workspace = Path.Combine(root, "sub");
        Directory.CreateDirectory(Path.Combine(workspace, "nested"));
        File.WriteAllText(Path.Combine(workspace, "inside.txt"), "inside\n");
        File.WriteAllText(Path.Combine(workspace, "nested", "deep.txt"), "deep\n");
        RunGit(root, "add", ".");
        RunGit(root, "commit", "-m", "subtree");
        var sessionRoot = Path.Combine(root, "..", "session-" + Guid.NewGuid().ToString("N"));
        try
        {
            // Changes on both sides of the boundary: only the workspace subtree may
            // appear, and its paths must be workspace-relative, never "..".
            File.WriteAllText(Path.Combine(root, "seed.txt"), "outside change\n");
            File.WriteAllText(Path.Combine(workspace, "inside.txt"), "inside change\n");
            File.WriteAllText(Path.Combine(workspace, "nested", "deep.txt"), "deep change\n");
            File.WriteAllText(Path.Combine(workspace, "new.txt"), "new\n");

            var result = await ExecuteAsync(workspace, sessionRoot);

            Assert.Equal(ToolResultStatuses.Succeeded, result.Status);
            var unstaged = ChangePaths(result.Output, "unstaged");
            Assert.Equal(new[] { "inside.txt", "nested/deep.txt" }, unstaged.OrderBy(path => path, StringComparer.Ordinal));
            Assert.Equal(new[] { "new.txt" }, StringPaths(result.Output, "untracked"));
            Assert.DoesNotContain(unstaged, path => path.Contains("seed.txt", StringComparison.Ordinal) || path.StartsWith("..", StringComparison.Ordinal));
            Assert.DoesNotContain(StringPaths(result.Output, "untracked"), path => path.StartsWith("..", StringComparison.Ordinal));
        }
        finally
        {
            TryDelete(sessionRoot);
            TryDelete(root);
        }
    }

    [Fact]
    public async Task Service_reports_a_plain_directory_without_failing_the_call()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-git-plain-" + Guid.NewGuid().ToString("N"));
        var sessionRoot = Path.Combine(Path.GetTempPath(), "ru-git-plain-session-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var result = await ExecuteAsync(root, sessionRoot);

            Assert.Equal(ToolResultStatuses.Succeeded, result.Status);
            Assert.False(result.Output["is_repository"]!.GetValue<bool>());
            Assert.Equal(GitStatusAdapter.NotARepositoryNotice, result.Output["notice"]!.GetValue<string>());
        }
        finally
        {
            TryDelete(sessionRoot);
            TryDelete(root);
        }
    }

    [Fact]
    public void Policy_context_is_a_target_free_read_and_plan_allows_it()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-git-policy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var snapshot = LeaseTestSnapshots.StandardEmpty();
            var session = BrokerSession.CreateForHost(root, DateTimeOffset.UtcNow.AddMinutes(5));
            var lease = BrokerRunLease.CreateValidated(
                "run_" + Guid.NewGuid().ToString("N"),
                "session-git-status",
                LeaseTestSnapshots.WorkspaceId,
                root,
                LeaseTestSnapshots.VolumeSerial,
                LeaseTestSnapshots.RootFileId,
                LeaseTestSnapshots.DisplayHash,
                snapshot.Preset,
                snapshot.Version,
                RuAgent.Protocol.Harness.BrokerPolicyContracts.ComputeCanonicalSnapshotHash(snapshot),
                DateTimeOffset.UtcNow.AddMinutes(5),
                snapshot);
            Assert.True(session.TryOpenRunLease(lease, out var leaseError), leaseError);

            var context = new PolicyContextFactory(lease).Create("git_status", "1.0.0", new JsonObject());

            Assert.Equal(PolicyOperationClasses.Read, context.OperationClass);
            Assert.Null(context.Target);
            Assert.Equal("none", context.NetworkIntent);
            // Plan is the read-only preset: a workspace read must still be automatic.
            var plan = BoundPolicySnapshot.Parse(snapshot with { Preset = BrokerPolicyContracts.PlanPresetWireValue });
            var evaluation = new PolicySnapshotEvaluator()
                .Evaluate(plan, context, PolicyRequestInvariants.Valid, DateTimeOffset.UtcNow);
            Assert.Equal("allow", evaluation.Decision);
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static async Task<ToolResult> ExecuteAsync(string workspace, string sessionRoot)
    {
        Directory.CreateDirectory(sessionRoot);
        var session = BrokerSession.CreateForHost(sessionRoot, DateTimeOffset.UtcNow.AddMinutes(5));
        const string sessionId = "session-git-status";
        session.Open(sessionId, new HashSet<string>([ToolScope.WorkspaceRead], StringComparer.Ordinal));
        using var service = new ToolExecutionService(session, workspace);
        return await service.ExecuteAsync(
            new AgentEnvelope(
                AgentEnvelopeCodec.ProtocolVersion,
                "git-status-" + Guid.NewGuid().ToString("N"),
                AgentMessageTypes.ToolRequest,
                sessionId,
                null,
                30_000,
                new JsonObject
                {
                    ["tool_id"] = "git_status",
                    ["tool_version"] = "1.0.0",
                    ["capability_class"] = "internal",
                    ["run_id"] = sessionId,
                    ["arguments"] = new JsonObject(),
                }),
            CancellationToken.None);
    }

    private static string[] ChangePaths(JsonObject output, string field) =>
        output[field]!.AsArray().Select(item => item!["path"]!.GetValue<string>()).ToArray();

    private static JsonObject ChangeAt(JsonObject output, string field, int index) =>
        output[field]!.AsArray()[index]!.AsObject();

    private static string[] StringPaths(JsonObject output, string field) =>
        output[field]!.AsArray().Select(item => item!.GetValue<string>()).ToArray();

    private static string CreateRepo()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-git-status-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        RunGit(root, "init");
        RunGit(root, "config", "user.email", "ru@example.test");
        RunGit(root, "config", "user.name", "Ru Tests");
        RunGit(root, "config", "commit.gpgsign", "false");
        File.WriteAllText(Path.Combine(root, "seed.txt"), "seed\n");
        RunGit(root, "add", ".");
        RunGit(root, "commit", "-m", "seed");
        return root;
    }

    private static void RunGit(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(30_000))
        {
            throw new InvalidOperationException("git fixture command timed out");
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git fixture command failed ({process.ExitCode}): git {string.Join(' ', arguments)} :: {stderr}");
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

    private sealed class RecordingGitCli(GitCliResult result) : IGitCli
    {
        public IReadOnlyList<string> Arguments { get; private set; } = [];

        public string WorkingDirectory { get; private set; } = string.Empty;

        public IReadOnlyDictionary<string, string>? Environment { get; private set; }

        public Task<GitCliResult> RunAsync(
            IReadOnlyList<string> arguments,
            string workingDirectory,
            CancellationToken cancellationToken,
            IReadOnlyDictionary<string, string>? additionalEnvironment = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Arguments = arguments.ToArray();
            WorkingDirectory = workingDirectory;
            Environment = additionalEnvironment;
            return Task.FromResult(result);
        }
    }
}
