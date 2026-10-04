param(
    [switch]$ValidateOnly,
    [switch]$RunScripted,
    [string]$ScenarioId = "recover_edit_no_match",
    [string]$ReportPath,
    [string]$FixturePath
)

$ErrorActionPreference = "Stop"
if ($ValidateOnly -and $RunScripted) { throw "Choose either -ValidateOnly or -RunScripted." }
if (-not $RunScripted) { $ValidateOnly = $true }
$repoRoot = Split-Path -Parent $PSScriptRoot
$defaultFixturePath = Join-Path $repoRoot "tests/fixtures/model-working-environment-scenarios.json"
$fixturePath = if ([string]::IsNullOrWhiteSpace($FixturePath)) { $defaultFixturePath } else { [System.IO.Path]::GetFullPath($FixturePath) }
$defaultReportPath = Join-Path $repoRoot "artifacts/model-working-environment/workflow-evaluation.json"
if ([string]::IsNullOrWhiteSpace($ReportPath)) { $ReportPath = $defaultReportPath }
$ReportPath = [System.IO.Path]::GetFullPath($ReportPath)
$fixtureInfo = Get-Item -LiteralPath $fixturePath
if ($fixtureInfo.Length -gt 2097152) { throw "Workflow fixture exceeds the 2 MiB cap." }
$fixture = Get-Content -LiteralPath $fixturePath -Raw | ConvertFrom-Json
$scenarioIds = @($fixture.scenarios | ForEach-Object { $_.id })
if ($scenarioIds.Count -ne 12 -or ($scenarioIds | Select-Object -Unique).Count -ne 12) {
    throw "The workflow fixture must contain exactly twelve unique scenarios."
}

if (-not ("RuWorkflow.BoundedProcessOutput" -as [type])) {
    Add-Type -TypeDefinition @"
using System;
using System.Diagnostics;
using System.Text;

namespace RuWorkflow {
    public sealed class BoundedProcessOutput {
        private readonly object gate = new object();
        private readonly StringBuilder text = new StringBuilder();
        private readonly int limit;
        private bool exceeded;
        public BoundedProcessOutput(int limit) { this.limit = limit; }
        public DataReceivedEventHandler Handler => Capture;
        public bool Exceeded { get { lock (gate) return exceeded; } }
        public string Text { get { lock (gate) return text.ToString(); } }
        private void Capture(object sender, DataReceivedEventArgs args) {
            if (args.Data == null) return;
            lock (gate) {
                int remaining = limit - text.Length;
                if (remaining <= 0) { exceeded = true; return; }
                if (args.Data.Length + Environment.NewLine.Length > remaining) exceeded = true;
                text.Append(args.Data, 0, Math.Min(args.Data.Length, remaining));
                if (text.Length < limit) text.AppendLine();
            }
        }
    }
}
"@
}

