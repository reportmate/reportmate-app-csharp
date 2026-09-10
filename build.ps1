<#
.SYNOPSIS
    Publishes the ReportMate dashboard and packages it as an MSI per architecture.

.DESCRIPTION
    Produces a single self-contained executable and, unless -SkipMsi is given, an
    MSI per architecture installing it to C:\Program Files\ReportMate alongside the
    reportmate CLI, which is fetched from the CLI repository's own GitHub release
    rather than built or vendored here.

    Both architectures are built by default. Windows on ARM can run the x64 build
    under emulation, but the fleet has real ARM64 machines and a 63 MB emulated
    binary is not what to give them when a native one costs a second build.

    Public builds are unsigned by design; the private pipeline signs and repacks
    a release from this repository.

.PARAMETER CliTag
    A reportmate-cli release tag to bundle. Defaults to that repository's latest
    release that carries the assets needed. Pin it when a build has to be
    reproducible.
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',
    [string]$Version = (Get-Date -Format 'yyyy.MM.dd.HHmm'),
    [ValidateSet('x64', 'arm64', 'both')]
    [string]$Architecture = 'both',
    [string]$CliTag = 'latest',
    [switch]$SkipMsi
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$output = Join-Path $root 'release'

if (-not (Test-Path (Join-Path $root 'client\src\Models\Modules'))) {
    throw "The client submodule is not initialised. Run: git submodule update --init --depth 1"
}

$architectures = if ($Architecture -eq 'both') { @('x64', 'arm64') } else { @($Architecture) }

# The CLI names its assets by target triple; the dashboard names its runtimes the
# .NET way. Keeping the mapping in one place stops the two vocabularies leaking
# into the rest of the script.
$cliAssetsByArch = @{
    'x64'   = @('reportmateutil-x86_64-pc-windows-msvc.tar.gz', 'reportmate-x86_64-pc-windows-msvc.tar.gz')
    'arm64' = @('reportmateutil-aarch64-pc-windows-msvc.tar.gz', 'reportmate-aarch64-pc-windows-msvc.tar.gz')
}

# ── The CLI release ─────────────────────────────────────────────────────
# Resolved once for every architecture, so one build cannot mix CLI versions
# across its packages.
$headers = @{ 'User-Agent' = 'reportmate-app-csharp' }
$wanted = $architectures | ForEach-Object { $cliAssetsByArch[$_] } | Select-Object -Unique

# Built with += so a response that is already a list is flattened into one release
# per element. Wrapping the call in @() instead leaves a single element holding the
# whole array, and every property read off it then comes back as an array.
$candidates = @()
if ($CliTag -ne 'latest') {
    $candidates += Invoke-RestMethod -Headers $headers `
        -Uri "https://api.github.com/repos/reportmate/reportmate-cli/releases/tags/$CliTag"
} else {
    # Not just the latest release: a release is published before its assets finish
    # uploading, so "latest" can name a release whose assets do not exist yet.
    # Walking back to the newest release that actually carries them turns a few
    # minutes of broken builds into a build that takes the previous version.
    $candidates += Invoke-RestMethod -Headers $headers `
        -Uri 'https://api.github.com/repos/reportmate/reportmate-cli/releases?per_page=10'
}

Write-Host "Resolving the reportmate CLI ($CliTag)"
$release = $null
foreach ($candidate in $candidates) {
    # Every architecture being built has to be served by the same release.
    $servesAll = $true
    foreach ($arch in $architectures) {
        if (-not ($candidate.assets | Where-Object { $_.name -in $cliAssetsByArch[$arch] })) {
            $servesAll = $false
            break
        }
    }
    if ($servesAll) { $release = $candidate; break }
}
if (-not $release) {
    throw "No reportmate-cli release carries a CLI for every architecture being built ($($architectures -join ', '))"
}
if ($CliTag -eq 'latest' -and $candidates[0].tag_name -ne $release.tag_name) {
    Write-Host ("  {0} does not carry every asset yet; using {1}" -f $candidates[0].tag_name, $release.tag_name)
}
Write-Host "  Using CLI $($release.tag_name)"

New-Item -ItemType Directory -Path $output -Force | Out-Null
$built = @()

