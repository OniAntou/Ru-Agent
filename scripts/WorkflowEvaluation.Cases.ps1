function Invoke-ScenarioTurnSequence($Scenario, $ScenarioWorkspace, $HostSession, [DateTimeOffset]$Deadline) {
    $events = [System.Collections.Generic.List[object]]::new()
    $sessionId = $null
    foreach ($turn in $Scenario.turns) {
        $result = Invoke-ScenarioCli $Scenario $ScenarioWorkspace $HostSession $Deadline ([string]$turn) $sessionId
        if ([string]::IsNullOrWhiteSpace($result.session_id)) { throw "case environment failure: CLI did not return a bounded session identity." }
        $sessionId = $result.session_id
        foreach ($event in $result.events) { $events.Add($event) }
    }
    return ,([pscustomobject]@{ events = @($events); session_id = $sessionId })
}

function Get-CliProgressSummary([string]$Output) {
    $events = @()
    foreach ($line in ($Output -split "`r?`n")) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        try { $events += $line | ConvertFrom-Json } catch { }
    }
    $types = @($events | ForEach-Object event_type | Where-Object { $_ } | Select-Object -Unique) -join ","
    $statuses = @($events | Where-Object event_type -eq "run.state_changed" | ForEach-Object status | Where-Object { $_ }) -join ","
    $codes = @($events | ForEach-Object code | Where-Object { $_ } | Select-Object -Unique) -join ","
    $approvals = @($events | Where-Object event_type -eq "approval.requested").Count
    return "CLI progress: events=[$types], terminal_statuses=[$statuses], codes=[$codes], approvals=$approvals."
}

function Test-ScriptedWorkflow($Scenario, $ScenarioWorkspace, $HostSession, $Events, [DateTimeOffset]$Deadline, $CliRun = $null) {
    if ($Scenario.runnerKind -eq "scripted_denial_workflow") {
        $metrics = Get-ScriptedDenialMetrics $Scenario $ScenarioWorkspace $HostSession $Events $Deadline $CliRun
        if (-not (Test-ScenarioFiles $Scenario $ScenarioWorkspace.workspace $ScenarioWorkspace.files)) { throw "Oracle failure: denial changed the workspace file set or contents." }
        return $metrics
    }
    if ($Scenario.runnerKind -eq "scripted_terminal_paging") {
        $metrics = Get-ScriptedTerminalPagingMetrics $Scenario $ScenarioWorkspace $HostSession $Events $Deadline $CliRun
        if (-not (Test-ScenarioFiles $Scenario $ScenarioWorkspace.workspace $ScenarioWorkspace.files)) { throw "Oracle failure: terminal paging changed the workspace file set or contents." }
        return $metrics
    }
    if ($Scenario.runnerKind -eq "scripted_edit_three_workflow") {
        $metrics = Get-ScriptedEditThreeMetrics $Scenario $ScenarioWorkspace $HostSession $Events $Deadline $CliRun
        if (-not (Test-ScenarioFiles $Scenario $ScenarioWorkspace.workspace $ScenarioWorkspace.files)) { throw "Oracle failure: dependent-edit workflow changed the workspace file set or contents." }
        return $metrics
    }
    if ($Scenario.runnerKind -eq "scripted_failed_test_recovery") {
        $metrics = Get-ScriptedFailedTestMetrics $Scenario $ScenarioWorkspace $HostSession $Events $Deadline $CliRun
        if (-not (Test-ScenarioFiles $Scenario $ScenarioWorkspace.workspace $ScenarioWorkspace.files)) { throw "Oracle failure: failed-test recovery changed the workspace file set or contents." }
        return $metrics
    }
    if ($Scenario.runnerKind -eq "scripted_restart_continue") {
        $metrics = Get-ScriptedRestartContinueMetrics $Scenario $ScenarioWorkspace $HostSession $Events $Deadline $CliRun
        if (-not (Test-ScenarioFiles $Scenario $ScenarioWorkspace.workspace $ScenarioWorkspace.files)) { throw "Oracle failure: restart continuation changed the workspace file set or contents." }
        return $metrics
    }
    if ($Scenario.runnerKind -eq "scripted_handoff_selection") {
        $metrics = Get-ScriptedHandoffMetrics $Scenario $ScenarioWorkspace $HostSession $Events $Deadline $CliRun
        if (-not (Test-ScenarioFiles $Scenario $ScenarioWorkspace.workspace $ScenarioWorkspace.files)) { throw "Oracle failure: selected handoff changed the workspace file set or contents." }
        return $metrics
    }
    $payloads = Get-ResultPayloads $Scenario $HostSession $Events $Deadline
    switch ($Scenario.runnerKind) {
        "scripted_edit_tool_cycle" { $metrics = Get-ScriptedEditMetrics $Scenario $Events }
        "scripted_paging_workflow" { $metrics = Get-ScriptedPagingMetrics $Scenario $Events $payloads $ScenarioWorkspace.files }
        "scripted_tool_batch" { $metrics = Get-ScriptedToolBatchMetrics $Scenario $Events $payloads $ScenarioWorkspace.files }
        "scripted_text" { $metrics = Get-ScriptedTextMetrics $Scenario $Events $payloads }
        "scripted_turn_sequence" { $metrics = Get-ScriptedTurnSequenceMetrics $Scenario $Events $payloads }
        default { throw "Scenario '$($Scenario.id)' has no registered deterministic executor." }
    }
    $filesMatch = Test-ScenarioFiles $Scenario $ScenarioWorkspace.workspace $ScenarioWorkspace.files
    if (-not $filesMatch) {
        throw "Oracle failure: scripted workflow did not preserve its expected workspace files."
    }
    return $metrics
}

