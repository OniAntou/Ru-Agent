using RuAgent.Cli.TerminalGui;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Testing;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TgProviderSetupTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Provider_picker_opens_masked_key_box_and_cancel_preserves_selection(bool cancel)
    {
        using var host = new ProviderSetupTests.Host(); using var http = new HttpClient(host);
        var output = new StringWriter(); var errors = new StringWriter(); var commands = host.Commands(http, output, errors);
        using IApplication app = Application.Create(); app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        window.Transcript.Append("provider setup", TranscriptStyle.Prose);
        commands.Picker = (list, ct) => TgPanelRequests.RequestChoiceAsync(window, list, ct);
        commands.SecretPanelPrompt = (title, ct) => TgPanelRequests.RequestSecretAsync(window, title, ct);
        Task<bool>? pending = null; var prompts = 0; var step = 0; Exception? failure = null;
        var nativeSecret = commands.SecretPanelPrompt;
        commands.SecretPanelPrompt = (title, ct) => { prompts++; return nativeSecret(title, ct); };
        app.AddTimeout(TimeSpan.FromMilliseconds(120), () =>
        {
            try
            {
                if (step == 0)
                {
                    window.FocusComposer(); window.Composer.Input.Text = "keep draft";
                    pending = commands.TryHandleAsync("/provider", null, default); step++; return true;
                }
                if (step == 1 && window.IsModalPanelOpen)
                {
                    Assert.Equal(0, prompts); Assert.Empty(host.Posts);
                    app.InjectKey(Key.Enter); step++; return true;
                }
                if (step == 2 && prompts == 1 && window.IsModalPanelOpen)
                {
                    Assert.Empty(host.Posts);
                    window.TryPasteText("synthetic-native-key"); step++; return true;
                }
                if (step == 3)
                {
                    var screen = string.Join('\n', TgSnapshot.Capture(app));
                    Assert.DoesNotContain("synthetic-native-key", screen);
                    Assert.Contains("•", screen); Assert.Empty(host.Posts);
                    app.InjectKey(cancel ? Key.Esc : Key.Enter); step++; return true;
                }
                if (step == 4 && pending!.IsCompleted)
                {
                    Assert.True(pending.GetAwaiter().GetResult());
                    Assert.Equal(cancel ? 0 : 1, host.Posts.Count);
                    if (!cancel) Assert.Equal("synthetic-native-key", host.Posts[0].GetProperty("openai_api_key").GetString());
                    Assert.Equal("keep draft", window.Composer.Input.Text);
                    Assert.DoesNotContain("synthetic-native-key", output.ToString() + errors.ToString());
                    step++; app.RequestStop(); return false;
                }
            }
            catch (Exception ex) { failure = ex; app.RequestStop(); return false; }
            return true;
        });
        app.AddTimeout(TimeSpan.FromSeconds(6), () => { app.RequestStop(); return false; });
        app.Run(window);
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        Assert.Equal(5, step);
    }
}
