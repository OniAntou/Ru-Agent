using System;
using System.Collections.ObjectModel;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RuApp.Models;
using RuApp.Services;

namespace RuApp.Controls
{
    /// <summary>
    /// The streamed terminal output panel above the composer: one block per execution,
    /// bounded by <see cref="TerminalBlockListPolicy"/>, with clear and show/hide
    /// controls.
    ///
    /// Extracted from ChatView so the block list, its bound, and the collapse state
    /// live with the surface that owns them.
    /// </summary>
    public sealed partial class TerminalPanelControl : UserControl
    {
        public ObservableCollection<TerminalOutputBlock> TerminalBlocks { get; } = new();

        /// <summary>The tool label carried onto the next block that opens.</summary>
        public string? NextBlockLabel { get; private set; }

        /// <summary>True while the output body is hidden; the header stays visible.</summary>
        public bool IsBodyCollapsed { get; private set; }

        public TerminalPanelControl()
        {
            InitializeComponent();
        }

        public bool IsPanelVisible => Panel.Visibility == Visibility.Visible;

        /// <summary>
        /// Records the tool behind the next block. Called when a terminal call starts,
        /// before any output arrives.
        /// </summary>
        public void NoteToolLabel(string? toolLabel)
        {
            NextBlockLabel = toolLabel;
        }

        /// <summary>
        /// Appends one streamed chunk to its execution's block, opening the block and
        /// evicting the oldest ones as needed.
        /// </summary>
        public void AppendOutput(string executionId, string text)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
            ArgumentNullException.ThrowIfNull(text);

            var block = TerminalBlocks.FirstOrDefault(candidate =>
                string.Equals(candidate.ExecutionId, executionId, StringComparison.Ordinal));
            if (block is null)
            {
                block = new TerminalOutputBlock(
                    executionId,
                    TerminalBlockListPolicy.NormalizeLabel(NextBlockLabel));
                TerminalBlocks.Add(block);

                // Add first, then trim, so the list never exceeds the cap by more than
                // the block just added.
                var evictions = TerminalBlockListPolicy.EvictionCount(TerminalBlocks.Count);
                for (var index = 0; index < evictions; index++)
                {
                    TerminalBlocks.RemoveAt(0);
                }
            }

            block.AppendText(text);

            // Showing the output must not undo a user who deliberately hid the body.
            if (!IsBodyCollapsed)
            {
                TerminalScrollHost.Visibility = Visibility.Visible;
                TerminalScrollHost.ChangeView(null, double.MaxValue, null);
            }

            Panel.Visibility = Visibility.Visible;
        }

        /// <summary>
        /// Completes the block for this outcome. The newest unfinished block carrying
        /// the same label wins; otherwise the newest unfinished block.
        /// </summary>
        public void CompleteBlock(string label, bool failed)
        {
            ArgumentNullException.ThrowIfNull(label);

            TerminalOutputBlock? block = null;
            foreach (var candidate in TerminalBlocks)
            {
                if (!candidate.IsCompleted
                    && string.Equals(candidate.Title, label, StringComparison.Ordinal))
                {
                    block = candidate;
                }
            }

            block ??= TerminalBlocks.LastOrDefault(candidate => !candidate.IsCompleted);
            block?.Complete(failed);
        }

        /// <summary>Shows the panel and pins the scroll to the newest output.</summary>
        public void Show()
        {
            IsBodyCollapsed = false;
            TerminalScrollHost.Visibility = Visibility.Visible;
            CollapseButton.Content = "Hide";
            Panel.Visibility = Visibility.Visible;
            TerminalScrollHost.ChangeView(null, double.MaxValue, null);
        }

        /// <summary>Hides the output body; the header and its controls stay reachable.</summary>
        public void ToggleBody()
        {
            IsBodyCollapsed = !IsBodyCollapsed;
            TerminalScrollHost.Visibility = IsBodyCollapsed ? Visibility.Collapsed : Visibility.Visible;
            CollapseButton.Content = IsBodyCollapsed ? "Show" : "Hide";
        }

        public void Clear()
        {
            TerminalBlocks.Clear();
            NextBlockLabel = null;
            Panel.Visibility = Visibility.Collapsed;
        }

        private void OnTerminalClearClick(object sender, RoutedEventArgs e) => Clear();

        private void OnTerminalCollapseClick(object sender, RoutedEventArgs e) => ToggleBody();
    }
}
