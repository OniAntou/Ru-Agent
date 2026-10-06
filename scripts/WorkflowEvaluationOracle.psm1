function Get-ExpectedAssertion($Scenario, [string]$Kind) {
    $assertion = $Scenario.assertions | Where-Object kind -eq $Kind | Select-Object -First 1
    if ($null -eq $assertion -or $null -eq $assertion.equals) {
        throw "Oracle failure: scenario '$($Scenario.id)' is missing assertion '$Kind'."
    }
    return $assertion.equals
}

function Get-ScriptedEditMetrics($Scenario, $Events) {
    $expectedError = Get-ExpectedAssertion $Scenario "first_edit_error"
    $expectedSuccesses = [int](Get-ExpectedAssertion $Scenario "successful_recovery_edit_count")
    $expectedStatus = Get-ExpectedAssertion $Scenario "final_status"
    $toolEvents = @($Events | Where-Object event_type -eq "tool.execution_completed" | Sort-Object { [long]$_.sequence })
    $completionEvents = @($Events | Where-Object { $_.event_type -eq "run.state_changed" -and $_.status -eq $expectedStatus })
    $terminalEvents = @($Events | Where-Object event_type -eq "run.state_changed")
    $expectedToolCount = 1 + $expectedSuccesses
    $orderedMatches = $toolEvents.Count -eq $expectedToolCount
    if ($orderedMatches) {
        if ($toolEvents[0].safe_summary -cne $expectedError) { $orderedMatches = $false }
        for ($index = 1; $index -lt $toolEvents.Count; $index++) {
            if ($toolEvents[$index].safe_summary -notin @("ok", "succeeded")) {
                $orderedMatches = $false
                break
            }
        }
    }
    $hasIncreasingSequence = $true
    for ($index = 1; $index -lt $toolEvents.Count; $index++) {
        if ([long]$toolEvents[$index].sequence -le [long]$toolEvents[$index - 1].sequence) {
            $hasIncreasingSequence = $false
            break
        }
    }
    $completionFollowsTools = $false
    if ($completionEvents.Count -eq 1 -and $toolEvents.Count -eq $expectedToolCount) {
        $completionFollowsTools = [long]$completionEvents[0].sequence -gt [long]$toolEvents[-1].sequence
    }
    if (-not $orderedMatches -or -not $hasIncreasingSequence -or $completionEvents.Count -ne 1 -or -not $completionFollowsTools) {
        throw "Oracle failure: expected $($expectedToolCount) ordered tool completions ($expectedError then $expectedSuccesses success) and one later '$expectedStatus' completion; observed tool summaries '$(@($toolEvents | ForEach-Object safe_summary) -join ',')' and $($completionEvents.Count) matching terminal events."
    }
    return [pscustomobject]@{ tool_calls = $toolEvents.Count; failed_tool_calls = 1; approval_requests = @($Events | Where-Object event_type -eq "approval.requested").Count; model_steps = @($Events | Where-Object event_type -eq "model.step_started").Count }
}

function Get-ScriptedTextMetrics($Scenario, $Events, $Payloads) {
    $expectedSentinel = Get-ExpectedAssertion $Scenario "assistant_payload_contains"
    $expectedStatus = Get-ExpectedAssertion $Scenario "final_status"
    $toolEvents = @($Events | Where-Object event_type -eq "tool.execution_completed")
    $modelSteps = @($Events | Where-Object event_type -eq "model.step_started")
    $completionEvents = @($Events | Where-Object { $_.event_type -eq "run.state_changed" -and $_.status -eq $expectedStatus })
    $assistantPayloads = @($Payloads | Where-Object event_type -eq "model.step_completed")
    $hasExpectedReply = $assistantPayloads.Count -eq 1
    if ($hasExpectedReply) { $hasExpectedReply = $assistantPayloads[0].content.IndexOf([string]$expectedSentinel, [System.StringComparison]::Ordinal) -ge 0 }
    if ($toolEvents.Count -ne 0 -or $modelSteps.Count -ne 1 -or $completionEvents.Count -ne 1 -or -not $hasExpectedReply) {
        throw "Oracle failure: text-only workflow expected one model step, no tool receipts, and one '$expectedStatus' completion."
    }

    return [pscustomobject]@{ tool_calls = 0; failed_tool_calls = 0; approval_requests = 0; model_steps = 1 }
}