function Get-ScriptedDenialMetrics($Scenario, $ScenarioWorkspace, $HostSession, $Events, [DateTimeOffset]$Deadline, $CliRun) {
    $runId = [string]$CliRun.approval_run_id
    $approvalId = [string]$CliRun.approval_id
    if ($runId -notmatch '^run_[0-9a-f]{32}$' -or $approvalId -notmatch '^apr_[0-9a-f]{32}$') { throw "Oracle failure: CLI did not expose a bounded pending approval command." }
    $waitingSnapshot = Get-ClientRunSnapshot $HostSession $runId $Deadline
    if ([string]$waitingSnapshot.status -cne "waiting_approval") { throw "Oracle failure: explicit denial did not begin from a waiting approval." }
    $waitingEvents = Get-PublicRunHistory $HostSession $runId ([long]$waitingSnapshot.last_sequence) $Deadline
    $approvalEvents = @($waitingEvents | Where-Object event_type -eq "approval.requested")
    $proposals = @($waitingEvents | Where-Object event_type -eq "tool.proposed")
    $executions = @($waitingEvents | Where-Object event_type -in @("tool.execution_requested", "tool.execution_completed"))
    $initialSteps = @($waitingEvents | Where-Object event_type -eq "model.step_started")
    if ($approvalEvents.Count -ne 1 -or [string]$approvalEvents[0].approval_id -cne $approvalId -or $proposals.Count -ne 1 -or $executions.Count -ne 0 -or $initialSteps.Count -ne 1) { throw "Oracle failure: pre-denial history did not contain exactly one approval-gated proposal and zero dispatched executions." }
    $approvalSequence = [long]$approvalEvents[0].sequence
    $denial = Invoke-ScenarioCliControl $ScenarioWorkspace $HostSession @("agent", "deny", $runId, $approvalId) $Deadline
    if ($denial.stdout -notmatch "approval $([regex]::Escape($approvalId)) denied") { throw "Oracle failure: CLI did not confirm the explicit approval denial." }
    $terminalSnapshot = Get-ClientRunSnapshot $HostSession $runId $Deadline
    if ([string]$terminalSnapshot.status -cne "failed" -or [string]$terminalSnapshot.terminal_error_code -cne "approval_denied") { throw "Oracle failure: denied run did not persist the named blocker." }
    $terminalEvents = Get-PublicRunHistory $HostSession $runId ([long]$terminalSnapshot.last_sequence) $Deadline
    $sameApproval = @($terminalEvents | Where-Object { $_.event_type -eq "approval.requested" -and $_.approval_id -ceq $approvalId })
    $postDenialEffects = @($terminalEvents | Where-Object { [long]$_.sequence -gt $approvalSequence -and $_.event_type -in @("model.step_started", "tool.proposed", "tool.execution_requested", "tool.execution_completed") })
    $allExecutions = @($terminalEvents | Where-Object event_type -in @("tool.execution_requested", "tool.execution_completed"))
    $allProposals = @($terminalEvents | Where-Object event_type -eq "tool.proposed")
    $allSteps = @($terminalEvents | Where-Object event_type -eq "model.step_started")
    if ($sameApproval.Count -ne 1 -or $postDenialEffects.Count -ne 0 -or $allExecutions.Count -ne 0 -or $allProposals.Count -ne 1 -or $allSteps.Count -ne 1) { throw "Oracle failure: post-denial durable history shows another model/tool effect, a dispatched command, or loses the approval identity." }
    foreach ($entry in $ScenarioWorkspace.externalFiles) {
        $path = Join-Path (Split-Path -Parent $ScenarioWorkspace.workspace) $entry.path
        if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or [System.IO.File]::ReadAllText($path, [System.Text.UTF8Encoding]::new($false, $true)) -cne [string]$entry.content) { throw "Oracle failure: denied read changed its external sentinel." }
    }
    return [pscustomobject]@{ tool_calls = 0; failed_tool_calls = 0; approval_requests = 1; model_steps = @($Events | Where-Object event_type -eq "model.step_started").Count }
}

