using RuApp.Models;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class SidebarOrganizationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ru-sidebar-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_directory, "layout.json");

    [Fact]
    public void Cli_sessions_stay_in_cli_section_independently_of_cwd_and_manual_placement()
    {
        var preferences = new SidebarOrganizationPreferences();
        preferences.Groups["cli"] = @"C:\organized";
        foreach (var cwd in new[] { null, @"D:\LLM", @"C:\project" })
        {
            var session = new SessionInfo { Id = "cli", ClientSource = "cli", WorkspacePath = cwd };
            var group = SidebarSessionOrganization.GroupFor(session, @"D:\LLM", preferences);
            Assert.Equal(SidebarSessionOrganization.CliKey, group.Key);
            Assert.True(SidebarSessionOrganization.CanMoveTo(session, group.Key));
            Assert.False(SidebarSessionOrganization.CanMoveTo(session, SidebarSessionOrganization.RecentsKey));
            Assert.Equal(cwd, session.WorkspacePath);
            Assert.Single(WorkspaceSessionProjection.Project([session], "CLI Session",
                row => SidebarSessionOrganization.GroupFor(row, @"D:\LLM", preferences)).Groups);
        }
        Assert.False(SidebarSessionOrganization.CanMoveTo(new() { ClientSource = "gui" }, SidebarSessionOrganization.CliKey));
    }

    [Fact]
    public void Source_change_invalidates_sidebar_poll_fingerprint()
    {
        var session = new SessionInfo { Id = "s", MessageCount = 2 };
        var before = SidebarRowFingerprint.ForSessions([session]);
        session.ClientSource = "cli";
        Assert.NotEqual(before, SidebarRowFingerprint.ForSessions([session]));
    }

    [Fact]
    public void Root_sessions_go_to_recents_but_nested_projects_keep_their_workspace()
    {
        var preferences = new SidebarOrganizationPreferences();
        var root = new SessionInfo { Id = "root", WorkspaceId = "root-id", WorkspacePath = @"D:\LLM\" };
        Assert.Equal("recents", SidebarSessionOrganization.GroupFor(root, @"d:\llm", preferences).Key);
        Assert.Equal("recents", SidebarSessionOrganization.GroupFor(new() { Id = "blank" }, @"D:\LLM", preferences).Key);
        var project = new SessionInfo { Id = "project", WorkspacePath = @"D:\LLM\project" };
        Assert.Equal(@"D:\LLM\project", SidebarSessionOrganization.GroupFor(project, @"D:\LLM", preferences).Path);
    }

    [Fact]
    public void Cross_group_move_survives_restart_and_polls_without_changing_execution_workspace()
    {
        var store = new SidebarOrganizationStore(FilePath);
        var session = new SessionInfo { Id = "s", WorkspaceId = "actual", WorkspacePath = @"C:\actual", MessageCount = 2 };
        Assert.True(store.Move("s", @"C:\organized", null, ["s", "other"]));
        var reopened = new SidebarOrganizationStore(FilePath);
        var projection = WorkspaceSessionProjection.Project([session], "", row => SidebarSessionOrganization.GroupFor(row, @"D:\LLM", reopened.Current));
        Assert.Equal(@"C:\organized", Assert.Single(projection.Groups).Path);
        Assert.Same(session, Assert.Single(Assert.Single(projection.Groups).Sessions));
        Assert.Equal(@"C:\actual", session.WorkspacePath);
        Assert.Equal("actual", session.WorkspaceId);
        Assert.Contains(@"Working directory: C:\actual", session.TooltipText);
        Assert.True(reopened.Move("s", null, null, ["s", "other"]));
        Assert.Equal("recents", SidebarSessionOrganization.GroupFor(session, @"D:\LLM", new SidebarOrganizationStore(FilePath).Current).Key);
    }

    [Fact]
    public void Reordering_keeps_hidden_siblings_and_keeps_new_session_visible()
    {
        var store = new SidebarOrganizationStore(FilePath);
        Assert.True(store.Move("c", null, "a", ["a", "b", "c"]));
        var rows = new SessionInfo[] { new() { Id = "a", MessageCount = 1 }, new() { Id = "b", MessageCount = 1 }, new() { Id = "c", MessageCount = 1 }, new() { Id = "draft" } };
        Assert.Equal(["draft", "c", "a", "b"], SidebarSessionOrganization.Order(rows, store.Current).Select(row => row.Id));
        rows[^1].MessageCount = 1;
        Assert.Equal(["draft", "c", "a", "b"], SidebarSessionOrganization.Order(rows, store.Current).Select(row => row.Id));
        Assert.False(store.Move("c", null, "c", ["a", "b", "c"]));
        Assert.False(store.Move("missing", null, null, ["a", "b", "c"]));
    }

    [Fact]
    public void Failed_save_does_not_apply_an_unsaved_move()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(FilePath, "not a directory");
        var store = new SidebarOrganizationStore(Path.Combine(FilePath, "layout.json"));
        Assert.False(store.Move("s", null, null, ["s"]));
        Assert.Empty(store.Current.Groups);
        Assert.Empty(store.Current.Order);
    }

    [Fact]
    public void Delete_removes_the_override_and_bad_preferences_fall_back_to_automatic_grouping()
    {
        var store = new SidebarOrganizationStore(FilePath);
        Assert.True(store.Move("s", null, null, ["s"]));
        Assert.True(store.Remove("s"));
        Assert.Empty(new SidebarOrganizationStore(FilePath).Current.Groups);
        File.WriteAllText(FilePath, "{\"groups\":null,\"order\":null}");
        Assert.Empty(new SidebarOrganizationStore(FilePath).Current.Order);
    }

    [Fact]
    public void Sidebar_has_one_drag_surface_and_a_keyboard_move_menu()
    {
        var document = GuiSource.Xml("MainWindow.xaml");
        Assert.Equal("RECENTS", GuiSource.AttributeValue(GuiSource.Require(document, "RecentsHeader"), "Text"));
        Assert.Contains(document.Descendants(), element => GuiSource.AttributeValue(element, "AutomationProperties.Name") == "Toggle CLI Session");
        var rendering = GuiSource.MethodBody(GuiSource.ViewText("MainWindow"), "RenderWorkspaceSidebar");
        Assert.True(rendering.IndexOf("\"WORKSPACES\"", StringComparison.Ordinal) < rendering.IndexOf("\"RECENTS\"", StringComparison.Ordinal));
        Assert.True(rendering.IndexOf("\"RECENTS\"", StringComparison.Ordinal) < rendering.IndexOf("\"CLI SESSION\"", StringComparison.Ordinal));
        var list = GuiSource.Require(document, "SidebarSessionList");
        Assert.Equal("True", GuiSource.AttributeValue(list, "CanDragItems"));
        Assert.Equal("OnSidebarDrop", GuiSource.AttributeValue(list, "Drop"));
        Assert.Contains(document.Descendants(), element => element.Name.LocalName == "MenuFlyoutSubItem"
            && GuiSource.AttributeValue(element, "Text") == "Move to");
        var move = GuiSource.MethodBody(GuiSource.ViewText("MainWindow"), "MoveSidebarSession");
        Assert.DoesNotContain("SetRequestedWorkspace", move, StringComparison.Ordinal);
        Assert.DoesNotContain("WorkspacePath =", move, StringComparison.Ordinal);
    }

    [Fact]
    public void Preserved_row_notifies_title_clock_and_working_directory_changes()
    {
        var row = new SessionInfo { Id = "draft", Title = "New Session" };
        var observable = Assert.IsAssignableFrom<System.ComponentModel.INotifyPropertyChanged>(row);
        var changed = new List<string?>();
        observable.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        row.Title = "First prompt";
        row.UpdatedAt = "2026-10-11T12:00:00Z";
        row.WorkspacePath = @"C:\project";
        Assert.Contains(nameof(SessionInfo.Title), changed);
        Assert.Contains(nameof(SessionInfo.DisplayTimeText), changed);
        Assert.Contains(nameof(SessionInfo.TooltipText), changed);
        changed.Clear();
        row.Title = "First prompt";
        Assert.Empty(changed);
    }

    [Fact]
    public void Section_spacing_belongs_to_non_hovering_containers()
    {
        var document = GuiSource.Xml("MainWindow.xaml");
        var list = GuiSource.Require(document, "SidebarSessionList");
        Assert.Equal("{StaticResource SidebarContainerSelector}", GuiSource.AttributeValue(list, "ItemContainerStyleSelector"));
        Assert.DoesNotContain(list.Elements(), element => element.Name.LocalName == "ListView.ItemContainerStyle");
        var heading = document.Descendants().First(element => element.Attribute(GuiSource.Xaml + "Key")?.Value == "SidebarHeadingContainerStyle");
        Assert.DoesNotContain(heading.Descendants(), element => element.Name.LocalName is "ListViewItemPresenter" or "VisualState");
        var recents = document.Descendants().First(element => element.Attribute(GuiSource.Xaml + "Key")?.Value == "SidebarRecentsContainerStyle");
        Assert.Contains(recents.Elements(), element => GuiSource.AttributeValue(element, "Property") == "Margin"
            && GuiSource.AttributeValue(element, "Value") == "0,36,0,6");
        var template = document.Descendants().First(element => element.Attribute(GuiSource.Xaml + "Key")?.Value == "SidebarRecentsTemplate");
        Assert.Equal("6,0", GuiSource.AttributeValue(template.Elements().Single(), "Margin"));
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
}