function Get-ScriptedTurnSequenceMetrics($Scenario, $Events, $Payloads) {
    $expectedTurns = @($Scenario.turns).Count
    $expectedSentinel = Get-ExpectedAssertion $Scenario "final_assistant_payload_contains"
    $expectedStatus = Get-ExpectedAssertion $Scenario "final_status"
    $toolEvents = @($Events | Where-Object event_type -eq "tool.execution_completed")
    $modelSteps = @($Events | Where-Object event_type -eq "model.step_started")
    $completedRuns = @($Events | Where-Object { $_.event_type -eq "run.state_changed" -and $_.status -eq $expectedStatus })
    $distinctRuns = @($completedRuns | ForEach-Object run_id | Select-Object -Unique)
    $assistantPayloads = @($Payloads | Where-Object event_type -eq "model.step_completed")
    $lastReplyContains = $assistantPayloads.Count -eq $expectedTurns
    if ($lastReplyContains) {
        $lastReplyContains = $assistantPayloads[-1].content.IndexOf([string]$expectedSentinel, [System.StringComparison]::Ordinal) -ge 0
    }
    $isValid = $toolEvents.Count -eq 0 -and
        $modelSteps.Count -eq $expectedTurns -and
        $completedRuns.Count -eq $expectedTurns -and
        $distinctRuns.Count -eq $expectedTurns -and
        $lastReplyContains
    if (-not $isValid) {
        throw "Oracle failure: turn sequence expected $expectedTurns separate completed runs, no tools, and a final client-readable assistant marker."
    }
    return [pscustomobject]@{ tool_calls = 0; failed_tool_calls = 0; approval_requests = 0; model_steps = $modelSteps.Count }
}

function Get-ScriptedToolBatchMetrics($Scenario, $Events, $Payloads, $WorkspaceFiles) {
    $batchAssertion = $Scenario.assertions | Where-Object kind -eq "distinct_files_inspected" | Select-Object -First 1
    if ($null -eq $batchAssertion -or $null -eq $batchAssertion.at_least) { throw "Oracle failure: independent read batch has no concrete call-count lower bound." }
    $minimumCalls = [int]$batchAssertion.at_least
    $expectedStatus = Get-ExpectedAssertion $Scenario "final_status"
    $toolEvents = @($Events | Where-Object event_type -eq "tool.execution_completed" | Sort-Object { [long]$_.sequence })
    $completionEvents = @($Events | Where-Object { $_.event_type -eq "run.state_changed" -and $_.status -eq $expectedStatus })
    $toolPayloads = @($Payloads | Where-Object event_type -eq "tool.execution_completed")
    if ($toolEvents.Count -ne $minimumCalls -or $toolPayloads.Count -ne $minimumCalls -or $completionEvents.Count -ne 1) {
        throw "Oracle failure: independent read batch expected $minimumCalls durable result payloads and one '$expectedStatus' completion."
    }

    $lastSequence = 0L
    foreach ($event in $toolEvents) {
        if ([long]$event.sequence -le $lastSequence -or $event.safe_summary -notin @("ok", "succeeded")) {
            throw "Oracle failure: independent read receipts must be ordered and successful."
        }
        $lastSequence = [long]$event.sequence
    }

    $readResults = @($toolPayloads | ForEach-Object { Get-ReadResult $_ })
    $inspectedPaths = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($expected in $Scenario.expectedFiles) {
        $source = $WorkspaceFiles | Where-Object path -eq $expected.path | Select-Object -First 1
        $matchedRead = @($readResults | Where-Object { $_.path -eq $expected.path -and $_.content -ceq $source.content })
        if ($null -eq $source -or $matchedRead.Count -ne 1 -or -not $inspectedPaths.Add($expected.path)) {
            throw "Oracle failure: no client-readable result payload contained the exact source for '$($expected.path)'."
        }
    }

    if ([long]$completionEvents[0].sequence -le $lastSequence) { throw "Oracle failure: completion did not follow the read batch." }
    return [pscustomobject]@{ tool_calls = $toolEvents.Count; failed_tool_calls = 0; approval_requests = @($Events | Where-Object event_type -eq "approval.requested").Count; model_steps = @($Events | Where-Object event_type -eq "model.step_started").Count }
}

