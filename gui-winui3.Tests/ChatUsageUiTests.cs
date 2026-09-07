using System;
using System.IO;
using System.Linq;
using RuApp.Models;
using RuApp.Services;
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
    public void Chat_markup_and_controller_include_provider_usage_contract()
    {
        var root = FindSourceRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "Views", "ChatView.xaml"));
        var codeBehind = File.ReadAllText(Path.Combine(root, "Views", "ChatView.xaml.cs"));
        var controller = File.ReadAllText(Path.Combine(root, "Services", "AgentRunController.cs"));

        Assert.Contains("UsageLabel", xaml, StringComparison.Ordinal);
        Assert.Contains("HasUsage", xaml, StringComparison.Ordinal);
        Assert.Contains("UsageReceived", codeBehind, StringComparison.Ordinal);
        Assert.Contains("model.usage_recorded", controller, StringComparison.Ordinal);
        Assert.Contains("GetPayloadAsync", controller, StringComparison.Ordinal);
    }

    private static string FindSourceRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            string sourceRoot = Path.Combine(directory.FullName, "gui-winui3");
            if (Directory.Exists(sourceRoot))
            {
                return sourceRoot;
            }
        }

        throw new DirectoryNotFoundException("gui-winui3 source root was not found.");
    }
}
