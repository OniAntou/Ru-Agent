using System;
using System.Collections.Generic;
using RuApp.Models;
using Xunit;

namespace RuApp.Tests;

public sealed class ChatUsageUiTests
{
    [Fact]
    public void Usage_parser_preserves_exact_zero_and_omits_unreported_fields()
    {
        var usage = AgentModelUsage.Parse("{\"input_tokens\":0,\"output_tokens\":12,\"total_tokens\":12}");

        Assert.NotNull(usage);
        Assert.Equal(0, usage!.InputTokens);
        Assert.Equal(12, usage.OutputTokens);
        Assert.Equal("0 input · 12 output · 12 total", usage.DisplayText);
        Assert.DoesNotContain("cache", usage.DisplayText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Usage_parser_returns_null_for_an_empty_or_unusable_payload()
    {
        Assert.Null(AgentModelUsage.Parse("{}"));
        Assert.Null(AgentModelUsage.Parse("null"));
        Assert.Null(AgentModelUsage.Parse("not-json"));
    }

    [Fact]
    public void Chat_message_exposes_usage_footer_only_when_usage_exists()
    {
        var message = new ChatMessageItem();
        Assert.False(message.HasUsage);
        Assert.Equal(string.Empty, message.UsageLabel);

        message.Usage = new AgentModelUsage(1, 2, 3, null, null);

        Assert.True(message.HasUsage);
        Assert.Equal("1 input · 2 output · 3 total", message.UsageLabel);
    }

    [Fact]
    public void Usage_parser_keeps_valid_fields_and_ignores_malformed_ones()
    {
        var usage = AgentModelUsage.Parse("{\"input_tokens\":\"oops\",\"output_tokens\":5,\"total_tokens\":-1}");

        Assert.NotNull(usage);
        Assert.Null(usage!.InputTokens);
        Assert.Equal(5, usage.OutputTokens);
        Assert.Null(usage.TotalTokens);
        Assert.Equal("5 output", usage.DisplayText);
    }

    [Fact]
    public void Usage_parser_keeps_the_model_the_report_names()
    {
        // This parser is hand-written and reads each field by name, so a field added to
        // the wire has to be added here too. It was not, for one commit: every count
        // arrived, the model did not, and the panel reported a priced session as one
        // whose cost nobody could name. Found by opening a real session, not by a test
        // on the record — the record's own round trip was covered and passed.
        var usage = AgentModelUsage.Parse(
            "{\"input_tokens\":4012,\"output_tokens\":17,\"cache_read_input_tokens\":3584,"
            + "\"model\":\"dots-studio/dots-3-note-preview:free\"}");

        Assert.NotNull(usage);
        Assert.Equal("dots-studio/dots-3-note-preview:free", usage!.Model);
        Assert.Equal(4012, usage.InputTokens);
        Assert.Equal(3584, usage.CacheReadInputTokens);
    }

    [Fact]
    public void Usage_parser_leaves_the_model_absent_for_a_report_that_names_none()
    {
        // A run recorded before the field existed still has to parse, as a run nobody
        // can price rather than a report that failed.
        Assert.Null(AgentModelUsage.Parse("{\"input_tokens\":12}")!.Model);
    }

    [Theory]
    [InlineData("12")]
    [InlineData("\"\"")]
    [InlineData("\"  \"")]
    [InlineData("\"bad\\nmodel\"")]
    public void Usage_parser_drops_a_model_that_is_not_a_usable_name(string model)
    {
        // Same bounds the host applies when it writes the field, so the two ends agree
        // on what a name is and neither has to defend itself again.
        var usage = AgentModelUsage.Parse($"{{\"input_tokens\":12,\"model\":{model}}}");

        Assert.NotNull(usage);
        Assert.Equal(12, usage!.InputTokens);
        Assert.Null(usage.Model);
    }

    [Fact]
    public void Chat_message_notifies_usage_bindings_when_usage_arrives_late()
    {
        var message = new ChatMessageItem { Role = "assistant", Content = "done" };
        var notified = new List<string>();
        message.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is not null)
            {
                notified.Add(args.PropertyName);
            }
        };

        message.Usage = new AgentModelUsage(null, 12, 12, null, null);

        Assert.True(message.HasUsage);
        Assert.Equal("12 output · 12 total", message.UsageLabel);
        Assert.Contains("Usage", notified);
        Assert.Contains("HasUsage", notified);
        Assert.Contains("UsageLabel", notified);
    }
}
