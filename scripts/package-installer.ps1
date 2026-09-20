[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$SourceDir,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+(?:[-+][0-9A-Za-z.-]+)?$')]
    [string]$Version,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$OutputDir,

    [string]$IsccPath
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$resolvedSource = (Resolve-Path -LiteralPath $SourceDir).Path
$resolvedOutput = [IO.Path]::GetFullPath($OutputDir)
$definition = Join-Path $repositoryRoot 'installer\DSHDesktop.iss'
$runtimeTool = Join-Path $repositoryRoot 'tools\DSHDesktop.RuntimeTool\DSHDesktop.RuntimeTool.csproj'

if (-not (Test-Path -LiteralPath $definition -PathType Leaf)) {
    throw "Installer definition was not found: $definition"
}
if (-not (Test-Path -LiteralPath $runtimeTool -PathType Leaf)) {
    throw "Runtime validation tool was not found: $runtimeTool"
}
$sourcePrefix = $resolvedSource.TrimEnd([char[]]@(
    [IO.Path]::DirectorySeparatorChar,
    [IO.Path]::AltDirectorySeparatorChar
)) + [IO.Path]::DirectorySeparatorChar
if (
    $resolvedOutput.Equals($resolvedSource, [StringComparison]::OrdinalIgnoreCase) -or
    $resolvedOutput.StartsWith($sourcePrefix, [StringComparison]::OrdinalIgnoreCase)
) {
    throw 'OutputDir must be outside SourceDir so generated installers cannot be included in their own payload.'
}

# This gate verifies active.json, its selected immutable slot, and every declared runtime file.
# A shell-only publish must fail before ISCC has an opportunity to produce an installer.
& dotnet run --project $runtimeTool -c Release --no-restore -- verify-portable --root $resolvedSource
if ($LASTEXITCODE -ne 0) {
    throw "Portable publish root validation failed (exit code $LASTEXITCODE)."
}

if ([string]::IsNullOrWhiteSpace($IsccPath)) {
    $candidates = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
    )
    $IsccPath = $candidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
}
if ([string]::IsNullOrWhiteSpace($IsccPath) -or -not (Test-Path -LiteralPath $IsccPath -PathType Leaf)) {
    throw 'Inno Setup 6 ISCC.exe was not found. Install Inno Setup or pass -IsccPath explicitly.'
}

New-Item -ItemType Directory -Path $resolvedOutput -Force | Out-Null
& $IsccPath "/DSourceDir=$resolvedSource" "/DMyAppVersion=$Version" "/DOutputDir=$resolvedOutput" $definition
if ($LASTEXITCODE -ne 0) {
    throw "ISCC failed (exit code $LASTEXITCODE)."
}

$installer = Join-Path $resolvedOutput "DSHDesktop-Setup-$Version.exe"
if (-not (Test-Path -LiteralPath $installer -PathType Leaf)) {
    throw "ISCC completed without producing the expected installer: $installer"
}
Write-Host "Created guarded per-user installer: $installer"
