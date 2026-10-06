function Start-ScenarioHost($ScenarioRoot, $ScenarioWorkspace, [DateTimeOffset]$Deadline, [string]$ScriptPath) {
    if ([string]::IsNullOrWhiteSpace($ScriptPath)) { $ScriptPath = $ScenarioWorkspace.scriptPath }
    $port = Get-FreePort
    $baseUrl = "http://127.0.0.1:$port"
    $token = Get-LoopbackBearerToken
    $start = New-Object System.Diagnostics.ProcessStartInfo
    $start.FileName = Join-Path $repoRoot "backend-dotnet/RuBackend/bin/Debug/net10.0/RuBackend.exe"
    $start.WorkingDirectory = $repoRoot
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    Add-ProcessArguments $start @("--urls", $baseUrl, "--managed-runtime")
    $start.Environment["RU_DATA_ROOT"] = $ScenarioWorkspace.dataRoot
    $start.Environment["RU_PROJECT_ROOT"] = $repoRoot
    $start.Environment["RU_SCRIPTED_MODEL"] = $ScriptPath
    $start.Environment["RU_DESKTOP_TOKEN"] = $token
    $start.Environment["RU_LOG_ROOT"] = (Join-Path $ScenarioRoot "logs")
    $start.Environment["RU_AGENT_BROKER_EXE"] = (Join-Path $repoRoot "backend-dotnet/RuAgent.Broker/bin/Debug/net10.0/RuAgent.Broker.exe")
    $start.Environment["RU_AGENT_BROKER_PROJECT_ROOT"] = $repoRoot
    $process = [System.Diagnostics.Process]::Start($start)
    try {
        $capture = Enable-BoundedProcessOutput $process $Limits.HostStdoutBytes $Limits.HostStderrBytes
        Wait-HostReady $process $capture $ScenarioWorkspace.dataRoot $baseUrl $token (Get-RemainingSeconds $Deadline)
        return [pscustomobject]@{ process = $process; capture = $capture; baseUrl = $baseUrl; token = $token }
    } catch {
        Stop-ProcessTree $process
        throw
    }
}

function Invoke-ScenarioCli($Scenario, $ScenarioWorkspace, $HostSession, [DateTimeOffset]$Deadline, [string]$Prompt = $Scenario.input, [string]$SessionId) {
    $start = New-Object System.Diagnostics.ProcessStartInfo
    $start.FileName = Join-Path $repoRoot "backend-dotnet/RuAgent.Cli/bin/Debug/net10.0-windows/RuAgent.Cli.exe"
    $start.WorkingDirectory = $repoRoot
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $arguments = @("agent", "run", "--preset", $Scenario.preset, "--workspace", $ScenarioWorkspace.workspace, "--json", "--trust-workspace")
    if (-not [string]::IsNullOrWhiteSpace($SessionId)) { $arguments += @("--session", $SessionId) }
    $arguments += $Prompt
    Add-ProcessArguments $start $arguments
    $start.Environment["RU_DATA_ROOT"] = $ScenarioWorkspace.dataRoot
    $start.Environment["RU_PROJECT_ROOT"] = $repoRoot
    $start.Environment["RU_SCRIPTED_MODEL"] = $ScenarioWorkspace.scriptPath
    $start.Environment["RU_DESKTOP_TOKEN"] = $HostSession.token
    $process = [System.Diagnostics.Process]::Start($start)
    try {
        $capture = Enable-BoundedProcessOutput $process
        if (-not $process.WaitForExit((Get-RemainingSeconds $Deadline) * 1000)) {
            $progress = Get-CliProgressSummary $capture.stdout.Text
            throw "case timeout: CLI exceeded the workflow deadline. $progress"
        }
        $process.WaitForExit()
        if ($capture.stdout.Exceeded -or $capture.stderr.Exceeded) { throw "CLI output exceeded its collection bounds." }
        if ($process.ExitCode -ne 0) { throw "CLI exited $($process.ExitCode): $($capture.stderr.Text)" }
        $events = @($capture.stdout.Text -split "`r?`n" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object { $_ | ConvertFrom-Json })
        $sessionMatch = [regex]::Match($capture.stderr.Text, '(?m)^session_id:\s*([A-Za-z0-9_-]{1,64})\s*$')
        $resolvedSessionId = if (-not [string]::IsNullOrWhiteSpace($SessionId)) { $SessionId } elseif ($sessionMatch.Success) { $sessionMatch.Groups[1].Value } else { $null }
        $process.Dispose()
        return [pscustomobject]@{ process = $null; events = $events; session_id = $resolvedSessionId }
    } catch {
        $cleanupFailure = Stop-ProcessTree $process
        if ($null -ne $cleanupFailure) { throw "case environment failure: isolated CLI cleanup failed." }
        throw
    }
}

