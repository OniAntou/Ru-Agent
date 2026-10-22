using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Input;
using RuApp.Services;
using Windows.System;

namespace RuApp.Controls
{
    public sealed class SidebarResizerControl : UserControl
    {
        private double _sidebarWidth = ResponsiveLayoutPolicy.DefaultSidebarWidth;
        public double SidebarWidth
        {
            get => _sidebarWidth;
            set
            {
                double previous = _sidebarWidth;
                _sidebarWidth = ResponsiveLayoutPolicy.ClampSidebarWidth(value, ResponsiveLayoutPolicy.GetForWidth(0));
                if (previous != _sidebarWidth)
                    FrameworkElementAutomationPeer.FromElement(this)?.RaisePropertyChangedEvent(
                        RangeValuePatternIdentifiers.ValueProperty, previous, _sidebarWidth);
            }
        }
        public event EventHandler<double>? WidthRequested;

        public SidebarResizerControl()
        {
            ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
        }

        protected override void OnKeyDown(KeyRoutedEventArgs e)
        {
            if (e.Handled) return;
            double? width = e.Key switch
            {
                VirtualKey.Left or VirtualKey.Up => SidebarWidth - 16,
                VirtualKey.Right or VirtualKey.Down => SidebarWidth + 16,
                VirtualKey.Home => ResponsiveLayoutPolicy.MinimumSidebarWidth,
                VirtualKey.End => ResponsiveLayoutPolicy.MaximumSidebarWidth,
                _ => null,
            };
            if (width is { } requested) { WidthRequested?.Invoke(this, requested); e.Handled = true; }
            else base.OnKeyDown(e);
        }

        protected override AutomationPeer OnCreateAutomationPeer() => new ResizePeer(this);

        private sealed class ResizePeer(SidebarResizerControl owner) : FrameworkElementAutomationPeer(owner), IRangeValueProvider
        {
            protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Thumb;
            protected override string GetClassNameCore() => nameof(SidebarResizerControl);
            protected override object GetPatternCore(PatternInterface patternInterface) =>
                patternInterface == PatternInterface.RangeValue ? this : base.GetPatternCore(patternInterface);
            public bool IsReadOnly => !owner.IsEnabled;
            public double Minimum => ResponsiveLayoutPolicy.MinimumSidebarWidth;
            public double Maximum => ResponsiveLayoutPolicy.MaximumSidebarWidth;
            public double SmallChange => 16;
            public double LargeChange => 40;
            public double Value => owner.SidebarWidth;
            public void SetValue(double value)
            {
                if (IsReadOnly) throw new InvalidOperationException("Sidebar resize is disabled.");
                if (!double.IsFinite(value) || value < Minimum || value > Maximum) throw new ArgumentOutOfRangeException(nameof(value));
                owner.WidthRequested?.Invoke(owner, value);
            }
        }
    }
}
