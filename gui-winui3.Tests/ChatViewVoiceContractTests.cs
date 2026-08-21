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
    public void Chat_initialize_accepts_a_voice_frame_factory()
    {
        string code = File.ReadAllText(FindSource("Views", "ChatView.xaml.cs"));

        Assert.Contains("Func<IVoiceFrameSource>?", code, StringComparison.Ordinal);
        Assert.Contains("new VoiceTurnCaptureService(voiceSourceFactory)", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Chat_exposes_stable_voice_automation_ids_and_status()
    {
        string xaml = File.ReadAllText(FindSource("Views", "ChatView.xaml"));

        Assert.Contains("AutomationProperties.AutomationId=\"MicButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"VoiceConversationStatus\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"ChatMessages\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"ChatInput\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"SendButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"VoiceConversationStatusText\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void App_and_shell_use_the_explicit_harness_composition_boundary()
    {
        string app = File.ReadAllText(FindSourceRootFile("App.xaml.cs"));
        string mainWindow = File.ReadAllText(FindSourceRootFile("MainWindow.xaml.cs"));
        string shell = File.ReadAllText(FindSourceRootFile("MainWindow.xaml"));

        Assert.Contains("RuAppComposition.FromArguments", app, StringComparison.Ordinal);
        Assert.Contains("MainWindow(composition)", app, StringComparison.Ordinal);
        Assert.Contains("IRuBackendSessionProvider", mainWindow, StringComparison.Ordinal);
        Assert.Contains("VoiceSourceFactory", mainWindow, StringComparison.Ordinal);
        Assert.Contains("PlaybackDeviceFactory", mainWindow, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"BackendStatus\"", shell, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"NewSessionButton\"", shell, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"SessionList\"", shell, StringComparison.Ordinal);
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

    private static string FindSourceRootFile(string fileName)
    {
        return Path.Combine(FindSourceRoot(), fileName);
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
