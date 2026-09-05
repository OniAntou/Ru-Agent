using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class AppUnhandledExceptionPolicyTests
{
    [Fact]
    public void Recoverable_exception_is_handled_without_writing_a_crash_event()
    {
        var written = new List<Exception?>();

        var handled = AppUnhandledExceptionPolicy.Handle(
            new InvalidOperationException("window is closing"),
            written.Add);

        Assert.True(handled);
        Assert.Empty(written);
    }

    [Fact]
    public void Fatal_exception_is_not_handled_and_writes_one_crash_event()
    {
        var exception = new OutOfMemoryException("fatal");
        var written = new List<Exception?>();

        var handled = AppUnhandledExceptionPolicy.Handle(exception, written.Add);

        Assert.False(handled);
        Assert.Equal([exception], written);
    }
}