function Get-WorkspaceHash($Files) {
    $builder = [System.Text.StringBuilder]::new()
    foreach ($entry in $Files | Sort-Object { $_.path.ToLowerInvariant() }) {
        [void]$builder.Append($entry.path).Append([char]0).Append($entry.content).Append([char]0)
    }

    $bytes = [System.Text.Encoding]::UTF8.GetBytes($builder.ToString())
    return [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
}

function Get-FixtureContent($Entry) {
    switch ($Entry.kind) {
        "repeated_utf8" {
            if ($Entry.count -lt 1 -or $Entry.count -gt 4096 -or [System.Text.Encoding]::UTF8.GetByteCount($Entry.line) * $Entry.count + [System.Text.Encoding]::UTF8.GetByteCount($Entry.suffix) -gt 262144) { throw "Repeated UTF-8 fixture exceeds its bounds." }
            $builder = [System.Text.StringBuilder]::new()
            for ($index = 0; $index -lt $Entry.count; $index++) { [void]$builder.Append($Entry.line) }
            [void]$builder.Append($Entry.suffix)
            return $builder.ToString()
        }
        "numbered_output" {
            if ($Entry.count -lt 1 -or $Entry.count -gt 5000 -or $Entry.sentinels.Count -gt 32 -or [System.Text.Encoding]::UTF8.GetByteCount($Entry.linePrefix) * $Entry.count -gt 262144) { throw "Numbered output fixture exceeds its bounds." }
            $builder = [System.Text.StringBuilder]::new()
            for ($index = 1; $index -le $Entry.count; $index++) {
                $line = if ($null -ne $Entry.sentinels -and $null -ne $Entry.sentinels."$index") { $Entry.sentinels."$index" } else { "$($Entry.linePrefix)$index" }
                [void]$builder.Append($line).Append("`n")
            }
            return $builder.ToString()
        }
        default { return $Entry.content }
    }
}

function Get-ScenarioWorkspaceFiles($Scenario) {
    $files = [System.Collections.Generic.Dictionary[string, object]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $fixture.workspaceFiles) { $files[$entry.path] = [pscustomobject]@{ path = $entry.path; content = $entry.content } }
    foreach ($entry in $Scenario.workspaceOverlay) { $files[$entry.path] = [pscustomobject]@{ path = $entry.path; content = (Get-FixtureContent $entry) } }
    return @($files.Values)
}

function Assert-SafeRelativePath([string]$Path, [string]$Label) {
    if ([string]::IsNullOrWhiteSpace($Path) -or $Path.Contains([char]0) -or [System.IO.Path]::IsPathRooted($Path) -or $Path.Contains(":")) {
        throw "$Label must be a non-empty relative path."
    }

    $segments = $Path -split "[\\/]"
    if ($segments | Where-Object { $_ -in @("", ".", "..") }) {
        throw "$Label contains an unsafe path segment."
    }
}

function Get-DirectoryWorkspaceHash([string]$WorkspacePath) {
    $files = [System.Collections.Generic.List[object]]::new()
    foreach ($item in Get-ChildItem -LiteralPath $WorkspacePath -File -Recurse) {
        $relativePath = [System.IO.Path]::GetRelativePath($WorkspacePath, $item.FullName).Replace([char]92, [char]47)
        $bytes = [System.IO.File]::ReadAllBytes($item.FullName)
        $content = [System.Text.UTF8Encoding]::new($false, $true).GetString($bytes)
        $files.Add([pscustomobject]@{ path = $relativePath; content = $content })
    }

    return Get-WorkspaceHash $files
}

function Get-RemainingSeconds([DateTimeOffset]$Deadline) {
    $remaining = [int][Math]::Ceiling(($Deadline - [DateTimeOffset]::UtcNow).TotalSeconds)
    if ($remaining -le 0) { throw "case timeout: the bounded workflow deadline expired." }
    return $remaining
}

function Enable-BoundedProcessOutput([System.Diagnostics.Process]$Process, [int]$StdoutLimit = 262144, [int]$StderrLimit = 65536) {
    $stdout = [RuWorkflow.BoundedProcessOutput]::new($StdoutLimit)
    $stderr = [RuWorkflow.BoundedProcessOutput]::new($StderrLimit)
    $Process.add_OutputDataReceived($stdout.Handler)
    $Process.add_ErrorDataReceived($stderr.Handler)
    $Process.BeginOutputReadLine()
    $Process.BeginErrorReadLine()
    return [pscustomobject]@{ stdout = $stdout; stderr = $stderr }
}

function Test-ScenarioFiles($Scenario, [string]$WorkspacePath, $WorkspaceFiles) {
    $expectedChanged = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $Scenario.expectedFiles) {
        $expectedPath = Join-Path $WorkspacePath $entry.path
        if (-not (Test-Path -LiteralPath $expectedPath -PathType Leaf)) { return $false }
        $actualContent = [System.IO.File]::ReadAllText($expectedPath, [System.Text.UTF8Encoding]::new($false, $true))
        if ($entry.state -in @("changed_to_expected", "changed_only_after_observed_failure")) {
            if ($actualContent -cne $entry.content) { return $false }
            [void]$expectedChanged.Add($entry.path.Replace([char]92, [char]47))
            continue
        }

        $initial = $WorkspaceFiles | Where-Object path -eq $entry.path | Select-Object -First 1
        if ($null -eq $initial -or $actualContent -cne $initial.content) { return $false }
    }

    $actualPaths = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($item in Get-ChildItem -LiteralPath $WorkspacePath -File -Recurse) {
        [void]$actualPaths.Add([System.IO.Path]::GetRelativePath($WorkspacePath, $item.FullName).Replace([char]92, [char]47))
    }
    if ($actualPaths.Count -ne $WorkspaceFiles.Count) { return $false }

    foreach ($initial in $WorkspaceFiles) {
        $expected = $Scenario.expectedFiles | Where-Object path -eq $initial.path | Select-Object -First 1
        $expectedContent = if ($expected.state -in @("changed_to_expected", "changed_only_after_observed_failure")) { $expected.content } else { $initial.content }
        $actualPath = Join-Path $WorkspacePath $initial.path
        if (-not (Test-Path -LiteralPath $actualPath -PathType Leaf)) { return $false }
        $actualContent = [System.IO.File]::ReadAllText($actualPath, [System.Text.UTF8Encoding]::new($false, $true))
        if ($actualContent -cne $expectedContent) { return $false }
    }

    $changedCount = @($Scenario.expectedFiles | Where-Object state -in @("changed_to_expected", "changed_only_after_observed_failure")).Count
    return $expectedChanged.Count -eq $changedCount
}