function Get-ScriptedTerminalPagingMetrics($Scenario, $ScenarioWorkspace, $HostSession, $Events, [DateTimeOffset]$Deadline, $CliRun) {
    $runId = [string]$CliRun.approval_run_id
    $approvalId = [string]$CliRun.approval_id
    if ($runId -notmatch '^run_[0-9a-f]{32}$' -or $approvalId -notmatch '^apr_[0-9a-f]{32}$') { throw "Oracle failure: CLI did not expose a bounded pending approval command." }
    $sentinelAssertion = $Scenario.assertions | Where-Object kind -eq "middle_sentinel_seen" | Select-Object -First 1
    if ($null -eq $sentinelAssertion -or [string]::IsNullOrWhiteSpace($sentinelAssertion.sentinel)) { throw "Oracle failure: terminal paging scenario has no concrete middle-output marker." }
    $waitingSnapshot = Get-ClientRunSnapshot $HostSession $runId $Deadline
    if ([string]$waitingSnapshot.status -cne "waiting_approval") { throw "Oracle failure: terminal paging did not begin from a waiting approval." }
    $waitingEvents = Get-PublicRunHistory $HostSession $runId ([long]$waitingSnapshot.last_sequence) $Deadline
    $approvalEvents = @($waitingEvents | Where-Object event_type -eq "approval.requested")
    $proposals = @($waitingEvents | Where-Object event_type -eq "tool.proposed")
    $preExecutions = @($waitingEvents | Where-Object event_type -in @("tool.execution_requested", "tool.execution_completed"))
    $preSteps = @($waitingEvents | Where-Object event_type -eq "model.step_started")
    if ($approvalEvents.Count -ne 1 -or [string]$approvalEvents[0].approval_id -cne $approvalId -or $proposals.Count -ne 1 -or $preExecutions.Count -ne 0 -or $preSteps.Count -ne 1) { throw "Oracle failure: pre-approval history did not contain exactly one approval-gated proposal, one model step, and zero executions." }
    $approval = Invoke-ScenarioCliControl $ScenarioWorkspace $HostSession @("agent", "approve", $runId, $approvalId) $Deadline
    if ($approval.stdout -notmatch "approval $([regex]::Escape($approvalId)) authorized") { throw "Oracle failure: CLI did not confirm the explicit allow-once approval." }
    $terminalSnapshot = Wait-ScenarioRunTerminal $HostSession $runId $Deadline
    if ([string]$terminalSnapshot.status -cne "completed") { throw "Oracle failure: approved terminal paging run ended '$([string]$terminalSnapshot.status)' instead of completed." }
    $history = Get-PublicRunHistory $HostSession $runId ([long]$terminalSnapshot.last_sequence) $Deadline
    $toolEvents = @($history | Where-Object event_type -eq "tool.execution_completed" | Sort-Object { [long]$_.sequence })
    $executionRequests = @($history | Where-Object event_type -eq "tool.execution_requested")
    $orderedHistory = @($history | Sort-Object { [long]$_.sequence })
    $finalEvent = $orderedHistory | Select-Object -Last 1
    $payloads = Get-ResultPayloads $Scenario $HostSession $history $Deadline
    $toolPayloads = @($payloads | Where-Object event_type -eq "tool.execution_completed")
    if ($toolEvents.Count -ne 3 -or $toolPayloads.Count -ne 3 -or $executionRequests.Count -ne 3 -or $finalEvent.event_type -ne "run.state_changed" -or [long]$finalEvent.sequence -ne [long]$terminalSnapshot.last_sequence) {
        throw "Oracle failure: terminal paging expected one command execution plus two spool pages and a terminal state change as the final durable event; observed summaries [$(@($toolEvents | ForEach-Object safe_summary) -join ',')], $($toolPayloads.Count) payloads, $($executionRequests.Count) executions, final '$($finalEvent.event_type)' at $($finalEvent.sequence)/$($terminalSnapshot.last_sequence)."
    }
    foreach ($event in $toolEvents) {
        if ($event.safe_summary -notin @("ok", "succeeded")) { throw "Oracle failure: terminal paging required successful tool receipts." }
    }
    $runResult = $null
    $pages = [System.Collections.Generic.List[object]]::new()
    foreach ($payload in $toolPayloads) {
        $parsed = $null
        try { $parsed = $payload.content | ConvertFrom-Json } catch { throw "Oracle failure: terminal paging tool result was not bounded JSON." }
        if ($null -eq $parsed) { throw "Oracle failure: terminal paging tool result was empty." }
        $names = @($parsed.PSObject.Properties.Name)
        if ($names -contains "page") { $pages.Add($parsed); continue }
        if ($names -contains "exit_code") {
            if ($null -ne $runResult) { throw "Oracle failure: terminal paging executed more than one command." }
            $runResult = $parsed
            continue
        }
        throw "Oracle failure: terminal paging tool result had an unexpected shape."
    }
    if ($null -eq $runResult -or $pages.Count -ne 2) { throw "Oracle failure: terminal paging expected one command receipt and two spool pages." }
    if ([string]::IsNullOrWhiteSpace([string]$runResult.spool_id) -or [int]$runResult.exit_code -ne 0) { throw "Oracle failure: terminal command receipt did not record a successful spooled execution." }
    $orderedPages = @($pages | Sort-Object { [int]$_.offset })
    $sentinel = [string]$sentinelAssertion.sentinel
    $markerPage = -1
    for ($index = 0; $index -lt $orderedPages.Count; $index++) {
        $page = $orderedPages[$index]
        if ([string]$page.spool_id -cne [string]$runResult.spool_id) { throw "Oracle failure: spool pages did not preserve the command's spool identity." }
        if ([int]$page.total_chars -ne [int]$orderedPages[0].total_chars) { throw "Oracle failure: spool pages disagreed on the retained total." }
        if ($index -eq 0 -and [int]$page.offset -ne 0) { throw "Oracle failure: the first spool page did not start at offset zero." }
        if ($index -gt 0) {
            $previous = $orderedPages[$index - 1]
            $previousEnd = [int]$previous.offset + ([string]$previous.page).Length
            if ([int]$page.offset -le [int]$previous.offset -or [int]$page.offset -gt $previousEnd) {
                throw "Oracle failure: spool page cursors did not advance within the previous page's UTF-16 range; offsets [$(@($orderedPages | ForEach-Object offset) -join ',')], lengths [$(@($orderedPages | ForEach-Object { ([string]$_.page).Length }) -join ',')], total $($page.total_chars)."
            }
        }
        if ($markerPage -lt 0 -and ([string]$page.page).IndexOf($sentinel, [System.StringComparison]::Ordinal) -ge 0) { $markerPage = $index }
    }
    if ($markerPage -lt 0) { throw "Oracle failure: no spool page contained '$sentinel'." }
    if ($markerPage -eq 0) { throw "Oracle failure: the middle output sentinel was returned by the first spool page." }
    if ([bool]$orderedPages[-1].truncated) { throw "Oracle failure: spool pages did not reach the end of the retained output." }
    if ([int]$orderedPages[-1].offset + ([string]$orderedPages[-1].page).Length -ne [int]$orderedPages[-1].total_chars) { throw "Oracle failure: the final spool page did not end exactly at the retained total." }
    $stepCount = @($history | Where-Object event_type -eq "model.step_started").Count
    return [pscustomobject]@{ tool_calls = $toolEvents.Count; failed_tool_calls = 0; approval_requests = 1; model_steps = $stepCount }
}

