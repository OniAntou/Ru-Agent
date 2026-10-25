using System.Runtime.InteropServices;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class WorkspaceFolderPickerTests
{
    [Theory]
    [InlineData(unchecked((int)0x800704C7))]
    [InlineData(unchecked((int)0x80004004))]
    public async Task Native_dialog_cancellation_is_not_a_workspace_error(int code)
    {
        var picker = new WorkspaceFolderPicker(() => Task.FromException<string?>(new COMException("", code)));
        Assert.Null(await picker.PickAsync());
    }

    [Fact]
    public async Task Repeated_open_does_not_launch_a_second_native_dialog()
    {
        var selected = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var picker = new WorkspaceFolderPicker(() => { calls++; return selected.Task; });
        var first = picker.PickAsync();
        var second = picker.PickAsync();
        selected.SetResult("C:\\qa");
        Assert.Null(await second);
        Assert.Equal(1, calls);
        Assert.Equal("C:\\qa", await first);
        Assert.Equal("C:\\qa", await picker.PickAsync());
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Unexpected_native_errors_propagate_and_release_the_dialog_slot()
    {
        var calls = 0;
        var picker = new WorkspaceFolderPicker(() => ++calls == 1
            ? Task.FromException<string?>(new COMException("", unchecked((int)0x80004005)))
            : Task.FromResult<string?>("C:\\qa"));
        await Assert.ThrowsAsync<COMException>(() => picker.PickAsync());
        Assert.Equal("C:\\qa", await picker.PickAsync());
    }
}
