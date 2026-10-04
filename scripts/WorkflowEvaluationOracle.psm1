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

Export-ModuleMember -Function Get-ScriptedEditMetrics