function Get-ScriptedEditThreeMetrics($Scenario, $ScenarioWorkspace, $HostSession, $Events, [DateTimeOffset]$Deadline, $CliRun) {
    $runId = [string]$CliRun.approval_run_id
    $approvalId = [string]$CliRun.approval_id
    if ($runId -notmatch '^run_[0-9a-f]{32}$' -or $approvalId -notmatch '^apr_[0-9a-f]{32}$') { throw "Oracle failure: CLI did not expose a bounded pending approval command." }
    $expectedChangedCount = [int](Get-ExpectedAssertion $Scenario "expected_files_changed")
    $expectedTestStatus = [string](Get-ExpectedAssertion $Scenario "fixture_tests")
    $expectedOutsideChanges = [int](Get-ExpectedAssertion $Scenario "outside_expected_files_changed")
    if ($expectedTestStatus -cne "passed" -or $expectedOutsideChanges -ne 0) { throw "Oracle failure: dependent-edit scenario oracle is not a passing verification with zero outside changes." }
    $waitingSnapshot = Get-ClientRunSnapshot $HostSession $runId $Deadline
    if ([string]$waitingSnapshot.status -cne "waiting_approval") { throw "Oracle failure: dependent-edit verification did not begin from a waiting approval." }
    $waitingEvents = Get-PublicRunHistory $HostSession $runId ([long]$waitingSnapshot.last_sequence) $Deadline
    $approvalEvents = @($waitingEvents | Where-Object event_type -eq "approval.requested")
    $proposals = @($waitingEvents | Where-Object event_type -eq "tool.proposed")
    $preExecutions = @($waitingEvents | Where-Object event_type -in @("tool.execution_requested", "tool.execution_completed"))
    $preSteps = @($waitingEvents | Where-Object event_type -eq "model.step_started")
    if ($approvalEvents.Count -ne 1 -or [string]$approvalEvents[0].approval_id -cne $approvalId -or $proposals.Count -ne 4 -or $preExecutions.Count -ne 6 -or $preSteps.Count -ne 4) { throw "Oracle failure: dependent-edit precondition expected three executed mutations, a fourth proposal awaiting approval, and no approval before it." }
    $approval = Invoke-ScenarioCliControl $ScenarioWorkspace $HostSession @("agent", "approve", $runId, $approvalId) $Deadline
    if ($approval.stdout -notmatch "approval $([regex]::Escape($approvalId)) authorized") { throw "Oracle failure: CLI did not confirm the explicit allow-once approval." }
    $terminalSnapshot = Wait-ScenarioRunTerminal $HostSession $runId $Deadline
    if ([string]$terminalSnapshot.status -cne "completed") {
        $failureHistory = Get-PublicRunHistory $HostSession $runId ([long]$terminalSnapshot.last_sequence) $Deadline
        $summaries = @($failureHistory | Where-Object { $_.event_type -in @("run.failed", "run.state_changed", "tool.execution_completed", "tool.execution_requested", "approval.requested") } | ForEach-Object { "$($_.sequence):$($_.event_type):$($_.safe_summary)" }) -join "; "
        throw "Oracle failure: dependent-edit verification ended '$([string]$terminalSnapshot.status)' (code=$([string]$terminalSnapshot.terminal_error_code)) instead of completed. History: $summaries"
    }
    $history = Get-PublicRunHistory $HostSession $runId ([long]$terminalSnapshot.last_sequence) $Deadline
    $executions = @($history | Where-Object event_type -eq "tool.execution_requested")
    $toolEvents = @($history | Where-Object event_type -eq "tool.execution_completed" | Sort-Object { [long]$_.sequence })
    $finalEvent = @($history | Sort-Object { [long]$_.sequence }) | Select-Object -Last 1
    $payloads = Get-ResultPayloads $Scenario $HostSession $history $Deadline
    $toolPayloads = @($payloads | Where-Object event_type -eq "tool.execution_completed")
    if ($executions.Count -ne 4 -or $toolEvents.Count -ne 4 -or $toolPayloads.Count -ne 4 -or $finalEvent.event_type -ne "run.state_changed" -or [long]$finalEvent.sequence -ne [long]$terminalSnapshot.last_sequence) {
        throw "Oracle failure: dependent-edit workflow expected four executions, four receipts, and a terminal state change as the final durable event; observed $($executions.Count) executions, $($toolEvents.Count) receipts, $($toolPayloads.Count) payloads, final '$($finalEvent.event_type)'."
    }
    foreach ($event in $toolEvents) {
        if ($event.safe_summary -notin @("ok", "succeeded")) { throw "Oracle failure: dependent-edit workflow needed successful mutation and verification receipts." }
    }
    $payloadById = @{}
    foreach ($payload in $toolPayloads) { $payloadById[[string]$payload.payload_id] = $payload }
    $orderedResults = [System.Collections.Generic.List[object]]::new()
    foreach ($event in $toolEvents) {
        $payload = $payloadById[[string]$event.payload_ref]
        if ($null -eq $payload) { throw "Oracle failure: a dependent-edit receipt had no client-readable payload." }
        try { $orderedResults.Add(($payload.content | ConvertFrom-Json)) } catch { throw "Oracle failure: dependent-edit tool result was not bounded JSON." }
    }
    $mutations = @($orderedResults | Select-Object -First 3)
    $verification = $orderedResults[3]
    if (@($mutations | Where-Object { -not ($_.PSObject.Properties.Name -contains "after_sha256" -and $_.PSObject.Properties.Name -contains "path") }).Count -ne 0) { throw "Oracle failure: dependent-edit mutations did not come before the verification run." }
    if (-not ($verification.PSObject.Properties.Name -contains "exit_code")) { throw "Oracle failure: dependent-edit verification receipt was not a terminal run." }
    $verifiedPaths = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($expected in $Scenario.expectedFiles) {
        if ($expected.state -notin @("changed_to_expected", "changed_only_after_observed_failure")) { continue }
        $matched = @($mutations | Where-Object { $_.path -ceq $expected.path -and $_.after_sha256 -ceq $expected.sha256 })
        if ($matched.Count -ne 1 -or -not $verifiedPaths.Add($expected.path)) { throw "Oracle failure: no single mutation receipt matched the expected final hash for '$($expected.path)'." }
    }
    if ($verifiedPaths.Count -ne $expectedChangedCount -or $verifiedPaths.Count -ne 3) { throw "Oracle failure: dependent-edit workflow did not mutate exactly the three expected files." }
    if ([int]$verification.exit_code -ne 0) { throw "Oracle failure: fixture verification command did not exit zero." }
    $verificationOutput = [string]$verification.stdout_head + [string]$verification.stdout_tail
    if ($verificationOutput.IndexOf("Fixture checks passed.", [System.StringComparison]::Ordinal) -lt 0) { throw "Oracle failure: fixture verification output did not carry its pass marker." }
    $stepCount = @($history | Where-Object event_type -eq "model.step_started").Count
    return [pscustomobject]@{ tool_calls = $toolEvents.Count; failed_tool_calls = 0; approval_requests = 1; model_steps = $stepCount }
}