$seenBasePaths = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
foreach ($entry in $fixture.workspaceFiles) {
    Assert-SafeRelativePath $entry.path "Workspace path"
    if (-not $seenBasePaths.Add($entry.path)) { throw "Base workspace path '$($entry.path)' is duplicated." }
    if ([System.Text.Encoding]::UTF8.GetByteCount($entry.content) -gt 262144) { throw "Base workspace file '$($entry.path)' exceeds 256 KiB." }
}
$workspaceHash = Get-WorkspaceHash $fixture.workspaceFiles
foreach ($scenario in $fixture.scenarios) {
    if ($scenario.workspaceOverlay.Count -gt 16) { throw "Scenario '$($scenario.id)' exceeds the workspace overlay cap." }
    $seenOverlayPaths = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $scenario.workspaceOverlay) {
        Assert-SafeRelativePath $entry.path "Workspace overlay path"
        if (-not $seenOverlayPaths.Add($entry.path)) { throw "Scenario '$($scenario.id)' repeats workspace overlay path '$($entry.path)'." }
        if ($entry.kind -eq "literal" -and [System.Text.Encoding]::UTF8.GetByteCount($entry.content) -gt 262144) { throw "Workspace overlay '$($entry.path)' exceeds 256 KiB." }
        if ($entry.kind -notin @("literal", "repeated_utf8", "numbered_output")) { throw "Scenario '$($scenario.id)' has an unknown workspace overlay kind." }
    }
    $scenarioFiles = Get-ScenarioWorkspaceFiles $scenario
    $scenarioHash = Get-WorkspaceHash $scenarioFiles
    if ($scenario.workspaceSha256 -ne $scenarioHash) {
        throw "Workspace hash mismatch for scenario '$($scenario.id)'."
    }
    if ([string]::IsNullOrWhiteSpace($scenario.input) -or [System.Text.Encoding]::UTF8.GetByteCount($scenario.input) -gt 8192 -or $scenario.expectedFiles.Count -eq 0 -or $scenario.expectedFiles.Count -gt 32 -or $scenario.assertions.Count -eq 0 -or $scenario.assertions.Count -gt 32) {
        throw "Scenario '$($scenario.id)' is missing its input or oracle."
    }
    if ($scenario.preset -notin @("plan", "standard")) {
        throw "Scenario '$($scenario.id)' has a preset outside the approved offline baseline."
    }
    if ($scenario.turns.Count -gt 32) { throw "Scenario '$($scenario.id)' exceeds the conversation turn cap." }
    foreach ($turn in $scenario.turns) {
        if ([System.Text.Encoding]::UTF8.GetByteCount($turn) -gt 8192) { throw "Scenario '$($scenario.id)' has an oversized turn." }
    }
    if ($scenarioFiles.Count -eq 0 -or $scenarioFiles.Count -gt 64) { throw "Scenario '$($scenario.id)' exceeds the workspace file cap." }
    $seenWorkspacePaths = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    $workspaceBytes = 0
    foreach ($entry in $scenarioFiles) {
        Assert-SafeRelativePath $entry.path "Workspace path"
        if (-not $seenWorkspacePaths.Add($entry.path)) { throw "Workspace path '$($entry.path)' is duplicated." }
        $fileBytes = [System.Text.Encoding]::UTF8.GetByteCount($entry.content)
        if ($fileBytes -gt 262144) { throw "Workspace file '$($entry.path)' exceeds 256 KiB." }
        $workspaceBytes += $fileBytes
    }
    if ($workspaceBytes -gt 1048576) {
        throw "The scenario workspace is empty or exceeds 1 MiB."
    }
    $seenExpectedPaths = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $scenario.expectedFiles) {
        Assert-SafeRelativePath $entry.path "Expected file path"
        if (-not $seenExpectedPaths.Add($entry.path)) { throw "Scenario '$($scenario.id)' repeats expected path '$($entry.path)'." }
        if ($entry.state -in @("changed_to_expected", "changed_only_after_observed_failure") -and ([string]::IsNullOrEmpty($entry.content) -or [string]::IsNullOrWhiteSpace($entry.sha256))) { throw "Changed expected file '$($entry.path)' needs exact content and SHA-256." }
        if ($entry.state -notin @("unchanged", "unchanged_until_constraint_is_recalled", "changed_to_expected", "changed_only_after_observed_failure")) { throw "Scenario '$($scenario.id)' has an unknown file oracle state." }
        if ($entry.state -in @("changed_to_expected", "changed_only_after_observed_failure")) {
            $expectedHash = [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData([System.Text.Encoding]::UTF8.GetBytes($entry.content))).ToLowerInvariant()
            if ($expectedHash -ne $entry.sha256) { throw "Expected content hash mismatch for '$($entry.path)'." }
        }
        if ($null -ne $entry.content -and [System.Text.Encoding]::UTF8.GetByteCount($entry.content) -gt 262144) { throw "Expected file '$($entry.path)' exceeds 256 KiB." }
        if (-not $seenWorkspacePaths.Contains($entry.path)) { throw "Expected path '$($entry.path)' is absent from the baseline workspace." }
        if ($entry.state -eq "unchanged" -and $null -ne $entry.content) {
            $initial = $scenarioFiles | Where-Object path -eq $entry.path | Select-Object -First 1
            if ($initial.content -cne $entry.content) { throw "Unchanged-file oracle does not match '$($entry.path)' baseline content." }
        }
    }
}