function Invoke-ScenarioCliUntilApproval($Scenario, $ScenarioWorkspace, $HostSession, [DateTimeOffset]$Deadline) {
    $start = New-Object System.Diagnostics.ProcessStartInfo
    $start.FileName = Join-Path $repoRoot "backend-dotnet/RuAgent.Cli/bin/Debug/net10.0-windows/RuAgent.Cli.exe"
    $start.WorkingDirectory = $repoRoot
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    Add-ProcessArguments $start @("agent", "chat", "--preset", $Scenario.preset, "--workspace", $ScenarioWorkspace.workspace, "--json", "--trust-workspace")
    $start.Environment["RU_DATA_ROOT"] = $ScenarioWorkspace.dataRoot
    $start.Environment["RU_PROJECT_ROOT"] = $repoRoot
    $start.Environment["RU_SCRIPTED_MODEL"] = $ScenarioWorkspace.scriptPath
    $start.Environment["RU_DESKTOP_TOKEN"] = $HostSession.token
    $process = [System.Diagnostics.Process]::Start($start)
    try {
        $capture = Enable-BoundedProcessOutput $process
        $process.StandardInput.WriteLine($Scenario.input)
        $process.StandardInput.Flush()
        $process.StandardInput.Close()
        while (-not $process.HasExited -and [DateTimeOffset]::UtcNow -lt $Deadline) {
            $events = @($capture.stdout.Text -split "`r?`n" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object { try { $_ | ConvertFrom-Json } catch { $null } } | Where-Object { $null -ne $_ })
            if ($capture.stderr.Text -match 'approval_required: run run_[0-9a-f]{32} is waiting for approval apr_[0-9a-f]{32}') {
                $process.WaitForExit(1000) | Out-Null
                if (-not $process.HasExited) {
                    $cleanupFailure = Stop-ProcessTree $process
                    if ($null -ne $cleanupFailure) { throw "case environment failure: approval stream detach failed." }
                }
                $sessionMatch = [regex]::Match($capture.stderr.Text, '(?m)^session_id:\s*([A-Za-z0-9_-]{1,64})\s*$')
                $sessionId = if ($sessionMatch.Success) { $sessionMatch.Groups[1].Value } else { $null }
                $approvalMatch = [regex]::Match($capture.stderr.Text, 'approval_required: run (run_[0-9a-f]{32}) is waiting for approval (apr_[0-9a-f]{32})')
                return [pscustomobject]@{ process = $null; events = $events; session_id = $sessionId; approval_run_id = $approvalMatch.Groups[1].Value; approval_id = $approvalMatch.Groups[2].Value }
            }
            Start-Sleep -Milliseconds 50
        }
        if ($process.HasExited -and $capture.stderr.Text -match 'approval_required: run run_[0-9a-f]{32} is waiting for approval apr_[0-9a-f]{32}') {
            $events = @($capture.stdout.Text -split "`r?`n" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object { try { $_ | ConvertFrom-Json } catch { $null } } | Where-Object { $null -ne $_ })
            $sessionMatch = [regex]::Match($capture.stderr.Text, '(?m)^session_id:\s*([A-Za-z0-9_-]{1,64})\s*$')
            $sessionId = if ($sessionMatch.Success) { $sessionMatch.Groups[1].Value } else { $null }
            $approvalMatch = [regex]::Match($capture.stderr.Text, 'approval_required: run (run_[0-9a-f]{32}) is waiting for approval (apr_[0-9a-f]{32})')
            $process.Dispose()
            return [pscustomobject]@{ process = $null; events = $events; session_id = $sessionId; approval_run_id = $approvalMatch.Groups[1].Value; approval_id = $approvalMatch.Groups[2].Value }
        }
        if ($process.HasExited) { throw "Oracle failure: CLI run completed before producing its required approval request (stderr=$($capture.stderr.Text))." }
        throw "case timeout: CLI did not produce an approval request before the workflow deadline. $(Get-CliProgressSummary $capture.stdout.Text)"
    } catch {
        if (-not $process.HasExited) {
            $cleanupFailure = Stop-ProcessTree $process
            if ($null -ne $cleanupFailure) { throw "case environment failure: isolated CLI cleanup failed." }
        } else { $process.Dispose() }
        throw
    }
}