function Get-ReadResult($Payload) {
    try {
        $result = $Payload.content | ConvertFrom-Json
        if ($result.path -is [string] -and $result.content -is [string] -and $result.truncated -is [bool]) { return $result }
    } catch {
    }

    return $null
}

function Get-ScriptedPagingMetrics($Scenario, $Events, $Payloads, $WorkspaceFiles) {
    $minimumCalls = [int](Get-ExpectedAssertion $Scenario "minimum_tool_calls")
    $expectedStatus = Get-ExpectedAssertion $Scenario "final_status"
    $toolEvents = @($Events | Where-Object event_type -eq "tool.execution_completed" | Sort-Object { [long]$_.sequence })
    $completionEvents = @($Events | Where-Object { $_.event_type -eq "run.state_changed" -and $_.status -eq $expectedStatus })
    $terminalEvents = @($Events | Where-Object event_type -eq "run.state_changed")
    $completionFollowsTools = $false
    if ($completionEvents.Count -eq 1 -and $toolEvents.Count -ge $minimumCalls) {
        $completionFollowsTools = [long]$completionEvents[0].sequence -gt [long]$toolEvents[-1].sequence
    }
    if ($toolEvents.Count -lt $minimumCalls -or $completionEvents.Count -ne 1 -or -not $completionFollowsTools) {
        $summaries = @($toolEvents | ForEach-Object safe_summary) -join ","
        $statuses = @($terminalEvents | ForEach-Object status) -join ","
        throw "Oracle failure: expected at least $minimumCalls tool completions and one later '$expectedStatus' completion; observed $($toolEvents.Count) tool completions [$summaries], terminal statuses [$statuses], and $(@($Events | Where-Object event_type -eq 'model.step_started').Count) model steps."
    }

    $sentinelAssertion = $Scenario.assertions | Where-Object { $_.kind -in @("expected_utf8_sentinel_seen", "marker_found_after_first_page") } | Select-Object -First 1
    if ($null -eq $sentinelAssertion -or [string]::IsNullOrWhiteSpace($sentinelAssertion.sentinel)) {
        throw "Oracle failure: paged read scenario has no concrete result marker oracle."
    }

    $toolPayloads = @($Payloads | Where-Object event_type -eq "tool.execution_completed")
    $markerPage = -1
    if ($Scenario.id -eq "grep_beyond_first_page") {
        $expectedFile = $WorkspaceFiles | Where-Object path -ceq $sentinelAssertion.path | Select-Object -First 1
        $query = [string]$Scenario.scriptedSteps[0].tool.arguments.query
        if ($null -eq $expectedFile -or [string]::IsNullOrWhiteSpace($query)) { throw "Oracle failure: grep fixture source/query is unavailable." }
        $expectedLines = [System.Collections.Generic.List[int]]::new()
        $lineNumber = 0
        foreach ($line in ($expectedFile.content -split "`n")) {
            $lineNumber++
            if ($line.IndexOf($query, [System.StringComparison]::Ordinal) -ge 0) { $expectedLines.Add($lineNumber) }
        }
        $actualLines = [System.Collections.Generic.List[int]]::new()
        $seenLines = [System.Collections.Generic.HashSet[int]]::new()
        for ($index = 0; $index -lt $toolPayloads.Count; $index++) {
            $result = Get-GrepResult $toolPayloads[$index]
            if ($null -eq $result) { continue }
            foreach ($item in $result.results) {
                $lineNumber = 0
                $isLineNumber = [int]::TryParse([string]$item.line, [ref]$lineNumber)
                $lineText = if ($item.line_text -is [string]) { $item.line_text } else { $item.preview }
                if ($item.path -cne [string]$sentinelAssertion.path -or -not $isLineNumber -or $lineText -isnot [string] -or $lineText.IndexOf($query, [System.StringComparison]::Ordinal) -lt 0 -or -not $seenLines.Add($lineNumber)) {
                    throw "Oracle failure: grep pages contained an invalid, duplicate, or unexpected file result."
                }
                $actualLines.Add($lineNumber)
            }
            $matched = @($result.results | Where-Object {
                $lineText = if ($_.line_text -is [string]) { $_.line_text } else { $_.preview }
                $_.path -ceq [string]$sentinelAssertion.path -and
                [int]$_.line -eq [int]$sentinelAssertion.line -and
                $lineText -is [string] -and
                $lineText.IndexOf([string]$sentinelAssertion.sentinel, [System.StringComparison]::Ordinal) -ge 0
            })
            if ($matched.Count -gt 0) { $markerPage = $index }
        }
        if ($actualLines.Count -ne $expectedLines.Count -or (($actualLines.ToArray() -join ",") -cne ($expectedLines.ToArray() -join ","))) {
            throw "Oracle failure: grep paging must return every expected matching source line exactly once and in order."
        }
        $pageSize = [int]$Scenario.scriptedSteps[0].tool.arguments.max_results
        $expectedPages = [int][Math]::Ceiling($expectedLines.Count / [double]$pageSize)
        if ($toolPayloads.Count -ne $expectedPages) { throw "Oracle failure: grep result pages did not cover the complete matching source set." }
    } else {
        for ($index = 0; $index -lt $toolPayloads.Count; $index++) {
            $result = Get-ReadResult $toolPayloads[$index]
            $isExpectedResult = $null -ne $result -and
                $result.path -ceq [string]$sentinelAssertion.path -and
                $result.content.IndexOf([string]$sentinelAssertion.sentinel, [System.StringComparison]::Ordinal) -ge 0
            if ($isExpectedResult) {
                $markerPage = $index
                break
            }
        }
    }
    if ($markerPage -lt 0) {
        $pageLengths = @($toolPayloads | ForEach-Object { $_.content.Length }) -join ","
        $toolSummaries = @($toolEvents | ForEach-Object safe_summary) -join ","
        $pageCursors = @($toolPayloads | ForEach-Object {
            try {
                $page = $_.content | ConvertFrom-Json
                "next=$($page.projection_next_offset);count=$($page.count);results=$(@($page.results).Count);truncated=$($page.truncated)"
            } catch { "non_json" }
        }) -join ","
        throw "Oracle failure: no durable client-readable tool result contained '$($sentinelAssertion.sentinel)' (payloads=$($toolPayloads.Count), lengths=[$pageLengths], pages=[$pageCursors], tool_results=$($toolEvents.Count), summaries=[$toolSummaries])."
    }
    if ($Scenario.id -eq "grep_beyond_first_page" -and $markerPage -eq 0) { throw "Oracle failure: grep sentinel was not returned after the first result page." }

    if ($Scenario.id -eq "read_long_utf8_file") {
        $expectedFile = $WorkspaceFiles | Where-Object path -ceq $sentinelAssertion.path | Select-Object -First 1
        $firstRead = Get-ReadResult $toolPayloads[0]
        $continuedRead = $null
        foreach ($payload in ($toolPayloads | Select-Object -Skip 1)) {
            $candidate = Get-ReadResult $payload
            if ($null -ne $candidate -and [long]$candidate.offset -gt 0) { $continuedRead = $candidate; break }
        }
        $emoji = [char]::ConvertFromUtf32(0x1F642)
        $sourceBytes = if ($null -ne $expectedFile) { [System.Text.Encoding]::UTF8.GetBytes([string]$expectedFile.content) } else { [byte[]]::new(0) }
        $expectedSuffix = if ($null -ne $continuedRead -and [long]$continuedRead.offset -le $sourceBytes.Length) {
            [System.Text.UTF8Encoding]::new($false, $true).GetString($sourceBytes, [int]$continuedRead.offset, $sourceBytes.Length - [int]$continuedRead.offset)
        } else { $null }
        $sha = [System.Security.Cryptography.SHA256]::Create()
        try { $expectedHash = [System.BitConverter]::ToString($sha.ComputeHash($sourceBytes)).Replace("-", "").ToLowerInvariant() }
        finally { $sha.Dispose() }
        $isExactUtf8Read = $null -ne $expectedFile -and $null -ne $firstRead -and $null -ne $continuedRead -and
            [long]$continuedRead.offset -gt 0 -and
            $firstRead.path -ceq [string]$sentinelAssertion.path -and
            $firstRead.offset -eq 0 -and $firstRead.content.StartsWith([string][char]0xFEFF, [System.StringComparison]::Ordinal) -and
            $firstRead.content.IndexOf($emoji, [System.StringComparison]::Ordinal) -ge 0 -and
            $continuedRead.path -ceq [string]$sentinelAssertion.path -and
            $continuedRead.content -ceq $expectedSuffix -and
            $continuedRead.content.IndexOf([string]$sentinelAssertion.sentinel, [System.StringComparison]::Ordinal) -eq $expectedSuffix.IndexOf([string]$sentinelAssertion.sentinel, [System.StringComparison]::Ordinal) -and
            $continuedRead.total_bytes -eq $sourceBytes.Length -and $continuedRead.bytes_read -eq $sourceBytes.Length - [int]$continuedRead.offset -and
            $continuedRead.sha256 -ceq $expectedHash -and $continuedRead.truncated -eq $false
        if (-not $isExactUtf8Read) {
            $readSummaries = @($toolPayloads | ForEach-Object {
                $read = Get-ReadResult $_
                if ($null -eq $read) { "unparsed" }
                else {
                    "path_match=$($read.path -ceq [string]$sentinelAssertion.path);offset=$($read.offset);bytes=$($read.bytes_read)/$($read.total_bytes);chars=$($read.content.Length);marker_index=$($read.content.IndexOf([string]$sentinelAssertion.sentinel, [System.StringComparison]::Ordinal));tail=$($read.content.Substring([Math]::Max(0, $read.content.Length - 24)).Replace("`r", "\\r").Replace("`n", "\\n"));truncated=$($read.truncated);sentinel=$($read.content.EndsWith([string]$sentinelAssertion.sentinel, [System.StringComparison]::Ordinal));hash_match=$($read.sha256 -ceq $expectedHash)"
                }
            }) -join ";"
            throw "Oracle failure: later model proposal and nonzero-offset read did not preserve the exact UTF-8 source suffix/hash for the expected path (reads=$readSummaries)."
        }
    }

    return [pscustomobject]@{
        tool_calls = $toolEvents.Count
        failed_tool_calls = @($toolEvents | Where-Object safe_summary -notin @("ok", "succeeded")).Count
        approval_requests = @($Events | Where-Object event_type -eq "approval.requested").Count
        model_steps = @($Events | Where-Object event_type -eq "model.step_started").Count
    }
}

function Get-GrepResult($Payload) {
    try {
        $result = $Payload.content | ConvertFrom-Json
        if ($result.results -is [System.Array]) { return $result }
    } catch { }
    return $null
}

Export-ModuleMember -Function Get-ExpectedAssertion, Get-ScriptedEditMetrics, Get-ScriptedPagingMetrics, Get-ScriptedTextMetrics, Get-ScriptedToolBatchMetrics, Get-ScriptedTurnSequenceMetrics