function New-ScenarioWorkspace($Scenario, [string]$ScenarioRoot) {
    $dataRoot = Join-Path $ScenarioRoot "data"
    $workspace = Join-Path $ScenarioRoot "workspace"
    $scriptPath = Join-Path $ScenarioRoot "scripted-model.json"
    New-Item -ItemType Directory -Path $dataRoot, $workspace | Out-Null
    $files = Get-ScenarioWorkspaceFiles $Scenario
    foreach ($file in $files) {
        $filePath = Join-Path $workspace $file.path
        New-Item -ItemType Directory -Path (Split-Path -Parent $filePath) -Force | Out-Null
        [System.IO.File]::WriteAllText($filePath, $file.content, [System.Text.UTF8Encoding]::new($false))
    }
    if ((Get-DirectoryWorkspaceHash $workspace) -ne $Scenario.workspaceSha256) { throw "Temporary workspace does not match its oracle hash." }
    $Scenario.scriptedSteps | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $scriptPath -Encoding utf8
    return [pscustomobject]@{ dataRoot = $dataRoot; workspace = $workspace; scriptPath = $scriptPath; files = $files }
}

function Start-ScenarioHost($ScenarioRoot, $ScenarioWorkspace, [DateTimeOffset]$Deadline) {
    $port = Get-FreePort
    $baseUrl = "http://127.0.0.1:$port"
    $token = [Convert]::ToHexString([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(32)).ToLowerInvariant()
    $start = New-Object System.Diagnostics.ProcessStartInfo
    $start.FileName = Join-Path $repoRoot "backend-dotnet/RuBackend/bin/Debug/net10.0/RuBackend.exe"
    $start.WorkingDirectory = $repoRoot
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in @("--urls", $baseUrl, "--managed-runtime")) { [void]$start.ArgumentList.Add($argument) }
    $start.Environment["RU_DATA_ROOT"] = $ScenarioWorkspace.dataRoot
    $start.Environment["RU_PROJECT_ROOT"] = $repoRoot
    $start.Environment["RU_SCRIPTED_MODEL"] = $ScenarioWorkspace.scriptPath
    $start.Environment["RU_DESKTOP_TOKEN"] = $token
    $start.Environment["RU_LOG_ROOT"] = (Join-Path $ScenarioRoot "logs")
    $start.Environment["RU_AGENT_BROKER_EXE"] = (Join-Path $repoRoot "backend-dotnet/RuAgent.Broker/bin/Debug/net10.0/RuAgent.Broker.exe")
    $start.Environment["RU_AGENT_BROKER_PROJECT_ROOT"] = $repoRoot
    $process = [System.Diagnostics.Process]::Start($start)
    try {
        $capture = Enable-BoundedProcessOutput $process 16384 32768
        Wait-HostReady $process $ScenarioWorkspace.dataRoot $baseUrl $token (Get-RemainingSeconds $Deadline)
        return [pscustomobject]@{ process = $process; capture = $capture; baseUrl = $baseUrl; token = $token }
    } catch {
        Stop-ProcessTree $process
        throw
    }
}

