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
