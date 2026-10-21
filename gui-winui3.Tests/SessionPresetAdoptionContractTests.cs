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
    public void Adoption_goes_through_the_preset_view_model_and_never_writes()
    {
        // Reading without rewriting, keeping the choice when nothing could be read,
        // and seeding the version cache are SessionPresetViewModelTests'
        // Adoption_reads_without_writing_and_seeds_the_version_cache,
        // A_missing_stored_selection_leaves_the_current_choice and
        // A_read_for_a_session_that_is_no_longer_open_is_dropped.
        var code = GuiSource.ViewText("ChatView");
        var adopt = GuiSource.MethodBody(code, "AdoptStoredSessionPresetAsync");

        Assert.Contains("Presets.AdoptStoredAsync", adopt, StringComparison.Ordinal);
        Assert.DoesNotContain("SetSessionPresetAsync", adopt, StringComparison.Ordinal);
        Assert.DoesNotContain("EnsureStoredAsync", adopt, StringComparison.Ordinal);
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
        Assert.Contains("Sidebar.ActiveSessionId", handler, StringComparison.Ordinal);
    }
}
