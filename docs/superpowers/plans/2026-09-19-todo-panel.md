# Todo Panel trên thanh nhập prompt Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Chuyển checklist todo ra khỏi activity bubble thành một panel card duy nhất ở `InputContainer` Grid.Row 1, ngay trên khung chat, kiểu Antigravity/DSH.

**Architecture:** `ChatView` giữ `_latestTodos` duy nhất làm owner, activity mới mang todos thì panel update tại chỗ, `TodoChecklistControl` tái dùng nguyên để vẽ rows, XAML cũ trong activity template bị gỡ.

**Tech Stack:** WinUI 3, C#, xUnit contract tests qua `GuiSource`, `dotnet test`.

**Không commit trong plan này theo yêu cầu của chủ nhân.**

---

## File structure

- Modify: `gui-winui3/Views/ChatView.xaml` — thêm card todo ở `InputContainer` Row 1, dính liền với khung chat thành một khối (panel giữ viền trên, input giữ viền dưới, giữa có divider), gỡ `TodoChecklistControl` khỏi `AgentActivityTemplate`.
- Modify: `gui-winui3/Views/ChatView.xaml.cs` — thêm field `_latestTodos`, method `UpdateTodoPanel` + `ClearTodoPanel` + toggle handler, gọi update từ `AttachActivityItem`, clear trong `ResetAgentState`, gỡ method `RenderTodoChecklist` (dòng 1524-1551) và call ở dòng 1501.
- Modify: `gui-winui3.Tests/TodoChecklistTests.cs` — sửa 4 tests trỏ sang `InputContainer` Row 1, thêm tests cho panel behavior.
- Reuse nguyên: `gui-winui3/Controls/TodoChecklistControl.cs`, `gui-winui3/Models/TodoItemModel.cs`.

---

### Task 1: Thêm card todo vào InputContainer Row 1

**Files:**
- Modify: `gui-winui3/Views/ChatView.xaml`
- Test: `gui-winui3.Tests/TodoChecklistTests.cs`

- [ ] **Step 1: Viết failing test cho vị trí panel mới**

```csharp
[Fact]
public void The_checklist_lives_above_the_composer_in_the_input_container()
{
    var document = GuiSource.Xml("Views", "ChatView.xaml");
    var checklist = ChecklistElement(document);

    var inputContainer = document.Descendants()
        .FirstOrDefault(element =>
            (string?)element.Attribute("Name") == "InputContainer" ||
            (string?)element.Attribute(GuiSource.Xaml + "Name") == "InputContainer");

    Assert.NotNull(inputContainer);
    Assert.True(GuiSource.IsDescendantOf(checklist, inputContainer));
    Assert.Equal("1", GuiSource.AttributeValue(checklist, "Grid.Row"));
}
```

- [ ] **Step 2: Chạy test để thấy đỏ**

Run: `dotnet test D:/LLM/gui-winui3.Tests/gui-winui3.Tests.csproj --filter "FullyQualifiedName~TodoChecklist"`
Expected: FAIL ở test mới vì checklist còn nằm trong `AgentActivityTemplate`, chưa có `Grid.Row="1"` trong `InputContainer`.

- [ ] **Step 3: Thêm card todo vào XAML**

Thêm ngay sau `TerminalPanelControl` (Grid.Row 0), trước `AgentInlinePanel` (Grid.Row 2):

```xml
<Border x:Name="TodoPanel"
        Grid.Row="1"
        Visibility="Collapsed"
        Margin="8,0,8,6"
        Background="{StaticResource RuSurfaceBrush}"
        BorderBrush="{StaticResource RuLineBrush}"
        BorderThickness="1"
        CornerRadius="{StaticResource AppCardCornerRadius}"
        AutomationProperties.Name="Agent task list">
    <StackPanel Spacing="4" Padding="12,8">
        <Grid ColumnSpacing="8">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="*" />
                <ColumnDefinition Width="Auto" />
            </Grid.ColumnDefinitions>
            <TextBlock x:Name="TodoPanelHeader"
                       FontFamily="{StaticResource RuUiFontFamily}"
                       FontSize="12"
                       FontWeight="SemiBold"
                       Foreground="{StaticResource RuInkBrush}"
                       VerticalAlignment="Center" />
            <Button x:Name="TodoPanelToggle"
                    Grid.Column="1"
                    Click="OnTodoPanelToggleClick"
                    AutomationProperties.Name="Expand or collapse task list"
                    Background="Transparent"
                    BorderThickness="0"
                    Padding="6,2">
                <FontIcon x:Name="TodoPanelToggleIcon" Glyph="&#xE70D;" FontSize="12" />
            </Button>
        </Grid>
        <controls:TodoChecklistControl x:Name="TodoPanelList"
                                       HorizontalAlignment="Stretch"
                                       AutomationProperties.Name="Agent task list" />
    </StackPanel>
</Border>
```

