param(
    [switch]$ValidateOnly,
    [switch]$RunScripted,
    [string]$ScenarioId = "recover_edit_no_match",
    [string]$ReportPath,
    [string]$FixturePath
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Net.Http
$Limits = @{
    FixtureBytes = 2MB
    ScenarioCount = 11
    WorkspaceOverlayCount = 16
    FileBytes = 256KB
    WorkspaceBytes = 1MB
    WorkspaceFiles = 64
    InputBytes = 8KB
    ExpectedFiles = 32
    Assertions = 32
    Turns = 32
    TurnBytes = 8KB
    CaseSeconds = 150
    ReadinessRequestSeconds = 2
    RepeatedLines = 4096
    OutputLines = 5000
    Sentinels = 32
    StdoutChars = 256KB
    StderrChars = 64KB
    HostStdoutChars = 16KB
    HostStderrChars = 32KB
    ResultPayloadBytes = 128KB
    ResultPayloadCount = 64
    HistoryEventCount = 256
    RunSnapshotBytes = 128KB
    MaxProcessTreeDepth = 8
    MaxTrackedProcesses = 64
    ProcessShutdownMilliseconds = 5000
}
$ownedScenarioRoots = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
Import-Module (Join-Path $PSScriptRoot "WorkflowEvaluationOracle.psm1") -Force
if ($ValidateOnly -and $RunScripted) { throw "Choose either -ValidateOnly or -RunScripted." }
if (-not $RunScripted) { $ValidateOnly = $true }
$repoRoot = Split-Path -Parent $PSScriptRoot
$defaultFixturePath = Join-Path $repoRoot "tests/fixtures/model-working-environment-scenarios.json"
$fixturePath = if ([string]::IsNullOrWhiteSpace($FixturePath)) { $defaultFixturePath } else { [System.IO.Path]::GetFullPath($FixturePath) }
$defaultReportPath = Join-Path $repoRoot "artifacts/model-working-environment/workflow-evaluation.json"
if ([string]::IsNullOrWhiteSpace($ReportPath)) { $ReportPath = $defaultReportPath }
$ReportPath = [System.IO.Path]::GetFullPath($ReportPath)
$fixtureInfo = Get-Item -LiteralPath $fixturePath
if ($fixtureInfo.Length -gt $Limits.FixtureBytes) { throw "Workflow fixture exceeds the 2 MiB cap." }
$fixtureJson = [System.IO.File]::ReadAllText($fixturePath, [System.Text.UTF8Encoding]::new($false, $true))
$fixture = $fixtureJson | ConvertFrom-Json
$scenarioIds = @($fixture.scenarios | ForEach-Object { $_.id })
if ($scenarioIds.Count -ne $Limits.ScenarioCount -or ($scenarioIds | Select-Object -Unique).Count -ne $Limits.ScenarioCount) {
    throw "The workflow fixture must contain exactly eleven unique scenarios."
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
        public DataReceivedEventHandler Handler { get { return Capture; } }
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

. (Join-Path $PSScriptRoot "WorkflowEvaluation.Workspace.ps1")
. (Join-Path $PSScriptRoot "WorkflowEvaluation.Process.ps1")
. (Join-Path $PSScriptRoot "WorkflowEvaluation.Transport.ps1")
. (Join-Path $PSScriptRoot "WorkflowEvaluation.Cases.ps1")
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
    if ($scenario.runnerKind -notin @("scripted_denial_workflow", "scripted_edit_three_workflow", "scripted_edit_tool_cycle", "scripted_failed_test_recovery", "scripted_paging_workflow", "scripted_restart_continue", "scripted_terminal_paging", "scripted_tool_batch", "scripted_text", "scripted_turn_sequence") -or $null -eq $scenario.scriptedSteps) { throw "Scenario '$ScenarioId' has no registered deterministic executor." }
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
        $scenarioDefinition = $_
        $scenarioRun = $results | Where-Object { $_.scenario_id -eq $scenarioDefinition.id } | Select-Object -First 1
        [pscustomobject]@{
            id = $scenarioDefinition.id
            capability = $scenarioDefinition.capabilityAtBaseline
            baseline_status = $scenarioDefinition.baselineStatus
            executor_kind = $scenarioDefinition.runnerKind
            execution_status = if ($null -ne $scenarioRun) { $scenarioRun.status } else { "not_run" }
            workspace_sha256 = $scenarioDefinition.workspaceSha256
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