function Get-ScriptedFailedTestMetrics($Scenario, $ScenarioWorkspace, $HostSession, $Events, [DateTimeOffset]$Deadline, $CliRun) {
    $runId = [string]$CliRun.approval_run_id
    $approvalId = [string]$CliRun.approval_id
    if ($runId -notmatch '^run_[0-9a-f]{32}$' -or $approvalId -notmatch '^apr_[0-9a-f]{32}$') { throw "Oracle failure: CLI did not expose a bounded pending approval command." }
    $steps = @($Scenario.scriptedSteps)
    if ($steps.Count -lt 4 -or (($steps[0].tool | ConvertTo-Json -Depth 8 -Compress) -cne ($steps[3].tool | ConvertTo-Json -Depth 8 -Compress))) { throw "Oracle failure: failed-test recovery did not script the identical test command before and after the fix." }
    $waitingSnapshot = Get-ClientRunSnapshot $HostSession $runId $Deadline
    if ([string]$waitingSnapshot.status -cne "waiting_approval") { throw "Oracle failure: failed-test recovery did not begin from a waiting approval." }
    $waitingEvents = Get-PublicRunHistory $HostSession $runId ([long]$waitingSnapshot.last_sequence) $Deadline
    $approvalEvents = @($waitingEvents | Where-Object event_type -eq "approval.requested")
    $proposals = @($waitingEvents | Where-Object event_type -eq "tool.proposed")
    $preExecutions = @($waitingEvents | Where-Object event_type -in @("tool.execution_requested", "tool.execution_completed"))
    $preSteps = @($waitingEvents | Where-Object event_type -eq "model.step_started")
    if ($approvalEvents.Count -ne 1 -or [string]$approvalEvents[0].approval_id -cne $approvalId -or $proposals.Count -ne 1 -or $preExecutions.Count -ne 0 -or $preSteps.Count -ne 1) { throw "Oracle failure: failed-test precondition expected one approval-gated proposal, one model step, and zero executions." }
    $approvalOrder = [System.Collections.Generic.List[string]]::new()
    $approvalOrder.Add($approvalId)
    $approval = Invoke-ScenarioCliControl $ScenarioWorkspace $HostSession @("agent", "approve", $runId, $approvalId) $Deadline
    if ($approval.stdout -notmatch "approval $([regex]::Escape($approvalId)) authorized") { throw "Oracle failure: CLI did not confirm the first explicit allow-once approval." }
    $terminalSnapshot = $null
    while ($null -eq $terminalSnapshot) {
        [void](Get-RemainingSeconds $Deadline)
        $snapshot = Get-ClientRunSnapshot $HostSession $runId $Deadline
        $status = [string]$snapshot.status
        if ($status -in @("completed", "failed", "cancelled", "interrupted")) { $terminalSnapshot = $snapshot; break }
        if ($status -eq "waiting_approval") {
            $pendingHistory = Get-PublicRunHistory $HostSession $runId ([long]$snapshot.last_sequence) $Deadline
            $pending = @($pendingHistory | Where-Object event_type -eq "approval.requested") | Select-Object -Last 1
            $nextApprovalId = [string]$pending.approval_id
            if ($nextApprovalId -notmatch '^apr_[0-9a-f]{32}$' -or $approvalOrder -contains $nextApprovalId) { throw "Oracle failure: failed-test recovery met an invalid or repeated approval identity." }
            if ($approvalOrder.Count -ge 2) { throw "Oracle failure: failed-test recovery expected exactly two explicit approvals." }
            $approvalOrder.Add($nextApprovalId)
            $second = Invoke-ScenarioCliControl $ScenarioWorkspace $HostSession @("agent", "approve", $runId, $nextApprovalId) $Deadline
            if ($second.stdout -notmatch "approval $([regex]::Escape($nextApprovalId)) authorized") { throw "Oracle failure: CLI did not confirm the second explicit allow-once approval." }
            continue
        }
        Start-Sleep -Milliseconds 150
    }
    if ([string]$terminalSnapshot.status -cne "completed" -or $approvalOrder.Count -ne 2) {
        $failureHistory = Get-PublicRunHistory $HostSession $runId ([long]$terminalSnapshot.last_sequence) $Deadline
        $summaries = @($failureHistory | Where-Object { $_.event_type -in @("run.failed", "tool.execution_completed") } | ForEach-Object { "$($_.sequence):$($_.event_type):$($_.safe_summary)" }) -join "; "
        $payloadPreview = "unavailable"
        try {
            $failurePayloads = Get-ResultPayloads $Scenario $HostSession $failureHistory $Deadline
            $payloadPreview = @($failurePayloads | Where-Object event_type -eq "tool.execution_completed" | ForEach-Object { ([string]$_.content).Substring(0, [Math]::Min(260, ([string]$_.content).Length)) }) -join " || "
        } catch { $payloadPreview = "payload read failed: $($_.Exception.Message)" }
        throw "Oracle failure: failed-test recovery ended '$([string]$terminalSnapshot.status)' with $($approvalOrder.Count) approvals (code=$([string]$terminalSnapshot.terminal_error_code)). History: $summaries. Payloads: $payloadPreview"
    }
    $history = Get-PublicRunHistory $HostSession $runId ([long]$terminalSnapshot.last_sequence) $Deadline
    $executions = @($history | Where-Object event_type -eq "tool.execution_requested")
    $toolEvents = @($history | Where-Object event_type -eq "tool.execution_completed" | Sort-Object { [long]$_.sequence })
    $finalEvent = @($history | Sort-Object { [long]$_.sequence }) | Select-Object -Last 1
    if ($executions.Count -ne 4 -or $toolEvents.Count -ne 4 -or $finalEvent.event_type -ne "run.state_changed" -or [long]$finalEvent.sequence -ne [long]$terminalSnapshot.last_sequence) { throw "Oracle failure: failed-test recovery expected four executions and a terminal state change as the final durable event." }
    if ($toolEvents[0].safe_summary -in @("ok", "succeeded")) { throw "Oracle failure: the first test command did not record a failed receipt." }
    for ($index = 1; $index -lt $toolEvents.Count; $index++) {
        if ($toolEvents[$index].safe_summary -notin @("ok", "succeeded")) { throw "Oracle failure: the diagnostic read, recovery edit, and retried command needed successful receipts." }
    }
    $payloads = Get-ResultPayloads $Scenario $HostSession $history $Deadline
    $toolPayloads = @($payloads | Where-Object event_type -eq "tool.execution_completed")
    if ($toolPayloads.Count -ne 4) { throw "Oracle failure: failed-test recovery expected four client-readable tool payloads." }
    $payloadById = @{}
    foreach ($payload in $toolPayloads) { $payloadById[[string]$payload.payload_id] = $payload }
    $results = [System.Collections.Generic.List[object]]::new()
    foreach ($event in $toolEvents) {
        $payload = $payloadById[[string]$event.payload_ref]
        if ($null -eq $payload) { throw "Oracle failure: a failed-test receipt had no client-readable payload." }
        try { $results.Add(($payload.content | ConvertFrom-Json)) } catch { throw "Oracle failure: failed-test tool result was not bounded JSON." }
    }
    $failure = $results[0]
    $diagnostics = $results[1]
    $edit = $results[2]
    $rerun = $results[3]
    $expectedEdit = $Scenario.expectedFiles | Where-Object { $_.state -eq "changed_only_after_observed_failure" } | Select-Object -First 1
    if ([string]$failure.error -cne "process_exit_nonzero" -or [string]::IsNullOrWhiteSpace([string]$failure.spool_id)) { throw "Oracle failure: the first test command did not record a failed receipt with a retained spool identity." }
    if (-not ($diagnostics.PSObject.Properties.Name -contains "page") -or ([string]$diagnostics.page).IndexOf("Format trims surrounding whitespace.", [System.StringComparison]::Ordinal) -lt 0) { throw "Oracle failure: the retained terminal output page did not expose the failure diagnostic." }
    if ([string]$diagnostics.spool_id -cne [string]$failure.spool_id -or [int]$diagnostics.offset -ne 0) { throw "Oracle failure: the diagnostic read did not page the failed command's own spool from offset zero." }
    if ($null -eq $expectedEdit -or -not ($edit.PSObject.Properties.Name -contains "after_sha256") -or $edit.path -cne $expectedEdit.path -or $edit.after_sha256 -cne $expectedEdit.sha256) { throw "Oracle failure: the recovery edit did not bind to the expected file and final hash." }
    if ([int]$rerun.exit_code -ne 0) { throw "Oracle failure: the retried test command did not exit zero." }
    $rerunOutput = [string]$rerun.stdout_head + [string]$rerun.stdout_tail
    if ($rerunOutput.IndexOf("Fixture checks passed.", [System.StringComparison]::Ordinal) -lt 0) { throw "Oracle failure: the retried test command did not report its pass marker." }
    $stepCount = @($history | Where-Object event_type -eq "model.step_started").Count
    return [pscustomobject]@{ tool_calls = $toolEvents.Count; failed_tool_calls = 1; approval_requests = 2; model_steps = $stepCount }
}

