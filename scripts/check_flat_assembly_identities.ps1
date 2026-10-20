[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$DirectoryList
)

$ErrorActionPreference = "Stop"
$Directories = @($DirectoryList -split ";" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
$assemblies = @(
    foreach ($directory in $Directories) {
        if (-not (Test-Path -LiteralPath $directory -PathType Container)) {
            throw "Assembly identity check directory does not exist: $directory"
        }

        Get-ChildItem -LiteralPath $directory -Filter "Microsoft.Extensions.*.dll" -File |
            ForEach-Object {
                $identity = [System.Reflection.AssemblyName]::GetAssemblyName($_.FullName)
                [pscustomobject]@{
                    File = $_.Name
                    Identity = $identity.FullName
                    Directory = $directory
                }
            }
    }
)

$conflicts = @(
    $assemblies |
        Group-Object -Property File |
        ForEach-Object {
            $identities = @($_.Group | Select-Object -ExpandProperty Identity -Unique)
            if ($identities.Count -gt 1) {
                [pscustomobject]@{
                    File = $_.Name
                    Identities = $identities -join "; "
                }
            }
        }
)

if ($conflicts.Count -gt 0) {
    foreach ($conflict in $conflicts) {
        [Console]::Error.WriteLine(
            "BUILD FAILED: conflicting assembly identities for {0}: {1}" -f
                $conflict.File, $conflict.Identities)
    }
    exit 1
}

"Validated {0} Microsoft.Extensions assembly identities across {1} publish directories." -f `
    $assemblies.Count, $Directories.Count