function Invoke-ScenarioCliControl($ScenarioWorkspace, $HostSession, [string[]]$Arguments, [DateTimeOffset]$Deadline) {
    $start = New-Object System.Diagnostics.ProcessStartInfo
    $start.FileName = Join-Path $repoRoot "backend-dotnet/RuAgent.Cli/bin/Debug/net10.0-windows/RuAgent.Cli.exe"
    $start.WorkingDirectory = $repoRoot
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    Add-ProcessArguments $start $Arguments
    $start.Environment["RU_DATA_ROOT"] = $ScenarioWorkspace.dataRoot
    $start.Environment["RU_PROJECT_ROOT"] = $repoRoot
    $start.Environment["RU_SCRIPTED_MODEL"] = $ScenarioWorkspace.scriptPath
    $start.Environment["RU_DESKTOP_TOKEN"] = $HostSession.token
    $process = [System.Diagnostics.Process]::Start($start)
    $processDisposed = $false
    try {
        $capture = Enable-BoundedProcessOutput $process
        if (-not $process.WaitForExit((Get-RemainingSeconds $Deadline) * 1000)) { throw "case timeout: CLI control command exceeded the workflow deadline." }
        $process.WaitForExit()
        if ($capture.stdout.Exceeded -or $capture.stderr.Exceeded) { throw "CLI control output exceeded its collection bounds." }
        if ($process.ExitCode -ne 0) { throw "CLI control command failed with exit code $($process.ExitCode): $($capture.stderr.Text)" }
        return [pscustomobject]@{ stdout = $capture.stdout.Text; stderr = $capture.stderr.Text; exit_code = $process.ExitCode }
    } catch {
        if (-not $process.HasExited) {
            $cleanupFailure = Stop-ProcessTree $process
            $processDisposed = $true
            if ($null -ne $cleanupFailure) { throw "case environment failure: isolated CLI control cleanup failed." }
        }
        throw
    } finally {
        if (-not $processDisposed) { $process.Dispose() }
    }
}

function Get-ResultPayloads($Scenario, $HostSession, $Events, [DateTimeOffset]$Deadline, [string[]]$EventTypes = @("tool.execution_completed", "model.step_completed")) {
    $resultEvents = @($Events | Where-Object { $_.event_type -in $EventTypes -and -not [string]::IsNullOrWhiteSpace($_.payload_ref) })
    if ($resultEvents.Count -gt $Limits.ResultPayloadCount) { throw "Oracle failure: result payload count exceeds its bound." }
    $contents = [System.Collections.Generic.List[object]]::new()
    foreach ($event in $resultEvents) {
        $contents.Add([pscustomobject]@{
            sequence = [long]$event.sequence
            event_type = [string]$event.event_type
            payload_id = [string]$event.payload_ref
            content = (Get-ClientResultPayload $HostSession $event.run_id $event.payload_ref $Deadline $event.event_type)
        })
    }

    return @($contents)
}

