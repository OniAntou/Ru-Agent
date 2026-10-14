# Tool preparation for disposable GitHub Actions runners only.
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or -not $env:RUNNER_TEMP) {
    throw 'Terminal prerequisite setup requires a GitHub Actions runner.'
}
$archive = Join-Path $env:RUNNER_TEMP 'ripgrep-14.1.1.zip'
$destination = Join-Path $env:RUNNER_TEMP 'ru-terminal-tools'
Invoke-WebRequest -Uri 'https://github.com/BurntSushi/ripgrep/releases/download/14.1.1/ripgrep-14.1.1-x86_64-pc-windows-msvc.zip' -OutFile $archive -TimeoutSec 60
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne 'd0f534024c42afd6cb4d38907c25cd2b249b79bbe6cc1dbee8e3e37c2b6e25a1') {
    throw 'Ripgrep archive checksum mismatch.'
}
Expand-Archive -LiteralPath $archive -DestinationPath $destination
$toolDirectory = Join-Path $destination 'ripgrep-14.1.1-x86_64-pc-windows-msvc'
& (Join-Path $toolDirectory 'rg.exe') --version
if ($LASTEXITCODE -ne 0) { throw 'Ripgrep startup failed.' }
$toolDirectory | Out-File -LiteralPath $env:GITHUB_PATH -Encoding utf8 -Append

# Compare ordinary Windows PowerShell startup and the same minimal environment
# used by terminal children; print only fixed probe output, never environment values.
foreach ($minimal in @($false, $true)) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = Join-Path ([Environment]::GetFolderPath('System')) 'WindowsPowerShell\v1.0\powershell.exe'
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.ArgumentList.Add('-NoProfile')
    $start.ArgumentList.Add('-NonInteractive')
    $start.ArgumentList.Add('-Command')
    $start.ArgumentList.Add("[Console]::WriteLine('before-module'); Start-Sleep -Milliseconds 100; [Console]::WriteLine('after-module')")
    if ($minimal) {
        $allowed = @('SYSTEMROOT','WINDIR','TEMP','TMP','USERPROFILE','HOME','LOCALAPPDATA','APPDATA','PROGRAMDATA','PROGRAMFILES','PROGRAMFILES(X86)','PROGRAMW6432','COMMONPROGRAMFILES','COMMONPROGRAMFILES(X86)','PATH','PATHEXT','COMSPEC','PROCESSOR_ARCHITECTURE','PROCESSOR_IDENTIFIER','PROCESSOR_LEVEL','PROCESSOR_REVISION','NUMBER_OF_PROCESSORS','GIT_CONFIG_GLOBAL','GIT_CONFIG_SYSTEM','DOTNET_ROOT','NUGET_PACKAGES','PYTHONHOME','PYTHONPATH','VIRTUAL_ENV','PIP_CACHE_DIR')
        foreach ($name in @($start.Environment.Keys)) {
            if ($name -notin $allowed) { $start.Environment.Remove($name) | Out-Null }
        }
    }
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $process = [Diagnostics.Process]::Start($start)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(20000)) {
            $process.Kill($true)
            $process.WaitForExit()
            throw "PowerShell probe timed out (minimal=$minimal)."
        }
        Write-Host "PowerShell probe minimal=$minimal elapsed_ms=$($watch.ElapsedMilliseconds) exit=$($process.ExitCode)"
        Write-Host $stdout.GetAwaiter().GetResult()
        Write-Host $stderr.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) { throw 'PowerShell probe failed.' }
    } finally { $process.Dispose() }
}
