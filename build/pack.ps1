<#
.SYNOPSIS
    Builds IZCode and assembles the mod folder ready for Stationeers.

.DESCRIPTION
    Produces dist/IZCode/ with About/ and the DLLs. With -Deploy, it copies
    straight into the game's mods folder, which is where StationeersLaunchPad looks.

.PARAMETER Deploy
    Copies the result to "Documents\My Games\Stationeers\mods\IZCode".

.PARAMETER StationeersDir
    Game installation, when it is not the Steam default.

.EXAMPLE
    pwsh build/pack.ps1 -Deploy
#>
[CmdletBinding()]
param(
    [switch]$Deploy,
    [string]$Configuration = 'Release',
    [string]$StationeersDir
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$distRoot = Join-Path $repoRoot 'dist'
$stageDir = Join-Path $distRoot 'IZCode'

Write-Host "==> Tests" -ForegroundColor Cyan
& dotnet test (Join-Path $repoRoot 'tests/IZLang.Tests/IZLang.Tests.csproj') `
    --configuration $Configuration --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "the tests failed; nothing was packaged" }

Write-Host "==> Building the mod" -ForegroundColor Cyan
$buildArgs = @(
    'build'
    (Join-Path $repoRoot 'src/IZCode.Mod/IZCode.Mod.csproj')
    '--configuration', $Configuration
    '--nologo', '-v', 'q'
)
if ($StationeersDir) { $buildArgs += "-p:StationeersDir=$StationeersDir" }

& dotnet @buildArgs
if ($LASTEXITCODE -ne 0) { throw "the mod build failed" }

Write-Host "==> Assembling $stageDir" -ForegroundColor Cyan
if (Test-Path $stageDir) { Remove-Item $stageDir -Recurse -Force }
New-Item -ItemType Directory -Path $stageDir -Force | Out-Null

Copy-Item (Join-Path $repoRoot 'mod/About') $stageDir -Recurse -Force

$binDir = Join-Path $repoRoot "src/IZCode.Mod/bin/$Configuration"
foreach ($dll in @('IZCode.dll', 'IZLang.dll')) {
    $source = Join-Path $binDir $dll
    if (-not (Test-Path $source)) { throw "could not find $source" }
    Copy-Item $source $stageDir -Force
}

# The samples ship along: they are the documentation the player actually reads.
$samplesSource = Join-Path $repoRoot 'samples'
if (Test-Path $samplesSource) {
    Copy-Item $samplesSource (Join-Path $stageDir 'samples') -Recurse -Force
}

Write-Host "Mod assembled at: $stageDir" -ForegroundColor Green
Get-ChildItem $stageDir -Recurse -File | ForEach-Object {
    Write-Host ("  " + $_.FullName.Substring($stageDir.Length + 1))
}

if ($Deploy) {
    $modsDir = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'My Games\Stationeers\mods'
    if (-not (Test-Path $modsDir)) {
        New-Item -ItemType Directory -Path $modsDir -Force | Out-Null
    }

    $target = Join-Path $modsDir 'IZCode'
    Write-Host "==> Installing to $target" -ForegroundColor Cyan
    if (Test-Path $target) { Remove-Item $target -Recurse -Force }
    Copy-Item $stageDir $target -Recurse -Force
    Write-Host "Installed. Enable the mod in the game menu." -ForegroundColor Green
}
