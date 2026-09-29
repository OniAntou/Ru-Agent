# End-to-end smoke for `ru agent rpc`: a real host, a scripted model and the
# stdio JSON-RPC bridge over redirected pipes.
#
# Usage: .\scripts\rpc_smoke.ps1
#
# The bridge owns stdin/stdout for its whole life, so the two phases of a run
# cannot be two CLI invocations. This script starts the bridge once, reads the
# session id, starts the run, subscribes with run.attach, streams run.event
# notifications until a terminal status, and only then closes stdin. A non-JSON
# line on stdout, a timeout or a non-zero exit is a failure.
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$work = Join-Path $env:TEMP ("ru-rpc-smoke-" + [guid]::NewGuid().ToString("N"))
$dataRoot = Join-Path $work "data"
$hostStdoutPath = Join-Path $work "host.stdout.log"
$hostStderrPath = Join-Path $work "host.stderr.log"
$backendExe = Join-Path $repoRoot "backend-dotnet/RuBackend/bin/Debug/net10.0/RuBackend.exe"
$cliExe = Join-Path $repoRoot "backend-dotnet/RuAgent.Cli/bin/Debug/net10.0/RuAgent.Cli.exe"
$fixtureSource = Join-Path $repoRoot "docs/operations/fixtures/rpc-smoke-scripted.json"
$baseUrl = "http://127.0.0.1:8000"
$publicPort = 8000
$token = "smoke-bearer"
$environmentNames = @("RU_DATA_ROOT", "RU_SCRIPTED_MODEL", "RU_DESKTOP_TOKEN", "RU_PROJECT_ROOT")

# Captured before anything can fail so the finally block always restores exactly
# the caller's environment, never a partially-set one.
$previousEnvironment = @{}
foreach ($name in $environmentNames) {
    $previousEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, "Process")
}

$hostProcess = $null
$bridgeProcess = $null
$bridgeInput = $null
$bridgeOutput = $null
$bridgeStderrTask = $null
$transcript = New-Object System.Collections.Generic.List[string]
$runEventCount = 0
$terminalEvent = $null
$lastRunStatus = "(none)"
$status = 1

function Write-Smoke([string]$Message) {
    Write-Host "rpc smoke: $Message"
}

function Get-TextTail([string]$Path, [int]$MaxCharacters = 2000) {
    if (-not (Test-Path -LiteralPath $Path)) {
        return "(no file)"
    }

    try {
        $text = Get-Content -LiteralPath $Path -Raw -ErrorAction Stop
        if ([string]::IsNullOrEmpty($text)) {
            return "(empty)"
        }

        if ($text.Length -le $MaxCharacters) {
            return $text
        }

        return $text.Substring($text.Length - $MaxCharacters)
    } catch {
        return "(unreadable: $($_.Exception.Message))"
    }
}

function Get-BridgeStderr([int]$WaitMilliseconds = 2000) {
    if ($null -eq $bridgeStderrTask) {
        return "(not captured)"
    }

    try {
        # ReadToEndAsync only completes at stderr EOF. The timeout branch runs while
        # the bridge is still alive, so the wait is bounded: diagnostics must never
        # be the reason the FAIL path hangs.
        if (-not $bridgeStderrTask.Wait($WaitMilliseconds)) {
            return "(stderr still open)"
        }

        $text = $bridgeStderrTask.Result
        if ([string]::IsNullOrWhiteSpace($text)) {
            return "(empty)"
        }

        if ($text.Length -le 2000) {
            return $text
        }

        return $text.Substring($text.Length - 2000)
    } catch {
        return "(unreadable: $($_.Exception.Message))"
    }
}

function Stop-ProcessTree([System.Diagnostics.Process]$Process) {
    if ($null -eq $Process) {
        return
    }

    try {
        if (-not $Process.HasExited) {
            & taskkill /PID $Process.Id /T /F 2>$null | Out-Null
        }
    } catch {
    }

    try {
        if (-not $Process.HasExited) {
            $Process.Kill()
        }
    } catch {
    }

    try { [void]$Process.WaitForExit(5000) } catch { }
    try { $Process.Dispose() } catch { }
}

function Assert-SmokePortFree([int]$Port) {
    $listener = New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Loopback, $Port)
    try {
        $listener.Start()
    } catch {
        $owner = "an unidentified process"
        try {
            $connection = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction Stop | Select-Object -First 1
            if ($connection) {
                $process = Get-Process -Id $connection.OwningProcess -ErrorAction SilentlyContinue
                $owner = "PID $($connection.OwningProcess) ($($process.ProcessName))"
            }
        } catch {
        }

        throw "port $Port is already in use by $owner. This smoke never kills a host it did not start; stop that process and rerun."
    } finally {
        try { $listener.Stop() } catch { }
    }
}

