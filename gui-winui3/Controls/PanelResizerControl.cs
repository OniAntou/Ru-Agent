using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using RuApp.Services;
using Windows.System;

namespace RuApp.Controls;

/// <summary>Focusable splitter with the same width contract for pointer, keyboard and assistive technology.</summary>
public sealed class PanelResizerControl : Control
{
    private double _panelWidth = AgentSidePanelPolicy.DefaultWidth;
    public double PanelWidth
    {
        get => _panelWidth;
        set
        {
            value = AgentSidePanelPolicy.ClampWidth(value);
            double previous = _panelWidth;
            if (previous == value) return;
            _panelWidth = value;
            FrameworkElementAutomationPeer.FromElement(this)?.RaisePropertyChangedEvent(
                RangeValuePatternIdentifiers.ValueProperty, previous, value);
        }
    }
    public event EventHandler<double>? WidthRequested;

    public PanelResizerControl() => ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);

    protected override void OnKeyDown(KeyRoutedEventArgs e)
    {
        double? width = e.Key switch
        {
            VirtualKey.Left or VirtualKey.Up => PanelWidth + 16,
            VirtualKey.Right or VirtualKey.Down => PanelWidth - 16,
            VirtualKey.Home => AgentSidePanelPolicy.MinimumWidth,
            VirtualKey.End => AgentSidePanelPolicy.MaximumWidth,
            _ => null,
        };
        if (width is { } value) { RequestWidth(value); e.Handled = true; }
        else base.OnKeyDown(e);
    }

    private void RequestWidth(double value) => WidthRequested?.Invoke(this, AgentSidePanelPolicy.ClampWidth(value));
    protected override AutomationPeer OnCreateAutomationPeer() => new ResizePeer(this);

    private sealed class ResizePeer(PanelResizerControl owner) : FrameworkElementAutomationPeer(owner), IRangeValueProvider
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Thumb;
        protected override string GetClassNameCore() => nameof(PanelResizerControl);
        protected override object GetPatternCore(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.RangeValue ? this : base.GetPatternCore(patternInterface);
        public bool IsReadOnly => !owner.IsEnabled;
        public double LargeChange => 64;
        public double SmallChange => 16;
        public double Maximum => AgentSidePanelPolicy.MaximumWidth;
        public double Minimum => AgentSidePanelPolicy.MinimumWidth;
        public double Value => owner.PanelWidth;
        public void SetValue(double value)
        {
            if (IsReadOnly) throw new InvalidOperationException("Panel resize is disabled.");
            if (!double.IsFinite(value) || value < Minimum || value > Maximum) throw new ArgumentOutOfRangeException(nameof(value));
            owner.RequestWidth(value);
        }
    }
}
