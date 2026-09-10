<#
.SYNOPSIS
    Publishes the ReportMate dashboard as a single self-contained executable.

.DESCRIPTION
    WPF cannot be trimmed, so the result is large; it is self-contained so it runs
    on an endpoint with no .NET installed. Signing is not done here: the public
    build is unsigned by design and the private pipeline signs it.
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',
    [string]$Version = (Get-Date -Format 'yyyy.MM.dd.HHmm')
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

if (-not (Test-Path (Join-Path $root 'client\src\Models\Modules'))) {
    throw "The client submodule is not initialised. Run: git submodule update --init --depth 1"
}

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

$exe = Get-ChildItem -Path (Join-Path $root "src\bin\$Configuration\net10.0-windows\win-x64\publish") -Filter '*.exe' |
    Sort-Object Length -Descending | Select-Object -First 1
Write-Host ("Published {0} ({1:N1} MB)" -f $exe.Name, ($exe.Length / 1MB))