function Get-ClientResultPayload($HostSession, [string]$RunId, [string]$PayloadId, [DateTimeOffset]$Deadline, [string]$EventType) {
    $client = [System.Net.Http.HttpClient]::new()
    $client.Timeout = [TimeSpan]::FromSeconds([Math]::Min($Limits.ReadinessRequestSeconds, (Get-RemainingSeconds $Deadline)))
    $request = $null
    $response = $null
    $stream = $null
    $buffer = $null
    $requestDeadline = $null
    try {
        $url = "$($HostSession.baseUrl)/agent/v1/runs/$RunId/payloads/$PayloadId"
        $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Get, $url)
        $request.Headers.Authorization = [System.Net.Http.Headers.AuthenticationHeaderValue]::new("Bearer", $HostSession.token)
        $requestDeadline = [System.Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds([Math]::Min($Limits.ReadinessRequestSeconds, (Get-RemainingSeconds $Deadline))))
        $response = $client.SendAsync($request, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead, $requestDeadline.Token).GetAwaiter().GetResult()
        if (-not $response.IsSuccessStatusCode) { throw "Oracle failure: $($EventType) result payload was not client-readable (HTTP $([int]$response.StatusCode))." }
        if ($null -ne $response.Content.Headers.ContentLength -and $response.Content.Headers.ContentLength -gt $Limits.ResultPayloadBytes) { throw "Oracle failure: result payload exceeds its collection bound." }
        $stream = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
        $buffer = [System.IO.MemoryStream]::new()
        $chunk = [byte[]]::new(8192)
        while (($count = $stream.ReadAsync($chunk, 0, $chunk.Length, $requestDeadline.Token).GetAwaiter().GetResult()) -gt 0) {
            if ($buffer.Length + $count -gt $Limits.ResultPayloadBytes) { throw "Oracle failure: result payload exceeds its collection bound." }
            $buffer.Write($chunk, 0, $count)
        }

        $envelope = [System.Text.Encoding]::UTF8.GetString($buffer.ToArray()) | ConvertFrom-Json
        if ($envelope.run_id -cne $RunId -or $envelope.payload_id -cne $PayloadId -or $envelope.encoding -notin @("json", "utf8text")) { throw "Oracle failure: result payload identity or encoding is invalid." }
        return [string]$envelope.content
    } catch [System.Net.Http.HttpRequestException] {
        throw "case environment failure: bounded result payload request failed ($($_.Exception.GetType().Name))."
    } catch [System.OperationCanceledException] {
        throw "case timeout: bounded result payload request exceeded its deadline."
    } finally {
        if ($null -ne $buffer) { $buffer.Dispose() }
        if ($null -ne $stream) { $stream.Dispose() }
        if ($null -ne $response) { $response.Dispose() }
        if ($null -ne $request) { $request.Dispose() }
        if ($null -ne $requestDeadline) { $requestDeadline.Dispose() }
        $client.Dispose()
    }
}

function Get-ClientRunSnapshot($HostSession, [string]$RunId, [DateTimeOffset]$Deadline) {
    if ($RunId -notmatch '^run_[A-Za-z0-9_-]{32,64}$') { throw "Oracle failure: bounded run ID was unavailable." }
    $client = [System.Net.Http.HttpClient]::new()
    $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Get, "$($HostSession.baseUrl)/agent/v1/runs/$RunId")
    $request.Headers.Authorization = [System.Net.Http.Headers.AuthenticationHeaderValue]::new("Bearer", $HostSession.token)
    $response = $null
    $stream = $null
    $buffer = $null
    $requestDeadline = $null
    try {
        $requestDeadline = [System.Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds([Math]::Min($Limits.ReadinessRequestSeconds, (Get-RemainingSeconds $Deadline))))
        $response = $client.SendAsync($request, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead, $requestDeadline.Token).GetAwaiter().GetResult()
        if (-not $response.IsSuccessStatusCode -or ($null -ne $response.Content.Headers.ContentLength -and $response.Content.Headers.ContentLength -gt $Limits.RunSnapshotBytes)) {
            throw "Oracle failure: durable run snapshot was unavailable or exceeded its bound."
        }
        $stream = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
        $buffer = [System.IO.MemoryStream]::new()
        $chunk = [byte[]]::new(8192)
        while (($count = $stream.ReadAsync($chunk, 0, $chunk.Length, $requestDeadline.Token).GetAwaiter().GetResult()) -gt 0) {
            if ($buffer.Length + $count -gt $Limits.RunSnapshotBytes) { throw "Oracle failure: durable run snapshot exceeded its bound." }
            $buffer.Write($chunk, 0, $count)
        }
        $json = [System.Text.Encoding]::UTF8.GetString($buffer.ToArray())
        $snapshot = $json | ConvertFrom-Json
        if ($snapshot.run_id -cne $RunId) { throw "Oracle failure: durable run snapshot identity did not match." }
        return $snapshot
    } catch [System.Net.Http.HttpRequestException] {
        throw "case environment failure: bounded run snapshot request failed."
    } catch [System.OperationCanceledException] {
        throw "case timeout: bounded run snapshot request exceeded its deadline."
    } finally {
        if ($null -ne $buffer) { $buffer.Dispose() }
        if ($null -ne $stream) { $stream.Dispose() }
        $request.Dispose()
        if ($null -ne $response) { $response.Dispose() }
        if ($null -ne $requestDeadline) { $requestDeadline.Dispose() }
        $client.Dispose()
    }
}

