using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using Xunit;

namespace RuAgent.Harness.Persistence.Tests;

public sealed class ActiveSkillSnapshotsTests
{
    [Fact]
    public void Explicit_arguments_cannot_expand_a_small_body_beyond_the_context_budget()
    {
        var body = string.Concat(Enumerable.Repeat("$ARGUMENTS ", 100));
        Assert.Throws<ArgumentException>(() => ActiveSkillSnapshots.EncodeExplicit("demo", "Demo", "Description",
            body, Hash(body), "C:\\skills\\demo\\SKILL.md", "C:\\skills\\demo", "revision-1", new string('x', 8192)));
    }

    [Fact]
    public void Escaped_workflow_is_reattached_when_the_visible_tool_receipt_exceeds_adapter_limits()
    {
        var body = new string('<', 60_000);
        var (call, result) = NativeReceipt(1, 2, "demo", body);
        Assert.True(result.Content!.Length > 128 * 1024);
        var projected = ActiveSkillSnapshots.Project([], [call, result]);
        var workflow = Assert.IsType<ModelContextEntry>(ActiveSkillSnapshots.CreateWorkflowEntry(projected.Snapshots, [call, result]));
        Assert.Equal(body, ReadRenderedBody(workflow.Content!));
    }

    [Fact]
    public void Successful_native_receipt_is_pinned_then_reattached_only_when_compaction_hides_it()
    {
        var (call, result) = NativeReceipt(1, 2, "demo", "first workflow");
        var projected = ActiveSkillSnapshots.Project([], [call, result]);
        var snapshot = Assert.Single(projected.Snapshots);
        Assert.Equal(Hash("first workflow"), snapshot.BodySha256);
        Assert.Null(ActiveSkillSnapshots.CreateWorkflowEntry(projected.Snapshots, [call, result]));

        // A restart/cold reconstruction can seed from durable receipts; a warm continuation
        // carries the bounded snapshot after compaction removed the original pair.
        var afterCompaction = ActiveSkillSnapshots.Project([], [], projected.Snapshots);
        var attached = Assert.IsType<ModelContextEntry>(ActiveSkillSnapshots.CreateWorkflowEntry(afterCompaction.Snapshots, []));
        Assert.Contains("first workflow", attached.Content, StringComparison.Ordinal);
        Assert.Contains("untrusted content", attached.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Failed_or_unassociated_skill_outputs_and_invalid_hashes_are_ignored()
    {
        var (call, result) = NativeReceipt(1, 2, "demo", "workflow");
        var failed = ModelContextEntry.ToolResult("failed-result", 2,
            Receipt("call_demo", ToolExecutionOutcome.Failed), result.Content!);
        Assert.Empty(ActiveSkillSnapshots.Project([], [call, failed]).Snapshots);

        var unrelated = ModelContextEntry.ToolResult("unrelated-result", 2,
            Receipt("call_other", ToolExecutionOutcome.Succeeded), result.Content!);
        Assert.Empty(ActiveSkillSnapshots.Project([], [call, unrelated]).Snapshots);

        var invalidJson = result.Content!.Replace(Hash("workflow"), new string('0', 64), StringComparison.Ordinal);
        var tampered = ModelContextEntry.ToolResult("tampered-result", 2,
            Receipt("call_demo", ToolExecutionOutcome.Succeeded), invalidJson);
        Assert.Empty(ActiveSkillSnapshots.Project([], [call, tampered]).Snapshots);
    }

    [Fact]
    public void Explicit_reserved_entry_is_filtered_as_data_and_latest_version_replaces_previous()
    {
        var first = Snapshot("demo", "old workflow");
        var encoded = ActiveSkillSnapshots.EncodeExplicit(first.Id, first.Name, first.Description, first.Body,
            first.BodySha256, first.SourcePath, first.ResourceBase, first.Revision, "arg value");
        var entry = ModelContextEntry.Text(ActiveSkillSnapshots.ExplicitSourceId, 1,
            ModelContextRole.User, encoded);
        Assert.True(ActiveSkillSnapshots.IsReservedSourceId(entry.SourceId));
        var old = ActiveSkillSnapshots.Project([entry], []).Snapshots;
        var (call, result) = NativeReceipt(1, 2, "demo", "new workflow");
        var latest = ActiveSkillSnapshots.Project([entry], [call, result], old).Snapshots;
        var only = Assert.Single(latest);
        Assert.Equal("new workflow", only.Body);
        var workflow = Assert.IsType<ModelContextEntry>(ActiveSkillSnapshots.CreateWorkflowEntry(latest, []));
        Assert.Equal("new workflow", ReadRenderedBody(workflow.Content!));
    }

    [Fact]
    public void Newer_explicit_invocation_does_not_get_reverted_by_an_older_visible_receipt()
    {
        var (call, result) = NativeReceipt(1, 2, "demo", "older native workflow");
        var previous = ActiveSkillSnapshots.Project([], [call, result]).Snapshots;
        const string body = "new explicit workflow";
        var encoded = ActiveSkillSnapshots.EncodeExplicit("demo", "Demo", "Description", body,
            Hash(body), "C:\\skill\\SKILL.md", "C:\\skill", "rev-2", "current arguments");
        var explicitEntry = ModelContextEntry.Text(ActiveSkillSnapshots.ExplicitRunSourceId + ":evt_1234", 3,
            ModelContextRole.User, encoded);

        var projected = ActiveSkillSnapshots.Project([], [call, result, explicitEntry], previous);

        Assert.Equal(body, Assert.Single(projected.Snapshots).Body);
        var attached = Assert.IsType<ModelContextEntry>(ActiveSkillSnapshots.CreateWorkflowEntry(projected.Snapshots, [call, result]));
        Assert.Equal("base:active-skills", attached.SourceId);
        Assert.Contains("new explicit workflow", attached.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Snapshot_count_and_total_body_bytes_are_bounded_and_omissions_are_disclosed()
    {
        var entries = Enumerable.Range(0, 5).Select((index, offset) =>
        {
            var (call, result) = NativeReceipt(offset * 2 + 1, offset * 2 + 2, "skill" + index, new string('x', 40_000));
            return new[] { call, result };
        }).SelectMany(items => items).ToArray();
        var projected = ActiveSkillSnapshots.Project([], entries);
        Assert.Equal(3, projected.Snapshots.Count); // Three bodies fit under 128 KiB.
        Assert.True(projected.Snapshots.Sum(item => item.BodyByteCount) <= ActiveSkillSnapshots.MaximumBodyBytes);
        Assert.Contains("omitted", projected.OmissionEntry?.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Oversized_explicit_snapshot_is_refused_and_oversized_native_body_is_disclosed()
    {
        var body = new string('x', ActiveSkillSnapshots.MaximumBodyBytes + 1);
        Assert.Throws<ArgumentException>(() => ActiveSkillSnapshots.EncodeExplicit("demo", "Demo", "Description", body,
            Hash(body), "C:\\skill\\SKILL.md", "C:\\skill", "rev"));
        var (call, result) = NativeReceipt(1, 2, "demo", body);
        var projection = ActiveSkillSnapshots.Project([], [call, result]);
        Assert.Empty(projection.Snapshots);
        Assert.Contains("omitted", projection.OmissionEntry?.Content, StringComparison.OrdinalIgnoreCase);
    }

    private static (ModelContextEntry Call, ModelContextEntry Result) NativeReceipt(long callSequence,
        long resultSequence, string name, string body)
    {
        using var arguments = JsonDocument.Parse(JsonSerializer.Serialize(new { name }));
        var call = ModelContextEntry.Assistant("assistant-step", callSequence,
            new AssistantStep(null, [new ModelToolCall("provider_demo", "skill", "1", arguments.RootElement)],
                AssistantFinishReason.ToolCalls, 0));
        var json = JsonSerializer.Serialize(new
        {
            id = name, name, description = "Description", body, body_sha256 = Hash(body),
            source_path = "C:\\workspace\\.agents\\skills\\" + name + "\\SKILL.md",
            resource_base = "C:\\workspace\\.agents\\skills\\" + name,
            revision = "rev-1"
        });
        return (call, ModelContextEntry.ToolResult("tool-result", resultSequence,
            Receipt("provider_demo", ToolExecutionOutcome.Succeeded), json));
    }

    private static ToolExecutionReceipt Receipt(string providerCallId, ToolExecutionOutcome outcome) => new(
        new ExecutionId("exec_11111111111111111111111111111111"), new ToolCallId("call_11111111111111111111111111111111"),
        outcome, outcome == ToolExecutionOutcome.Succeeded ? "ok" : "failed", null, false,
        providerCallId: providerCallId);

    private static ActiveSkillSnapshots.Snapshot Snapshot(string id, string body) => new(id, id, "Description", body,
        Hash(body), "C:\\skill\\SKILL.md", "C:\\skill", "rev-1");

    private static string Hash(string body) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();

    private static string ReadRenderedBody(string content)
    {
        var line = content.Split('\n').Skip(1).First();
        using var document = JsonDocument.Parse(line);
        return document.RootElement.GetProperty("body").GetString()!;
    }
}
