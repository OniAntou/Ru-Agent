using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

/// <summary>
/// The interactive terminal's ConPTY session, exercised against a real shell:
/// output streams back, and disposing the session ends the process.
/// </summary>
public sealed class ConPtyTerminalSessionTests
{
    [Fact]
    public void A_session_runs_a_shell_and_streams_its_output()
    {
        var root = TempRoot();
        Directory.CreateDirectory(root);
        try
        {
            using var session = ConPtyTerminalSession.Start(root, "cmd.exe /Q", 80, 24);
            var output = new StringBuilder();
            var exited = false;
            using var received = new ManualResetEventSlim(false);
            session.Exited += () => exited = true;
            session.OutputReceived += chunk =>
            {
                lock (output)
                {
                    output.Append(Encoding.UTF8.GetString(chunk));
                    if (output.ToString().Contains("RU_TERMINAL_OK", StringComparison.Ordinal))
                    {
                        received.Set();
                    }
                }
            };

            // Let the shell finish attaching to the console before typing.
            Thread.Sleep(1_500);
            session.Write(Encoding.UTF8.GetBytes("echo RU_TERMINAL_OK\r\n"));

            bool gotMarker = received.Wait(TimeSpan.FromSeconds(20));
            bool alive = IsAlive(session.ProcessId);
            Assert.True(
                gotMarker,
                $"no marker in output: {Snapshot(output)} | exited={exited} alive={alive} pid={session.ProcessId}");
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void Disposing_the_session_ends_the_shell()
    {
        var root = TempRoot();
        Directory.CreateDirectory(root);
        try
        {
            var session = ConPtyTerminalSession.Start(root, "cmd.exe /Q", 80, 24);
            int processId = session.ProcessId;
            Assert.True(processId > 0);

            session.Dispose();

            Assert.True(WaitForExit(processId, TimeSpan.FromSeconds(10)), "shell survived Dispose");
        }
        finally
        {
            Cleanup(root);
        }
    }

    private static string Snapshot(StringBuilder builder)
    {
        lock (builder)
        {
            return builder.ToString();
        }
    }

    private static bool IsAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool WaitForExit(int processId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                if (process.HasExited)
                {
                    return true;
                }
            }
            catch (ArgumentException)
            {
                return true;
            }

            Thread.Sleep(100);
        }

        return false;
    }

    private static string TempRoot() =>
        Path.Combine(Path.GetTempPath(), "ru-conpty-tests", Guid.NewGuid().ToString("N"));

    private static void Cleanup(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
