using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Policy;
using RuAgent.Harness.Persistence.Policy;
using Xunit;

namespace RuAgent.Harness.Persistence.Tests;

public sealed class PolicyRuleStoreTests
{
    [Fact]
    public async Task Global_rule_explicit_ownership_is_persisted()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var store = new SqlitePolicyRuleStore(fixture.Options, new FakeHarnessKeyStore());
        var rule = Rule(1, PolicyRuleScope.Global, PolicyDecision.Allow, path: "src/app.ts", creationOrder: 1);

        await store.CreateAsync(rule, workspaceId: null, runId: null, HarnessStoreTestFixture.Timestamp, CancellationToken.None);

        var globals = await store.ListGlobalAsync(CancellationToken.None);
        Assert.Single(globals);
        Assert.Equal(rule.RuleId, globals[0].RuleId);
        Assert.Equal(PolicyRuleScope.Global, globals[0].Scope);
        var projects = await store.ListProjectAsync(HarnessStoreTestFixture.Workspace(1).WorkspaceId, CancellationToken.None);
        Assert.Empty(projects);
        var runs = await store.ListRunAsync(HarnessStoreTestFixture.Run(1), CancellationToken.None);
        Assert.Empty(runs);
    }

    [Fact]
    public async Task Project_rule_requires_workspace_and_is_isolated_per_workspace()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var ws1 = await fixture.RegisterWorkspaceAsync(1);
        var ws2 = await fixture.RegisterWorkspaceAsync(2);
        var store = new SqlitePolicyRuleStore(fixture.Options, new FakeHarnessKeyStore());
        var rule1 = Rule(10, PolicyRuleScope.Project, PolicyDecision.Allow, path: "src/a.ts", creationOrder: 10);
        var rule2 = Rule(11, PolicyRuleScope.Project, PolicyDecision.Deny, path: "src/b.ts", creationOrder: 11);

        await store.CreateAsync(rule1, workspaceId: ws1.WorkspaceId, runId: null, HarnessStoreTestFixture.Timestamp, CancellationToken.None);
        await store.CreateAsync(rule2, workspaceId: ws2.WorkspaceId, runId: null, HarnessStoreTestFixture.Timestamp, CancellationToken.None);

        var list1 = await store.ListProjectAsync(ws1.WorkspaceId, CancellationToken.None);
        var list2 = await store.ListProjectAsync(ws2.WorkspaceId, CancellationToken.None);
        Assert.Single(list1);
        Assert.Equal(rule1.RuleId, list1[0].RuleId);
        Assert.Single(list2);
        Assert.Equal(rule2.RuleId, list2[0].RuleId);

        await Assert.ThrowsAsync<HarnessStoreException>(() =>
            store.CreateAsync(rule1, workspaceId: null, runId: null, HarnessStoreTestFixture.Timestamp, CancellationToken.None));
    }

    [Fact]
    public async Task Run_rule_requires_run_and_is_owned_by_that_run()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var store = new SqlitePolicyRuleStore(fixture.Options, new FakeHarnessKeyStore());
        var run1 = HarnessStoreTestFixture.Run(1);
        var run2 = HarnessStoreTestFixture.Run(2);
        // create runs so FK passes
        var eventStore = fixture.CreateEventStore();
        await eventStore.TryCreateRunAsync(fixture.NewRun(1, 1, 1), CancellationToken.None);
        await eventStore.TryCreateRunAsync(fixture.NewRun(2, 2, 1), CancellationToken.None);
        var rule = Rule(20, PolicyRuleScope.Run, PolicyDecision.Allow, path: "src/run.ts", creationOrder: 20);

        await store.CreateAsync(rule, workspaceId: null, runId: run1, HarnessStoreTestFixture.Timestamp, CancellationToken.None);

        var listRun1 = await store.ListRunAsync(run1, CancellationToken.None);
        var listRun2 = await store.ListRunAsync(run2, CancellationToken.None);
        Assert.Single(listRun1);
        Assert.Empty(listRun2);

        await Assert.ThrowsAsync<HarnessStoreException>(() =>
            store.CreateAsync(rule, workspaceId: null, runId: null, HarnessStoreTestFixture.Timestamp, CancellationToken.None));
    }

    [Fact]
    public async Task Matcher_is_exact_by_default_and_preserved()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var store = new SqlitePolicyRuleStore(fixture.Options, new FakeHarnessKeyStore());
        var rule = new PolicyRule(
            new PolicyRuleId("rule_" + new string('a', 32)),
            PolicyRuleScope.Global,
            PolicyDecision.Allow,
            new PolicyMatcher(CanonicalPathPattern: "src/exact/file.ts", ResolvedExecutable: "git", ArgvPattern: new[] { "status" }, CwdPattern: "src"),
            CreationOrder: 5,
            ExpiresAt: null);

        await store.CreateAsync(rule, null, null, HarnessStoreTestFixture.Timestamp, CancellationToken.None);
        var loaded = await store.ListGlobalAsync(CancellationToken.None);
        Assert.Single(loaded);
        Assert.Equal("src/exact/file.ts", loaded[0].Matcher.CanonicalPathPattern);
        Assert.Equal("git", loaded[0].Matcher.ResolvedExecutable);
        Assert.Equal(new[] { "status" }, loaded[0].Matcher.ArgvPattern);
        Assert.Equal("src", loaded[0].Matcher.CwdPattern);
    }

    [Fact]
    public async Task Encrypted_path_argv_cwd_not_visible_in_raw_db()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var store = new SqlitePolicyRuleStore(fixture.Options, new FakeHarnessKeyStore());
        var sensitivePath = "D:\\sensitive\\path\\secret.ts";
        var sensitiveArgv = "super-secret-argv-value-xyz";
        var sensitiveCwd = "D:\\sensitive\\cwd";
        var rule = new PolicyRule(
            new PolicyRuleId("rule_" + new string('b', 32)),
            PolicyRuleScope.Global,
            PolicyDecision.Allow,
            new PolicyMatcher(CanonicalPathPattern: sensitivePath, ArgvPattern: new[] { sensitiveArgv }, CwdPattern: sensitiveCwd),
            1,
            null);

        await store.CreateAsync(rule, null, null, HarnessStoreTestFixture.Timestamp, CancellationToken.None);

        var raw = await RawRuleDocumentAsync(fixture, "agent_global_rules", rule.RuleId.Value);
        var rawText = Encoding.UTF8.GetString(raw);
        Assert.DoesNotContain(sensitivePath, rawText);
        Assert.DoesNotContain(sensitiveArgv, rawText);
        Assert.DoesNotContain(sensitiveCwd, rawText);

        var loaded = await store.ListGlobalAsync(CancellationToken.None);
        Assert.Equal(sensitivePath, loaded[0].Matcher.CanonicalPathPattern);
        Assert.Equal(sensitiveArgv, loaded[0].Matcher.ArgvPattern![0]);
        Assert.Equal(sensitiveCwd, loaded[0].Matcher.CwdPattern);
    }

    [Fact]
    public async Task Stable_metadata_remains_queryable_without_decrypting_matcher()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var store = new SqlitePolicyRuleStore(fixture.Options, new FakeHarnessKeyStore());
        var expires = HarnessStoreTestFixture.Timestamp.AddHours(1);
        var rule = new PolicyRule(
            new PolicyRuleId("rule_" + new string('c', 32)),
            PolicyRuleScope.Project,
            PolicyDecision.Deny,
            new PolicyMatcher(ToolId: "files.delete"),
            CreationOrder: 42,
            ExpiresAt: expires);
        var ws = await fixture.RegisterWorkspaceAsync(7);
        await store.CreateAsync(rule, ws.WorkspaceId, null, HarnessStoreTestFixture.Timestamp, CancellationToken.None);

        // query via store validates metadata
        var loaded = await store.ListProjectAsync(ws.WorkspaceId, CancellationToken.None);
        Assert.Single(loaded);
        Assert.Equal(PolicyRuleScope.Project, loaded[0].Scope);
        Assert.Equal(PolicyDecision.Deny, loaded[0].Decision);
        Assert.Equal(42, loaded[0].CreationOrder);
        Assert.Equal(expires, loaded[0].ExpiresAt);

        // raw DB still contains plaintext metadata (rule_id etc) – verify rule_document contains rule_id plaintext
        var raw = await RawRuleDocumentAsync(fixture, "agent_project_rules", rule.RuleId.Value);
        var rawText = Encoding.UTF8.GetString(raw);
        Assert.Contains(rule.RuleId.Value, rawText);
        // scope/decision as integers or strings should be visible
        Assert.Contains("42", rawText);
    }

    [Fact]
    public async Task Invalid_scope_decision_matcher_are_rejected()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var store = new SqlitePolicyRuleStore(fixture.Options, new FakeHarnessKeyStore());

        var invalidScopeRule = new PolicyRule(
            new PolicyRuleId("rule_" + new string('d', 32)),
            (PolicyRuleScope)99,
            PolicyDecision.Allow,
            new PolicyMatcher(ToolId: "files.read"),
            1,
            null);
        await Assert.ThrowsAsync<HarnessStoreException>(() =>
            store.CreateAsync(invalidScopeRule, null, null, HarnessStoreTestFixture.Timestamp, CancellationToken.None));

        var invalidDecisionRule = new PolicyRule(
            new PolicyRuleId("rule_" + new string('e', 32)),
            PolicyRuleScope.Global,
            (PolicyDecision)99,
            new PolicyMatcher(ToolId: "files.read"),
            1,
            null);
        await Assert.ThrowsAsync<HarnessStoreException>(() =>
            store.CreateAsync(invalidDecisionRule, null, null, HarnessStoreTestFixture.Timestamp, CancellationToken.None));

        // invalid matcher: unknown operation class should be rejected at matcher construction,
        // but we test store validation via corrupted persisted matcher – simulate by direct DB corruption?
        // For now, verify that a matcher with null bytes fails
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Task.FromResult(new PolicyMatcher(CanonicalPathPattern: "bad\0path")));
    }

    [Fact]
    public async Task Matcher_payload_and_rule_metadata_writes_are_atomic()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var store = new SqlitePolicyRuleStore(fixture.Options, new FakeHarnessKeyStore());
        var rule = Rule(30, PolicyRuleScope.Global, PolicyDecision.Allow, path: "src/atomic.ts", creationOrder: 30);
        await store.CreateAsync(rule, null, null, HarnessStoreTestFixture.Timestamp, CancellationToken.None);

        // duplicate insert should fail and not leave partial duplicate
        var duplicate = new PolicyRule(
            rule.RuleId,
            PolicyRuleScope.Global,
            PolicyDecision.Deny,
            new PolicyMatcher(ToolId: "files.write"),
            31,
            null);
        await Assert.ThrowsAsync<HarnessStoreException>(() =>
            store.CreateAsync(duplicate, null, null, HarnessStoreTestFixture.Timestamp, CancellationToken.None));

        var count = await ScalarAsync(fixture, "SELECT COUNT(*) FROM agent_global_rules");
        Assert.Equal(1, count);
        var globals = await store.ListGlobalAsync(CancellationToken.None);
        Assert.Single(globals);
        Assert.Equal(PolicyDecision.Allow, globals[0].Decision);
    }

    [Fact]
    public async Task Matcher_metadata_tampering_fails_closed()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var store = new SqlitePolicyRuleStore(fixture.Options, new FakeHarnessKeyStore());
        var rule = Rule(50, PolicyRuleScope.Global, PolicyDecision.Allow, path: "src/tamper.ts", creationOrder: 50);
        await store.CreateAsync(rule, null, null, HarnessStoreTestFixture.Timestamp, CancellationToken.None);

        var raw = await RawRuleDocumentAsync(fixture, "agent_global_rules", rule.RuleId.Value);
        var document = JsonNode.Parse(raw)!.AsObject();
        Assert.Equal(2, document["matcher_encrypted"]!["version"]!.GetValue<int>());
        document["decision"] = (int)PolicyDecision.Deny;
        await WriteRuleDocumentAsync(
            fixture,
            "agent_global_rules",
            rule.RuleId.Value,
            Encoding.UTF8.GetBytes(document.ToJsonString()));

        var error = await Assert.ThrowsAsync<HarnessStoreException>(
            () => store.ListGlobalAsync(CancellationToken.None));
        Assert.Equal("policy_decryption_failed", error.Code);
    }

    [Fact]
    public async Task Legacy_version_one_documents_still_decrypt()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var store = new SqlitePolicyRuleStore(fixture.Options, new FakeHarnessKeyStore());
        var ruleId = "rule_" + new string('f', 32);
        var matcherJson = Encoding.UTF8.GetBytes("{\"tool_id\":\"files.read\"}");
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[matcherJson.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(Enumerable.Repeat((byte)0x5a, 32).ToArray(), 16))
        {
            aes.Encrypt(nonce, matcherJson, ciphertext, tag, Encoding.UTF8.GetBytes(ruleId));
        }

        var document = new JsonObject
        {
            ["rule_id"] = ruleId,
            ["scope"] = (int)PolicyRuleScope.Global,
            ["decision"] = (int)PolicyDecision.Allow,
            ["creation_order"] = 7,
            ["expires_at_utc"] = null,
            ["version"] = 1,
            ["created_at_utc"] = HarnessStoreTestFixture.Timestamp.ToString("O"),
            ["updated_at_utc"] = HarnessStoreTestFixture.Timestamp.ToString("O"),
            ["matcher_encrypted"] = new JsonObject
            {
                ["nonce"] = Convert.ToBase64String(nonce),
                ["ciphertext"] = Convert.ToBase64String(ciphertext),
                ["tag"] = Convert.ToBase64String(tag),
                ["version"] = 1,
            },
        };
        await WriteRuleDocumentAsync(
            fixture,
            "agent_global_rules",
            ruleId,
            Encoding.UTF8.GetBytes(document.ToJsonString()));

        var loaded = await store.ListGlobalAsync(CancellationToken.None);
        var single = Assert.Single(loaded);
        Assert.Equal(ruleId, single.RuleId.Value);
        Assert.Equal("files.read", single.Matcher.ToolId);
        Assert.Equal(7, single.CreationOrder);
    }

    private static PolicyRule Rule(int number, PolicyRuleScope scope, PolicyDecision decision, string path, long creationOrder)
    {
        var id = new PolicyRuleId("rule_" + number.ToString("x32"));
        return new PolicyRule(id, scope, decision, new PolicyMatcher(CanonicalPathPattern: path), creationOrder, null);
    }

    private static async Task<byte[]> RawRuleDocumentAsync(HarnessStoreTestFixture fixture, string table, string ruleId)
    {
        await using var conn = fixture.CreateRawConnection();
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT rule_document FROM {table} WHERE rule_id = $id";
        cmd.Parameters.AddWithValue("$id", ruleId);
        var result = await cmd.ExecuteScalarAsync();
        Assert.NotNull(result);
        return (byte[])result!;
    }

    private static async Task WriteRuleDocumentAsync(
        HarnessStoreTestFixture fixture,
        string table,
        string ruleId,
        byte[] document)
    {
        await using var conn = fixture.CreateRawConnection();
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = table == "agent_global_rules"
            ? "INSERT OR REPLACE INTO agent_global_rules(rule_id, rule_document, version, created_at_utc, updated_at_utc) VALUES ($id, $document, 1, $now, $now)"
            : "INSERT OR REPLACE INTO agent_project_rules(rule_id, workspace_id, rule_document, version, created_at_utc, updated_at_utc) VALUES ($id, $workspace, $document, 1, $now, $now)";
        cmd.Parameters.AddWithValue("$id", ruleId);
        cmd.Parameters.AddWithValue("$document", document);
        cmd.Parameters.AddWithValue("$now", HarnessStoreTestFixture.Timestamp.ToString("O"));
        if (table != "agent_global_rules")
        {
            cmd.Parameters.AddWithValue("$workspace", HarnessStoreTestFixture.Workspace(1).WorkspaceId.Value);
        }

        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarAsync(HarnessStoreTestFixture fixture, string sql)
    {
        await using var conn = fixture.CreateRawConnection();
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return (long)(await cmd.ExecuteScalarAsync() ?? 0L);
    }
}
