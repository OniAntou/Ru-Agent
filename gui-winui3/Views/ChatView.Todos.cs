using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using RuApp.Models;
using RuApp.Services;

namespace RuApp.Views;

public sealed partial class ChatView
{
    // Driven by chronological activity data, never by recycled visual rows.
    private void UpdateTodoPanel(IReadOnlyList<TodoItemModel>? todos)
    {
        _latestTodos = todos;
        var state = AgentTodoPanelPresentation.Create(todos, RootGrid.ActualHeight);
        if (!state.IsVisible)
        {
            ClearTodoPanel();
            return;
        }

        TodoPanelCount.Text = $"{state.Done}/{state.Total}";
        TodoPanelHeader.Text = state.Current;
        ToolTipService.SetToolTip(TodoPanelHeader, state.Current);
        TodoPanelProgress.Maximum = state.Total;
        TodoPanelProgress.Value = state.Done;
        AutomationProperties.SetName(TodoPanelProgress, $"Tasks {state.Done} of {state.Total} done");
        TodoPanelScroll.MaxHeight = state.ListHeight;
        TodoPanelHeader.Visibility = _todoPanelExpanded ? Visibility.Collapsed : Visibility.Visible;
        TodoPanelScroll.Visibility = _todoPanelExpanded ? Visibility.Visible : Visibility.Collapsed;
        TodoPanelList.Visibility = TodoPanelScroll.Visibility;
        TodoPanelToggleIcon.Glyph = _todoPanelExpanded ? "\uE70E" : "\uE70D";
        AutomationProperties.SetName(TodoPanelToggle, _todoPanelExpanded ? "Collapse task list" : "Expand task list");
        ToolTipService.SetToolTip(TodoPanelToggle, _todoPanelExpanded ? "Collapse task list" : "Expand task list");
        if (_todoPanelExpanded) TodoPanelList.Render(todos);
        UiMotion.SetVisible(TodoPanel, true);
    }

    private void ClearTodoPanel()
    {
        _latestTodos = null;
        _todoPanelExpanded = false;
        TodoPanelToggleIcon.Glyph = "\uE70D";
        TodoPanelList.Render(null);
        TodoPanelScroll.Visibility = Visibility.Collapsed;
        UiMotion.SetVisible(TodoPanel, false);
    }

    private void OnTodoPanelToggleClick(object sender, RoutedEventArgs e)
    {
        _todoPanelExpanded = !_todoPanelExpanded;
        UpdateTodoPanel(_latestTodos);
    }
}
