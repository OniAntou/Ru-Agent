using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using RuApp.Models;
using RuApp.Services;

namespace RuApp.Controls
{
    /// <summary>
    /// The right side surface's hosts: the floating summary card and the
    /// full-height details panel. It renders what it is told and raises what the
    /// user did; which host shows and which tabs are open is decided by
    /// <see cref="AgentSidePanelPolicy"/> in the view.
    /// </summary>
    public sealed partial class AgentSidePanelControl : UserControl
    {
        public ObservableCollection<AgentTaskRow> Tasks { get; } = new();
        public ObservableCollection<AgentTaskRow> CardTasks { get; } = new();

        public ObservableCollection<ChangesTabRow> Changes { get; } = new();

        public event EventHandler? ExpandRequested;

        public event EventHandler<SidePanelTabKind>? TabOpenRequested;

        public event EventHandler? CollapseRequested;

        public event EventHandler? FullscreenToggleRequested;

        public event EventHandler<SidePanelTabKind>? TabActivated;

        public event EventHandler<SidePanelTabKind>? TabClosed;

        public event EventHandler<SidePanelTabMove>? TabMoved;

        public event EventHandler<string>? TaskStopRequested;
        public event EventHandler? TasksRefreshRequested;

        public event EventHandler<double>? WidthChanged;

        public event EventHandler<double>? WidthCommitted;

        private AgentSidePanelMode _mode = AgentSidePanelMode.Hidden;

        /// <summary>
        /// The mode the surface was last rendered with. The policy can already read
        /// Hidden while the fade-out is still on screen, so the host's view logic
        /// needs the last painted mode to keep the reserved space stable.
        /// </summary>
        public AgentSidePanelMode CurrentMode => _mode;
        private IReadOnlyList<SidePanelTabKind> _openTabs = Array.Empty<SidePanelTabKind>();
        private SidePanelTabKind _activeTab = SidePanelTabKind.Changes;
        private bool _changesChipHovered;
        private bool _tasksChipHovered;
        private bool _filesChipHovered;
        private bool _terminalChipHovered;
        private SidePanelTabKind? _draggingTab;
        private Border? _draggingChip;
        private double _dragStartX;
        private bool _isDragActive;

        public AgentSidePanelControl()
        {
            InitializeComponent();
            InitializeSubagents();
            InitializeTabInteractions();
            PanelResizeGrip.WidthRequested += OnPanelWidthRequested;
            Unloaded += (_, _) =>
            {
                EndPanelResize();
                CancelVisibilityAnimations();
                CancelBodyTransition();
            };
        }

        public void SetSessionTitle(string title)
        {
            CardTitleText.Text = string.IsNullOrWhiteSpace(title) ? "Session" : title;
            ToolTipService.SetToolTip(CardTitleText, CardTitleText.Text);
        }

        public void SetTasksStatus(string status)
        {
            CardTasksStatusText.Text = status;
            PanelTasksStatusText.Text = status;
        }

