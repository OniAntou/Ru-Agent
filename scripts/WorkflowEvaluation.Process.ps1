function Get-LoopbackBearerToken {
    $bytes = [byte[]]::new(32)
    $generator = [System.Security.Cryptography.RNGCryptoServiceProvider]::new()
    try {
        $generator.GetBytes($bytes)
        return [System.BitConverter]::ToString($bytes).Replace("-", "").ToLowerInvariant()
    } finally {
        $generator.Dispose()
    }
}

function Get-RemainingSeconds([DateTimeOffset]$Deadline) {
    $remaining = [int][Math]::Ceiling(($Deadline - [DateTimeOffset]::UtcNow).TotalSeconds)
    if ($remaining -le 0) { throw "case timeout: the bounded workflow deadline expired." }
    return $remaining
}

function Enable-BoundedProcessOutput([System.Diagnostics.Process]$Process, [int]$StdoutLimit = $Limits.StdoutChars, [int]$StderrLimit = $Limits.StderrChars) {
    $stdout = [RuWorkflow.BoundedProcessOutput]::new($StdoutLimit)
    $stderr = [RuWorkflow.BoundedProcessOutput]::new($StderrLimit)
    $Process.add_OutputDataReceived($stdout.Handler)
    $Process.add_ErrorDataReceived($stderr.Handler)
    $Process.BeginOutputReadLine()
    $Process.BeginErrorReadLine()
    return [pscustomobject]@{ stdout = $stdout; stderr = $stderr }
}

function ConvertTo-WindowsArgument([string]$Argument) {
    $builder = [System.Text.StringBuilder]::new()
    [void]$builder.Append('"')
    $backslashes = 0
    foreach ($character in $Argument.ToCharArray()) {
        if ($character -eq [char]92) {
            $backslashes++
            continue
        }

        if ($character -eq '"') {
            [void]$builder.Append([char]92, ($backslashes * 2) + 1).Append('"')
            $backslashes = 0
            continue
        }

        if ($backslashes -gt 0) { [void]$builder.Append([char]92, $backslashes) }
        [void]$builder.Append($character)
        $backslashes = 0
    }

    if ($backslashes -gt 0) { [void]$builder.Append([char]92, $backslashes * 2) }
    [void]$builder.Append('"')
    return $builder.ToString()
}

function Add-ProcessArguments([System.Diagnostics.ProcessStartInfo]$Start, [string[]]$Arguments) {
    $argumentListProperty = $Start.GetType().GetProperty("ArgumentList")
    if ($null -ne $argumentListProperty) {
        $argumentList = $argumentListProperty.GetValue($Start)
        foreach ($argument in $Arguments) { [void]$argumentList.Add($argument) }
        return
    }

    $Start.Arguments = ($Arguments | ForEach-Object { ConvertTo-WindowsArgument $_ }) -join " "
}

function Invoke-DotnetBuild([string]$ProjectPath, [DateTimeOffset]$Deadline) {
    $start = New-Object System.Diagnostics.ProcessStartInfo
    $start.FileName = "dotnet"
    $start.WorkingDirectory = $repoRoot
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    Add-ProcessArguments $start @("build", $ProjectPath, "-c", "Debug", "--nologo")
    $process = [System.Diagnostics.Process]::Start($start)
    $capture = Enable-BoundedProcessOutput $process
    if (-not $process.WaitForExit((Get-RemainingSeconds $Deadline) * 1000)) {
        $cleanupFailure = Stop-ProcessTree $process
        if ($null -ne $cleanupFailure) { throw "case environment failure: isolated build cleanup failed." }
        throw "case timeout: dotnet build exceeded the workflow deadline for '$ProjectPath'."
    }
    $process.WaitForExit()
    if ($capture.stdout.Exceeded -or $capture.stderr.Exceeded) { throw "dotnet build output exceeded its collection bounds." }
    if ($process.ExitCode -ne 0) { throw "dotnet build failed with exit code $($process.ExitCode) for '$ProjectPath'. $($capture.stderr.Text)" }
    return [pscustomobject]@{ command = "dotnet build $ProjectPath -c Debug --nologo"; exit_code = $process.ExitCode; stdout = $capture.stdout.Text; stderr = $capture.stderr.Text }
}

