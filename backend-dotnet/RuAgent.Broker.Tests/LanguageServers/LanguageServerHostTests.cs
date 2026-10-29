using System.Diagnostics;
using System.Text;
using RuAgent.Broker.Tools.LanguageServers;
using Xunit;

namespace RuAgent.Broker.Tests.LanguageServers;

public sealed class LanguageServerHostTests
{
    [Fact]
    public async Task RejectsResultsAfterTheDocumentChangesAndStopsTheOwnedProcess()
    {
        using var fixture = new FakeServerFixture("delayed");
        await using var host = fixture.CreateHost();
        var query = host.ExecuteAsync("diagnostics", "csharp", "src\\Example.cs", null, null, false, CancellationToken.None);
        await fixture.WaitForFileAsync(fixture.ReadyPath);
        await File.WriteAllTextAsync(fixture.SourcePath, "class Example { int Changed; }");

        var error = await Assert.ThrowsAsync<LspException>(() => query);
        Assert.Equal("lsp_stale_document", error.Code);
        await File.WriteAllTextAsync(fixture.SourcePath, "class Example { }");
        _ = await host.ExecuteAsync("symbols", "csharp", "src\\Example.cs", null, null, false, CancellationToken.None);
        var processId = int.Parse(await File.ReadAllTextAsync(fixture.ProcessIdPath));
        await host.DisposeAsync();
        Assert.False(IsProcessRunning(processId));
    }

    [Fact]
    public async Task RejectsOperationsMissingFromNegotiatedServerCapabilities()
    {
        using var fixture = new FakeServerFixture("no-symbols");
        await using var host = fixture.CreateHost();
        var error = await Assert.ThrowsAsync<LspException>(() =>
            host.ExecuteAsync("symbols", "csharp", "src\\Example.cs", null, null, false, CancellationToken.None));
        Assert.Equal("lsp_method_unsupported", error.Code);
    }

    [Fact]
    public async Task InitializationFailureCleansUpTheStartedProcess()
    {
        using var fixture = new FakeServerFixture("bad-init");
        await using var host = fixture.CreateHost();
        var error = await Assert.ThrowsAsync<LspException>(() =>
            host.ExecuteAsync("symbols", "csharp", "src\\Example.cs", null, null, false, CancellationToken.None));
        Assert.Equal("lsp_initialization_failed", error.Code);
        await fixture.WaitForFileAsync(fixture.ProcessIdPath);
        var processId = int.Parse(await File.ReadAllTextAsync(fixture.ProcessIdPath));
        await WaitForProcessExitAsync(processId);
        Assert.False(IsProcessRunning(processId));
    }