function Invoke-ScenarioCli($Scenario, $ScenarioWorkspace, $HostSession, [DateTimeOffset]$Deadline) {
    $start = New-Object System.Diagnostics.ProcessStartInfo
    $start.FileName = Join-Path $repoRoot "backend-dotnet/RuAgent.Cli/bin/Debug/net10.0-windows/RuAgent.Cli.exe"
    $start.WorkingDirectory = $repoRoot
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in @("agent", "run", "--preset", $Scenario.preset, "--workspace", $ScenarioWorkspace.workspace, "--json", "--trust-workspace", $Scenario.input)) { [void]$start.ArgumentList.Add($argument) }
    $start.Environment["RU_DATA_ROOT"] = $ScenarioWorkspace.dataRoot
    $start.Environment["RU_PROJECT_ROOT"] = $repoRoot
    $start.Environment["RU_SCRIPTED_MODEL"] = $ScenarioWorkspace.scriptPath
    $start.Environment["RU_DESKTOP_TOKEN"] = $HostSession.token
    $process = [System.Diagnostics.Process]::Start($start)
    try {
        $capture = Enable-BoundedProcessOutput $process
        if (-not $process.WaitForExit((Get-RemainingSeconds $Deadline) * 1000)) { throw "case timeout: CLI exceeded the workflow deadline." }
        $process.WaitForExit()
        if ($capture.stdout.Exceeded -or $capture.stderr.Exceeded) { throw "CLI output exceeded its collection bounds." }
        if ($process.ExitCode -ne 0) { throw "CLI exited $($process.ExitCode): $($capture.stderr.Text)" }
        $events = @($capture.stdout.Text -split "`r?`n" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object { $_ | ConvertFrom-Json })
        return [pscustomobject]@{ process = $process; events = $events }
    } catch {
        Stop-ProcessTree $process
        throw
    }
}

function Test-ScriptedEditToolCycle($Scenario, $ScenarioWorkspace, $Events) {
    $toolEvents = @($Events | Where-Object event_type -eq "tool.execution_completed")
    $failedEvents = @($toolEvents | Where-Object safe_summary -eq "edit_no_match")
    $successfulEvents = @($toolEvents | Where-Object safe_summary -in @("ok", "succeeded"))
    $completedEvents = @($Events | Where-Object { $_.event_type -eq "run.state_changed" -and $_.status -eq "completed" })
    $filesMatch = Test-ScenarioFiles $Scenario $ScenarioWorkspace.workspace $ScenarioWorkspace.files
    if ($failedEvents.Count -lt 1 -or $successfulEvents.Count -lt 1 -or $completedEvents.Count -lt 1 -or -not $filesMatch) {
        $eventSummary = @($Events | ForEach-Object { "$($_.event_type):$($_.status):$($_.safe_summary)" }) -join ","
        throw "Oracle failure: failed_tool=$($failedEvents.Count), successful_tool=$($successfulEvents.Count), completed=$($completedEvents.Count), files_match=$filesMatch, events=$eventSummary."
    }
    return [pscustomobject]@{
        tool_calls = $toolEvents.Count
        failed_tool_calls = $failedEvents.Count
        approval_requests = @($Events | Where-Object event_type -eq "approval.requested").Count
        model_steps = @($Events | Where-Object event_type -eq "model.step_started").Count
    }
}

