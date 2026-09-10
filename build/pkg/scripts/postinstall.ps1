<#
    Runs after the payload is laid down. Verifies what arrived, then puts the
    install directory on the machine PATH so the bundled CLI is callable from any
    shell.
#>

$ErrorActionPreference = 'Stop'
$InstallDir = 'C:\Program Files\ReportMate'

# Every file this package is supposed to deliver. build.ps1 reads this list back
# out of the finished MSI and fails the build if one is missing, because a payload
# file that silently fails to ship is the failure this package has already had:
# the dashboard was absent from an installer for its entire existence and nothing
# said so.
$expectedPayload = @(
    'ReportMateDashboard.exe'
    'reportmate.exe'
)

$missing = @()
foreach ($name in $expectedPayload) {
    if (-not (Test-Path (Join-Path $InstallDir $name))) { $missing += $name }
}
if ($missing.Count -gt 0) {
    throw "Missing expected files from payload: $($missing -join ', ')"
}

Write-Host "Adding ReportMate to system PATH..."
try {
    $currentPath = [Environment]::GetEnvironmentVariable('PATH', 'Machine')
    if ($currentPath -notlike "*$InstallDir*") {
        [Environment]::SetEnvironmentVariable('PATH', $currentPath.TrimEnd(';') + ";$InstallDir", 'Machine')
        Write-Host "Added '$InstallDir' to system PATH"
    } else {
        Write-Host "'$InstallDir' is already on the system PATH"
    }
} catch {
    Write-Host "Could not update the system PATH: $($_.Exception.Message)"
    Write-Host "  'reportmate' will still work from '$InstallDir'"
}

Write-Host "ReportMate dashboard installed to $InstallDir"
