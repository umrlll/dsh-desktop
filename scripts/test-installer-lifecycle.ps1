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
$testIdentity = 'DSHDesktop-Lifecycle-' + [Guid]::NewGuid().ToString('N')
$testRegistryKey = 'Software\' + $testIdentity

function Invoke-Setup([string]$InstallerPath, [string[]]$Arguments, [string]$Stage) {
    # Invoke directly instead of Start-Process: some hosts expose both Path and PATH in the
    # process environment, which makes Windows PowerShell 5.1 fail before launching ISCC output.
    & $InstallerPath @Arguments
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "$Stage failed with exit code $exitCode."
    }
}

function Compare-SemanticVersion([string]$Left, [string]$Right) {
    function Split-SemanticVersion([string]$Value) {
        $withoutBuild = $Value.Split('+', 2)[0]
        $parts = $withoutBuild.Split('-', 2)
        [PSCustomObject]@{
            Core = @($parts[0].Split('.') | ForEach-Object { [Int64]$_ })
            Prerelease = if ($parts.Count -gt 1) { @($parts[1].Split('.')) } else { @() }
        }
    }

    $leftVersion = Split-SemanticVersion $Left
    $rightVersion = Split-SemanticVersion $Right
    foreach ($index in 0..2) {
        if ($leftVersion.Core[$index] -ne $rightVersion.Core[$index]) {
            return [Math]::Sign($leftVersion.Core[$index] - $rightVersion.Core[$index])
        }
    }
    if ($leftVersion.Prerelease.Count -eq 0 -and $rightVersion.Prerelease.Count -eq 0) { return 0 }
    if ($leftVersion.Prerelease.Count -eq 0) { return 1 }
    if ($rightVersion.Prerelease.Count -eq 0) { return -1 }

    $common = [Math]::Min($leftVersion.Prerelease.Count, $rightVersion.Prerelease.Count)
    foreach ($index in 0..($common - 1)) {
        $leftId = $leftVersion.Prerelease[$index]
        $rightId = $rightVersion.Prerelease[$index]
        $leftNumeric = $leftId -match '^\d+$'
        $rightNumeric = $rightId -match '^\d+$'
        if ($leftNumeric -and $rightNumeric) {
            $leftNormalized = $leftId.TrimStart('0'); if ($leftNormalized.Length -eq 0) { $leftNormalized = '0' }
            $rightNormalized = $rightId.TrimStart('0'); if ($rightNormalized.Length -eq 0) { $rightNormalized = '0' }
            if ($leftNormalized.Length -ne $rightNormalized.Length) {
                return [Math]::Sign($leftNormalized.Length - $rightNormalized.Length)
            }
            $comparison = [string]::CompareOrdinal($leftNormalized, $rightNormalized)
        }
        elseif ($leftNumeric) { return -1 }
        elseif ($rightNumeric) { return 1 }
        else { $comparison = [string]::CompareOrdinal($leftId, $rightId) }
        if ($comparison -ne 0) { return [Math]::Sign($comparison) }
    }
    return [Math]::Sign($leftVersion.Prerelease.Count - $rightVersion.Prerelease.Count)
}

if ((Compare-SemanticVersion $OlderVersion $NewerVersion) -ge 0) {
    throw 'Lifecycle verification requires OlderVersion to be semantically lower than NewerVersion.'
}

function Invoke-SetupExpectingFailure([string]$InstallerPath, [string[]]$Arguments, [string]$Stage) {
    & $InstallerPath @Arguments
    $exitCode = $LASTEXITCODE
    if ($exitCode -eq 0) {
        throw "$Stage unexpectedly succeeded."
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

    & $packager -SourceDir $olderSource -Version $OlderVersion -OutputDir $installerOutput -IsccPath $IsccPath `
        -AppId $testIdentity -RegistryKey $testRegistryKey
    & $packager -SourceDir $newerSource -Version $NewerVersion -OutputDir $installerOutput -IsccPath $IsccPath `
        -AppId $testIdentity -RegistryKey $testRegistryKey

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

    Invoke-SetupExpectingFailure $olderInstaller $installArguments 'Silent downgrade rejection'
    Wait-ForHash $installedShell (Get-FileHash -LiteralPath $newerShell -Algorithm SHA256).Hash 'Silent downgrade rejection'

    Invoke-Setup $uninstaller @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') 'Silent uninstall'
    Wait-ForMissingPath $installRoot 'Silent uninstall'
    if (-not (Test-Path -LiteralPath $sentinel -PathType Leaf)) {
        throw 'Ordinary uninstall removed data outside the install root.'
    }
    if (Test-Path -LiteralPath ("HKCU:\" + $testRegistryKey)) {
        throw 'Silent uninstall retained the lifecycle-only installed-version registry key.'
    }
    Write-Host 'Installer lifecycle passed: install, upgrade, downgrade rejection, uninstall, and external user-data retention.'
}
finally {
    Remove-Item -LiteralPath ("HKCU:\" + $testRegistryKey) -Recurse -Force -ErrorAction SilentlyContinue
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