function Invoke-ScriptedScenario($Scenario, $BuildEvidence) {
    $scenarioRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("ru-workflow-" + [guid]::NewGuid().ToString("N"))
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(150)
    $duration = [System.Diagnostics.Stopwatch]::StartNew()
    $outcome = "environment_failure"
    $failure = $null
    $managedHost = $null
    $cli = $null
    $metrics = $null
    try {
        $scenarioWorkspace = New-ScenarioWorkspace $Scenario $scenarioRoot
        $BuildEvidence.Add((Invoke-DotnetBuild (Join-Path $repoRoot "backend-dotnet/RuBackend/RuBackend.csproj") $deadline))
        $BuildEvidence.Add((Invoke-DotnetBuild (Join-Path $repoRoot "backend-dotnet/RuAgent.Cli/RuAgent.Cli.csproj") $deadline))
        $managedHost = Start-ScenarioHost $scenarioRoot $scenarioWorkspace $deadline
        $cli = Invoke-ScenarioCli $Scenario $scenarioWorkspace $managedHost $deadline
        $metrics = Test-ScriptedEditToolCycle $Scenario $scenarioWorkspace $cli.events
        $outcome = "scripted_wiring_pass"
    } catch {
        $failure = $_.Exception.Message
        if ($failure.StartsWith("Oracle failure:", [System.StringComparison]::Ordinal)) { $outcome = "oracle_failure" }
        if ($failure.StartsWith("case timeout:", [System.StringComparison]::Ordinal)) { $outcome = "case_timeout" }
    } finally {
        $duration.Stop()
        if ($null -ne $cli -and -not $cli.process.HasExited) { Stop-ProcessTree $cli.process }
        if ($null -ne $managedHost) { Stop-ProcessTree $managedHost.process }
        Remove-ScenarioRoot $scenarioRoot
    }
    return [pscustomobject]@{
        scenario_id = $Scenario.id
        status = $outcome
        quality_status = "not_measured"
        preset = $Scenario.preset
        workspace_sha256 = $Scenario.workspaceSha256
        duration_ms = [int]$duration.ElapsedMilliseconds
        tool_calls = if ($null -ne $metrics) { $metrics.tool_calls } else { $null }
        failed_tool_calls = if ($null -ne $metrics) { $metrics.failed_tool_calls } else { $null }
        approval_requests = if ($null -ne $metrics) { $metrics.approval_requests } else { $null }
        model_steps = if ($null -ne $metrics) { $metrics.model_steps } else { $null }
        tokens = $null
        cost = $null
        failure = $failure
        invocation_contract = "RuAgent.Cli agent run --preset <fixture-preset> --workspace <temporary-workspace> --json --trust-workspace <fixture-input>"
    }
}

function Invoke-DotnetBuild([string]$ProjectPath, [DateTimeOffset]$Deadline) {
    $start = New-Object System.Diagnostics.ProcessStartInfo
    $start.FileName = "dotnet"
    $start.WorkingDirectory = $repoRoot
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in @("build", $ProjectPath, "-c", "Debug", "--nologo")) { [void]$start.ArgumentList.Add($argument) }
    $process = [System.Diagnostics.Process]::Start($start)
    $capture = Enable-BoundedProcessOutput $process
    if (-not $process.WaitForExit((Get-RemainingSeconds $Deadline) * 1000)) {
        $process.Kill($true)
        throw "case timeout: dotnet build exceeded the workflow deadline for '$ProjectPath'."
    }
    $process.WaitForExit()
    if ($capture.stdout.Exceeded -or $capture.stderr.Exceeded) { throw "dotnet build output exceeded its collection bounds." }
    if ($process.ExitCode -ne 0) { throw "dotnet build failed with exit code $($process.ExitCode) for '$ProjectPath'. $($capture.stderr.Text)" }
    return [pscustomobject]@{ command = "dotnet build $ProjectPath -c Debug --nologo"; exit_code = $process.ExitCode; stdout = $capture.stdout.Text; stderr = $capture.stderr.Text }
}

function Stop-ProcessTree([System.Diagnostics.Process]$Process) {
    try {
        if (-not $Process.HasExited) { $Process.Kill($true); [void]$Process.WaitForExit(5000) }
    } catch { }
    try { $Process.Dispose() } catch { }
}