function Get-ScriptedRestartContinueMetrics($Scenario, $ScenarioWorkspace, $HostSession, $Events, [DateTimeOffset]$Deadline, $CliRun) {
    $runId = [string]$CliRun.approval_run_id
    $firstApprovalId = [string]$CliRun.approval_id
    if ($runId -notmatch '^run_[0-9a-f]{32}$' -or $firstApprovalId -notmatch '^apr_[0-9a-f]{32}$') { throw "Oracle failure: restart flow did not expose a bounded pending approval command." }
    $waitingSnapshot = $null
    while ($null -eq $waitingSnapshot) {
        [void](Get-RemainingSeconds $Deadline)
        $snapshot = Get-ClientRunSnapshot $HostSession $runId $Deadline
        if ([string]$snapshot.status -eq "waiting_approval") { $waitingSnapshot = $snapshot; break }
        if ([string]$snapshot.status -in @("completed", "failed", "cancelled", "interrupted")) { throw "Oracle failure: the restarted host did not rehydrate the parked run (status '$([string]$snapshot.status)')." }
        Start-Sleep -Milliseconds 150
    }
    if (-not [string]::IsNullOrWhiteSpace([string]$CliRun.session_id) -and [string]$waitingSnapshot.session_id -cne [string]$CliRun.session_id) { throw "Oracle failure: the restarted run did not preserve its session identity." }
    $renewedApprovalId = $null
    $renewedSequence = 0L
    $approvalEvents = @()
    while ($null -eq $renewedApprovalId) {
        [void](Get-RemainingSeconds $Deadline)
        $snapshot = Get-ClientRunSnapshot $HostSession $runId $Deadline
        if ([string]$snapshot.status -in @("completed", "failed", "cancelled", "interrupted")) { throw "Oracle failure: the restarted run left waiting approval without a renewed approval (status '$([string]$snapshot.status)')." }
        $waitingEvents = Get-PublicRunHistory $HostSession $runId ([long]$snapshot.last_sequence) $Deadline
        $approvalEvents = @($waitingEvents | Where-Object event_type -eq "approval.requested")
        $preExecutions = @($waitingEvents | Where-Object event_type -in @("tool.execution_requested", "tool.execution_completed"))
        if ($preExecutions.Count -ne 0) { throw "Oracle failure: an execution started before the renewed approval." }
        if ($approvalEvents.Count -ge 1 -and [string]$approvalEvents[0].approval_id -cne $firstApprovalId) { throw "Oracle failure: the original approval identity was not preserved in durable history." }
        $renewed = @($approvalEvents | Where-Object { [string]$_.approval_id -ne $firstApprovalId })
        if ($renewed.Count -ge 1) {
            $renewedApprovalId = [string]$renewed[-1].approval_id
            $renewedSequence = [long]$renewed[-1].sequence
            break
        }
        Start-Sleep -Milliseconds 200
    }
    if ($renewedApprovalId -notmatch '^apr_[0-9a-f]{32}$') { throw "Oracle failure: the renewed approval identity was invalid." }
    $approval = Invoke-ScenarioCliControl $ScenarioWorkspace $HostSession @("agent", "approve", $runId, $renewedApprovalId) $Deadline
    if ($approval.stdout -notmatch "approval $([regex]::Escape($renewedApprovalId)) authorized") { throw "Oracle failure: CLI did not confirm the renewed allow-once approval." }
    $terminalSnapshot = Wait-ScenarioRunTerminal $HostSession $runId $Deadline
    if ([string]$terminalSnapshot.status -cne "completed") {
        $failureHistory = Get-PublicRunHistory $HostSession $runId ([long]$terminalSnapshot.last_sequence) $Deadline
        $summaries = @($failureHistory | Where-Object { $_.event_type -in @("run.failed", "tool.execution_completed") } | ForEach-Object { "$($_.sequence):$($_.event_type):$($_.safe_summary)" }) -join "; "
        throw "Oracle failure: restarted run ended '$([string]$terminalSnapshot.status)' (code=$([string]$terminalSnapshot.terminal_error_code)). History: $summaries"
    }
    $history = Get-PublicRunHistory $HostSession $runId ([long]$terminalSnapshot.last_sequence) $Deadline
    $executions = @($history | Where-Object event_type -eq "tool.execution_requested")
    $completions = @($history | Where-Object event_type -eq "tool.execution_completed")
    $steps = @($history | Where-Object event_type -eq "model.step_started")
    $finalEvent = @($history | Sort-Object { [long]$_.sequence }) | Select-Object -Last 1
    if ($executions.Count -ne 1 -or $completions.Count -ne 1 -or $steps.Count -ne 2 -or $finalEvent.event_type -ne "run.state_changed" -or [long]$finalEvent.sequence -ne [long]$terminalSnapshot.last_sequence) {
        throw "Oracle failure: restart flow expected exactly one executed retained call, two model steps, and a terminal state change as the final durable event; observed executions=$($executions.Count), completions=$($completions.Count), steps=$($steps.Count), final '$($finalEvent.event_type)'."
    }
    if ($completions[0].safe_summary -notin @("ok", "succeeded") -or [long]$executions[0].sequence -le $renewedSequence -or $renewed.Count -ne 1) { throw "Oracle failure: the retained call did not execute exactly once after a single renewed approval." }
    return [pscustomobject]@{ tool_calls = 1; failed_tool_calls = 0; approval_requests = 2; model_steps = $steps.Count }
}

