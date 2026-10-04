using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RuBackend.Business.Sessions;
using Xunit;

namespace RuBackend.Tests.Business;

public sealed class SessionWorkspaceResolverTests
{
    [Fact]
    public void SessionSummary_roundtrips_nullable_workspace_fields()
    {
        var summary = new SessionSummary(
            "sess_1",
            "Title",
            DateTimeOffset.Parse("2026-09-17T00:00:00Z"),
            DateTimeOffset.Parse("2026-09-17T01:00:00Z"),
            3,
            null,
            null,
            "ws_abc",
            "LLM",
            @"D:\LLM");

        var json = JsonSerializer.Serialize(summary);
        var back = JsonSerializer.Deserialize<SessionSummary>(json)!;

        Assert.Equal("ws_abc", back.WorkspaceId);
        Assert.Equal("LLM", back.WorkspaceLabel);
        Assert.Equal(@"D:\LLM", back.WorkspacePath);
    }

    [Fact]
    public void WorkspaceLabel_uses_folder_name_of_root()
    {
        Assert.Equal("LLM", SessionWorkspaceResolver.LabelForRoot(@"D:\LLM"));
        Assert.Equal("LLM", SessionWorkspaceResolver.LabelForRoot(@"D:\LLM\"));
        Assert.Equal("workspace", SessionWorkspaceResolver.LabelForRoot(@"D:\"));
    }

    [Fact]
    public async Task Latest_workspace_lookup_is_scoped_indexed_and_breaks_timestamp_ties_by_run_id()
    {
        var builder = typeof(SessionWorkspaceResolver).GetMethod(
            "BuildLatestWorkspaceSql",
            BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(builder);
        var sql = Assert.IsType<string>(builder!.Invoke(null, [2]));
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using (var setup = connection.CreateCommand())
        {
            setup.CommandText = """
                CREATE TABLE agent_runs(run_id TEXT PRIMARY KEY, session_id TEXT NOT NULL, workspace_id TEXT NOT NULL, created_at_utc TEXT NOT NULL);
                CREATE INDEX ix_agent_runs_session_created_run ON agent_runs(session_id, created_at_utc DESC, run_id DESC);
                INSERT INTO agent_runs VALUES
                    ('run-a', 'wanted', 'ws-old', '2026-10-01T00:00:00Z'),
                    ('run-z', 'wanted', 'ws-tie-winner', '2026-10-01T00:00:00Z'),
                    ('run-unrelated', 'unrelated', 'ws-unrelated', '2099-01-01T00:00:00Z');
                """;
            await setup.ExecuteNonQueryAsync();
        }

        var query = connection.CreateCommand();
        query.CommandText = sql;
        query.Parameters.AddWithValue("$session_0", "wanted");
        query.Parameters.AddWithValue("$session_1", "missing");
        var resolved = new List<(string SessionId, string WorkspaceId)>();
        await using (var reader = await query.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                resolved.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        Assert.Equal([("wanted", "ws-tie-winner")], resolved);

        var explain = connection.CreateCommand();
        explain.CommandText = $"EXPLAIN QUERY PLAN {sql}";
        explain.Parameters.AddWithValue("$session_0", "wanted");
        explain.Parameters.AddWithValue("$session_1", "missing");
        var plan = new List<string>();
        await using (var reader = await explain.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                plan.Add(reader.GetString(3));
            }
        }

        Assert.Contains(plan, detail => detail.Contains(
            "ix_agent_runs_session_created_run",
            StringComparison.Ordinal));
    }
}