function Remove-ScenarioRoot([string]$ScenarioRoot) {
    if (-not (Test-Path -LiteralPath $ScenarioRoot)) { return }
    $tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    $resolved = [System.IO.Path]::GetFullPath($ScenarioRoot)
    if (-not $resolved.StartsWith($tempRoot, [System.StringComparison]::OrdinalIgnoreCase)) { throw "Refusing cleanup outside the temporary root." }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}

function Get-FreePort {
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $listener.Start()
    try { return ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port }
    finally { $listener.Stop() }
}

function Wait-HostReady($Process, [string]$DataRoot, [string]$BaseUrl, [string]$Token, [int]$TimeoutSeconds) {
    $descriptorPath = Join-Path $DataRoot "runtime_descriptor.json"
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        if ($Process.HasExited) { throw "Managed Host exited during isolated startup with code $($Process.ExitCode)." }
        if (Test-Path -LiteralPath $descriptorPath) {
            $client = [System.Net.Http.HttpClient]::new()
            $client.Timeout = [TimeSpan]::FromSeconds(2)
            $request = $null
            $response = $null
            try {
                $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Get, "$BaseUrl/status")
                $request.Headers.Authorization = [System.Net.Http.Headers.AuthenticationHeaderValue]::new("Bearer", $Token)
                $response = $client.Send($request)
                if ($response.IsSuccessStatusCode) { return }
            } catch { }
            finally {
                if ($null -ne $response) { $response.Dispose() }
                if ($null -ne $request) { $request.Dispose() }
                $client.Dispose()
            }
        }
        Start-Sleep -Milliseconds 200
    }
    throw "Managed Host did not become ready within $TimeoutSeconds seconds."
}

$results = [System.Collections.Generic.List[object]]::new()
$buildEvidence = [System.Collections.Generic.List[object]]::new()
$results.Add([pscustomobject]@{
    status = "validated"
    scenarios = $fixture.scenarios.Count
    workspace_sha256 = $workspaceHash
    providers = "not_used"
    credential_access = "none"
    live_evaluation = "not_run"
})

if ($RunScripted) {
    $scenario = $fixture.scenarios | Where-Object id -eq $ScenarioId | Select-Object -First 1
    if ($null -eq $scenario) { throw "Unknown scenario id '$ScenarioId'." }
    if ($scenario.baselineStatus -eq "unmet") { throw "Scenario '$ScenarioId' is an unmet capability and cannot be run as a pass." }
    if ($scenario.runnerKind -ne "scripted_edit_tool_cycle" -or $null -eq $scenario.scriptedSteps) { throw "Scenario '$ScenarioId' has no registered deterministic executor." }
    $results.Add((Invoke-ScriptedScenario $scenario $buildEvidence))
}

$report = [ordered]@{
    schema_version = 1
    created_utc = [DateTimeOffset]::UtcNow.ToString("O")
    source = "tests/fixtures/model-working-environment-scenarios.json"
    workspace_sha256 = $workspaceHash
    scripted_provider = if ($RunScripted) { "used_for_wiring_only" } else { "not_run" }
    live_provider = "not_run"
    provider = $null
    model = $null
    protocol = $null
    task_quality_pass_rate = $null
    forgotten_constraints = $null
    repeated_calls = $null
    approval_requests = $null
    model_steps = $null
    latency_ms = $null
    input_tokens = $null
    output_tokens = $null
    cost = $null
    scenarios = @($fixture.scenarios | ForEach-Object {
        [pscustomobject]@{
            id = $_.id
            capability = $_.capabilityAtBaseline
            baseline_status = $_.baselineStatus
            workspace_sha256 = $_.workspaceSha256
            quality_status = "not_measured"
        }
    })
    runs = @($results)
    builds = @($buildEvidence)
}

$reportDirectory = Split-Path -Parent $ReportPath
New-Item -ItemType Directory -Path $reportDirectory -Force | Out-Null
$report | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $ReportPath -Encoding utf8
Write-Output "workflow evaluation: $($fixture.scenarios.Count) oracles validated; live provider not run; report $ReportPath"
foreach ($result in $results) {
    Write-Output ("workflow evaluation: {0}" -f ($result | ConvertTo-Json -Compress -Depth 10))
}
if ($results | Where-Object { $_.status -notin @("validated", "scripted_wiring_pass") }) {
    throw "Workflow evaluation failed. Read the bounded per-case result in '$ReportPath'."
}
