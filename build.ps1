<#
.SYNOPSIS
    Publishes the ReportMate dashboard and packages it as an MSI.

.DESCRIPTION
    Produces a single self-contained executable and, unless -SkipMsi is given, an
    MSI that installs it to C:\Program Files\ReportMate alongside the reportmate
    CLI, which is fetched from the CLI repository's own GitHub release rather than
    built or vendored here.

    Public builds are unsigned by design; the private pipeline signs and repacks
    a release from this repository.

.PARAMETER CliTag
    A reportmate-cli release tag to bundle. Defaults to that repository's latest
    release. Pin it when a build has to be reproducible.
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',
    [string]$Version = (Get-Date -Format 'yyyy.MM.dd.HHmm'),
    [string]$CliTag = 'latest',
    [switch]$SkipMsi
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$payload = Join-Path $root 'build\pkg\payload'
$output = Join-Path $root 'release'

if (-not (Test-Path (Join-Path $root 'client\src\Models\Modules'))) {
    throw "The client submodule is not initialised. Run: git submodule update --init --depth 1"
}

# ── Publish ─────────────────────────────────────────────────────────────
Write-Host "Publishing ReportMate $Version ($Configuration)"
dotnet publish (Join-Path $root 'src\ReportMate.App.csproj') `
    --configuration $Configuration `
    --runtime win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:PublishTrimmed=false `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:VersionPrefix=$Version
if ($LASTEXITCODE -ne 0) { throw "Publish failed with exit code $LASTEXITCODE" }

$publishDir = Join-Path $root "src\bin\$Configuration\net10.0-windows\win-x64\publish"
$appExe = Get-ChildItem -Path $publishDir -Filter '*.exe' | Sort-Object Length -Descending | Select-Object -First 1
if (-not $appExe) { throw "No executable was produced in $publishDir" }
Write-Host ("Published {0} ({1:N1} MB)" -f $appExe.Name, ($appExe.Length / 1MB))

if ($SkipMsi) { return }

# ── Payload ─────────────────────────────────────────────────────────────
# Assembled fresh each run: a payload directory that accumulates between builds is how
# a package ends up shipping something nobody meant to ship.
if (Test-Path $payload) { Remove-Item -LiteralPath $payload -Recurse -Force }
New-Item -ItemType Directory -Path $payload -Force | Out-Null
New-Item -ItemType Directory -Path $output -Force | Out-Null

# The dashboard takes the plain name; the CLI is reportmateutil.exe. The two cannot
# share one: Windows filenames are case-insensitive, so a second copy differing only
# in case silently replaces the first, and a case-insensitive payload check does not
# notice -- which is how this package once built containing the CLI alone.
Copy-Item $appExe.FullName (Join-Path $payload 'reportmate.exe') -Force

# The CLI is a released binary from its own repository. Taking it from the release
# rather than building or vendoring it means this package ships exactly what that
# repository published, and the version is visible in the build log.
# The CLI is being renamed from reportmate to reportmateutil, and its release assets
# with it. Accept either name so a build works on both sides of that change rather
# than breaking on whichever release it happens to meet.
$assetNames = @(
    'reportmateutil-x86_64-pc-windows-msvc.tar.gz',
    'reportmate-x86_64-pc-windows-msvc.tar.gz'
)
$headers = @{ 'User-Agent' = 'reportmate-app-csharp' }

Write-Host "Fetching the reportmate CLI ($CliTag)"
# Built with += so a response that is already a list is flattened into one release
# per element. Wrapping the call in @() instead leaves a single element holding the
# whole array, and every property read off it then comes back as an array.
$candidates = @()
if ($CliTag -ne 'latest') {
    $candidates += Invoke-RestMethod -Headers $headers `
        -Uri "https://api.github.com/repos/reportmate/reportmate-cli/releases/tags/$CliTag"
} else {
    # Not just the latest release: a release is published before its assets finish
    # uploading, so "latest" can name a release whose Windows asset does not exist
    # yet. Walking back to the newest release that actually has one turns a few
    # minutes of broken builds into a build that takes the previous version.
    $candidates += Invoke-RestMethod -Headers $headers `
        -Uri 'https://api.github.com/repos/reportmate/reportmate-cli/releases?per_page=10'
}

$release = $null; $asset = $null
foreach ($candidate in $candidates) {
    foreach ($name in $assetNames) {
        $asset = $candidate.assets | Where-Object { $_.name -eq $name } | Select-Object -First 1
        if ($asset) { $release = $candidate; break }
    }
    if ($asset) { break }
}
if (-not $asset) {
    throw "No reportmate-cli release carries any of: $($assetNames -join ', ')"
}
if ($CliTag -eq 'latest' -and $candidates[0].tag_name -ne $release.tag_name) {
    Write-Host ("  {0} has no Windows asset yet; using {1}" -f $candidates[0].tag_name, $release.tag_name)
}

$tarball = Join-Path $env:TEMP $asset.name
Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $tarball -UseBasicParsing
$extract = Join-Path $env:TEMP 'reportmate-cli-extract'
if (Test-Path $extract) { Remove-Item -LiteralPath $extract -Recurse -Force }
New-Item -ItemType Directory -Path $extract -Force | Out-Null
tar -xzf $tarball -C $extract
if ($LASTEXITCODE -ne 0) { throw "Could not extract $($asset.name)" }

$cli = Get-ChildItem -Path $extract -Recurse -Include 'reportmateutil.exe', 'reportmate.exe' |
    Sort-Object { $_.Name -eq 'reportmateutil.exe' } -Descending | Select-Object -First 1
if (-not $cli) { throw "No reportmateutil.exe or reportmate.exe inside $($asset.name)" }
$cliTarget = Join-Path $payload 'reportmateutil.exe'
if (Test-Path $cliTarget) {
    throw "Refusing to overwrite an existing payload file at $cliTarget. Two payload files differing only in case collide on Windows."
}
Copy-Item $cli.FullName $cliTarget -Force
Write-Host ("Bundled the CLI from {0} as reportmateutil.exe, taken from {1} ({2:N1} MB)" -f `
    $release.tag_name, $asset.name, ($cli.Length / 1MB))

