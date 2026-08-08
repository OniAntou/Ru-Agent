using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using RuApp.Models;
using RuApp.Services;
using WinRT.Interop;

namespace RuApp.Views;

public sealed partial class SubtitleOverlayWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private static readonly IntPtr WS_EX_LAYERED = new IntPtr(0x00080000);
    private static readonly IntPtr WS_EX_TRANSPARENT = new IntPtr(0x00000020);
    private static readonly IntPtr WS_EX_NOACTIVATE = new IntPtr(0x08000000);
    private static readonly IntPtr WS_EX_TOOLWINDOW = new IntPtr(0x00000080);

    private static readonly SolidColorBrush SpokenBrush = Argb(0x66, 0xFF, 0xFF, 0xFF);
    private static readonly SolidColorBrush CurrentBrush = Argb(0xFF, 0xFF, 0xD5, 0x4F);
    private static readonly SolidColorBrush UpcomingBrush = Argb(0x99, 0xFF, 0xFF, 0xFF);

    private readonly AppWindow _appWindow;
    private Storyboard? _fadeStoryboard;
    private Action? _fadeCompleted;
    private bool _shown;

    public SubtitleOverlayWindow()
    {
        InitializeComponent();
        Title = "Ru Subtitle Overlay";

        ApplyClickThroughStyle(WindowNative.GetWindowHandle(this));
        _appWindow = AppWindow;

        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsMinimizable = false;
            presenter.IsMaximizable = false;
            presenter.IsAlwaysOnTop = true;
        }
        _appWindow.IsShownInSwitchers = false;
    }

    public void ShowOverlay()
    {
        if (_shown) return;
        _shown = true;
        _appWindow.Show();
    }

    public void HideOverlay()
    {
        if (!_shown) return;
        _shown = false;
        _appWindow.Hide();
    }

    public void MoveAndResize(RectPx rect, int fontSize)
    {
        _appWindow.MoveAndResize(new Windows.Graphics.RectInt32(rect.X, rect.Y, rect.Width, rect.Height));
        SubtitleText.FontSize = fontSize;
    }

    public void ApplySnapshot(SubtitleSnapshot snapshot)
    {
        SubtitleText.Inlines.Clear();
        if (string.IsNullOrEmpty(snapshot.VisibleText) || string.IsNullOrEmpty(snapshot.HighlightToken))
        {
            return;
        }

        string[] tokens = snapshot.VisibleText.Split(' ');
        int highlightIndex = Array.IndexOf(tokens, snapshot.HighlightToken);
        for (int i = 0; i < tokens.Length; i++)
        {
            SolidColorBrush brush = i < highlightIndex ? SpokenBrush : i == highlightIndex ? CurrentBrush : UpcomingBrush;
            SubtitleText.Inlines.Add(new Run { Text = tokens[i], Foreground = brush });
            if (i < tokens.Length - 1)
            {
                SubtitleText.Inlines.Add(new Run { Text = " " });
            }
        }
    }

    public void BeginFadeOut(Action onCompleted)
    {
        CancelFadeOut();
        _fadeCompleted = onCompleted;

        var animation = new DoubleAnimation
        {
            From = OverlayRoot.Opacity,
            To = 0,
            Duration = TimeSpan.FromSeconds(2),
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(animation, OverlayRoot);
        Storyboard.SetTargetProperty(animation, "Opacity");
        _fadeStoryboard = new Storyboard();
        _fadeStoryboard.Children.Add(animation);
        _fadeStoryboard.Completed += OnFadeCompleted;
        _fadeStoryboard.Begin();
    }

    public void CancelFadeOut()
    {
        if (_fadeStoryboard == null) return;
        _fadeStoryboard.Stop();
        _fadeStoryboard = null;
        _fadeCompleted = null;
        OverlayRoot.Opacity = 1;
    }

    private void OnFadeCompleted(object? sender, object e)
    {
        _fadeStoryboard = null;
        HideOverlay();
        Action? completed = _fadeCompleted;
        _fadeCompleted = null;
        completed?.Invoke();
    }

    private static void ApplyClickThroughStyle(IntPtr hwnd)
    {
        IntPtr style = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        style |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, style);
    }

    private static SolidColorBrush Argb(byte a, byte r, byte g, byte b)
    {
        return new SolidColorBrush { Color = Windows.UI.Color.FromArgb(a, r, g, b) };
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
}