function Wait-HostReady([System.Diagnostics.Process]$HostProcess, [string]$Url, [string]$Bearer, [int]$TimeoutSeconds) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if ($HostProcess.HasExited) {
            throw "host exited with code $($HostProcess.ExitCode) before it served HTTP."
        }

        $client = New-Object System.Net.Http.HttpClient
        try {
            $client.Timeout = [TimeSpan]::FromSeconds(2)
            $request = New-Object System.Net.Http.HttpRequestMessage -ArgumentList ([System.Net.Http.HttpMethod]::Get), ($Url + "/settings")
            try {
                $request.Headers.Authorization = New-Object System.Net.Http.Headers.AuthenticationHeaderValue -ArgumentList "Bearer", $Bearer
                $response = $client.SendAsync($request).GetAwaiter().GetResult()
                $code = [int]$response.StatusCode
                $response.Dispose()
                if ($code -ge 200 -and $code -lt 300) {
                    return
                }
            } catch {
            } finally {
                $request.Dispose()
            }
        } finally {
            $client.Dispose()
        }

        Start-Sleep -Milliseconds 250
    }

    throw "host did not answer $Url/settings within $TimeoutSeconds s."
}

function New-JsonRpcRequest([int]$Id, [string]$Method, $Params) {
    return (@{ jsonrpc = "2.0"; id = $Id; method = $Method; params = $Params } | ConvertTo-Json -Depth 8 -Compress)
}

function Send-BridgeLine([string]$Line, [string]$Context) {
    try {
        $bridgeInput.WriteLine($Line)
        $bridgeInput.Flush()
    } catch {
        throw "could not write the $Context request to the bridge: $($_.Exception.Message)"
    }
}

function Read-BridgeMessage([int]$TimeoutMilliseconds, [string]$Context) {
    $task = $bridgeOutput.ReadLineAsync()
    if (-not $task.Wait($TimeoutMilliseconds)) {
        throw "timed out after $TimeoutMilliseconds ms waiting for $Context (last run status: $lastRunStatus)."
    }

    $line = $task.Result
    if ($null -eq $line) {
        throw "the bridge closed stdout while waiting for $Context."
    }

    try {
        $message = $line | ConvertFrom-Json
    } catch {
        throw "the bridge wrote a non-JSON stdout line while waiting for ${Context}: $line"
    }

    $script:transcript.Add($line)
    if ($message.method -eq "run.event") {
        $script:runEventCount++
        $script:lastRunStatus = [string]$message.params.status
        if ($null -eq $script:terminalEvent -and @("completed", "failed", "cancelled", "interrupted") -contains $script:lastRunStatus) {
            $script:terminalEvent = $message
        }
    }

    return $message
}

function Read-RpcResponse([int]$Id, [int]$TimeoutMilliseconds, [string]$Context) {
    $deadline = (Get-Date).AddMilliseconds($TimeoutMilliseconds)
    while ($true) {
        $remaining = [int]($deadline - (Get-Date)).TotalMilliseconds
        if ($remaining -lt 1) {
            throw "timed out waiting for the $Context response (last run status: $lastRunStatus)."
        }

        $message = Read-BridgeMessage -TimeoutMilliseconds $remaining -Context "$Context response"
        if ($null -ne $message.error) {
            throw "$Context failed: $($message.error | ConvertTo-Json -Compress)"
        }

        if ($null -ne $message.id -and [int]$message.id -eq $Id) {
            return $message
        }
    }
}

function Invoke-DotnetBuild([string]$ProjectPath) {
    Write-Smoke "building $(Split-Path -Leaf $ProjectPath) (Debug)..."
    & dotnet build $ProjectPath -c Debug --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed with exit code $LASTEXITCODE for $ProjectPath. Install the .NET 10 SDK, then run: dotnet build `"$ProjectPath`" -c Debug"
    }
}

