using System.Diagnostics;
using System.Text;
using System.Text.Json;
using RuAgent.Cli.TerminalGui;
using RuAgent.Cli.Tui;
using RuApp.Services;
using Terminal.Gui.App;

if (args.Length > 0 && args[0] == "control-child")
{
    Console.InputEncoding = Encoding.UTF8;
    Console.OutputEncoding = Encoding.UTF8;
    var root = args[1];
    File.WriteAllText(Path.Combine(root, "ready.txt"), "ready");
    var text = Console.ReadLine();
    var target = File.ReadAllText(Path.Combine(root, "expected.txt"));
    File.WriteAllText(Path.Combine(root, "result.tmp"), JsonSerializer.Serialize(new { text, expected = target, passed = text == target }));
    File.Move(Path.Combine(root, "result.tmp"), Path.Combine(root, "result.json"));
    return text == target ? 0 : 1;
}

if (args.Length > 0 && args[0] == "child")
{
    // Match RuAgent.Cli.Program.Main before testing the production driver's input path.
    Console.InputEncoding = Encoding.UTF8;
    Console.OutputEncoding = Encoding.UTF8;
    var root = args[1];
    var target = File.ReadAllText(Path.Combine(root, "expected.txt"));
    var submissions = 0;
    var pastes = 0;
    string? rawPaste = null;
    using IApplication app = Application.Create();
    TgTerminalInput.Initialize(app);
    using var window = TgChatWindow.Build();
    var presentation = new TuiSessionRenderer(new TgTranscriptWriter(app, window.Transcript));
    presentation.Transcript.FullScreenCards = true;
    var loop = new TgChatLoop(window);
    loop.SetPromptHandler((_, _, _) => { submissions++; return Task.CompletedTask; });
    app.Paste += (_, e) => rawPaste = e.Text;
    TgTerminalPaste.Attach(app);
    app.Paste += (_, _) => pastes++;
    app.AddTimeout(TimeSpan.FromMilliseconds(200), () =>
    {
        presentation.Transcript.BeginTurn();
        presentation.Transcript.AppendAssistant("# Native transcript\n**Tiếng Việt 😀** in `README.md`\n| name | value |\n| --- | --- |\n| total | 140,600 |\n```cs\nreturn 42;\n```\n");
        presentation.Transcript.EndTurn(TuiTurnOutcome.Completed, null);
        window.FocusComposer();
        File.WriteAllText(Path.Combine(root, "ready.txt"), "ready");
        return false;
    });
    app.AddTimeout(TimeSpan.FromMilliseconds(300), () =>
    {
        var text = window.Composer.Input.Text;
        if (text != target) return true;
        File.WriteAllText(Path.Combine(root, "result.tmp"), JsonSerializer.Serialize(new
        {
            text, expected = target, submissions, pastes, rawPaste,
            cursor = window.Composer.Input.InsertionPoint,
            loadedCli = typeof(TgTerminalPaste).Assembly.Location,
            structuredTranscriptRows = window.Transcript.RetainedRowCount,
            passed = submissions == 0 && pastes == 1 && window.Transcript.RetainedRowCount >= 4,
        }, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(Path.Combine(root, "result.tmp"), Path.Combine(root, "result.json"));
        app.RequestStop();
        return false;
    });
    app.AddTimeout(TimeSpan.FromSeconds(15), () =>
    {
        File.WriteAllText(Path.Combine(root, "timeout.json"), JsonSerializer.Serialize(new { text = window.Composer.Input.Text, submissions, pastes, rawPaste, codeUnits = rawPaste?.Select(c => ((int)c).ToString("X4")).ToArray() }));
        app.RequestStop();
        return false;
    });
    app.Run(window);
    return File.Exists(Path.Combine(root, "result.json")) ? 0 : 1;
}

var probeRoot = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(AppContext.BaseDirectory, "evidence");
Directory.CreateDirectory(probeRoot);
if (File.Exists(Path.Combine(probeRoot, "ready.txt")) || File.Exists(Path.Combine(probeRoot, "result.json")))
    throw new ArgumentException("Use a fresh evidence directory for each probe.");
var control = args.Length > 1 && args[1] == "control";
var bmp = args.Length > 1 && args[1] == "bmp";
var fragmented = args.Length > 1 && args[1] == "fragmented";
var navigation = args.Length > 1 && args[1] == "navigation";
var typing = args.Length > 1 && args[1] == "typing";
var payload = control ? "Tiếng Việt 😀 中文" : bmp ? "Tiếng Việt 中文\r\n  if (ready)\r\n\trun();" : "Tiếng Việt 😀\r\n  if (ready)\r\n\trun();";
var normalized = payload.Replace("\r\n", "\n").Normalize();
var expected = control ? payload : navigation ? "type" + normalized + "d " : (typing ? "typed 😀 " : "typed ") + normalized;
File.WriteAllText(Path.Combine(probeRoot, "expected.txt"), expected);
var executable = Environment.ProcessPath!;
using var session = ConPtyTerminalSession.Start(probeRoot, $"\"{executable}\" {(control ? "control-child" : "child")} \"{probeRoot}\"", 100, 30);
// Pin the process handle while the child is alive. Looking it up only after the
// result arrives can lose access to ExitCode when Windows has already reaped it.
using var child = Process.GetProcessById(session.ProcessId);
_ = child.Handle;
var terminalOutput = new StringBuilder();
session.OutputReceived += bytes =>
{
    lock (terminalOutput) terminalOutput.Append(Encoding.UTF8.GetString(bytes));
};
var readyDeadline = Stopwatch.StartNew();
while (!File.Exists(Path.Combine(probeRoot, "ready.txt")) && readyDeadline.Elapsed < TimeSpan.FromSeconds(12))
{
    await Task.Delay(50);
}
if (!File.Exists(Path.Combine(probeRoot, "ready.txt")))
{
    lock (terminalOutput) File.WriteAllText(Path.Combine(probeRoot, "terminal.log"), terminalOutput.ToString());
    Console.Error.WriteLine("The real console child did not reach its ready marker.");
    return 2;
}
if (control) session.Write(Encoding.UTF8.GetBytes(payload + "\r"));
else
{
    if (navigation) session.Resize(72, 22);
    session.Write(Encoding.UTF8.GetBytes(typing ? "typed 😀 " : "typed "));
    await Task.Delay(100);
    if (navigation) session.Write(Encoding.UTF8.GetBytes("\x1b[D\x1b[D"));
    var paste = Encoding.UTF8.GetBytes("\x1b[200~" + payload + "\x1b[201~");
    if (fragmented)
    {
        foreach (var value in paste)
        {
            session.Write([value]);
            await Task.Delay(2);
        }
    }
    else session.Write(paste);
}
var completionDeadline = Stopwatch.StartNew();
while (!File.Exists(Path.Combine(probeRoot, "result.json")) && !File.Exists(Path.Combine(probeRoot, "timeout.json")) && completionDeadline.Elapsed < TimeSpan.FromSeconds(18))
{
    await Task.Delay(50);
}
lock (terminalOutput) File.WriteAllText(Path.Combine(probeRoot, "terminal.log"), terminalOutput.ToString());
if (!File.Exists(Path.Combine(probeRoot, "result.json")))
{
    Console.Error.WriteLine("Bracketed paste did not produce the expected composer text.");
    if (File.Exists(Path.Combine(probeRoot, "timeout.json"))) Console.Error.WriteLine(File.ReadAllText(Path.Combine(probeRoot, "timeout.json")));
    return 3;
}
using var result = JsonDocument.Parse(File.ReadAllText(Path.Combine(probeRoot, "result.json")));
Console.WriteLine(result.RootElement.GetRawText());
try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
catch (TimeoutException) { Console.Error.WriteLine("The console child did not shut down after its completed paste."); return 5; }
if (child.ExitCode != 0) return 4;
return result.RootElement.GetProperty("passed").GetBoolean() ? 0 : 4;