function Invoke-ScenarioRestartToApproval($Scenario, $ScenarioWorkspace, $HostSession, [DateTimeOffset]$Deadline) {
    $cli = Invoke-ScenarioCliUntilApproval $Scenario $ScenarioWorkspace $HostSession $Deadline
    $runId = [string]$cli.approval_run_id
    if ($runId -notmatch '^run_[0-9a-f]{32}$') { throw "Oracle failure: restart flow did not expose a bounded run identity." }
    $cleanupFailure = Stop-ProcessTree $HostSession.process
    if ($null -ne $cleanupFailure) { throw "case environment failure: pre-restart host cleanup failed." }
    Start-Sleep -Milliseconds 300
    $restarted = Start-ScenarioHost (Split-Path -Parent $ScenarioWorkspace.dataRoot) $ScenarioWorkspace $Deadline
    return [pscustomobject]@{ cli = $cli; host = $restarted }
}

function Wait-ScenarioStderrMatch($Process, $StderrCapture, [string]$Pattern, [DateTimeOffset]$Deadline) {
    while (-not $Process.HasExited -and [DateTimeOffset]::UtcNow -lt $Deadline) {
        if ($StderrCapture.Text.IndexOf($Pattern, [System.StringComparison]::Ordinal) -ge 0) { return }
        Start-Sleep -Milliseconds 50
    }
    if ($Process.HasExited) { throw "Oracle failure: handoff chat exited before '$Pattern' (stderr=$($StderrCapture.Text))." }
    throw "case timeout: handoff chat did not emit '$Pattern' before the workflow deadline."
}

function Invoke-ScenarioHandoffChat($Scenario, $ScenarioWorkspace, $HostSession, [string]$SourceRunId, [DateTimeOffset]$Deadline, [bool]$Attach) {
    $start = New-Object System.Diagnostics.ProcessStartInfo
    $start.FileName = Join-Path $repoRoot "backend-dotnet/RuAgent.Cli/bin/Debug/net10.0-windows/RuAgent.Cli.exe"
    $start.WorkingDirectory = $repoRoot
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    Add-ProcessArguments $start @("agent", "chat", "--preset", $Scenario.preset, "--workspace", $ScenarioWorkspace.workspace, "--json", "--trust-workspace")
    $start.Environment["RU_DATA_ROOT"] = $ScenarioWorkspace.dataRoot
    $start.Environment["RU_PROJECT_ROOT"] = $repoRoot
    $start.Environment["RU_SCRIPTED_MODEL"] = $ScenarioWorkspace.scriptPath
    $start.Environment["RU_DESKTOP_TOKEN"] = $HostSession.token
    $process = [System.Diagnostics.Process]::Start($start)
    try {
        $capture = Enable-BoundedProcessOutput $process
        $process.StandardInput.WriteLine("/handoff $SourceRunId")
        $process.StandardInput.Flush()
        Wait-ScenarioStderrMatch $process $capture.stderr "Type /handoff attach to add this preview" $Deadline
        if ($Attach) {
            $process.StandardInput.WriteLine("/handoff attach")
            $process.StandardInput.Flush()
            Wait-ScenarioStderrMatch $process $capture.stderr "saved context attached" $Deadline
        }
        $process.StandardInput.WriteLine("Continue from the selected saved work context.")
        $process.StandardInput.Flush()
        $events = @()
        $runId = $null
        $terminalStatus = $null
        while ($null -eq $terminalStatus) {
            [void](Get-RemainingSeconds $Deadline)
            if ($process.HasExited) { throw "Oracle failure: handoff chat exited before its run completed (stderr=$($capture.stderr.Text))." }
            $events = @($capture.stdout.Text -split "`r?`n" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object { try { $_ | ConvertFrom-Json } catch { $null } } | Where-Object { $null -ne $_ })
            foreach ($event in $events) {
                if ([string]$event.run_id -match '^run_[0-9a-f]{32}$') { $runId = [string]$event.run_id }
                if ($event.event_type -eq "run.state_changed" -and [string]$event.status -in @("completed", "failed", "cancelled", "interrupted")) { $terminalStatus = [string]$event.status }
            }
            if ($null -eq $terminalStatus) { Start-Sleep -Milliseconds 150 }
        }
        $process.StandardInput.WriteLine("/exit")
        $process.StandardInput.Flush()
        $process.StandardInput.Close()
        if (-not $process.WaitForExit(5000)) {
            $cleanupFailure = Stop-ProcessTree $process
            if ($null -ne $cleanupFailure) { throw "case environment failure: handoff chat cleanup failed." }
        } else {
            $process.WaitForExit()
            if ($capture.stdout.Exceeded -or $capture.stderr.Exceeded) { throw "CLI output exceeded its collection bounds." }
            if ($process.ExitCode -ne 0) { throw "CLI exited $($process.ExitCode): $($capture.stderr.Text)" }
            $process.Dispose()
        }
        return [pscustomobject]@{ process = $null; events = $events; run_id = $runId; attached = $Attach }
    } catch {
        $cleanupFailure = Stop-ProcessTree $process
        if ($null -ne $cleanupFailure) { throw "case environment failure: isolated CLI cleanup failed." }
        throw
    }
}