- [ ] **Step 4: Chạy test để thấy test mới xanh**

Run: `dotnet test D:/LLM/gui-winui3.Tests/gui-winui3.Tests.csproj --filter "FullyQualifiedName~TodoChecklist"`
Expected: test mới PASS, 4 tests cũ đỏ (đúng dự kiến, sửa ở Task 3).

---

### Task 2: Owner `_latestTodos` và update tại chỗ trong code-behind

**Files:**
- Modify: `gui-winui3/Views/ChatView.xaml.cs:87-88` (cạnh `_activeSessionId`)
- Modify: `gui-winui3/Views/ChatView.xaml.cs:1437` (trong `AttachActivityItem`)
- Modify: `gui-winui3/Views/ChatView.xaml.cs:2013` (trong `ResetAgentState`)
- Test: `gui-winui3.Tests/TodoChecklistTests.cs`

- [ ] **Step 1: Viết failing test cho owner method**

```csharp
[Fact]
public void The_panel_is_updated_from_the_activity_that_carries_todos()
{
    string code = GuiSource.Text("Views", "ChatView.xaml.cs");

    Assert.True(GuiSource.HasMember(code, "UpdateTodoPanel"));

    string attach = GuiSource.MethodBody(code, "AttachActivityItem");
    Assert.Contains("UpdateTodoPanel", attach, StringComparison.Ordinal);
}

[Fact]
public void Switching_session_clears_the_panel()
{
    string code = GuiSource.Text("Views", "ChatView.xaml.cs");

    string reset = GuiSource.MethodBody(code, "ResetAgentState");
    Assert.Contains("ClearTodoPanel", reset, StringComparison.Ordinal);
}
```

- [ ] **Step 2: Chạy test để thấy đỏ**

Run: `dotnet test D:/LLM/gui-winui3.Tests/gui-winui3.Tests.csproj --filter "FullyQualifiedName~TodoChecklist"`
Expected: FAIL vì chưa có `UpdateTodoPanel` và `ClearTodoPanel`.

- [ ] **Step 3: Thêm fields và methods tối thiểu**

Thêm cạnh `_activeSessionId` (dòng 87):

```csharp
private IReadOnlyList<TodoItemModel>? _latestTodos;
private bool _todoPanelExpanded;
```

Thêm methods mới (đặt ngay sau `RenderTodoChecklist` cũ trước khi gỡ):

```csharp
private void UpdateTodoPanel(IReadOnlyList<TodoItemModel>? todos)
{
    _latestTodos = todos;
    if (TodoPanel is null || TodoPanelList is null || TodoPanelHeader is null)
    {
        return;
    }

    if (todos is not { Count: > 0 })
    {
        TodoPanel.Visibility = Visibility.Collapsed;
        return;
    }

    int done = 0;
    string? current = null;
    foreach (var todo in todos)
    {
        if (todo.IsCompleted)
        {
            done++;
        }
        else if (current is null)
        {
            current = todo.Content;
        }
    }

            TodoPanelHeader.Text = current is null
                ? $"Tasks {done}/{todos.Count} done"
                : $"Tasks {done}/{todos.Count} — {current}";
            TodoPanel.Visibility = Visibility.Visible;

            if (_todoPanelExpanded)
            {
                TodoPanelList.Visibility = Visibility.Visible;
                TodoPanelList.Render(todos);
            }
            else
            {
                TodoPanelList.Visibility = Visibility.Collapsed;
            }
}

private void ClearTodoPanel()
{
    _latestTodos = null;
    _todoPanelExpanded = false;
    if (TodoPanel is not null)
    {
        TodoPanel.Visibility = Visibility.Collapsed;
    }
}

private void OnTodoPanelToggleClick(object sender, RoutedEventArgs e)
{
    _todoPanelExpanded = !_todoPanelExpanded;
    if (TodoPanelToggleIcon is not null)
    {
        TodoPanelToggleIcon.Glyph = _todoPanelExpanded ? "\uE70D" : "\uE70E";
    }

    UpdateTodoPanel(_latestTodos);
}
```

Trong `AttachActivityItem`, thay call `RenderTodoChecklist(grid, item);` (dòng 1501) bằng:

```csharp
if (item.Todos is { Count: > 0 } latestTodos)
{
    DispatcherQueue?.TryEnqueue(() => UpdateTodoPanel(latestTodos));
}
```

