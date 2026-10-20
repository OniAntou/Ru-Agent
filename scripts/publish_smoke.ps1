# Publish smoke: publish RuBackend as a self-contained win-x64 Release app and
# prove the published output boots in managed mode, authenticates /status with
# the descriptor bearer and shuts down cleanly.
#
# This covers the publish layout (apphost, runtimeconfig) that the Debug
# build-and-run rpc smoke never exercises, without paying for the full
# build.bat release matrix.
#
# Usage: .\scripts\publish_smoke.ps1            (Release, free loopback port)
#        .\scripts\publish_smoke.ps1 -Port N    (pin a port for debugging)
#        .\scripts\publish_smoke.ps1 -PublishedDirectory PATH (test an existing package)
param(
    [int]$Port = 0,

    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    # Exercise an existing product package without rebuilding or changing it.
    [string]$PublishedDirectory = ""
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$work = Join-Path $env:TEMP ("ru-publish-smoke-" + [guid]::NewGuid().ToString("N"))
$work = [IO.Path]::GetFullPath($work)
$smokeTempRoot = [IO.Path]::GetFullPath($env:TEMP).TrimEnd('\', '/')
if (-not [string]::Equals((Split-Path -Parent $work), $smokeTempRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Publish smoke work directory must be a direct child of the temporary directory."
}
$publishDir = Join-Path $work "publish"
if ($PublishedDirectory) { $publishDir = (Resolve-Path -LiteralPath $PublishedDirectory).Path }
$dataRoot = Join-Path $work "data"
$logRoot = Join-Path $work "logs"
$hostStdoutPath = Join-Path $work "host.stdout.log"
$hostStderrPath = Join-Path $work "host.stderr.log"
$projectPath = Join-Path $repoRoot "backend-dotnet/RuBackend/RuBackend.csproj"
$token = "publish-smoke-bearer"
$environmentNames = @("RU_DATA_ROOT", "RU_LOG_ROOT", "RU_DESKTOP_TOKEN")

# Captured before anything can fail so the finally block always restores exactly
# the caller's environment, never a partially-set one.
$previousEnvironment = @{}
foreach ($name in $environmentNames) {
    $previousEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, "Process")
}

$hostProcess = $null
$status = 1

function Write-Smoke([string]$Message) {
    Write-Host "publish smoke: $Message"
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

function Resolve-SmokePort([int]$RequestedPort) {
    if ($RequestedPort -eq 0) {
        return 0
    }

    if ($RequestedPort -ne 0) {
        if ($RequestedPort -lt 1024 -or $RequestedPort -gt 65535) {
            throw "-Port must be between 1024 and 65535; got $RequestedPort."
        }

    }

    $listener = New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Loopback, $RequestedPort)
    try {
        $listener.Start()
        return ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port
    } catch {
        if ($RequestedPort -ne 0) {
            $owner = "an unidentified process"
            try {
                $connection = Get-NetTCPConnection -LocalPort $RequestedPort -State Listen -ErrorAction Stop | Select-Object -First 1
                if ($connection) {
                    $process = Get-Process -Id $connection.OwningProcess -ErrorAction SilentlyContinue
                    $owner = "PID $($connection.OwningProcess) ($($process.ProcessName))"
                }
            } catch {
            }

            throw "port $RequestedPort is already in use by $owner. This smoke never kills a host it did not start; stop that process and rerun."
        }

        throw
    } finally {
        try { $listener.Stop() } catch { }
    }
}

function Wait-StatusReady([System.Diagnostics.Process]$HostProcess, [string]$Url, [string]$Bearer, [int]$TimeoutSeconds) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $lastOutcome = "no attempt completed"
    while ((Get-Date) -lt $deadline) {
        if ($HostProcess.HasExited) {
            throw "host exited before GET $Url/status succeeded. stderr: $(Get-TextTail $hostStderrPath)"
        }

        try {
            $response = Invoke-SmokeRequest -Method ([System.Net.Http.HttpMethod]::Get) -Url "$Url/status" -Bearer $Bearer -TimeoutSeconds 2
            $lastOutcome = "HTTP $($response.StatusCode)"
            if ($response.StatusCode -eq 200) {
                $body = $response.Body | ConvertFrom-Json
                if ($body.status -eq "ok") {
                    return $body
                }

                $lastOutcome = "status '$($body.status)'"
            }
        } catch {
            # Descriptor publication follows the bind, but HTTP application
            # initialization can still be finishing; keep polling.
            $lastOutcome = "connection failed: $($_.Exception.Message)"
        }

        Start-Sleep -Milliseconds 250
    }

    throw "host did not answer GET $Url/status with status=ok within $TimeoutSeconds s (last attempt: $lastOutcome). stderr: $(Get-TextTail $hostStderrPath)"
}

function Invoke-SmokeRequest([System.Net.Http.HttpMethod]$Method, [string]$Url, [string]$Bearer, [int]$TimeoutSeconds = 15) {
    $client = New-Object System.Net.Http.HttpClient
    try {
        $client.Timeout = [TimeSpan]::FromSeconds($TimeoutSeconds)
        $request = New-Object System.Net.Http.HttpRequestMessage -ArgumentList $Method, $Url
        try {
            $request.Headers.Authorization = New-Object System.Net.Http.Headers.AuthenticationHeaderValue -ArgumentList "Bearer", $Bearer
            $response = $client.SendAsync($request).GetAwaiter().GetResult()
            try {
                return [pscustomobject]@{
                    StatusCode = [int]$response.StatusCode
                    Body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                    UpdateReady = if ($response.Headers.Contains('X-Ru-Update-Ready')) { @($response.Headers.GetValues('X-Ru-Update-Ready'))[0] } else { $null }
                }
            } finally {
                $response.Dispose()
            }
        } finally {
            $request.Dispose()
        }
    } finally {
        $client.Dispose()
    }
}

try {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw "dotnet was not found on PATH. Install the .NET 10 SDK, then rerun this smoke."
    }

    if (-not (Test-Path -LiteralPath $projectPath)) {
        throw "RuBackend project not found: $projectPath"
    }

    New-Item -ItemType Directory -Path $dataRoot, $logRoot -Force | Out-Null

    if (-not $PublishedDirectory) {
        Write-Smoke "publishing RuBackend ($Configuration, win-x64, self-contained) to $publishDir..."
        & dotnet publish $projectPath -c $Configuration -r win-x64 --self-contained true -o $publishDir --nologo
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }
    }

    $backendExe = Join-Path $publishDir "RuBackend.exe"
    if (-not (Test-Path -LiteralPath $backendExe)) {
        throw "dotnet publish did not produce $backendExe."
    }

    # Execution is in-process: the Broker ships as a library, so a published
    # RuAgent.Broker.exe would mean a stale executable stage survived.
    $brokerExe = Join-Path $publishDir "RuAgent.Broker.exe"
    if (Test-Path -LiteralPath $brokerExe) {
        throw "RuAgent.Broker.exe must not be published: execution is in-process."
    }

    # Probe the port as late as possible: the publish can take minutes, and a
    # port probed before it is another process's opportunity to steal it.
    $publicPort = Resolve-SmokePort -RequestedPort $Port
    $baseUrl = "http://127.0.0.1:$publicPort"

    $env:RU_DATA_ROOT = $dataRoot
    $env:RU_LOG_ROOT = $logRoot
    $env:RU_DESKTOP_TOKEN = $token

    Write-Smoke "starting published RuBackend on $baseUrl (data root: $dataRoot)..."
    # --urls must come before --managed-runtime because the command-line
    # provider treats the value-less switch as consuming the next token.
    $hostProcess = Start-Process -FilePath $backendExe `
        -ArgumentList "--urls", $baseUrl, "--managed-runtime" `
        -WorkingDirectory $publishDir -PassThru -WindowStyle Hidden `
        -RedirectStandardOutput $hostStdoutPath -RedirectStandardError $hostStderrPath

    $descriptorPath = Join-Path $dataRoot "runtime_descriptor.json"
    $deadline = (Get-Date).AddSeconds(60)
    while (-not (Test-Path -LiteralPath $descriptorPath)) {
        if ($hostProcess.HasExited) {
            throw "host exited before publishing runtime_descriptor.json. stderr: $(Get-TextTail $hostStderrPath)"
        }

        if ((Get-Date) -ge $deadline) {
            throw "host did not publish runtime_descriptor.json within 60 s. stderr: $(Get-TextTail $hostStderrPath)"
        }

        Start-Sleep -Milliseconds 200
    }

    $descriptor = Get-Content -LiteralPath $descriptorPath -Raw | ConvertFrom-Json
    $descriptorUri = [Uri]$descriptor.base_url
    if (-not $descriptorUri.IsLoopback -or $descriptorUri.Scheme -ne "http" -or $descriptorUri.Port -lt 1024) {
        throw "descriptor base_url '$($descriptor.base_url)' is not a bound loopback URL."
    }
    if ($publicPort -ne 0 -and $descriptorUri.Port -ne $publicPort) {
        throw "descriptor base_url '$($descriptor.base_url)' does not match requested port $publicPort."
    }
    $baseUrl = $descriptorUri.GetLeftPart([System.UriPartial]::Authority).TrimEnd("/")

    if ([string]::IsNullOrWhiteSpace($descriptor.bearer_token)) {
        throw "descriptor carries no bearer_token."
    }
    if ($descriptor.pid -ne $hostProcess.Id) { throw 'Descriptor does not belong to the owned smoke process.' }

    Add-Type -AssemblyName System.Net.Http -ErrorAction SilentlyContinue
    # The descriptor is published after Kestrel binds; poll for authenticated
    # readiness on a bounded budget in case app initialization is still running.
    $ready = Wait-StatusReady -HostProcess $hostProcess -Url $baseUrl -Bearer $descriptor.bearer_token -TimeoutSeconds 30
    if ($ready.maintenance_protocol -ne 1) { throw 'Published host does not support idle-only maintenance.' }
    $markerPath = Join-Path $publishDir 'ru-install.json'
    if (Test-Path -LiteralPath $markerPath) {
        $marker = Get-Content -LiteralPath $markerPath -Raw | ConvertFrom-Json
        if ($ready.software_version -ne $marker.Version) { throw 'Packaged Host version does not match its frontend marker.' }
    }

    Write-Smoke "status ok; asking the host to shut down..."
    $shutdownResponse = Invoke-SmokeRequest -Method ([System.Net.Http.HttpMethod]::Post) -Url "$baseUrl/host/shutdown?when_idle=true" -Bearer $descriptor.bearer_token
    if ($shutdownResponse.StatusCode -ne 200 -or $shutdownResponse.UpdateReady -ne '1') {
        throw "POST /host/shutdown returned $($shutdownResponse.StatusCode); expected 200. body: $($shutdownResponse.Body)"
    }

    if (-not $hostProcess.WaitForExit(15000)) {
        throw "the host did not exit within 15 s after /host/shutdown."
    }

    Write-Smoke "published host shut down cleanly"
    $status = 0
} catch {
    Write-Smoke "FAIL - $($_.Exception.Message)"
    if ($null -ne $hostProcess) {
        Write-Smoke "host stderr tail: $(Get-TextTail $hostStderrPath)"
    }
} finally {
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
        Write-Warning "publish smoke: could not remove the temp work directory $work; remove it manually."
    }
}

if ($status -eq 0) {
    Write-Smoke "PASS"
    exit 0
}

exit 1