function Invoke-ScenarioHandoffWorkflow($Scenario, $ScenarioWorkspace, $HostSession, [DateTimeOffset]$Deadline) {
    $sourceCli = Invoke-ScenarioCli $Scenario $ScenarioWorkspace $HostSession $Deadline
    $sourceRunId = $null
    foreach ($event in $sourceCli.events) {
        if ([string]$event.run_id -match '^run_[0-9a-f]{32}$') { $sourceRunId = [string]$event.run_id; break }
    }
    if ($null -eq $sourceRunId) { throw "Oracle failure: handoff source run did not expose a bounded run identity." }
    $sourceSnapshot = Get-ClientRunSnapshot $HostSession $sourceRunId $Deadline
    if ([string]$sourceSnapshot.status -cne "completed") { throw "Oracle failure: handoff source run did not complete (status '$([string]$sourceSnapshot.status)')." }
    $cleanupFailure = Stop-ProcessTree $HostSession.process
    if ($null -ne $cleanupFailure) { throw "case environment failure: handoff source host cleanup failed." }
    Start-Sleep -Milliseconds 300
    $scenarioRoot = Split-Path -Parent $ScenarioWorkspace.dataRoot
    $targetHost = Start-ScenarioHost $scenarioRoot $ScenarioWorkspace $Deadline
    $target = Invoke-ScenarioHandoffChat $Scenario $ScenarioWorkspace $targetHost $sourceRunId $Deadline $true
    $cleanupFailure = Stop-ProcessTree $targetHost.process
    if ($null -ne $cleanupFailure) { throw "case environment failure: handoff target host cleanup failed." }
    Start-Sleep -Milliseconds 300
    $controlScript = Join-Path $scenarioRoot "handoff-control-script.json"
    [System.IO.File]::WriteAllText($controlScript, (ConvertTo-Json -InputObject @($Scenario.controlSteps | Where-Object { $null -ne $_ }) -Depth 20), [System.Text.UTF8Encoding]::new($false))
    $controlHost = Start-ScenarioHost $scenarioRoot $ScenarioWorkspace $Deadline $controlScript
    $control = Invoke-ScenarioHandoffChat $Scenario $ScenarioWorkspace $controlHost $sourceRunId $Deadline $false
    return [pscustomobject]@{ cli = $target; host = $controlHost; source_run_id = $sourceRunId; source_session_id = $sourceCli.session_id; control = $control }
}

