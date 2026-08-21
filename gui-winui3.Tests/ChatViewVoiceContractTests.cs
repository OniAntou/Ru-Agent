using Xunit;

namespace RuApp.Tests;

public sealed class ChatViewVoiceContractTests
{
    [Fact]
    public void Chat_uses_the_hands_free_controller_and_reply_completion_boundary()
    {
        var xaml = File.ReadAllText(FindSource("Views", "ChatView.xaml"));
        var code = File.ReadAllText(FindSource("Views", "ChatView.xaml.cs"));

        Assert.Contains("VoiceConversationService", code, StringComparison.Ordinal);
        Assert.Contains("VoiceTurnCaptureService", code, StringComparison.Ordinal);
        Assert.Contains("TranscribeAudioAsync(wavBytes, cancellationToken)", code, StringComparison.Ordinal);
        Assert.Contains("NotifyReplyCompleted", code, StringComparison.Ordinal);
        Assert.Contains("NotifyReplyError", code, StringComparison.Ordinal);
        Assert.Contains("Nói chuyện với Ru bằng giọng nói", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("MicRecorderService", code, StringComparison.Ordinal);
        Assert.DoesNotContain("ChatInputTextBox.Text = text", code, StringComparison.Ordinal);
    }

    [Fact]
    public void The_retired_one_shot_recorder_has_no_remaining_source_file_or_reference()
    {
        string sourceRoot = FindSourceRoot();
        Assert.False(File.Exists(Path.Combine(sourceRoot, "Services", "MicRecorderService.cs")));
        Assert.DoesNotContain(
            "MicRecorderService",
            Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
                .Where(path => !path.Contains("bin", StringComparison.OrdinalIgnoreCase)
                    && !path.Contains("obj", StringComparison.OrdinalIgnoreCase))
                .Select(File.ReadAllText)
                .Aggregate(string.Concat),
            StringComparison.Ordinal);
    }

    private static string FindSource(params string[] parts)
    {
        return Path.Combine(FindSourceRoot(), Path.Combine(parts));
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