# ── Package ─────────────────────────────────────────────────────────────
$cimipkg = (Get-Command cimipkg -ErrorAction SilentlyContinue)?.Source
if (-not $cimipkg -and (Test-Path 'C:\Program Files\Cimian\cimipkg.exe')) {
    $cimipkg = 'C:\Program Files\Cimian\cimipkg.exe'
}
if (-not $cimipkg) { throw 'cimipkg was not found; it is needed to build the MSI' }

$buildInfo = Join-Path $root 'build\pkg\build-info.yaml'
$original = Get-Content $buildInfo -Raw
try {
    Set-Content $buildInfo ($original -replace '\{\{VERSION\}\}', $Version) -Encoding UTF8 -NoNewline
    Push-Location (Join-Path $root 'build\pkg')
    & $cimipkg --verbose . 2>&1 | Write-Host
    if ($LASTEXITCODE -ne 0) { throw "cimipkg failed with exit code $LASTEXITCODE" }
    Pop-Location
} finally {
    Set-Content $buildInfo $original -Encoding UTF8 -NoNewline
}

$msi = Get-ChildItem -Path (Join-Path $root 'build\pkg\build') -Filter '*.msi' -Recurse |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $msi) { throw 'cimipkg produced no MSI' }
Copy-Item $msi.FullName (Join-Path $output $msi.Name) -Force

# ── Verify the payload actually shipped ─────────────────────────────────
# The reason this exists: the dashboard was absent from the client MSI for its
# entire existence because the installer assembles its payload separately from the
# publish step, and nothing checked. Reading the finished MSI back is the only
# thing that would have caught it.
$postinstall = Get-Content (Join-Path $root 'build\pkg\scripts\postinstall.ps1') -Raw
$expected = [regex]::Match($postinstall, '(?s)\$expectedPayload\s*=\s*@\((.*?)\)')
if (-not $expected.Success) { throw 'Could not read $expectedPayload from postinstall.ps1' }
$required = [regex]::Matches($expected.Groups[1].Value, "'([^']+)'") | ForEach-Object { $_.Groups[1].Value }

$installer = $null; $db = $null; $view = $null
$inMsi = [System.Collections.Generic.List[string]]::new()
try {
    $installer = New-Object -ComObject WindowsInstaller.Installer
    $db = $installer.OpenDatabase((Join-Path $output $msi.Name), 0)
    $view = $db.OpenView('SELECT FileName FROM File')
    $view.Execute($null)
    while ($true) {
        $record = $view.Fetch()
        if (-not $record) { break }
        # The MSI FileName column is 'SHORTNAME|LONGNAME'; the long name is the one
        # that lands on disk.
        $raw = $record.StringData(1)
        $inMsi.Add($(if ($raw -match '\|') { ($raw -split '\|', 2)[1] } else { $raw }))
    }
    $view.Close()
} finally {
    foreach ($com in @($view, $db, $installer)) {
        if ($com) { try { [Runtime.InteropServices.Marshal]::FinalReleaseComObject($com) | Out-Null } catch {} }
    }
    [GC]::Collect(); [GC]::WaitForPendingFinalizers()
}

# Case-sensitive on purpose: ReportMate.exe and reportmate.exe are one file on
# Windows, and a case-insensitive comparison reports both as present when only one
# shipped -- which is exactly how this package first built with the dashboard
# missing and the check satisfied.
$absent = @($required | Where-Object { $name = $_; -not ($inMsi | Where-Object { $_ -ceq $name }) })
if ($absent.Count -gt 0) {
    throw "The MSI is missing files the postinstall requires: $($absent -join ', ')"
}

Write-Host ("MSI verified: all {0} expected files present" -f $required.Count)
Write-Host ("Built {0}" -f (Join-Path $output $msi.Name))
