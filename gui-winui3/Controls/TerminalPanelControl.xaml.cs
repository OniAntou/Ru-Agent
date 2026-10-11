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
        /// Drops the text streamed for one execution's superseded attempt. A
        /// reset for an execution with no block yet settles nothing: the retry's
        /// output opens the block.
        /// </summary>
        public void ResetOutput(string executionId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(executionId);

            TerminalBlocks.FirstOrDefault(candidate =>
                    string.Equals(candidate.ExecutionId, executionId, StringComparison.Ordinal))
                ?.ResetText();
        }

        /// <summary>
        /// Completes the block opened for this execution. Matching is by execution
        /// id (see <see cref="TerminalBlockListPolicy.SelectCompletionTarget"/>):
        /// a known id that owns no block settles nothing, so one command's outcome
        /// can never land on another command's block.
        /// </summary>
        public void CompleteBlock(string? executionId, bool failed)
        {
            TerminalBlockListPolicy.SelectCompletionTarget(TerminalBlocks, executionId)?.Complete(failed);
        }

        /// <summary>
        /// Settles every block still open. A block that never recorded its
        /// completion cannot keep claiming "running" after the run has ended, so
        /// it settles as failed: it did not finish in front of the reader.
        /// </summary>
        public void SettleRunningBlocks()
        {
            foreach (var block in TerminalBlocks.Where(candidate => !candidate.IsCompleted).ToList())
            {
                block.Complete(failed: true);
            }
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
