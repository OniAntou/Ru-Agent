using System.Text.Json.Nodes;
using RuAgent.Broker.Policy;
using RuAgent.Broker.Runtime;
using RuAgent.Protocol;
using RuAgent.Protocol.Harness;
using RuAgent.Protocol.Tools;
using Xunit;

namespace RuAgent.Broker.Tests;

/// <summary>
/// The remembered-approval plan is derived only from the Broker PolicyContext the evaluator
/// matches. Each rememberable tool binds a fixed identity; every other identity change must miss.
/// </summary>
public sealed class ApprovalRememberPolicyTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Fact]
    public void Every_descriptor_tool_has_an_explicit_allowlist_entry()
    {
        Assert.NotEmpty(AgentToolDescriptorTable.All);
        foreach (var descriptor in AgentToolDescriptorTable.All)
        {
            Assert.True(
                ApprovalRememberPolicy.Classify(descriptor.Id) != ApprovalIdentityClass.Unknown,
                $"{descriptor.Id} has no explicit entry in the remember allowlist.");
        }
    }

    [Theory]
    [InlineData("apply_patch")]
    [InlineData("file_restore")]
    [InlineData("run_code")]
    [InlineData("terminal_read")]
    [InlineData("terminal_stop")]
    [InlineData("help")]
    [InlineData("todo_write")]
    [InlineData("web_search")]
    [InlineData("web_fetch")]
    [InlineData("skill")]
    [InlineData("ask_user")]
    [InlineData("agent_spawn")]
    [InlineData("agent_send")]
    [InlineData("agent_wait")]
    [InlineData("agent_cancel")]
    [InlineData("agent_apply")]
    public void Tools_that_bind_no_target_identity_are_not_rememberable(string toolId)
    {
        Assert.Equal(ApprovalIdentityClass.NotRememberable, ApprovalRememberPolicy.Classify(toolId));
        var context = Context("read_file", new JsonObject { ["path"] = "src\\Example.cs" }) with { ToolId = toolId };

        Assert.False(Build(context).IsRememberable);
    }

    [Fact]
    public void Unknown_tools_and_mismatched_identity_are_not_rememberable()
    {
        var context = CaseContext("read_file");

        Assert.Equal(ApprovalIdentityClass.Unknown, ApprovalRememberPolicy.Classify("banana"));
        Assert.False(Build(context with { ToolId = "banana" }).IsRememberable);
        Assert.False(ApprovalRememberPolicy.Build(context, "grep", "1.0.0", null, false).IsRememberable);
        Assert.False(ApprovalRememberPolicy.Build(context, "read_file", "9.9.9", null, false).IsRememberable);
    }

    [Fact]
    public void Only_mcp_list_tools_and_call_and_computer_grants_are_rememberable()
    {
        Assert.False(Build(Context("mcp", new JsonObject { ["op"] = "list_servers" })).IsRememberable);
        Assert.False(Build(Context("computer", new JsonObject { ["action"] = "screenshot", ["monitor"] = 0 })).IsRememberable);
        Assert.True(Build(CaseContext("mcp_call")).IsRememberable);
        Assert.True(Build(CaseContext("computer_focus")).IsRememberable);
    }

    [Theory]
    [InlineData("read_file", "tool,version,path,operation,network")]
    [InlineData("list", "tool,version,path,operation,network")]
    [InlineData("grep", "tool,version,path,operation,network")]
    [InlineData("glob", "tool,version,path,operation,network")]
    [InlineData("write_file", "tool,version,path,operation,network")]
    [InlineData("edit_file", "tool,version,path,operation,network")]
    [InlineData("file_delete", "tool,version,path,operation,network")]
    [InlineData("run_process", "tool,version,executable,argv,cwd,operation,network")]
    [InlineData("powershell", "tool,version,executable,argv,cwd,operation,network")]
    [InlineData("mcp_call", "tool,version,executable,argv,operation,network")]
    [InlineData("mcp_list_tools", "tool,version,executable,argv,operation,network")]
    [InlineData("computer_request_access", "tool,version,executable,argv,operation,network")]
    [InlineData("computer_focus", "tool,version,executable,argv,operation,network")]
    [InlineData("code_intelligence", "tool,version,path,executable,argv,cwd,operation,network")]
    public void Remembered_matchers_match_their_own_context_and_miss_on_each_bound_field(string caseName, string boundFields)
    {
        var context = CaseContext(caseName);
        var plan = Build(context);
        var expected = boundFields.Split(',').OrderBy(field => field, StringComparer.Ordinal).ToArray();
        var matchers = new[] { plan.SessionMatcher, plan.DurableMatcher }.OfType<BrokerPolicyMatcherWire>().ToArray();

        Assert.NotEmpty(matchers);
        foreach (var matcher in matchers)
        {
            Assert.True(Matches(matcher, context), $"{caseName}: a remembered matcher does not match its own call.");
            Assert.Equal(expected, BoundFields(matcher));
            foreach (var field in expected)
            {
                Assert.False(Matches(matcher, Mutate(context, field)), $"{caseName}: a changed {field} still matched.");
            }
        }
    }

    [Fact]
    public void Process_durable_matcher_generalizes_to_two_leading_non_flag_tokens()
    {
        var context = Context("run_process", new JsonObject
        {
            ["executable"] = "git", ["arguments"] = new JsonArray("commit", "-m", "msg"), ["working_directory"] = ".",
        });
        var plan = Build(context);

        Assert.Equal(new[] { "commit", "-m", "msg" }, plan.SessionMatcher!.ArgvPattern);
        Assert.Equal(new[] { "commit" }, plan.DurableMatcher!.ArgvPattern);
        Assert.True(Matches(plan.DurableMatcher, context with { Argv = ["commit", "--amend"] }));
        Assert.False(Matches(plan.DurableMatcher, context with { Argv = ["push"] }));
    }

    [Fact]
    public void Process_durable_matcher_keeps_two_tokens_before_a_flag()
    {
        var context = Context("run_process", new JsonObject
        {
            ["executable"] = "npm", ["arguments"] = new JsonArray("run", "build", "--", "watch"), ["working_directory"] = ".",
        });

        Assert.Equal(new[] { "run", "build" }, Build(context).DurableMatcher!.ArgvPattern);
    }

    [Fact]
    public void Shell_invocations_remember_the_exact_script_in_both_scopes()
    {
        var context = CaseContext("powershell");
        var plan = Build(context);

        Assert.Equal(context.Argv, plan.SessionMatcher!.ArgvPattern);
        Assert.Equal(context.Argv, plan.DurableMatcher!.ArgvPattern);
        Assert.False(Matches(plan.DurableMatcher, context with { Argv = [.. context.Argv.Take(3), "Get-Date; Remove-Item x"] }));
    }

    [Fact]
    public void Shell_script_with_a_wildcard_is_remembered_exactly_because_shell_argv_is_not_globbed()
    {
        var context = Context("powershell", new JsonObject { ["script"] = "Get-ChildItem *.cs", ["working_directory"] = "." });
        var plan = Build(context);

        Assert.NotNull(plan.SessionMatcher);
        Assert.NotNull(plan.DurableMatcher);
        Assert.False(Matches(plan.DurableMatcher, context with { Argv = [.. context.Argv.Take(3), "Get-ChildItem other.cs"] }));
    }

    [Fact]
    public void Wildcard_argv_that_the_evaluator_would_glob_is_not_rememberable()
    {
        var context = Context("run_process", new JsonObject
        {
            ["executable"] = "git", ["arguments"] = new JsonArray("log", "*.cs"), ["working_directory"] = ".",
        });

        Assert.False(Build(context).IsRememberable);
    }

    [Fact]
    public void Empty_argv_is_not_rememberable_for_a_terminal_run()
    {
        var context = Context("run_process", new JsonObject
        {
            ["executable"] = "git", ["arguments"] = new JsonArray(), ["working_directory"] = ".",
        });

        Assert.False(Build(context).IsRememberable);
    }

    [Fact]
    public void An_empty_argv_element_keeps_the_durable_prefix_but_not_the_exact_session_matcher()
    {
        var context = Context("run_process", new JsonObject
        {
            ["executable"] = "git", ["arguments"] = new JsonArray("commit", "-m", ""), ["working_directory"] = ".",
        });
        var plan = Build(context);

        Assert.Null(plan.SessionMatcher);
        Assert.Equal(new[] { "commit" }, plan.DurableMatcher!.ArgvPattern);
    }

    [Fact]
    public void The_workspace_root_is_not_rememberable_because_it_has_no_relative_identity()
    {
        Assert.False(Build(Context("list", new JsonObject { ["path"] = "." })).IsRememberable);
    }

    [Fact]
    public void An_executable_beyond_the_host_matcher_limit_is_not_rememberable()
    {
        var context = CaseContext("run_process") with { ResolvedExecutable = new string('a', 257) };

        Assert.False(Build(context).IsRememberable);
    }

    [Fact]
    public void An_argv_beyond_the_matcher_count_loses_only_the_exact_session_matcher()
    {
        var context = CaseContext("run_process") with { Argv = Enumerable.Repeat("x", 65).ToArray() };
        var plan = Build(context);

        Assert.Null(plan.SessionMatcher);
        Assert.NotNull(plan.DurableMatcher);
    }

    [Theory]
    [InlineData("rule_asks")]
    [InlineData("terminal_protected_path_requires_approval")]
    public void Explicit_and_protected_asks_keep_only_the_session_matcher(string askSafeCode)
    {
        var plan = Build(CaseContext("run_process"), askSafeCode);

        Assert.NotNull(plan.SessionMatcher);
        Assert.Null(plan.DurableMatcher);
    }

    [Fact]
    public void Timeout_asks_keep_both_matchers()
    {
        var plan = Build(CaseContext("run_process"), "terminal_timeout_requires_approval");

        Assert.NotNull(plan.SessionMatcher);
        Assert.NotNull(plan.DurableMatcher);
    }

    [Fact]
    public void External_directory_approval_remembers_only_the_nearest_existing_folder()
    {
        var external = Path.Combine(Path.GetTempPath(), "ru-remember-ext-" + Guid.NewGuid().ToString("N"));
        var sub = Path.Combine(external, "sub");
        Directory.CreateDirectory(sub);
        try
        {
            var context = Context("read_file", new JsonObject { ["path"] = Path.Combine(sub, "missing", "file.txt") });
            var plan = ApprovalRememberPolicy.Build(context, "read_file", "1.0.0", "external_directory_required", externalDirectoryRequested: true);
            var durable = plan.DurableMatcher;

            Assert.Null(plan.SessionMatcher);
            Assert.NotNull(durable);
            Assert.Null(durable.ToolId);
            Assert.Null(durable.ArgvPattern);
            Assert.Equal(Path.GetFullPath(sub).TrimEnd('\\') + "\\**", durable.CanonicalPathPattern);
            Assert.True(Matches(durable, context));
            Assert.True(Matches(durable, Context("write_file", new JsonObject { ["path"] = Path.Combine(sub, "other.txt"), ["content"] = "x" })));
            Assert.False(Matches(durable, Context("read_file", new JsonObject { ["path"] = Path.Combine(external, "elsewhere.txt") })));
        }
        finally
        {
            Directory.Delete(external, recursive: true);
        }
    }

    [Fact]
    public void External_directory_approval_never_remembers_a_workspace_target()
    {
        var plan = ApprovalRememberPolicy.Build(
            CaseContext("read_file"), "read_file", "1.0.0", "external_directory_required", externalDirectoryRequested: true);

        Assert.False(plan.IsRememberable);
    }

    [Fact]
    public void External_directory_approval_is_not_offered_for_a_language_server_launch()
    {
        var external = Path.Combine(Path.GetTempPath(), "ru-remember-lsp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(external);
        try
        {
            var context = Context("code_intelligence", new JsonObject
            {
                ["operation"] = "diagnostics", ["language"] = "csharp", ["path"] = Path.Combine(external, "Example.cs"),
            });
            var plan = ApprovalRememberPolicy.Build(context, "code_intelligence", "1.0.0", "external_directory_required", externalDirectoryRequested: true);

            Assert.False(plan.IsRememberable);
        }
        finally
        {
            Directory.Delete(external, recursive: true);
        }
    }

    [Theory]
    [InlineData("D:")]
    [InlineData(@"D:\")]
    [InlineData(@"\\server\share")]
    [InlineData(@"\\server\share\")]
    [InlineData(@"\\?\Volume{6f1d2f2a-0000-0000-0000-100000000000}\")]
    public void Drive_volume_and_share_roots_are_root_folders(string folder)
    {
        Assert.True(ApprovalRememberPolicy.IsRootFolder(folder));
    }

    [Theory]
    [InlineData(@"D:\data")]
    [InlineData(@"\\server\share\data")]
    [InlineData(@"C:\Users\user\.ssh")]
    public void Folders_below_a_root_are_not_root_folders(string folder)
    {
        Assert.False(ApprovalRememberPolicy.IsRootFolder(folder));
    }

    [Fact]
    public void External_approval_whose_nearest_existing_folder_is_a_drive_root_is_not_rememberable()
    {
        // The only existing ancestor is the drive root, so a folder pattern would cover the whole volume.
        var driveRoot = Path.GetPathRoot(Path.GetTempPath())!;
        var missing = Path.Combine(driveRoot, "ru-missing-" + Guid.NewGuid().ToString("N"), "note.txt");
        var context = Context("read_file", new JsonObject { ["path"] = missing });

        Assert.Null(ApprovalRememberPolicy.ExternalDirectoryPattern(context.Target!.CanonicalPath));
        var plan = ApprovalRememberPolicy.Build(context, "read_file", "1.0.0", "external_directory_required", externalDirectoryRequested: true);

        Assert.Null(plan.SessionMatcher);
        Assert.Null(plan.DurableMatcher);
        Assert.False(plan.IsRememberable);
    }

    [Fact]
    public void Root_boundary_is_not_a_session_grant_but_a_folder_boundary_is()
    {
        Assert.True(ApprovalRememberPolicy.IsRootBoundary(Path.GetPathRoot(Path.GetTempPath())));
        Assert.False(ApprovalRememberPolicy.IsRootBoundary(null));
        Assert.False(ApprovalRememberPolicy.IsRootBoundary(Path.Combine(Path.GetTempPath(), "ru-boundary")));
    }

    [Fact]
    public void Matchers_use_the_relative_candidate_the_evaluator_compares_for_workspace_paths()
    {
        var plan = Build(CaseContext("read_file"));

        Assert.Equal("src/Example.cs", plan.SessionMatcher!.CanonicalPathPattern);
    }

    private static ApprovalRememberPlan Build(PolicyContext context, string? askSafeCode = null) =>
        ApprovalRememberPolicy.Build(context, context.ToolId, context.ToolVersion, askSafeCode, externalDirectoryRequested: false);

    private static bool Matches(BrokerPolicyMatcherWire matcher, PolicyContext context)
    {
        var rule = PolicyRuleMatcher.Parse(new BrokerPolicyRuleWire("approval-probe", "project", "allow", matcher, 0, null));
        return PolicyRuleMatcher.Matches(rule, context, Now);
    }

    private static string[] BoundFields(BrokerPolicyMatcherWire matcher)
    {
        var fields = new List<string>();
        if (matcher.ToolId is not null) fields.Add("tool");
        if (matcher.ToolVersion is not null) fields.Add("version");
        if (matcher.CanonicalPathPattern is not null) fields.Add("path");
        if (matcher.ResolvedExecutable is not null) fields.Add("executable");
        if (matcher.ArgvPattern is { Count: > 0 }) fields.Add("argv");
        if (matcher.CwdPattern is not null) fields.Add("cwd");
        if (matcher.OperationClass is not null) fields.Add("operation");
        if (matcher.NetworkIntent is not null) fields.Add("network");
        return fields.OrderBy(field => field, StringComparer.Ordinal).ToArray();
    }

    private static PolicyContext Mutate(PolicyContext context, string field) => field switch
    {
        "tool" => context with { ToolId = "help" },
        "version" => context with { ToolVersion = "9.9.9" },
        "path" => context with { RelativePathInsideWorkspace = "zz/other.txt" },
        "executable" => context with { ResolvedExecutable = context.ResolvedExecutable + ".other" },
        "argv" => context with { Argv = [.. context.Argv.Select((token, index) => index == 0 ? "zz-changed" : token)] },
        "cwd" => context with { CanonicalCwd = context.CanonicalCwd + "-other" },
        "operation" => context with { OperationClass = context.OperationClass == "read" ? "list" : "read" },
        "network" => context with { NetworkIntent = "declared" },
        _ => throw new ArgumentOutOfRangeException(nameof(field), field, "unmapped identity field"),
    };

    private static PolicyContext CaseContext(string caseName) => caseName switch
    {
        "read_file" => Context("read_file", new JsonObject { ["path"] = "src\\Example.cs" }),
        "list" => Context("list", new JsonObject { ["path"] = "src" }),
        "grep" => Context("grep", new JsonObject { ["query"] = "TODO", ["path"] = "src" }),
        "glob" => Context("glob", new JsonObject { ["pattern"] = "**/*.cs", ["path"] = "src" }),
        "write_file" => Context("write_file", new JsonObject { ["path"] = "src\\New.cs", ["content"] = "x" }),
        "edit_file" => Context("edit_file", new JsonObject
        {
            ["path"] = "src\\Example.cs",
            ["edits"] = new JsonArray(new JsonObject { ["old_string"] = "a", ["new_string"] = "b" }),
        }),
        "file_delete" => Context("file_delete", new JsonObject { ["path"] = "src\\Old.cs" }),
        "run_process" => Context("run_process", new JsonObject
        {
            ["executable"] = "git", ["arguments"] = new JsonArray("status"), ["working_directory"] = ".",
        }),
        "powershell" => Context("powershell", new JsonObject { ["script"] = "Get-Date", ["working_directory"] = "." }),
        "mcp_call" => Context("mcp", new JsonObject { ["op"] = "call", ["server"] = "github", ["tool"] = "create_issue" }),
        "mcp_list_tools" => Context("mcp", new JsonObject { ["op"] = "list_tools", ["server"] = "github" }),
        "computer_request_access" => Context("computer", new JsonObject
        {
            ["action"] = "request_access", ["apps"] = new JsonArray("notepad.exe"),
        }),
        "computer_focus" => Context("computer", new JsonObject { ["action"] = "focus", ["app"] = "notepad.exe" }),
        "code_intelligence" => Context("code_intelligence", new JsonObject
        {
            ["operation"] = "definition", ["language"] = "csharp", ["path"] = "src\\Example.cs", ["line"] = 2, ["character"] = 4,
        }),
        _ => throw new ArgumentOutOfRangeException(nameof(caseName), caseName, "unmapped case"),
    };

    private static PolicyContext Context(string toolId, JsonObject arguments) =>
        Shared.Value.Factory.Create(toolId, "1.0.0", arguments);

    private static readonly Lazy<SharedContexts> Shared = new(CreateShared);

    private static SharedContexts CreateShared()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-remember-policy-" + Guid.NewGuid().ToString("N"));
        var profile = Path.Combine(root, "profile");
        Directory.CreateDirectory(Path.Combine(root, "src"));
        Directory.CreateDirectory(Path.Combine(profile, ".ru"));
        File.WriteAllText(Path.Combine(profile, ".ru", "lsp.json"),
            """{"servers":{"csharp":{"command":"csharp-ls.exe","args":["--stdio","--flag"]}}}""");
        var snapshot = PolicyEvalSnapshots.Empty("standard");
        var lease = RuAgent.Broker.Runtime.BrokerRunLease.CreateValidated(
            PolicyEvalSnapshots.RunId,
            PolicyEvalSnapshots.SessionId,
            PolicyEvalSnapshots.WorkspaceId,
            root,
            PolicyEvalSnapshots.VolumeSerial,
            PolicyEvalSnapshots.RootFileId,
            PolicyEvalSnapshots.DisplayHash,
            snapshot.Preset,
            snapshot.Version,
            BrokerPolicyContracts.ComputeCanonicalSnapshotHash(snapshot),
            DateTimeOffset.UtcNow.AddMinutes(5),
            snapshot);
        return new SharedContexts(new PolicyContextFactory(lease, userHome: () => profile));
    }

    private sealed record SharedContexts(PolicyContextFactory Factory);
}