function Get-ScriptedHandoffMetrics($Scenario, $ScenarioWorkspace, $HostSession, $Events, [DateTimeOffset]$Deadline, $CliRun) {
    $sourceRunId = [string]$CliRun.source_run_id
    $targetRunId = [string]$CliRun.cli.run_id
    $controlRunId = [string]$CliRun.control.run_id
    if ($sourceRunId -notmatch '^run_[0-9a-f]{32}$' -or $targetRunId -notmatch '^run_[0-9a-f]{32}$' -or $controlRunId -notmatch '^run_[0-9a-f]{32}$') { throw "Oracle failure: handoff workflow did not expose bounded run identities (source='$sourceRunId', target='$targetRunId', control='$controlRunId')." }
    if ($targetRunId -eq $sourceRunId -or $controlRunId -eq $sourceRunId -or $controlRunId -eq $targetRunId) { throw "Oracle failure: handoff workflow reused a run identity." }
    $targetSnapshot = Get-ClientRunSnapshot $HostSession $targetRunId $Deadline
    $controlSnapshot = Get-ClientRunSnapshot $HostSession $controlRunId $Deadline
    if ([string]$targetSnapshot.status -cne "completed" -or [string]$controlSnapshot.status -cne "completed") { throw "Oracle failure: handoff target/control runs did not complete (target='$([string]$targetSnapshot.status)', control='$([string]$controlSnapshot.status)')." }
    if (-not [string]::IsNullOrWhiteSpace([string]$CliRun.source_session_id) -and [string]$targetSnapshot.session_id -eq [string]$CliRun.source_session_id) { throw "Oracle failure: the handoff target run did not start a new conversation session." }
    $targetHistory = Get-PublicRunHistory $HostSession $targetRunId ([long]$targetSnapshot.last_sequence) $Deadline
    $targetSteps = @($targetHistory | Where-Object event_type -eq "model.step_started")
    $targetApprovals = @($targetHistory | Where-Object event_type -eq "approval.requested")
    $targetTools = @($targetHistory | Where-Object { $_.event_type -in @("tool.execution_requested", "tool.execution_completed") })
    if ($targetSteps.Count -ne 1 -or $targetApprovals.Count -ne 0 -or $targetTools.Count -ne 0) { throw "Oracle failure: the selected-handoff target run did not stay a single text-only step with no approval or tool effects." }
    $controlHistory = Get-PublicRunHistory $HostSession $controlRunId ([long]$controlSnapshot.last_sequence) $Deadline
    $controlSteps = @($controlHistory | Where-Object event_type -eq "model.step_started")
    $controlApprovals = @($controlHistory | Where-Object event_type -eq "approval.requested")
    $controlTools = @($controlHistory | Where-Object { $_.event_type -in @("tool.execution_requested", "tool.execution_completed") })
    if ($controlSteps.Count -ne 1 -or $controlApprovals.Count -ne 0 -or $controlTools.Count -ne 0) { throw "Oracle failure: the handoff control run did not stay a single text-only step with no approval or tool effects." }
    $sourceSnapshot = Get-ClientRunSnapshot $HostSession $sourceRunId $Deadline
    if ([string]$sourceSnapshot.status -cne "completed") { throw "Oracle failure: the handoff source run was not durable after the workflow." }
    return [pscustomobject]@{ tool_calls = 0; failed_tool_calls = 0; approval_requests = 0; model_steps = $targetSteps.Count }
}

function Invoke-ScriptedScenario($Scenario, $BuildEvidence) {
    $scenarioRoot = New-OwnedScenarioRoot
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($Limits.CaseSeconds)
    $duration = [System.Diagnostics.Stopwatch]::StartNew()
    $outcome = "environment_failure"
    $failure = $null
    $managedHost = $null
    $cli = $null
    $metrics = $null
    $cleanupFailure = $null
    try {
        $scenarioWorkspace = New-ScenarioWorkspace $Scenario $scenarioRoot
        $BuildEvidence.Add((Invoke-DotnetBuild (Join-Path $repoRoot "backend-dotnet/RuBackend/RuBackend.csproj") $deadline))
        $BuildEvidence.Add((Invoke-DotnetBuild (Join-Path $repoRoot "backend-dotnet/RuAgent.Cli/RuAgent.Cli.csproj") $deadline))
        $managedHost = Start-ScenarioHost $scenarioRoot $scenarioWorkspace $deadline
        if ($Scenario.runnerKind -eq "scripted_turn_sequence") {
            $cli = Invoke-ScenarioTurnSequence $Scenario $scenarioWorkspace $managedHost $deadline
        } elseif ($Scenario.runnerKind -eq "scripted_denial_workflow") {
            $cli = Invoke-ScenarioCliUntilApproval $Scenario $scenarioWorkspace $managedHost $deadline
        } elseif ($Scenario.runnerKind -eq "scripted_terminal_paging") {
            $cli = Invoke-ScenarioCliUntilApproval $Scenario $scenarioWorkspace $managedHost $deadline
        } elseif ($Scenario.runnerKind -eq "scripted_edit_three_workflow") {
            $cli = Invoke-ScenarioCliUntilApproval $Scenario $scenarioWorkspace $managedHost $deadline
        } elseif ($Scenario.runnerKind -eq "scripted_failed_test_recovery") {
            $cli = Invoke-ScenarioCliUntilApproval $Scenario $scenarioWorkspace $managedHost $deadline
        } elseif ($Scenario.runnerKind -eq "scripted_restart_continue") {
            $restart = Invoke-ScenarioRestartToApproval $Scenario $scenarioWorkspace $managedHost $deadline
            $cli = $restart.cli
            $managedHost = $restart.host
        } elseif ($Scenario.runnerKind -eq "scripted_handoff_selection") {
            $handoff = Invoke-ScenarioHandoffWorkflow $Scenario $scenarioWorkspace $managedHost $deadline
            $cli = $handoff
            $managedHost = $handoff.host
        } else {
            $cli = Invoke-ScenarioCli $Scenario $scenarioWorkspace $managedHost $deadline
        }
        $metrics = Test-ScriptedWorkflow $Scenario $scenarioWorkspace $managedHost $cli.events $deadline $cli
        $outcome = "scripted_wiring_pass"
    } catch {
        $failure = $_.Exception.Message
        if ($failure.StartsWith("Oracle failure:", [System.StringComparison]::Ordinal)) { $outcome = "oracle_failure" }
        if ($failure.StartsWith("case timeout:", [System.StringComparison]::Ordinal)) { $outcome = "case_timeout" }
    } finally {
        $duration.Stop()
        if ($null -ne $cli -and $null -ne $cli.process) { $cleanupFailure = Stop-ProcessTree $cli.process }
        if ($null -ne $managedHost) {
            $hostCleanupFailure = Stop-ProcessTree $managedHost.process
            if ($null -eq $cleanupFailure) { $cleanupFailure = $hostCleanupFailure }
        }
        if ($null -eq $cleanupFailure) {
            try { Remove-ScenarioRoot $scenarioRoot }
            catch { $cleanupFailure = "Temporary scenario cleanup failed." }
        }
        if ($null -ne $cleanupFailure) {
            $outcome = "environment_failure"
            $failure = $cleanupFailure
        }
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

