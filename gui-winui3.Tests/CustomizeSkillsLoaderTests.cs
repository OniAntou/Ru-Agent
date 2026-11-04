using RuAgent.Client;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class CustomizeSkillsLoaderTests
{
    [Fact]
    public async Task The_first_open_without_a_chat_workspace_loads_user_skills_instead_of_an_empty_page()
    {
        using var loader = new CustomizeSkillsLoader(action => action());
        var response = new TaskCompletionSource<SkillCatalogResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        string? requestedWorkspace = null;
        loader.Attach((workspace, _, _, _) => { requestedWorkspace = workspace; return response.Task; }, () => null);
        loader.SetActive(true);
        Assert.Equal(string.Empty, requestedWorkspace);
        Assert.True(loader.State.IsLoading);
        var load = loader.LoadAsync();
        response.SetResult(Result("user-skill"));
        await load;
        Assert.Equal("user-skill", Assert.Single(loader.State.Items).Id);
        Assert.False(loader.State.IsLoading);
        Assert.Equal(string.Empty, loader.State.Workspace);
    }

    [Fact]
    public async Task An_open_page_loads_when_its_backend_becomes_ready_without_reopening()
    {
        using var loader = new CustomizeSkillsLoader(action => action());
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        loader.StateChanged += (_, state) => { if (state.Items.Count > 0) ready.TrySetResult(); };
        loader.SetActive(true);
        loader.Attach((_, _, _, _) => Task.FromResult(Result("ready")), () => @"D:\project");
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("ready", Assert.Single(loader.State.Items).Id);
    }

    [Fact]
    public async Task Delayed_first_load_publishes_data_while_the_page_stays_open()
    {
        using var loader = new CustomizeSkillsLoader(action => action());
        var response = new TaskCompletionSource<SkillCatalogResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        loader.Attach((_, _, _, _) => response.Task, () => @"D:\project");
        loader.SetActive(true);
        Assert.True(loader.State.IsLoading);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        loader.StateChanged += (_, state) => { if (!state.IsLoading) done.TrySetResult(); };
        response.SetResult(Result("first"));
        await done.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("first", Assert.Single(loader.State.Items).Id);
        Assert.False(loader.State.IsLoading);
    }

    [Fact]
    public async Task Hidden_or_superseded_responses_cannot_replace_the_current_page()
    {
        var queue = new List<Action>();
        using var loader = new CustomizeSkillsLoader(queue.Add);
        loader.Attach((_, _, _, _) => Task.FromResult(Result("old")), () => @"D:\project");
        loader.SetActive(true);
        loader.SetActive(false);
        loader.Attach((_, _, _, _) => Task.FromResult(Result("new")), () => @"D:\project");
        loader.SetActive(true);
        foreach (var apply in queue) apply();
        Assert.Equal("new", Assert.Single(loader.State.Items).Id);
        await loader.LoadAsync();
    }

    [Fact]
    public async Task Rebinding_an_open_page_replaces_old_data_and_rejects_the_old_completion()
    {
        using var loader = new CustomizeSkillsLoader(action => action());
        var old = new TaskCompletionSource<SkillCatalogResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        loader.Attach((_, _, _, _) => old.Task, () => @"D:\project");
        loader.SetActive(true);
        var oldLoad = loader.LoadAsync();
        loader.Attach((_, _, _, _) => Task.FromResult(Result("reconnected")), () => @"D:\project");
        Assert.Equal("reconnected", Assert.Single(loader.State.Items).Id);
        old.SetResult(Result("stale"));
        await oldLoad;
        Assert.Equal("reconnected", Assert.Single(loader.State.Items).Id);
    }

    [Fact]
    public void A_workspace_that_becomes_ready_while_open_refreshes_without_navigation()
    {
        var workspace = string.Empty;
        using var loader = new CustomizeSkillsLoader(action => action());
        var reads = 0;
        loader.Attach((_, _, _, _) => { reads++; return Task.FromResult(Result("workspace-ready")); }, () => workspace);
        loader.SetActive(true);
        Assert.Equal(1, reads); // User catalog is available before a workspace.
        workspace = @"D:\project";
        loader.RefreshWorkspace();
        Assert.Equal(2, reads);
        Assert.Equal("workspace-ready", Assert.Single(loader.State.Items).Id);
        loader.RefreshWorkspace();
        Assert.Equal(2, reads);
    }

    [Fact]
    public async Task Late_user_catalog_cannot_replace_the_workspace_catalog_after_the_workspace_becomes_ready()
    {
        using var loader = new CustomizeSkillsLoader(action => action());
        var user = new TaskCompletionSource<SkillCatalogResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var workspace = string.Empty;
        loader.Attach((path, _, _, _) => path.Length == 0 ? user.Task : Task.FromResult(Result("project")), () => workspace);
        loader.SetActive(true);
        var oldLoad = loader.LoadAsync();
        workspace = @"D:\project";
        loader.RefreshWorkspace();
        Assert.Equal("project", Assert.Single(loader.State.Items).Id);
        user.SetResult(Result("user"));
        await oldLoad;
        Assert.Equal("project", Assert.Single(loader.State.Items).Id);
        Assert.Equal(workspace, loader.State.Workspace);
    }

    [Fact]
    public async Task A_failed_refresh_keeps_visible_data_and_an_explicit_retry_can_recover()
    {
        using var loader = new CustomizeSkillsLoader(action => action());
        var fail = false;
        var forced = false;
        loader.Attach((_, _, refresh, _) =>
        {
            forced = refresh;
            return fail ? Task.FromException<SkillCatalogResponse>(new IOException("offline")) : Task.FromResult(Result("cached"));
        }, () => @"D:\project");
        loader.SetActive(true);
        fail = true;
        await loader.LoadAsync();
        Assert.Equal("cached", Assert.Single(loader.State.Items).Id);
        Assert.False(loader.State.IsLoading);
        Assert.StartsWith("Could not load skills", loader.State.Status);
        fail = false;
        await loader.LoadAsync(refresh: true);
        Assert.True(forced);
        Assert.Null(loader.State.Status);
    }

    private static SkillCatalogResponse Result(string id) => new(
        [new(id, id, "description", "SKILL.md", @"D:\skills", "revision", true, true, true, [], null)], [], false, 1);
}
