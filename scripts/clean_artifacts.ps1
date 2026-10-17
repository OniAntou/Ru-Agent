[CmdletBinding()]
param(
    # Dry-run by default: the heavy listing walks the same candidates the apply
    # would take, so the operator sees exactly what would be removed and what
    # the keep rules shielded before anything irreversible happens.
    [switch]$Apply,

    # Candidate age floor: a directory whose newest content is younger than
    # this many days is never a candidate, regardless of its name.
    [ValidateRange(1, 365)]
    [int]$OlderThanDays = 14
)

$ErrorActionPreference = "Stop"

# Cleans stale session directories under artifacts\<name>\.
#
# Every verification run, QA probe and publish smoke writes its own directory
# there; nothing else ever prunes them, so the working tree quietly collects
# gigabytes of residue. The script is deliberately narrow:
#   - Directories only. Root-level *.log files are evidence from each
#     verification run and are never touched here; deleting them by age is a
#     separate decision someone must make by hand.
#   - Content-aged. A directory entry's LastWriteTime only tracks its direct
#     children, so an old-looking parent can hide live working files; the age
#     decision reads the newest file inside the subtree instead.
#   - Re-checked at deletion time. Apply re-runs the content probe immediately
#     before each Remove-Item and skips anything that turned fresh or became
#     unreadable since the listing.
#   - Fixed keep names: artifacts\release-verification\ (current release
#     verification evidence) and artifacts\releases\ (packaged releases) are
#     never candidates, no matter how old they look.

$artifactsRoot = Join-Path $PSScriptRoot "..\artifacts"
if (-not (Test-Path -LiteralPath $artifactsRoot)) {
    Write-Output "artifacts\ does not exist; nothing to clean."
    return
}

$artifactsRoot = (Resolve-Path -LiteralPath $artifactsRoot).Path

# Names that are never candidates (see the header). Path-anchored and exact.
$keepNames = @(
    "release-verification",
    "releases"
)

function GetDirectoryProfile([System.IO.FileSystemInfo]$directory) {
    # One traversal answers both sweep questions: how much residue the
    # directory holds and how fresh its most recent file is. An unreadable
    # subtree is reported as not-readable so the sweep keeps it: deleting
    # what we cannot see is exactly the failure mode this guard exists for.
    $lastActivity = $directory.LastWriteTime
    $bytes = 0L
    try
    {
        foreach ($file in (Get-ChildItem -LiteralPath $directory.FullName -Recurse -File -ErrorAction Stop)) {
            $bytes += $file.Length
            if ($file.LastWriteTime -gt $lastActivity) {
                $lastActivity = $file.LastWriteTime
            }
        }
    }
    catch
    {
        return [pscustomobject]@{ LastActivity = $directory.LastWriteTime; SizeMb = 0.0; Readable = $false }
    }

    return [pscustomobject]@{
        LastActivity = $lastActivity
        SizeMb = [Math]::Round($bytes / 1MB, 1)
        Readable = $true
    }
}

$cutoff = ((Get-Date).AddDays(-$OlderThanDays))
$candidates = New-Object System.Collections.Generic.List[object]
$kept = New-Object System.Collections.Generic.List[object]
foreach ($entry in (Get-ChildItem -LiteralPath $artifactsRoot -Directory)) {
    if ($keepNames -contains $entry.Name) {
        $kept.Add([pscustomobject]@{ Name = $entry.Name; Reason = "fixed keep list" })
        continue
    }

    $profile = GetDirectoryProfile $entry
    if (-not $profile.Readable) {
        $kept.Add([pscustomobject]@{ Name = $entry.Name; Reason = "unreadable subtree (kept for safety)" })
        continue
    }

    if ($profile.LastActivity -lt $cutoff) {
        $candidates.Add([pscustomobject]@{ Dir = $entry; Profile = $profile })
    } else {
        $kept.Add([pscustomobject]@{ Name = $entry.Name; Reason = ("newest content {0:yyyy-MM-dd HH:mm}" -f $profile.LastActivity) })
    }
}

$totalCandidateMb = 0
Write-Output ("artifacts\ sweep - {0} candidate directories (newest content older than {1} days, as of {2}):" -f $candidates.Count, $OlderThanDays, (Get-Date -Format "yyyy-MM-dd HH:mm"))
foreach ($item in $candidates) {
    $ageDays = [Math]::Round(((Get-Date) - $item.Profile.LastActivity).TotalDays, 1)
    $totalCandidateMb += $item.Profile.SizeMb
    Write-Output ("  delete {0,-45} {1,7} d  {2,10} MB" -f $item.Dir.Name, $ageDays, $item.Profile.SizeMb)
}

Write-Output ("{0} kept directories (fixed keep list, unreadable, or fresh):" -f $kept.Count)
foreach ($entry in $kept) {
    Write-Output ("  keep   {0,-45} {1}" -f $entry.Name, $entry.Reason)
}
Write-Output ("candidate total: {0} MB" -f ([Math]::Round($totalCandidateMb, 1)))

if (-not $Apply) {
    Write-Output "Dry-run only. Re-run with -Apply to delete the listed directories."
    return
}

$failed = 0
foreach ($item in $candidates) {
    # TOCTOU guard: a long-running job could have written into a listed
    # directory between the listing and this deletion. Repeat the content
    # probe and delete only when the directory still looks retired.
    $recheck = GetDirectoryProfile $item.Dir
    if (-not $recheck.Readable) {
        Write-Output ("skipped (unreadable at deletion time) {0}" -f $item.Dir.Name)
        continue
    }

    if ($recheck.LastActivity -ge $cutoff) {
        Write-Output ("skipped (content refreshed since listing) {0}" -f $item.Dir.Name)
        continue
    }

    try {
        Remove-Item -LiteralPath $item.Dir.FullName -Recurse -Force
        Write-Output ("removed {0}" -f $item.Dir.Name)
    } catch {
        # One locked directory must not stop the sweep; report and continue.
        $failed++
        Write-Output ("FAILED removing {0}: {1}" -f $item.Dir.Name, $_.Exception.Message)
    }
}

Write-Output ("done: removed {0}, blocked {1}." -f ($candidates.Count - $failed), $failed)