function Stop-ProcessTree([System.Diagnostics.Process]$Process) {
    if ($Process.HasExited) { $Process.Dispose(); return $null }
    $shutdownDeadline = [DateTimeOffset]::UtcNow.AddMilliseconds($Limits.ProcessShutdownMilliseconds)
    $trackedChildren = [System.Collections.Generic.List[System.Diagnostics.Process]]::new()
    try {
        $killTree = $Process.GetType().GetMethod("Kill", [type[]]@([bool]))
        if ($null -ne $killTree) {
            if (-not $Process.HasExited) { [void]$killTree.Invoke($Process, @($true)) }
        } else {
            $descendants = [System.Collections.Generic.List[int]]::new()
            Add-ProcessDescendants $Process.Id $descendants 0
            foreach ($processId in $descendants) {
                $child = [System.Diagnostics.Process]::GetProcessById($processId)
                $trackedChildren.Add($child)
                if (-not $child.HasExited) { $child.Kill() }
            }
            if (-not $Process.HasExited) { $Process.Kill() }
        }
        foreach ($child in $trackedChildren) {
            $remainingMilliseconds = Get-RemainingMilliseconds $shutdownDeadline
            if ($remainingMilliseconds -gt 0) { [void]$child.WaitForExit($remainingMilliseconds) }
            if (-not $child.HasExited) { throw "tracked descendant did not stop" }
        }
        $remainingMilliseconds = Get-RemainingMilliseconds $shutdownDeadline
        if ($remainingMilliseconds -gt 0) { [void]$Process.WaitForExit($remainingMilliseconds) }
        if (-not $Process.HasExited) { throw "Isolated process did not stop within five seconds." }
        $Process.Dispose()
        return $null
    } catch {
        try { $Process.Dispose() } catch { }
        return "Isolated process cleanup failed within its bound."
    } finally {
        foreach ($child in $trackedChildren) { $child.Dispose() }
    }
}

function Get-RemainingMilliseconds([DateTimeOffset]$Deadline) {
    return [Math]::Max(0, [int][Math]::Floor(($Deadline - [DateTimeOffset]::UtcNow).TotalMilliseconds))
}

function Add-ProcessDescendants([int]$ParentProcessId, [System.Collections.Generic.List[int]]$ProcessIds, [int]$Depth) {
    if ($Depth -ge $Limits.MaxProcessTreeDepth -or $ProcessIds.Count -ge $Limits.MaxTrackedProcesses) {
        throw "Tracked process tree exceeded its bounds."
    }
    $children = @(Get-CimInstance Win32_Process -Filter ("ParentProcessId = {0}" -f $ParentProcessId) -ErrorAction Stop)
    foreach ($child in $children) {
        if ($ProcessIds.Count -ge $Limits.MaxTrackedProcesses) { throw "Tracked process tree exceeded its bounds." }
        Add-ProcessDescendants ([int]$child.ProcessId) $ProcessIds ($Depth + 1)
        $ProcessIds.Add([int]$child.ProcessId)
    }
}

function New-OwnedScenarioRoot {
    $tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
    $candidate = [System.IO.Path]::GetFullPath((Join-Path $tempRoot ("ru-workflow-" + [guid]::NewGuid().ToString("N"))))
    if (Test-Path -LiteralPath $candidate) { throw "Generated temporary scenario root already exists." }
    [void][System.IO.Directory]::CreateDirectory($candidate)
    if (-not $ownedScenarioRoots.Add($candidate)) { throw "Temporary scenario root registration failed." }
    return $candidate
}

function Remove-ScenarioRoot([string]$ScenarioRoot) {
    if (-not (Test-Path -LiteralPath $ScenarioRoot)) { return }
    $tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    $resolved = [System.IO.Path]::GetFullPath($ScenarioRoot)
    if (-not $resolved.StartsWith($tempRoot, [System.StringComparison]::OrdinalIgnoreCase) -or -not $ownedScenarioRoots.Remove($resolved)) { throw "Refusing cleanup outside the registered temporary scenario root." }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}

function Get-FreePort {
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $listener.Start()
    try { return ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port }
    finally { $listener.Stop() }
}

function Wait-HostReady($Process, $Capture, [string]$DataRoot, [string]$BaseUrl, [string]$Token, [int]$TimeoutSeconds) {
    $descriptorPath = Join-Path $DataRoot "runtime_descriptor.json"
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        if ($Process.HasExited) {
            $diagnostic = $Capture.stderr.Text
            if ([string]::IsNullOrWhiteSpace($diagnostic)) { $diagnostic = "no bounded stderr was emitted" }
            throw "Managed Host exited during isolated startup with code $($Process.ExitCode): $diagnostic"
        }
        if (Test-Path -LiteralPath $descriptorPath) {
            $client = [System.Net.Http.HttpClient]::new()
    $client.Timeout = [TimeSpan]::FromSeconds($Limits.ReadinessRequestSeconds)
            $request = $null
            $response = $null
            try {
                $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Get, "$BaseUrl/status")
                $request.Headers.Authorization = [System.Net.Http.Headers.AuthenticationHeaderValue]::new("Bearer", $Token)
                $response = $client.SendAsync($request, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
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