    private static bool IsProcessRunning(int processId)
    {
        try { using var process = Process.GetProcessById(processId); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }

    private static async Task WaitForProcessExitAsync(int processId)
    {
        var deadline = Stopwatch.StartNew();
        while (IsProcessRunning(processId) && deadline.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(20);
    }

    private sealed class FakeServerFixture : IDisposable
    {
        private const string ServerScript = """
            $ErrorActionPreference = 'Stop'
            $inputStream = [Console]::OpenStandardInput()
            $outputStream = [Console]::OpenStandardOutput()
            [IO.File]::WriteAllText($args[1], [string]$PID)
            function Read-Frame {
              $header = New-Object 'System.Collections.Generic.List[byte]'
              while ($true) {
                $byte = $inputStream.ReadByte()
                if ($byte -lt 0) { return $null }
                $header.Add([byte]$byte)
                $count = $header.Count
                if ($count -ge 4 -and $header[$count-4] -eq 13 -and $header[$count-3] -eq 10 -and $header[$count-2] -eq 13 -and $header[$count-1] -eq 10) { break }
              }
              $headerText = [Text.Encoding]::ASCII.GetString($header.ToArray())
              $length = [int]([regex]::Match($headerText, 'Content-Length: (\d+)').Groups[1].Value)
              $body = New-Object byte[] $length
              $offset = 0
              while ($offset -lt $length) { $offset += $inputStream.Read($body, $offset, $length - $offset) }
              return [Text.Encoding]::UTF8.GetString($body) | ConvertFrom-Json
            }
            function Send-Frame($message) {
              $body = [Text.Encoding]::UTF8.GetBytes((ConvertTo-Json -InputObject $message -Depth 32 -Compress))
              $header = [Text.Encoding]::ASCII.GetBytes("Content-Length: $($body.Length)`r`n`r`n")
              $outputStream.Write($header, 0, $header.Length)
              $outputStream.Write($body, 0, $body.Length)
              $outputStream.Flush()
            }
            while ($true) {
              $message = Read-Frame
              if ($null -eq $message) { break }
              if ($message.method -eq 'initialize') {
                if ($args[0] -eq 'bad-init') {
                  Send-Frame @{ jsonrpc='2.0'; id=$message.id; result=@{} }
                } else {
                  $symbols = $args[0] -ne 'no-symbols'
                  Send-Frame @{ jsonrpc='2.0'; id=$message.id; result=@{ capabilities=@{ textDocumentSync=@{ openClose=$true; change=1 }; documentSymbolProvider=$symbols; definitionProvider=$true; referencesProvider=$true } } }
                }
              } elseif ($message.method -eq 'textDocument/didOpen') {
                [IO.File]::WriteAllText($args[2], 'ready')
                if ($args[0] -eq 'delayed') { Start-Sleep -Milliseconds 500 }
                Send-Frame @{ jsonrpc='2.0'; method='textDocument/publishDiagnostics'; params=@{ uri=$message.params.textDocument.uri; version=$message.params.textDocument.version; diagnostics=@() } }
              } elseif ($message.method -eq 'textDocument/documentSymbol') {
                Send-Frame @{ jsonrpc='2.0'; id=$message.id; result=@() }
              }
            }
            """;

        private readonly string _root = Path.Combine(Path.GetTempPath(), "ru-lsp-host-" + Guid.NewGuid().ToString("N"));
        private readonly string _profile;
        private readonly string _scriptPath;
        private readonly string _mode;
        public string SourcePath { get; }
        public string ReadyPath { get; }
        public string ProcessIdPath { get; }

        public FakeServerFixture(string mode)
        {
            _mode = mode;
            _profile = Path.Combine(_root, "profile");
            _scriptPath = Path.Combine(_root, "fake-server.ps1");
            var workspace = Path.Combine(_root, "workspace");
            SourcePath = Path.Combine(workspace, "src", "Example.cs");
            ReadyPath = Path.Combine(_root, "ready");
            ProcessIdPath = Path.Combine(_root, "pid");
            Directory.CreateDirectory(Path.Combine(_profile, ".ru"));
            Directory.CreateDirectory(Path.GetDirectoryName(SourcePath)!);
            File.WriteAllText(SourcePath, "class Example { }");
            File.WriteAllText(_scriptPath, ServerScript);
            var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
            var args = new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", _scriptPath, mode, ProcessIdPath, ReadyPath };
            var json = System.Text.Json.JsonSerializer.Serialize(new { servers = new { csharp = new { command = powershell, args } } });
            File.WriteAllText(LanguageServerConfiguration.PathFor(_profile), json);
        }

        public LanguageServerHost CreateHost() => new(Path.Combine(_root, "workspace"), () => _profile,
            new RuAgent.Broker.Tools.Terminal.AgentCommandEnvironment(new Dictionary<string, string>
            {
                ["SYSTEMROOT"] = Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                ["WINDIR"] = Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                ["TEMP"] = Path.GetTempPath(), ["TMP"] = Path.GetTempPath(),
            }).ChildEnvironment);

        public async Task WaitForFileAsync(string path)
        {
            var deadline = Stopwatch.StartNew();
            while (!File.Exists(path) && deadline.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(20);
            Assert.True(File.Exists(path), "Fake language server did not write its ready marker.");
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        }
    }
}
