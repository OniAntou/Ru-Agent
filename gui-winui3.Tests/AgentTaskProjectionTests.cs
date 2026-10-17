using System.Text.Json;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class AgentTaskProjectionTests
{
    [Fact]
    public void Background_process_rows_name_the_command_and_keep_exit_status()
    {
        using var data = JsonDocument.Parse("""
        {"tasks":[
          {"execution_id":"old","background":true,"state":"exited","exit_code":0,"command":"npm run build"},
          {"execution_id":"foreground","background":false,"state":"running"},
          {"execution_id":"live","background":true,"state":"running","exit_code":null,"command":"npm run dev"}
        ]}
        """);
        var result = AgentTaskProjection.Project(data.RootElement);
        Assert.Equal(2, result.Rows.Count);
        Assert.Equal("npm run dev", result.Rows[0].Label);
        Assert.Contains("exit 0", result.Rows[1].DetailText);
        Assert.Equal("1 running · 1 finished", result.Status);
    }

    [Fact]
    public void Older_hosts_and_malformed_optional_fields_remain_readable()
    {
        using var data = JsonDocument.Parse("""
        {"tasks":[
          {"execution_id":7},
          {"execution_id":"legacy","state":"running","command":false,"exit_code":"invalid"}
        ]}
        """);
        var row = Assert.Single(AgentTaskProjection.Project(data.RootElement).Rows);
        Assert.Equal("legacy · running", row.Label);
    }
}