function Wait-ScenarioRunTerminal($HostSession, [string]$RunId, [DateTimeOffset]$Deadline) {
    while ($true) {
        [void](Get-RemainingSeconds $Deadline)
        $snapshot = Get-ClientRunSnapshot $HostSession $RunId $Deadline
        if ([string]$snapshot.status -in @("completed", "failed", "cancelled", "interrupted")) { return $snapshot }
        Start-Sleep -Milliseconds 150
    }
}

function Get-PublicRunHistory($HostSession, [string]$RunId, [long]$ThroughSequence, [DateTimeOffset]$Deadline) {
    if ($RunId -notmatch '^run_[A-Za-z0-9_-]{32,64}$' -or $ThroughSequence -lt 1) { throw "Oracle failure: bounded run history identity or sequence was unavailable." }
    $client = [System.Net.Http.HttpClient]::new()
    $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Get, "$($HostSession.baseUrl)/agent/v1/runs/$RunId/events?after_sequence=0&live=0")
    $request.Headers.Authorization = [System.Net.Http.Headers.AuthenticationHeaderValue]::new("Bearer", $HostSession.token)
    $request.Headers.Accept.ParseAdd("text/event-stream")
    $response = $null
    $stream = $null
    $buffer = $null
    $requestDeadline = $null
    try {
        $requestDeadline = [System.Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds([Math]::Min(4, (Get-RemainingSeconds $Deadline))))
        $response = $client.SendAsync($request, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead, $requestDeadline.Token).GetAwaiter().GetResult()
        if (-not $response.IsSuccessStatusCode) { throw "Oracle failure: public run history was unavailable (HTTP $([int]$response.StatusCode))." }
        $stream = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
        $buffer = [System.IO.MemoryStream]::new()
        $chunk = [byte[]]::new(4096)
        while (($count = $stream.ReadAsync($chunk, 0, $chunk.Length, $requestDeadline.Token).GetAwaiter().GetResult()) -gt 0) {
            if ($buffer.Length + $count -gt $Limits.ResultPayloadBytes) { throw "Oracle failure: public run history exceeded its byte cap." }
            $buffer.Write($chunk, 0, $count)
        }
        $text = [System.Text.Encoding]::UTF8.GetString($buffer.ToArray())
        $events = [System.Collections.Generic.List[object]]::new()
        $dataLines = [System.Collections.Generic.List[string]]::new()
        $reachedSequence = $false
        foreach ($rawLine in ($text -split "`n")) {
            $line = $rawLine.TrimEnd([char]13)
            if ($line.StartsWith("data:", [System.StringComparison]::Ordinal)) {
                $dataLines.Add($line.Substring(5).TrimStart())
                continue
            }
            if ($line.Length -ne 0 -or $dataLines.Count -eq 0) { continue }
            $event = (($dataLines -join "`n") | ConvertFrom-Json)
            $dataLines.Clear()
            if ([string]$event.run_id -cne $RunId) { throw "Oracle failure: public history event belongs to a different run." }
            if ($event.event_type -in @("heartbeat", "transient")) { continue }
            if ($null -eq $event.sequence) { throw "Oracle failure: public history event sequence was unavailable." }
            $events.Add($event)
            if ($events.Count -gt $Limits.HistoryEventCount) { throw "Oracle failure: public run history exceeded its event cap." }
            if ([long]$event.sequence -eq $ThroughSequence) { $reachedSequence = $true; break }
        }
        if (-not $reachedSequence) { throw "Oracle failure: public run history ended before the durable snapshot sequence." }
        return @($events)
    } catch [System.Net.Http.HttpRequestException] {
        throw "case environment failure: bounded public history request failed."
    } catch [System.OperationCanceledException] {
        throw "case timeout: bounded public history request exceeded its deadline."
    } finally {
        if ($null -ne $buffer) { $buffer.Dispose() }
        if ($null -ne $stream) { $stream.Dispose() }
        $request.Dispose()
        if ($null -ne $response) { $response.Dispose() }
        if ($null -ne $requestDeadline) { $requestDeadline.Dispose() }
        $client.Dispose()
    }
}

