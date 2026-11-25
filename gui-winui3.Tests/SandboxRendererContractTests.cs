using System;
using System.IO;
using Xunit;

namespace RuApp.Tests;

/// <summary>
/// The picture renderer (ADR 0057) ships its page and libraries, reaches no network,
/// and keeps its web view locked down.
/// </summary>
public sealed class SandboxRendererContractTests
{
    [Fact]
    public void The_renderer_ships_its_vendored_assets()
    {
        var root = GuiSource.Path("Assets", "render");
        foreach (var name in new[]
                 {
                     "render.html", "render.js", "render-config.js", "mermaid.min.js", "tex-svg.js",
                     "LICENSE-mermaid.txt", "LICENSE-mathjax.txt",
                 })
        {
            Assert.True(File.Exists(Path.Combine(root, name)), name + " is missing");
        }

        Assert.True(new FileInfo(Path.Combine(root, "mermaid.min.js")).Length > 1_000_000, "mermaid.min.js looks truncated");
        Assert.True(new FileInfo(Path.Combine(root, "tex-svg.js")).Length > 1_000_000, "tex-svg.js looks truncated");
        Assert.Contains("Assets\\render\\**", GuiSource.Text("RuApp.csproj"), StringComparison.Ordinal);
    }

    [Fact]
    public void The_renderer_page_reaches_no_network()
    {
        var root = GuiSource.Path("Assets", "render");
        var html = File.ReadAllText(Path.Combine(root, "render.html"));
        Assert.Contains("default-src 'none'", html, StringComparison.Ordinal);
        Assert.Contains("script-src 'self'", html, StringComparison.Ordinal);
        Assert.Contains("connect-src 'none'", html, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-eval", html, StringComparison.Ordinal);
        Assert.DoesNotContain("http://", html, StringComparison.Ordinal);
        Assert.DoesNotContain("https://", html, StringComparison.Ordinal);

        var script = File.ReadAllText(Path.Combine(root, "render.js"));
        Assert.Contains("securityLevel: 'strict'", script, StringComparison.Ordinal);
        Assert.Contains("htmlLabels: false", script, StringComparison.Ordinal);
        // The drawn SVG reaches the canvas as an image, where it can run no script.
        Assert.Contains("new Image()", script, StringComparison.Ordinal);
        Assert.DoesNotContain("innerHTML", script, StringComparison.Ordinal);
        Assert.DoesNotContain("fetch(", script, StringComparison.Ordinal);

        var config = File.ReadAllText(Path.Combine(root, "render-config.js"));
        Assert.Contains("'require', 'autoload'", config, StringComparison.Ordinal);
    }

    [Fact]
    public void The_renderer_web_view_is_locked_down()
    {
        var code = GuiSource.Text("Services", "SandboxRenderer.cs");
        foreach (var setting in new[]
                 {
                     "AreDevToolsEnabled = false", "AreDefaultContextMenusEnabled = false", "AreHostObjectsAllowed = false",
                     "AreDefaultScriptDialogsEnabled = false", "IsGeneralAutofillEnabled = false", "IsPasswordAutosaveEnabled = false",
                     "controller.IsVisible = false", "CoreWebView2HostResourceAccessKind.DenyCors",
                     "AddWebResourceRequestedFilter(\"*\", CoreWebView2WebResourceContext.All)",
                     "SandboxRenderPolicy.IsRendererResource(args.Request.Uri)",
                     "SandboxRenderPolicy.IsRendererPage(args.Uri)",
                     "SandboxRenderPolicy.IsRendererPage(args.Source)",
                     "NewWindowRequested += (_, args) => args.Handled = true",
                     "DownloadStarting += (_, args) => args.Cancel = true",
                     "FrameNavigationStarting += (_, args) => args.Cancel = true",
                     "CoreWebView2PermissionState.Deny",
                     "SandboxRenderPolicy.ReadResult(json, id)",
                 })
        {
            Assert.Contains(setting, code, StringComparison.Ordinal);
        }

        // Presentation only: no host object, script injection or Broker path.
        Assert.DoesNotContain("AddHostObjectToScript", code, StringComparison.Ordinal);
        Assert.DoesNotContain("ExecuteScriptAsync", code, StringComparison.Ordinal);
        Assert.DoesNotContain("AddScriptToExecuteOnDocumentCreatedAsync", code, StringComparison.Ordinal);
        Assert.DoesNotContain("ToolGateway", code, StringComparison.Ordinal);
    }

    [Fact]
    public void The_app_closes_the_renderer_on_shutdown() =>
        Assert.Contains("SandboxRenderer.Shared.Shutdown()", GuiSource.Text("Views", "ChatView.SessionHistory.cs"), StringComparison.Ordinal);
}
