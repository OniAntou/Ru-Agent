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
            Assert.Single(WorkspaceSessionProjection.Project([session],
                row => SidebarSessionOrganization.GroupFor(row, @"D:\LLM", preferences)).Groups);
        }
        Assert.False(SidebarSessionOrganization.CanMoveTo(new() { ClientSource = "gui" }, SidebarSessionOrganization.CliKey));
    }

    [Fact]
    public void Scratch_sessions_stay_in_recents_regardless_of_their_private_session_folder()
    {
        var preferences = new SidebarOrganizationPreferences();
        var first = new SessionInfo
        {
            Id = "a",
            WorkspacePath = @"C:\Users\USER\AppData\Local\Ru\workspaces\aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            WorkspaceKind = "scratch",
        };
        var second = new SessionInfo
        {
            Id = "b",
            WorkspacePath = @"C:\Users\USER\AppData\Local\Ru\workspaces\bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            WorkspaceKind = "scratch",
        };

        var group = SidebarSessionOrganization.GroupFor(first, appRoot: null, preferences);
        Assert.Equal(SidebarSessionOrganization.RecentsKey, group.Key);
        Assert.Equal("Recents", group.Label);
        Assert.Null(group.Path);
        Assert.Equal(group.Key, SidebarSessionOrganization.GroupFor(second, appRoot: null, preferences).Key);

        var projection = WorkspaceSessionProjection.Project(
            [first, second],
            row => SidebarSessionOrganization.GroupFor(row, appRoot: null, preferences));
        var only = Assert.Single(projection.Groups);
        Assert.Equal(2, only.Sessions.Count);
    }

    [Fact]
    public void Explicit_user_placement_can_group_a_scratch_backed_chat_with_a_project()
    {
        var preferences = new SidebarOrganizationPreferences();
        var session = new SessionInfo { Id = "chat", WorkspaceKind = "scratch",
            WorkspacePath = @"C:\RuData\workspaces\chat" };
        preferences.Groups[session.Id] = @"C:\selected-project";

        var group = SidebarSessionOrganization.GroupFor(session, appRoot: null, preferences);

        Assert.Equal(SidebarSessionOrganization.PathKey(@"C:\selected-project"), group.Key);
        Assert.Equal(@"C:\selected-project", group.Path);
        Assert.Equal(@"C:\RuData\workspaces\chat", session.WorkspacePath);
        preferences.Groups.Remove(session.Id);
        Assert.Equal(SidebarSessionOrganization.RecentsKey,
            SidebarSessionOrganization.GroupFor(session, appRoot: null, preferences).Key);
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
        var projection = WorkspaceSessionProjection.Project([session], row => SidebarSessionOrganization.GroupFor(row, @"D:\LLM", reopened.Current));
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
    public void Sidebar_presenter_persists_placement_without_changing_execution_workspace()
    {
        var session = new SessionInfo { Id = "session", WorkspacePath = @"C:\execution", MessageCount = 1 };
        var store = new SidebarOrganizationStore(Path.Combine(FilePath, "presenter.json"));

        Assert.True(SidebarSessionPresenter.TryMove(
            store,
            [session],
            store.Current,
            session.Id,
            SidebarSessionOrganization.RecentsKey,
            path: null,
            beforeId: null));

        Assert.Null(store.Current.Groups[session.Id]);
        Assert.Equal(new[] { session.Id }, store.Current.Order);
        Assert.Equal(@"C:\execution", session.WorkspacePath);
    }

    [Fact]
    public void Sidebar_has_one_drag_surface_and_a_keyboard_move_menu()
    {
        var document = GuiSource.Xml("MainWindow.xaml");
        Assert.Equal("RECENTS", GuiSource.AttributeValue(GuiSource.Require(document, "RecentsHeader"), "Text"));
        Assert.Contains(document.Descendants(), element => GuiSource.AttributeValue(element, "AutomationProperties.Name") == "Toggle CLI Session");
        var list = GuiSource.Require(document, "SidebarSessionList");
        Assert.Equal("True", GuiSource.AttributeValue(list, "CanDragItems"));
        Assert.Equal("OnSidebarDrop", GuiSource.AttributeValue(list, "Drop"));
        Assert.Contains(document.Descendants(), element => element.Name.LocalName == "MenuFlyoutSubItem"
            && GuiSource.AttributeValue(element, "Text") == "Move to");
    }

    [Fact]
    public void Session_list_reserves_a_gutter_so_the_overlay_scrollbar_never_covers_rows()
    {
        // WinUI scrollbars overlay content; with many sessions the expanded bar hid the
        // section chevrons, the "+" buttons and the row clocks at the right edge.
        var list = GuiSource.Require(GuiSource.Xml("MainWindow.xaml"), "SidebarSessionList");
        var padding = GuiSource.AttributeValue(list, "Padding")!.Split(',').Select(int.Parse).ToArray();
        Assert.Equal(4, padding.Length);
        Assert.True(padding[2] >= 12, "the right padding must leave room for the expanded scrollbar");
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
