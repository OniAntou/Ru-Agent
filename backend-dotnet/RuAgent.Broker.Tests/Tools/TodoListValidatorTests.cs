using System.Text.Json.Nodes;
using RuAgent.Broker.Tools.Todo;
using Xunit;

namespace RuAgent.Broker.Tests.Tools;

public sealed class TodoListValidatorTests
{
    private static JsonArray List(params (string Content, string Status)[] items) =>
        new(items.Select(item => (JsonNode)new JsonObject
        {
            ["content"] = item.Content,
            ["status"] = item.Status,
        }).ToArray());

    [Fact]
    public void A_well_formed_list_is_accepted()
    {
        var result = TodoListValidator.Validate(
            List(("Read the file", "completed"), ("Edit it", "in_progress")));

        Assert.True(result.IsValid);
        Assert.Equal(2, result.Items.Count);
        Assert.Equal("Read the file", result.Items[0].Content);
    }

    [Fact]
    public void An_empty_list_is_valid()
    {
        // Clearing the plan is a legitimate write, not a malformed one.
        var result = TodoListValidator.Validate(List());

        Assert.True(result.IsValid);
        Assert.Empty(result.Items);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_content_is_rejected(string content)
    {
        var result = TodoListValidator.Validate(List((content, "pending")));

        Assert.False(result.IsValid);
        Assert.Equal("todo_content_empty", result.ErrorCode);
    }

    [Fact]
    public void Content_is_trimmed_before_it_is_stored()
    {
        var result = TodoListValidator.Validate(List(("  Read the file  ", "pending")));

        Assert.True(result.IsValid);
        Assert.Equal("Read the file", result.Items[0].Content);
    }

    [Fact]
    public void Duplicate_content_is_rejected()
    {
        var result = TodoListValidator.Validate(List(("Same", "pending"), ("Same", "completed")));

        Assert.False(result.IsValid);
        Assert.Equal("todo_content_duplicate", result.ErrorCode);
    }

    [Fact]
    public void Duplicate_detection_ignores_surrounding_whitespace()
    {
        var result = TodoListValidator.Validate(List(("Same", "pending"), ("  Same  ", "completed")));

        Assert.False(result.IsValid);
        Assert.Equal("todo_content_duplicate", result.ErrorCode);
    }

    [Fact]
    public void More_than_one_active_task_is_rejected()
    {
        var result = TodoListValidator.Validate(
            List(("A", "in_progress"), ("B", "in_progress")));

        Assert.False(result.IsValid);
        Assert.Equal("todo_multiple_active", result.ErrorCode);
    }

    [Fact]
    public void Many_completed_tasks_are_not_mistaken_for_many_active_ones()
    {
        var result = TodoListValidator.Validate(
            List(("A", "completed"), ("B", "completed"), ("C", "completed")));

        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData("blocked")]
    [InlineData("")]
    [InlineData("Pending")]
    [InlineData("in progress")]
    public void An_unknown_status_is_rejected(string status)
    {
        var result = TodoListValidator.Validate(List(("A", status)));

        Assert.False(result.IsValid);
        Assert.Equal("todo_status_unknown", result.ErrorCode);
    }

    [Fact]
    public void Sixty_four_items_are_accepted_and_sixty_five_are_not()
    {
        var atLimit = List(Enumerable.Range(0, 64).Select(index => ($"task {index}", "pending")).ToArray());
        var overLimit = List(Enumerable.Range(0, 65).Select(index => ($"task {index}", "pending")).ToArray());

        Assert.True(TodoListValidator.Validate(atLimit).IsValid);

        var result = TodoListValidator.Validate(overLimit);
        Assert.False(result.IsValid);
        Assert.Equal("todo_too_many", result.ErrorCode);
    }

    [Fact]
    public void Two_hundred_characters_are_accepted_and_two_hundred_and_one_are_not()
    {
        Assert.True(TodoListValidator.Validate(List((new string('x', 200), "pending"))).IsValid);

        var result = TodoListValidator.Validate(List((new string('x', 201), "pending")));
        Assert.False(result.IsValid);
        Assert.Equal("todo_content_too_long", result.ErrorCode);
    }

    [Fact]
    public void A_missing_or_non_array_argument_is_rejected()
    {
        Assert.Equal("todo_list_required", TodoListValidator.Validate(null).ErrorCode);
        Assert.Equal("todo_list_required", TodoListValidator.Validate(new JsonObject()).ErrorCode);
    }

    [Fact]
    public void A_row_that_is_not_an_object_is_rejected()
    {
        var result = TodoListValidator.Validate(new JsonArray("just a string"));

        Assert.False(result.IsValid);
        Assert.Equal("todo_item_invalid", result.ErrorCode);
    }

    [Fact]
    public void A_rejected_list_reports_no_items()
    {
        // The caller must not store a partially validated list, so a failure carries
        // nothing to store.
        var result = TodoListValidator.Validate(List(("good", "pending"), ("", "pending")));

        Assert.False(result.IsValid);
        Assert.Empty(result.Items);
    }
}
