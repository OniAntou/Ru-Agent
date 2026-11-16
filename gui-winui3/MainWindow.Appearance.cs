using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using RuApp.Services;

namespace RuApp;

/// <summary>
/// Settings &gt; Appearance in the shell: the background layer, the user's color
/// overrides on top of the theme pack, and the sidebar side. Fonts and corner radii
/// are applied before this window exists (<see cref="AppearanceResources"/>).
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>The sidebar keeps some of its own tint over a background so the two areas stay distinct.</summary>
    private const double SidebarOpacityOverBackground = 0.55;

    /// <summary>The fills on the chat and docked side panel that follow the panel opacity over a background.</summary>
    private static readonly string[] BackdropSurfaceBrushKeys =
        ["RuBackdropSurfaceBrush", "RuBackdropInsetBrush", "RuBackdropCodeBrush"];

    private static AppearanceState Appearance => AppearanceState.Instance;

    private AppearanceSidebarSide _appliedSidebarSide = AppearanceSidebarSide.Left;
    private AppearancePreferences? _appliedAppearance;

    private void InitializeAppearance()
    {
        Appearance.Changed += OnAppearanceChanged;
        AppearanceBackdrop.MediaFailed += OnAppearanceBackgroundFailed;
        VisibilityChanged += (_, args) => AppearanceBackdrop.SetWindowVisible(args.Visible);
        // The startup theme write already used these colors.
        _appliedAppearance = Appearance.Current;
        ApplyAppearance();
    }

    private static RuThemePalettes.ThemePalette AppearancePaletteFor(UiTheme theme)
    {
        bool isDark = RuThemePalettes.IsDark(theme);
        return AppearancePalette.Build(RuThemePalettes.PaletteFor(theme), Appearance.Current.ColorsFor(isDark), isDark);
    }

    private void OnAppearanceChanged(object? sender, EventArgs e)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(ApplyAppearance);
            return;
        }

        ApplyAppearance();
    }

    private void ApplyAppearance()
    {
        var preferences = Appearance.Current;
        bool colorsChanged = _appliedAppearance is null
            || _appliedAppearance.LightColors != preferences.LightColors
            || _appliedAppearance.DarkColors != preferences.DarkColors;
        _appliedAppearance = preferences;

        // Colors live in the theme dictionaries, so a change re-runs the theme write
        // (instantly: the user is watching the swatch they just picked).
        if (colorsChanged && _hasAppliedTheme) ApplyThemeCore(CurrentTheme);

        AppearanceBackdrop.Apply(preferences, Appearance.BackgroundPath);
        bool showing = AppearanceBackdrop.IsShowing;
        SetBrushOpacity("RuBackdropCanvasBrush", showing ? 0 : 1);
        SetBrushOpacity("RuBackdropSidebarBrush", showing ? SidebarOpacityOverBackground : 1);
        double surfaceOpacity = showing ? preferences.PanelOpacity : 1;
        foreach (var key in BackdropSurfaceBrushKeys) SetBrushOpacity(key, surfaceOpacity);
        ApplySidebarSide(preferences.SidebarSide);
    }

    /// <summary>A file that no longer decodes falls back to no background rather than a blank window.</summary>
    private void OnAppearanceBackgroundFailed(object? sender, EventArgs e)
    {
        GuiDiagnosticWriter.AppendTraceLine("[Appearance] Background media failed to load; cleared.");
        Appearance.Update(current => current with
        {
            BackgroundKind = AppearanceBackgroundKind.None,
            BackgroundFile = null,
        });
    }

    private static void SetBrushOpacity(string key, double opacity)
    {
        if (Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush) brush.Opacity = opacity;
    }

    /// <summary>
    /// Moves the sidebar (and its resizer) to the other edge by reordering the shell
    /// columns. The rounded corners follow, so the square edge stays on the window edge.
    /// </summary>
    private void ApplySidebarSide(AppearanceSidebarSide side)
    {
        if (side == _appliedSidebarSide) return;
        _appliedSidebarSide = side;
        EndSidebarResize();
        bool right = side == AppearanceSidebarSide.Right;
        var columns = RootLayoutGrid.ColumnDefinitions;
        columns.Clear();
        foreach (var column in right
                     ? new[] { ContentColumn, SidebarDividerColumn, SidebarColumn }
                     : new[] { SidebarColumn, SidebarDividerColumn, ContentColumn })
        {
            columns.Add(column);
        }

        Microsoft.UI.Xaml.Controls.Grid.SetColumn(NavigationSidebar, right ? 2 : 0);
        Microsoft.UI.Xaml.Controls.Grid.SetColumn(ContentFrame, right ? 0 : 2);
        // Mirror the markup's asymmetric radius (square on the window edge) instead of
        // restating it, so the radius stays the one App.xaml/markup define.
        var radius = NavigationSidebar.CornerRadius;
        NavigationSidebar.CornerRadius = new CornerRadius(radius.TopRight, radius.TopLeft, radius.BottomLeft, radius.BottomRight);
        SidebarResizer.IsOnRight = right;
    }

    /// <summary>
    /// +1 when moving the sidebar edge right widens it (sidebar on the left), -1 when it
    /// narrows it; pointer drags and Ctrl+Left/Right both move the edge with the input.
    /// </summary>
    private double SidebarDragDirection => _appliedSidebarSide == AppearanceSidebarSide.Right ? -1 : 1;
}