        public void RenderChanges(IReadOnlyList<ChangesTabRow> rows, int added, int removed)
        {
            ChangesTabReconciler.Apply(Changes, rows);
            CardChangesLabel.Text = SidePanelSummary.ChangesLabel(rows);
            CardChangeDeltas.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            CardAddedText.Text = "+" + added;
            CardRemovedText.Text = "−" + removed;
            PanelAddedText.Text = "+" + added;
            PanelRemovedText.Text = "−" + removed;
            ChangesEmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ChangesList.Visibility = rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        public void RenderTasks(IReadOnlyList<AgentTaskRow> rows, string status)
        {
            SetTasksStatus(status);
            if (Tasks.Count == rows.Count && Tasks.Zip(rows).All(pair => pair.First.HasSameObservation(pair.Second))) return;
            Tasks.Clear();
            foreach (var row in rows)
            {
                row.IsStopping = _stoppingTaskIds.Contains(row.ExecutionId);
                Tasks.Add(row);
            }
            CardTasks.Clear();
            foreach (var row in SidePanelSummary.PreviewTasks(rows)) CardTasks.Add(row);
            int remaining = rows.Count - CardTasks.Count;
            CardMoreTasksButton.Content = $"View all {rows.Count} tasks";
            CardMoreTasksButton.Visibility = remaining > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        public void ApplyState(
            AgentSidePanelMode mode,
            IReadOnlyList<SidePanelTabKind> openTabs,
            SidePanelTabKind activeTab,
            bool fullscreen,
            double panelWidth)
        {
            CancelVisibilityExit();
            var previous = _mode;
            var previousActive = _activeTab;
            _mode = mode;
            _openTabs = openTabs;
            _activeTab = activeTab;

            bool showCard = mode == AgentSidePanelMode.Card;
            bool showPanel = mode == AgentSidePanelMode.Panel;
            if (!showPanel) CancelBodyTransition();
            if (!showPanel || fullscreen) EndPanelResize();
            CardHost.Visibility = showCard ? Visibility.Visible : Visibility.Collapsed;
            PanelHost.Visibility = showPanel ? Visibility.Visible : Visibility.Collapsed;

            RefreshChip(ChangesChip, ChangesTabLabel, SidePanelTabKind.Changes);
            RefreshChip(TasksChip, TasksTabLabel, SidePanelTabKind.Tasks);
            RefreshChip(SubagentsChip, SubagentsTabLabel, SidePanelTabKind.Subagents);
            RefreshChip(FilesChip, FilesTabLabel, SidePanelTabKind.Files);
            RefreshChip(TerminalChip, TerminalTabLabel, SidePanelTabKind.Terminal);
            RefreshChip(PreviewChip, PreviewTabLabel, SidePanelTabKind.Preview);
            AddTabButton.Visibility = Visibility.Visible;
            SyncChipOrder(openTabs);
            if (showPanel && (activeTab != previousActive || previous != mode))
            {
                QueueActiveTabReveal();
            }

            ChangesBody.Visibility = showPanel && activeTab == SidePanelTabKind.Changes ? Visibility.Visible : Visibility.Collapsed;
            SubagentBrowser.Visibility = showPanel && activeTab == SidePanelTabKind.Subagents ? Visibility.Visible : Visibility.Collapsed;
            TasksBody.Visibility = showPanel && activeTab == SidePanelTabKind.Tasks ? Visibility.Visible : Visibility.Collapsed;
            FilesBody.Visibility = showPanel && activeTab == SidePanelTabKind.Files ? Visibility.Visible : Visibility.Collapsed;
            TerminalBody.Visibility = showPanel && activeTab == SidePanelTabKind.Terminal ? Visibility.Visible : Visibility.Collapsed;
            FilePreview.Visibility = showPanel && activeTab == SidePanelTabKind.Preview ? Visibility.Visible : Visibility.Collapsed;
            SetPreviewVisibility(Visibility == Visibility.Visible);

            if (showPanel && (activeTab != previousActive || previous != AgentSidePanelMode.Panel))
            {
                PlayBodyTransition(activeTab switch
                {
                    SidePanelTabKind.Changes => (FrameworkElement)ChangesBody,
                    SidePanelTabKind.Tasks => TasksBody,
                    SidePanelTabKind.Subagents => SubagentBrowser,
                    SidePanelTabKind.Files => FilesBody,
                    SidePanelTabKind.Preview => FilePreview,
                    _ => TerminalBody,
                });

                if (activeTab == SidePanelTabKind.Terminal)
                {
                    RevealTerminal();
                }
            }
            else if (showPanel && activeTab == SidePanelTabKind.Terminal)
            {
                SynchronizeTerminalWorkspace();
            }

            bool push = showPanel && !fullscreen;
            PanelHost.HorizontalAlignment = push ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
            if (push)
            {
                PanelHost.Width = AgentSidePanelPolicy.ClampWidth(panelWidth);
                PanelResizeGrip.PanelWidth = PanelHost.Width;
            }
            else
            {
                PanelHost.ClearValue(FrameworkElement.WidthProperty);
            }

            PanelResizeGrip.Visibility = push ? Visibility.Visible : Visibility.Collapsed;
            PanelDivider.Visibility = push ? Visibility.Visible : Visibility.Collapsed;

            FullscreenIcon.Glyph = fullscreen ? "\uE73F" : "\uE740";
            string fullscreenName = fullscreen ? "Exit fullscreen" : "Fullscreen panel";
            ToolTipService.SetToolTip(FullscreenButton, fullscreenName);
            AutomationProperties.SetName(FullscreenButton, fullscreenName);

            bool wasHidden = previous == AgentSidePanelMode.Hidden;
            if (mode != previous && mode != AgentSidePanelMode.Hidden && !wasHidden)
            {
                PlayEntrance(showPanel ? PanelHost : CardHost, showPanel ? PanelSlide : CardSlide);
            }
        }

        private void OnExpandClick(object sender, RoutedEventArgs e) =>
            ExpandRequested?.Invoke(this, EventArgs.Empty);

        private void OnCardChangesRowClick(object sender, RoutedEventArgs e) =>
            TabOpenRequested?.Invoke(this, SidePanelTabKind.Changes);

        private void OnCardTasksRowClick(object sender, RoutedEventArgs e) =>
            TabOpenRequested?.Invoke(this, SidePanelTabKind.Tasks);

        private void OnCollapseClick(object sender, RoutedEventArgs e) =>
            CollapseRequested?.Invoke(this, EventArgs.Empty);

        private void OnFullscreenClick(object sender, RoutedEventArgs e) =>
            FullscreenToggleRequested?.Invoke(this, EventArgs.Empty);

        private void OnChangesTabClick(object sender, RoutedEventArgs e) =>
            TabActivated?.Invoke(this, SidePanelTabKind.Changes);

        private void OnTasksTabClick(object sender, RoutedEventArgs e) =>
            TabActivated?.Invoke(this, SidePanelTabKind.Tasks);

        private void OnChangesTabCloseClick(object sender, RoutedEventArgs e) =>
            TabClosed?.Invoke(this, SidePanelTabKind.Changes);

        private void OnTasksTabCloseClick(object sender, RoutedEventArgs e) =>
            TabClosed?.Invoke(this, SidePanelTabKind.Tasks);

        private void OnFilesTabClick(object sender, RoutedEventArgs e) =>
            TabActivated?.Invoke(this, SidePanelTabKind.Files);

        private void OnFilesTabCloseClick(object sender, RoutedEventArgs e) =>
            TabClosed?.Invoke(this, SidePanelTabKind.Files);

        private void OnTerminalTabClick(object sender, RoutedEventArgs e) =>
            TabActivated?.Invoke(this, SidePanelTabKind.Terminal);

        private void OnTerminalTabCloseClick(object sender, RoutedEventArgs e) =>
            TabClosed?.Invoke(this, SidePanelTabKind.Terminal);

        private void OpenPanelTab(SidePanelTabKind kind) => TabOpenRequested?.Invoke(this, kind);

        private void OnOpenPanelTabClick(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { Tag: string tag }) OpenPanelTab(KindFromTag(tag));
        }

        private void OnChangeRowToggleClick(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { Tag: ChangesTabRow row })
            {
                row.IsExpanded = !row.IsExpanded;
            }
        }

        private void OnTaskStopClick(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { Tag: string executionId })
            {
                TaskStopRequested?.Invoke(this, executionId);
            }
        }

        private void OnTasksRefreshClick(object sender, RoutedEventArgs e) =>
            TasksRefreshRequested?.Invoke(this, EventArgs.Empty);

    }
}
