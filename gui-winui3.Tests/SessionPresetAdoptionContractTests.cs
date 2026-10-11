using System;
using Xunit;

namespace RuApp.Tests;

/// <summary>
/// Opening a session must adopt its stored permission preset before the user
/// sends anything. A stale plan session reopened with the previous session's
/// standard choice (or vice versa) either shows the wrong mode or silently
/// re-grades the session on the next send.
/// </summary>
public sealed class SessionPresetAdoptionContractTests
{
    [Fact]
    public void Opening_a_session_adopts_its_stored_preset()
    {
        var code = GuiSource.ViewText("ChatView");
        var open = GuiSource.MethodBody(code, "OpenSessionAsync");

        Assert.Contains("AdoptStoredSessionPresetAsync", open, StringComparison.Ordinal);
    }

    [Fact]
    public void Adoption_reads_the_preset_without_rewriting_it()
    {
        var code = GuiSource.ViewText("ChatView");
        var adopt = GuiSource.MethodBody(code, "AdoptStoredSessionPresetAsync");

        Assert.Contains("GetSessionPresetAsync", adopt, StringComparison.Ordinal);
        // A missing or unreadable selection keeps the current choice: the
        // device must never re-grade a session it could not read.
        Assert.Contains("stored is null", adopt, StringComparison.Ordinal);
        Assert.DoesNotContain("SetSessionPresetAsync", adopt, StringComparison.Ordinal);
    }

    [Fact]
    public void Adoption_seeds_the_version_cache_so_the_next_send_does_not_rewrite()
    {
        var code = GuiSource.ViewText("ChatView");
        var adopt = GuiSource.MethodBody(code, "AdoptStoredSessionPresetAsync");

        Assert.Contains("_agentPresetSessionId = sessionId", adopt, StringComparison.Ordinal);
        Assert.Contains("_agentPresetVersion = stored.Version", adopt, StringComparison.Ordinal);
    }

    [Fact]
    public void Adoption_notifies_the_host_so_the_settings_dropdown_follows()
    {
        var code = GuiSource.ViewText("ChatView");

        Assert.Contains("event EventHandler<SessionPresetAdoptedEventArgs>", code, StringComparison.Ordinal);
        var adopt = GuiSource.MethodBody(code, "AdoptStoredSessionPresetAsync");
        Assert.Contains("SessionPresetAdopted?.Invoke", adopt, StringComparison.Ordinal);

        var shell = GuiSource.ViewText("MainWindow");
        var handler = GuiSource.MethodBody(shell, "OnChatSessionPresetAdopted");
        Assert.Contains("SetCurrentPreset", handler, StringComparison.Ordinal);
        // The adoption may arrive after another switch: only the still-active
        // session may repaint the dropdown.
        Assert.Contains("_activeSessionId", handler, StringComparison.Ordinal);
    }
}
