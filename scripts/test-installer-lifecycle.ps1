[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$OlderSourceDir,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+(?:[-+][0-9A-Za-z.-]+)?$')]
    [string]$OlderVersion,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$NewerSourceDir,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+(?:[-+][0-9A-Za-z.-]+)?$')]
    [string]$NewerVersion,

    [string]$IsccPath
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$packager = Join-Path $PSScriptRoot 'package-installer.ps1'
$olderSource = (Resolve-Path -LiteralPath $OlderSourceDir).Path
$newerSource = (Resolve-Path -LiteralPath $NewerSourceDir).Path

if (-not (Test-Path -LiteralPath $packager -PathType Leaf)) {
    throw "Installer packager was not found: $packager"
}
if ([string]::Equals($OlderVersion, $NewerVersion, [StringComparison]::Ordinal)) {
    throw 'Lifecycle verification requires two different package versions.'
}

$olderShell = Join-Path $olderSource 'DSHDesktop.exe'
$newerShell = Join-Path $newerSource 'DSHDesktop.exe'
if (-not (Test-Path -LiteralPath $olderShell -PathType Leaf) -or -not (Test-Path -LiteralPath $newerShell -PathType Leaf)) {
    throw 'Both Portable sources must contain DSHDesktop.exe.'
}
if ((Get-FileHash -LiteralPath $olderShell -Algorithm SHA256).Hash -eq (Get-FileHash -LiteralPath $newerShell -Algorithm SHA256).Hash) {
    throw 'Lifecycle verification requires differing shell binaries so the upgrade result can be proven.'
}

$work = Join-Path ([IO.Path]::GetTempPath()) ('dsh-installer-lifecycle-' + [Guid]::NewGuid().ToString('N'))
$installerOutput = Join-Path $work 'installers'
$installRoot = Join-Path $work 'install-root'
$userDataRoot = Join-Path $work 'user-data'
$sentinel = Join-Path $userDataRoot 'retain-after-uninstall.txt'

function Invoke-Setup([string]$InstallerPath, [string[]]$Arguments, [string]$Stage) {
    # Invoke directly instead of Start-Process: some hosts expose both Path and PATH in the
    # process environment, which makes Windows PowerShell 5.1 fail before launching ISCC output.
    & $InstallerPath @Arguments
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "$Stage failed with exit code $exitCode."
    }
}

function Wait-ForPath([string]$Path, [string]$Stage) {
    foreach ($attempt in 1..80) {
        if (Test-Path -LiteralPath $Path -PathType Leaf) { return }
        Start-Sleep -Milliseconds 250
    }
    throw "$Stage did not create the expected file: $Path"
}

function Wait-ForHash([string]$Path, [string]$ExpectedHash, [string]$Stage) {
    foreach ($attempt in 1..80) {
        if ((Test-Path -LiteralPath $Path -PathType Leaf) -and
            (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -eq $ExpectedHash) { return }
        Start-Sleep -Milliseconds 250
    }
    throw "$Stage did not produce the expected desktop shell hash."
}

function Wait-ForMissingPath([string]$Path, [string]$Stage) {
    foreach ($attempt in 1..80) {
        if (-not (Test-Path -LiteralPath $Path)) { return }
        Start-Sleep -Milliseconds 250
    }
    throw "$Stage left the install root behind: $Path"
}

function Find-Uninstaller([string]$Root) {
    foreach ($attempt in 1..80) {
        $uninstaller = Get-ChildItem -LiteralPath $Root -Filter 'unins*.exe' -File -ErrorAction SilentlyContinue |
            Sort-Object -Property LastWriteTimeUtc -Descending |
            Select-Object -First 1
        if ($null -ne $uninstaller) { return $uninstaller.FullName }
        Start-Sleep -Milliseconds 250
    }
    throw "No uninstaller was found under: $Root"
}

try {
    New-Item -ItemType Directory -Path $installerOutput, $userDataRoot -Force | Out-Null
    [IO.File]::WriteAllText($sentinel, 'must survive ordinary uninstall')

    & $packager -SourceDir $olderSource -Version $OlderVersion -OutputDir $installerOutput -IsccPath $IsccPath
    & $packager -SourceDir $newerSource -Version $NewerVersion -OutputDir $installerOutput -IsccPath $IsccPath

    $olderInstaller = Join-Path $installerOutput "DSHDesktop-Setup-$OlderVersion.exe"
    $newerInstaller = Join-Path $installerOutput "DSHDesktop-Setup-$NewerVersion.exe"
    foreach ($installer in @($olderInstaller, $newerInstaller)) {
        if (-not (Test-Path -LiteralPath $installer -PathType Leaf)) {
            throw "Expected installer was not produced: $installer"
        }
    }

    $installArguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', ('/DIR="{0}"' -f $installRoot))
    Invoke-Setup $olderInstaller $installArguments 'Silent install'
    $installedShell = Join-Path $installRoot 'DSHDesktop.exe'
    Wait-ForPath $installedShell 'Silent install'
    $uninstaller = Find-Uninstaller $installRoot
    Wait-ForHash $installedShell (Get-FileHash -LiteralPath $olderShell -Algorithm SHA256).Hash 'Silent install'

    Invoke-Setup $newerInstaller $installArguments 'Silent upgrade'
    Wait-ForHash $installedShell (Get-FileHash -LiteralPath $newerShell -Algorithm SHA256).Hash 'Silent upgrade'
    $uninstaller = Find-Uninstaller $installRoot

    Invoke-Setup $uninstaller @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') 'Silent uninstall'
    Wait-ForMissingPath $installRoot 'Silent uninstall'
    if (-not (Test-Path -LiteralPath $sentinel -PathType Leaf)) {
        throw 'Ordinary uninstall removed data outside the install root.'
    }
    Write-Host 'Installer lifecycle passed: install, upgrade, uninstall, and external user-data retention.'
}
finally {
    if (Test-Path -LiteralPath $work) {
        # Inno Setup can retain the just-launched installer handle briefly after its child
        # process exits. Cleanup must not turn a successful lifecycle assertion into a failure.
        foreach ($attempt in 1..10) {
            try {
                Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction Stop
                break
            }
            catch {
                if ($attempt -eq 10) {
                    Write-Warning "Could not remove lifecycle temporary directory: $work"
                }
                else {
                    Start-Sleep -Milliseconds 300
                }
            }
        }
    }
}