Trong `ResetAgentState` (dòng 2013), thêm sau `ClearAgentActivities();`:

```csharp
ClearTodoPanel();
```

- [ ] **Step 4: Chạy test để thấy xanh**

Run: `dotnet test D:/LLM/gui-winui3.Tests/gui-winui3.Tests.csproj --filter "FullyQualifiedName~TodoChecklist"`
Expected: 2 tests mới PASS.

---

### Task 3: Gỡ checklist khỏi activity template và sửa tests cũ

**Files:**
- Modify: `gui-winui3/Views/ChatView.xaml:190-201`
- Modify: `gui-winui3/Views/ChatView.xaml.cs:1501,1524-1551`
- Modify: `gui-winui3.Tests/TodoChecklistTests.cs`

- [ ] **Step 1: Xác nhận 4 tests cũ đang đỏ với lý do đúng**

Run: `dotnet test D:/LLM/gui-winui3.Tests/gui-winui3.Tests.csproj --filter "FullyQualifiedName~TodoChecklist"`
Expected: đỏ ở `The_checklist_is_inside_the_agent_activity_template`, `The_checklist_is_shown_only_when_there_is_a_list`, `The_checklist_offers_nothing_to_click`, `The_checklist_is_named_for_a_screen_reader` vì checklist còn 2 nơi.

- [ ] **Step 2: Gỡ XAML cũ trong activity template**

Xóa block dòng 190-201 trong `ChatView.xaml`:

```xml
<!--
  The agent's task list. Read-only: nothing here is clickable, because the
  agent plans and the user watches. The rows come from the activity that
  carries them, so the control has no binding of its own.
-->
<controls:TodoChecklistControl Grid.Row="3"
                               x:Name="TodoChecklist"
                               Visibility="{x:Bind HasTodos, Mode=OneWay, Converter={StaticResource BoolToVisibilityConverter}}"
                               HorizontalAlignment="Stretch"
                               Margin="0,10,0,2"
                               MaxWidth="760"
                               AutomationProperties.Name="Agent task list" />
```

- [ ] **Step 3: Gỡ `RenderTodoChecklist` khỏi code-behind**

Xóa method `RenderTodoChecklist` (dòng 1524-1551) cùng doc comment, giữ lại `UpdateTodoPanel` đã thêm ở Task 2. Kiểm tra không còn reference nào tới `RenderTodoChecklist` ngoài test cũ sắp sửa.

- [ ] **Step 4: Sửa 4 tests cũ trỏ sang panel mới**

`The_checklist_is_inside_the_agent_activity_template` đổi thành check panel nằm trong `InputContainer` (đã có test ở Task 1, giữ một bản, xóa bản trùng). `The_checklist_is_shown_only_when_there_is_a_list` đổi expected từ binding `HasTodos` sang check code-behind: `UpdateTodoPanel` set `Visibility.Collapsed` khi rỗng và `Visible` khi có list. `The_checklist_is_named_for_a_screen_reader` đổi expected element sang `TodoPanel` (`AutomationProperties.Name="Agent task list"` giữ nguyên). `The_checklist_offers_nothing_to_click` giữ nguyên ý read-only cho `TodoPanelList` (không `Click`/`Tapped`/`SelectionChanged`) nhưng cho phép `TodoPanelToggle` có `Click` vì nút chỉ để xem, không sửa list.

- [ ] **Step 5: Chạy toàn bộ tests todo**

Run: `dotnet test D:/LLM/gui-winui3.Tests/gui-winui3.Tests.csproj --filter "FullyQualifiedName~TodoChecklist"`
Expected: PASS hết.

---

### Task 4: Chạy full suite GUI và rà soát

**Files:** không sửa, chỉ verify.

- [ ] **Step 1: Chạy full gui-winui3.Tests**

Run: `dotnet test D:/LLM/gui-winui3.Tests/gui-winui3.Tests.csproj`
Expected: PASS hết, không test nào khác reference `TodoChecklist` tên cũ hay `RenderTodoChecklist`.

- [ ] **Step 2: Grep không còn reference cũ**

Run: `rg -n "RenderTodoChecklist|x:Name=\"TodoChecklist\"" D:/LLM/gui-winui3 D:/LLM/gui-winui3.Tests`
Expected: không có match nào (tên mới là `TodoPanel`, `TodoPanelList`, `UpdateTodoPanel`).

- [ ] **Step 3: Build RuApp để chắc XAML hợp lệ**

Run: `dotnet build D:/LLM/gui-winui3/RuApp.csproj --nologo -v q`
Expected: build thành công, không lỗi XAML.
