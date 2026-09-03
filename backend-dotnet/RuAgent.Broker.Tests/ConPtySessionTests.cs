using System.Text;
using RuAgent.Broker.Tools.Terminal.ConPty;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed class ConPtySessionTests
{
    [Fact]
    public void ConPty_IsSupported_ReturnsTrueOnModernWindows()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.True(ConPtySession.IsSupported);
        }
    }

    [Fact]
    public void StructSize_Check()
    {
        Assert.Equal(112, System.Runtime.InteropServices.Marshal.SizeOf<ConPtyNative.STARTUPINFOEX>());
    }

    [Fact]
    public async Task ConPty_SpawnsProcess_AndStreamsOutput()
    {
        if (!ConPtySession.IsSupported) return;

        using var session = ConPtySession.Start("where.exe cmd", ".");

        var output = await session.ReadOutputUntilExitAsync(TimeSpan.FromSeconds(5));
        var exitCode = await session.WaitForExitAsync(CancellationToken.None);

        Assert.True(output.Contains("cmd.exe"), $"Output: {output}");
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task ConPty_InteractiveInput_EchoesAndExits()
    {
        if (!ConPtySession.IsSupported) return;

        using var session = ConPtySession.Start("cmd.exe", ".");
        await Task.Delay(100);
        session.Write("echo CatgirlPtyReady\r\nexit\r\n");

        var output = await session.ReadOutputUntilExitAsync(TimeSpan.FromSeconds(5));
        var exitCode = await session.WaitForExitAsync(CancellationToken.None);

        Assert.Contains("CatgirlPtyReady", output);
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public void ConPty_Resize_DoesNotThrow()
    {
        if (!ConPtySession.IsSupported) return;

        var cmd = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
        using var session = ConPtySession.Start($"{cmd} /c echo Resizing", ".");
        session.Resize(120, 40);
        session.Terminate();
    }

    [Fact]
    public void ConPty_Throws_WhenWorkingDirectoryDoesNotExist()
    {
        if (!ConPtySession.IsSupported) return;
        Assert.Throws<DirectoryNotFoundException>(() => ConPtySession.Start("cmd.exe", "Z:\\NonExistentPath_xyz123"));
    }

    [Fact]
    public async Task ConPty_Dispose_KillsRunningProcessImmediately()
    {
        if (!ConPtySession.IsSupported) return;
        var session = ConPtySession.Start("cmd.exe", ".");
        var pid = session.ProcessId;
        Assert.True(pid > 0);
        session.Dispose();
        await Task.Delay(200);
        Assert.Throws<ArgumentException>(() => System.Diagnostics.Process.GetProcessById(pid));
    }
}
