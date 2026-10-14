function Get-WorkspaceHash($Files) {
    $builder = [System.Text.StringBuilder]::new()
    foreach ($entry in $Files | Sort-Object { $_.path.ToLowerInvariant() }) {
        [void]$builder.Append($entry.path).Append([char]0).Append($entry.content).Append([char]0)
    }

    $bytes = [System.Text.Encoding]::UTF8.GetBytes($builder.ToString())
    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        return [System.BitConverter]::ToString($sha256.ComputeHash($bytes)).Replace("-", "").ToLowerInvariant()
    } finally {
        $sha256.Dispose()
    }
}

function Get-Utf8Sha256([string]$Content) {
    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        return [System.BitConverter]::ToString($sha256.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($Content))).Replace("-", "").ToLowerInvariant()
    } finally {
        $sha256.Dispose()
    }
}

function Get-FixtureContent($Entry) {
    switch ($Entry.kind) {
        "repeated_utf8" {
            if ($Entry.count -lt 1 -or $Entry.count -gt $Limits.RepeatedLines -or [System.Text.Encoding]::UTF8.GetByteCount($Entry.line) * $Entry.count + [System.Text.Encoding]::UTF8.GetByteCount($Entry.suffix) -gt $Limits.FileBytes) { throw "Repeated UTF-8 fixture exceeds its bounds." }
            $builder = [System.Text.StringBuilder]::new()
            for ($index = 0; $index -lt $Entry.count; $index++) { [void]$builder.Append($Entry.line) }
            [void]$builder.Append($Entry.suffix)
            return $builder.ToString()
        }
        "numbered_output" {
            if ($Entry.count -lt 1 -or $Entry.count -gt $Limits.OutputLines -or $Entry.sentinels.Count -gt $Limits.Sentinels -or [System.Text.Encoding]::UTF8.GetByteCount($Entry.linePrefix) * $Entry.count -gt $Limits.FileBytes) { throw "Numbered output fixture exceeds its bounds." }
            $builder = [System.Text.StringBuilder]::new()
            for ($index = 1; $index -le $Entry.count; $index++) {
                $line = if ($null -ne $Entry.sentinels -and $null -ne $Entry.sentinels."$index") { $Entry.sentinels."$index" } else { "$($Entry.linePrefix)$index" }
                [void]$builder.Append($line).Append("`n")
            }
            return $builder.ToString()
        }
        "sparse_search_output" {
            if ($Entry.count -lt 1 -or $Entry.count -gt $Limits.OutputLines -or $Entry.matchEvery -lt 2 -or $Entry.matchEvery -gt 32 -or $Entry.sentinels.Count -gt $Limits.Sentinels) { throw "Sparse search fixture exceeds its bounds." }
            $builder = [System.Text.StringBuilder]::new()
            for ($index = 1; $index -le $Entry.count; $index++) {
                if (($index % $Entry.matchEvery) -eq ($Entry.matchEvery - 1)) {
                    $line = if ($null -ne $Entry.sentinels -and $null -ne $Entry.sentinels."$index") { $Entry.sentinels."$index" } else { "$($Entry.linePrefix)$index" }
                } else {
                    $line = "unrelated note $index"
                }
                [void]$builder.Append($line).Append("`n")
            }
            if ([System.Text.Encoding]::UTF8.GetByteCount($builder.ToString()) -gt $Limits.FileBytes) { throw "Sparse search fixture exceeds its byte bound." }
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

function Get-WorkspaceRelativePath([string]$WorkspacePath, [string]$FullPath) {
    $separator = [System.IO.Path]::DirectorySeparatorChar
    $root = [System.IO.Path]::GetFullPath($WorkspacePath).TrimEnd($separator) + $separator
    $resolved = [System.IO.Path]::GetFullPath($FullPath)
    if (-not $resolved.StartsWith($root, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Workspace enumeration returned a path outside its root."
    }

    return $resolved.Substring($root.Length).Replace([char]92, [char]47)
}

function Get-DirectoryWorkspaceHash([string]$WorkspacePath) {
    $files = [System.Collections.Generic.List[object]]::new()
    foreach ($item in Get-ChildItem -LiteralPath $WorkspacePath -File -Recurse) {
        $relativePath = Get-WorkspaceRelativePath $WorkspacePath $item.FullName
        $bytes = [System.IO.File]::ReadAllBytes($item.FullName)
        $content = [System.Text.UTF8Encoding]::new($false, $true).GetString($bytes)
        $files.Add([pscustomobject]@{ path = $relativePath; content = $content })
    }

    return Get-WorkspaceHash $files
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
        [void]$actualPaths.Add((Get-WorkspaceRelativePath $WorkspacePath $item.FullName))
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
    if ([System.Text.Encoding]::UTF8.GetByteCount($entry.content) -gt $Limits.FileBytes) { throw "Base workspace file '$($entry.path)' exceeds 256 KiB." }
}
$workspaceHash = Get-WorkspaceHash $fixture.workspaceFiles
foreach ($scenario in $fixture.scenarios) {
    if ($scenario.workspaceOverlay.Count -gt $Limits.WorkspaceOverlayCount) { throw "Scenario '$($scenario.id)' exceeds the workspace overlay cap." }
    $seenOverlayPaths = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $scenario.workspaceOverlay) {
        Assert-SafeRelativePath $entry.path "Workspace overlay path"
        if (-not $seenOverlayPaths.Add($entry.path)) { throw "Scenario '$($scenario.id)' repeats workspace overlay path '$($entry.path)'." }
        if ($entry.kind -eq "literal" -and [System.Text.Encoding]::UTF8.GetByteCount($entry.content) -gt $Limits.FileBytes) { throw "Workspace overlay '$($entry.path)' exceeds 256 KiB." }
        if ($entry.kind -notin @("literal", "repeated_utf8", "numbered_output", "sparse_search_output")) { throw "Scenario '$($scenario.id)' has an unknown workspace overlay kind." }
    }
    $scenarioFiles = Get-ScenarioWorkspaceFiles $scenario
    $scenarioHash = Get-WorkspaceHash $scenarioFiles
    if ($scenario.workspaceSha256 -ne $scenarioHash) {
        throw "Workspace hash mismatch for scenario '$($scenario.id)': expected $($scenario.workspaceSha256), got $scenarioHash."
    }
    if ([string]::IsNullOrWhiteSpace($scenario.input) -or [System.Text.Encoding]::UTF8.GetByteCount($scenario.input) -gt $Limits.InputBytes -or $scenario.expectedFiles.Count -eq 0 -or $scenario.expectedFiles.Count -gt $Limits.ExpectedFiles -or $scenario.assertions.Count -eq 0 -or $scenario.assertions.Count -gt $Limits.Assertions) {
        throw "Scenario '$($scenario.id)' is missing its input or oracle."
    }
    if ($scenario.preset -notin @("plan", "standard")) {
        throw "Scenario '$($scenario.id)' has a preset outside the approved offline baseline."
    }
    if ($scenario.turns.Count -gt $Limits.Turns) { throw "Scenario '$($scenario.id)' exceeds the conversation turn cap." }
    foreach ($turn in $scenario.turns) {
        if ([System.Text.Encoding]::UTF8.GetByteCount($turn) -gt $Limits.TurnBytes) { throw "Scenario '$($scenario.id)' has an oversized turn." }
    }
    if ($scenarioFiles.Count -eq 0 -or $scenarioFiles.Count -gt $Limits.WorkspaceFiles) { throw "Scenario '$($scenario.id)' exceeds the workspace file cap." }
    $seenWorkspacePaths = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    $workspaceBytes = 0
    foreach ($entry in $scenarioFiles) {
        Assert-SafeRelativePath $entry.path "Workspace path"
        if (-not $seenWorkspacePaths.Add($entry.path)) { throw "Workspace path '$($entry.path)' is duplicated." }
        $fileBytes = [System.Text.Encoding]::UTF8.GetByteCount($entry.content)
        if ($fileBytes -gt $Limits.FileBytes) { throw "Workspace file '$($entry.path)' exceeds 256 KiB." }
        $workspaceBytes += $fileBytes
    }
    if ($workspaceBytes -gt $Limits.WorkspaceBytes) {
        throw "The scenario workspace is empty or exceeds 1 MiB."
    }
    $seenExpectedPaths = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $scenario.expectedFiles) {
        Assert-SafeRelativePath $entry.path "Expected file path"
        if (-not $seenExpectedPaths.Add($entry.path)) { throw "Scenario '$($scenario.id)' repeats expected path '$($entry.path)'." }
        if ($entry.state -in @("changed_to_expected", "changed_only_after_observed_failure") -and ([string]::IsNullOrEmpty($entry.content) -or [string]::IsNullOrWhiteSpace($entry.sha256))) { throw "Changed expected file '$($entry.path)' needs exact content and SHA-256." }
        if ($entry.state -notin @("unchanged", "unchanged_until_constraint_is_recalled", "changed_to_expected", "changed_only_after_observed_failure")) { throw "Scenario '$($scenario.id)' has an unknown file oracle state." }
        if ($entry.state -in @("changed_to_expected", "changed_only_after_observed_failure")) {
            $expectedHash = Get-Utf8Sha256 $entry.content
            if ($expectedHash -ne $entry.sha256) { throw "Expected content hash mismatch for '$($entry.path)'." }
        }
        if ($null -ne $entry.content -and [System.Text.Encoding]::UTF8.GetByteCount($entry.content) -gt $Limits.FileBytes) { throw "Expected file '$($entry.path)' exceeds 256 KiB." }
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
    $externalFiles = if ($null -eq $Scenario.externalFiles) { @() } else { @($Scenario.externalFiles) }
    if ($externalFiles.Count -gt 4) { throw "Scenario external-file fixture exceeds its count cap." }
    foreach ($entry in $externalFiles) {
        Assert-SafeRelativePath $entry.path "External fixture path"
        if ([System.Text.Encoding]::UTF8.GetByteCount([string]$entry.content) -gt $Limits.FileBytes) { throw "Scenario external-file fixture exceeds its byte cap." }
        $filePath = [System.IO.Path]::GetFullPath((Join-Path $ScenarioRoot $entry.path))
        $ownedRoot = [System.IO.Path]::GetFullPath($ScenarioRoot).TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
        if (-not $filePath.StartsWith($ownedRoot, [System.StringComparison]::OrdinalIgnoreCase)) { throw "External fixture path escaped the owned scenario root." }
        New-Item -ItemType Directory -Path (Split-Path -Parent $filePath) -Force | Out-Null
        [System.IO.File]::WriteAllText($filePath, [string]$entry.content, [System.Text.UTF8Encoding]::new($false))
    }
    if ((Get-DirectoryWorkspaceHash $workspace) -ne $Scenario.workspaceSha256) { throw "Temporary workspace does not match its oracle hash." }
    $scriptedJson = ConvertTo-Json -InputObject @($Scenario.scriptedSteps) -Depth 20
    [System.IO.File]::WriteAllText($scriptPath, $scriptedJson, [System.Text.UTF8Encoding]::new($false))
    return [pscustomobject]@{ dataRoot = $dataRoot; workspace = $workspace; scriptPath = $scriptPath; files = $files; externalFiles = $externalFiles }
}