foreach ($arch in $architectures) {
    Write-Host ""
    Write-Host "=== $arch ==="

    # ── Publish ─────────────────────────────────────────────────────────
    dotnet publish (Join-Path $root 'src\ReportMate.App.csproj') `
        --configuration $Configuration `
        --runtime "win-$arch" `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:PublishTrimmed=false `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:VersionPrefix=$Version
    if ($LASTEXITCODE -ne 0) { throw "Publish failed for $arch with exit code $LASTEXITCODE" }

    $publishDir = Join-Path $root "src\bin\$Configuration\net10.0-windows\win-$arch\publish"
    $appExe = Get-ChildItem -Path $publishDir -Filter '*.exe' | Sort-Object Length -Descending | Select-Object -First 1
    if (-not $appExe) { throw "No executable was produced in $publishDir" }
    Write-Host ("Published {0} ({1:N1} MB)" -f $appExe.Name, ($appExe.Length / 1MB))

    if ($SkipMsi) { continue }

    # ── Payload ─────────────────────────────────────────────────────────
    # Assembled fresh each run: a payload directory that accumulates between builds
    # is how a package ends up shipping something nobody meant to ship.
    $payload = Join-Path $root 'build\pkg\payload'
    if (Test-Path $payload) { Remove-Item -LiteralPath $payload -Recurse -Force }
    New-Item -ItemType Directory -Path $payload -Force | Out-Null

    # The dashboard takes the plain name; the CLI is reportmateutil.exe. The two
    # cannot share one: Windows filenames are case-insensitive, so a second copy
    # differing only in case silently replaces the first, and a case-insensitive
    # payload check does not notice -- which is how this package once built
    # containing the CLI alone.
    Copy-Item $appExe.FullName (Join-Path $payload 'reportmate.exe') -Force

    $asset = $release.assets | Where-Object { $_.name -in $cliAssetsByArch[$arch] } | Select-Object -First 1
    $tarball = Join-Path $env:TEMP $asset.name
    Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $tarball -UseBasicParsing
    $extract = Join-Path $env:TEMP "reportmate-cli-$arch"
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
    Write-Host ("Bundled the CLI as reportmateutil.exe from {0} ({1:N1} MB)" -f $asset.name, ($cli.Length / 1MB))

    # ── Package ─────────────────────────────────────────────────────────
    $cimipkg = (Get-Command cimipkg -ErrorAction SilentlyContinue)?.Source
    if (-not $cimipkg -and (Test-Path 'C:\Program Files\Cimian\cimipkg.exe')) {
        $cimipkg = 'C:\Program Files\Cimian\cimipkg.exe'
    }
    if (-not $cimipkg) { throw 'cimipkg was not found; it is needed to build the MSI' }

    $buildInfo = Join-Path $root 'build\pkg\build-info.yaml'
    $original = Get-Content $buildInfo -Raw
    try {
        Set-Content $buildInfo (($original -replace '\{\{VERSION\}\}', $Version) -replace '\{\{ARCH\}\}', $arch) -Encoding UTF8 -NoNewline
        Push-Location (Join-Path $root 'build\pkg')
        & $cimipkg --verbose . 2>&1 | Write-Host
        if ($LASTEXITCODE -ne 0) { throw "cimipkg failed with exit code $LASTEXITCODE" }
        Pop-Location
    } finally {
        Set-Content $buildInfo $original -Encoding UTF8 -NoNewline
    }

    $msi = Get-ChildItem -Path (Join-Path $root 'build\pkg\build') -Filter '*.msi' -Recurse |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $msi) { throw "cimipkg produced no MSI for $arch" }

    # The architecture is in the file name because both packages carry the same
    # product and version, and nothing else distinguishes them once downloaded.
    $final = Join-Path $output ("ReportMateApp-{0}-{1}.msi" -f $Version, $arch)
    Copy-Item $msi.FullName $final -Force

    # ── Verify the payload actually shipped ─────────────────────────────
    # The reason this exists: the dashboard was absent from the client MSI for its
    # entire existence because the installer assembles its payload separately from
    # the publish step, and nothing checked. Reading the finished MSI back is the
    # only thing that would have caught it.
    $postinstall = Get-Content (Join-Path $root 'build\pkg\scripts\postinstall.ps1') -Raw
    $expected = [regex]::Match($postinstall, '(?s)\$expectedPayload\s*=\s*@\((.*?)\)')
    if (-not $expected.Success) { throw 'Could not read $expectedPayload from postinstall.ps1' }
    $required = [regex]::Matches($expected.Groups[1].Value, "'([^']+)'") | ForEach-Object { $_.Groups[1].Value }

    $installer = $null; $db = $null; $view = $null
    $inMsi = [System.Collections.Generic.List[string]]::new()
    try {
        $installer = New-Object -ComObject WindowsInstaller.Installer
        $db = $installer.OpenDatabase($final, 0)
        $view = $db.OpenView('SELECT FileName FROM File')
        $view.Execute($null)
        while ($true) {
            $record = $view.Fetch()
            if (-not $record) { break }
            # The MSI FileName column is 'SHORTNAME|LONGNAME'; the long name is the
            # one that lands on disk.
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

    # Case-sensitive on purpose: reportmate.exe and ReportMate.exe are one file on
    # Windows, and a case-insensitive comparison reports both as present when only
    # one shipped -- which is exactly how this package first built with the
    # dashboard missing and the check satisfied.
    $absent = @($required | Where-Object { $name = $_; -not ($inMsi | Where-Object { $_ -ceq $name }) })
    if ($absent.Count -gt 0) {
        throw "The $arch MSI is missing files the postinstall requires: $($absent -join ', ')"
    }

    Write-Host ("MSI verified: all {0} expected files present" -f $required.Count)
    Write-Host ("Built {0}" -f $final)
    $built += $final
}

if ($built.Count -gt 0) {
    Write-Host ""
    Write-Host "Packages:"
    foreach ($p in $built) { Write-Host ("  {0} ({1:N1} MB)" -f (Split-Path $p -Leaf), ((Get-Item $p).Length / 1MB)) }
}
