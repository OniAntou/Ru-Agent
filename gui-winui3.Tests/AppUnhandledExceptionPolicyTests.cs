using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class AppUnhandledExceptionPolicyTests
{
    [Fact]
    public void Recoverable_exception_is_handled_and_writes_one_crash_event()
    {
        var exception = new InvalidOperationException("window is closing");
        var written = new List<Exception?>();

        var handled = AppUnhandledExceptionPolicy.Handle(
            exception,
            written.Add);

        Assert.True(handled);
        Assert.Equal([exception], written);
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

    [Theory]
    [InlineData(typeof(ArgumentException))]
    [InlineData(typeof(Microsoft.UI.Xaml.Markup.XamlParseException))]
    public void Programming_bug_exceptions_are_not_handled_and_write_crash_event(Type exceptionType)
    {
        var exception = (Exception)Activator.CreateInstance(exceptionType, "programming bug")!;
        var written = new List<Exception?>();

        var handled = AppUnhandledExceptionPolicy.Handle(exception, written.Add);

        Assert.False(handled);
        Assert.Equal([exception], written);
    }
}