try {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw "dotnet was not found on PATH. Install the .NET 10 SDK, then rerun this smoke."
    }

    if (-not (Test-Path -LiteralPath $fixtureSource)) {
        throw "scripted-model fixture is missing: $fixtureSource"
    }

    New-Item -ItemType Directory -Path $dataRoot -Force | Out-Null
    $scriptPath = Join-Path $work "scripted.json"
    Copy-Item -LiteralPath $fixtureSource -Destination $scriptPath -Force

    $scriptedRaw = Get-Content -LiteralPath $scriptPath -Raw
    if ([string]::IsNullOrWhiteSpace($scriptedRaw) -or -not $scriptedRaw.TrimStart().StartsWith("[", [System.StringComparison]::Ordinal)) {
        throw "the scripted-model fixture must be a JSON array of steps."
    }

    try {
        # @() forces array semantics on both hosts: PowerShell 5.1 enumerates a
        # JSON array into the pipeline, PowerShell 7 unwraps a one-element array
        # to a bare object, and the count check below has to read the same either way.
        $scripted = @($scriptedRaw | ConvertFrom-Json)
    } catch {
        throw "the scripted-model fixture is not valid JSON: $($_.Exception.Message)"
    }

    if ($scripted.Count -lt 1) {
        throw "the scripted-model fixture must be a non-empty JSON array of steps."
    }

    Invoke-DotnetBuild (Join-Path $repoRoot "backend-dotnet/RuAgent.Cli/RuAgent.Cli.csproj")
    Invoke-DotnetBuild (Join-Path $repoRoot "backend-dotnet/RuBackend/RuBackend.csproj")

    Assert-SmokePortFree -Port $publicPort

    $env:RU_DATA_ROOT = $dataRoot
    $env:RU_SCRIPTED_MODEL = $scriptPath
    $env:RU_DESKTOP_TOKEN = $token
    $env:RU_PROJECT_ROOT = $repoRoot

    Write-Smoke "starting RuBackend on $baseUrl (data root: $dataRoot)..."
    $hostProcess = Start-Process -FilePath $backendExe `
        -ArgumentList "--managed-runtime", "--urls", $baseUrl `
        -WorkingDirectory $repoRoot -PassThru -WindowStyle Hidden `
        -RedirectStandardOutput $hostStdoutPath -RedirectStandardError $hostStderrPath

    $descriptorPath = Join-Path $dataRoot "runtime_descriptor.json"
    $deadline = (Get-Date).AddSeconds(30)
    while (-not (Test-Path -LiteralPath $descriptorPath)) {
        if ($hostProcess.HasExited) {
            throw "host exited with code $($hostProcess.ExitCode) before publishing runtime_descriptor.json. stderr: $(Get-TextTail $hostStderrPath)"
        }

        if ((Get-Date) -ge $deadline) {
            throw "host did not publish runtime_descriptor.json within 30 s. stderr: $(Get-TextTail $hostStderrPath)"
        }

        Start-Sleep -Milliseconds 200
    }

    # The descriptor is written before Kestrel binds, so a TCP/HTTP-ready host is
    # a prerequisite for the bridge's own host locator to attach instead of starting
    # a competing host.
    Add-Type -AssemblyName System.Net.Http -ErrorAction SilentlyContinue
    Wait-HostReady -HostProcess $hostProcess -Url $baseUrl -Bearer $token -TimeoutSeconds 30
    Write-Smoke "host ready; starting RuAgent.Cli agent rpc..."

    $bridgeStartInfo = New-Object System.Diagnostics.ProcessStartInfo
    $bridgeStartInfo.FileName = $cliExe
    $bridgeStartInfo.Arguments = "agent rpc"
    $bridgeStartInfo.WorkingDirectory = $repoRoot
    $bridgeStartInfo.UseShellExecute = $false
    $bridgeStartInfo.CreateNoWindow = $true
    $bridgeStartInfo.RedirectStandardInput = $true
    $bridgeStartInfo.RedirectStandardOutput = $true
    $bridgeStartInfo.RedirectStandardError = $true
    $bridgeProcess = New-Object System.Diagnostics.Process
    $bridgeProcess.StartInfo = $bridgeStartInfo
    if (-not $bridgeProcess.Start()) {
        throw "failed to start the rpc bridge process."
    }

    $bridgeInput = $bridgeProcess.StandardInput
    $bridgeInput.NewLine = "`n"
    $bridgeOutput = $bridgeProcess.StandardOutput
    $bridgeStderrTask = $bridgeProcess.StandardError.ReadToEndAsync()

    # Phase 1: initialize, then session.create.
    Send-BridgeLine (New-JsonRpcRequest -Id 1 -Method "initialize" -Params @{ protocolVersion = 1 }) "initialize"
    $initialize = Read-RpcResponse -Id 1 -TimeoutMilliseconds 30000 -Context "initialize"
    if ([int]$initialize.result.protocolVersion -ne 1) {
        throw "initialize returned protocolVersion '$($initialize.result.protocolVersion)'; expected 1."
    }

    Send-BridgeLine (New-JsonRpcRequest -Id 2 -Method "session.create" -Params @{}) "session.create"
    $sessionCreate = Read-RpcResponse -Id 2 -TimeoutMilliseconds 30000 -Context "session.create"
    $sessionId = [string]$sessionCreate.result.session_id
    if ([string]::IsNullOrWhiteSpace($sessionId)) {
        throw "session.create returned no session_id."
    }

    Write-Smoke "session created: $sessionId"

    # Phase 2: run.start binds the run to that session, run.attach subscribes the
    # bridge to its durable events. The same stdin stays open while events stream.
    $runStartParams = [ordered]@{
        workspace = $repoRoot
        prompt = "RPC smoke: reply with one short line."
        preset = "standard"
        sessionId = $sessionId
        trustWorkspace = $true
    }
    Send-BridgeLine (New-JsonRpcRequest -Id 3 -Method "run.start" -Params $runStartParams) "run.start"
    $runStart = Read-RpcResponse -Id 3 -TimeoutMilliseconds 30000 -Context "run.start"
    $runId = [string]$runStart.result.run_id
    if ($runId -notmatch "^run_[0-9a-f]{32}$") {
        throw "run.start returned an invalid run_id '$runId'."
    }

    Write-Smoke "run started: $runId (status $($runStart.result.status))"

    Send-BridgeLine (New-JsonRpcRequest -Id 4 -Method "run.attach" -Params @{ runId = $runId; afterSequence = 0 }) "run.attach"
    $null = Read-RpcResponse -Id 4 -TimeoutMilliseconds 30000 -Context "run.attach"

    $deadline = (Get-Date).AddSeconds(60)
    while ($null -eq $terminalEvent) {
        $remaining = [int]($deadline - (Get-Date)).TotalMilliseconds
        if ($remaining -lt 1) {
            # The bridge is still alive here with stdin open. Closing stdin lets it
            # shut down so the bounded stderr wait can drain instead of blocking on
            # a pipe that stays open for the process's whole life.
            try { $bridgeInput.Close() } catch { }
            throw "timed out after 60 s waiting for a terminal run.event (last run status: $lastRunStatus). stderr: $(Get-BridgeStderr)"
        }

        $null = Read-BridgeMessage -TimeoutMilliseconds $remaining -Context "run events"
    }

    if ($runEventCount -lt 1) {
        throw "no run.event notifications were observed."
    }

    if ($terminalEvent.params.status -ne "completed") {
        throw "run ended as '$($terminalEvent.params.status)' at sequence $($terminalEvent.params.sequence)."
    }

    Write-Smoke "terminal run.event: status=$($terminalEvent.params.status) sequence=$($terminalEvent.params.sequence) events=$runEventCount"

    # EOF on stdin: the bridge must report bridge.shutdown and exit 0.
    $bridgeInput.Close()
    if (-not $bridgeProcess.WaitForExit(15000)) {
        throw "the bridge did not exit within 15 s after stdin was closed."
    }

    if ($bridgeProcess.ExitCode -ne 0) {
        throw "the bridge exited with code $($bridgeProcess.ExitCode). stderr: $(Get-BridgeStderr)"
    }

    $tail = $bridgeOutput.ReadToEnd()
    foreach ($line in @($tail -split "\r?\n")) {
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }

        try {
            $null = $line | ConvertFrom-Json
        } catch {
            throw "the bridge wrote a non-JSON stdout line: $line"
        }

        $script:transcript.Add($line)
    }

    $status = 0
} catch {
    Write-Smoke "FAIL - $($_.Exception.Message)"
    if ($null -ne $hostProcess -and $hostProcess.HasExited) {
        Write-Smoke "host exit code: $($hostProcess.ExitCode)"
    }

    if ($null -ne $hostProcess) {
        Write-Smoke "host stderr tail: $(Get-TextTail $hostStderrPath)"
    }

    if ($transcript.Count -gt 0) {
        Write-Smoke "last bridge stdout lines:"
        $first = [Math]::Max(0, $transcript.Count - 5)
        for ($index = $first; $index -lt $transcript.Count; $index++) {
            Write-Host "  $($transcript[$index])"
        }
    }
} finally {
    Stop-ProcessTree $bridgeProcess
    Stop-ProcessTree $hostProcess
    foreach ($name in $environmentNames) {
        [Environment]::SetEnvironmentVariable($name, $previousEnvironment[$name], "Process")
    }

    $workRemoved = $false
    for ($attempt = 0; $attempt -lt 5; $attempt++) {
        try {
            Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction Stop
            $workRemoved = $true
            break
        } catch {
            Start-Sleep -Milliseconds 250
        }
    }

    if (-not $workRemoved) {
        Write-Warning "rpc smoke: could not remove the temp work directory $work; remove it manually."
    }
}

if ($status -eq 0) {
    Write-Smoke "PASS"
    exit 0
}

exit 1
