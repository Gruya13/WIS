# Windows build i potvrda porekla artefakata. Ne instalira Windows niti menja NAS.
[CmdletBinding()]
param([string]$OutputDir = '', [string]$MsBuild = 'msbuild')
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -ne 'Desktop' -or $PSVersionTable.PSVersion.Major -ne 5 -or $PSVersionTable.PSVersion.Minor -ne 1) {
    throw 'Build potvrda zahteva Windows PowerShell 5.1.'
}
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if (-not $OutputDir) { $OutputDir = Join-Path $root '.workspace\artifacts\windows' }
Push-Location $root
try {
    $commit = (& git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or (& git status --porcelain)) { throw 'Potreban je cist Git commit.' }
    & python (Join-Path $PSScriptRoot 'Sync-Versions.py') --check
    if ($LASTEXITCODE -ne 0) { throw 'Verzije izvora nisu uskladjene sa version.json.' }
    & (Join-Path $root 'UI\Tests\Test-PowerShell51Parse.ps1')
    & (Join-Path $root 'UI\Tests\Test-StatusContract.ps1')
    & (Join-Path $root 'Launcher\Tests\Run-LauncherTests.ps1')
    foreach ($project in @('UI', 'Launcher')) {
        & $MsBuild (Join-Path $root "$project\WIS.$project.csproj") /restore /t:Rebuild /p:Configuration=Release /p:Platform=AnyCPU /p:LangVersion=5
        if ($LASTEXITCODE -ne 0) { throw "Build nije uspeo: $project" }
    }
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
    Copy-Item (Join-Path $root 'UI\bin\Release\WIS.UI.exe') $OutputDir -Force
    Copy-Item (Join-Path $root 'Launcher\bin\Release\WIS.Launcher.exe') $OutputDir -Force
    foreach ($dll in @('HidSharp.dll', 'LibreHardwareMonitorLib.dll')) {
        Copy-Item (Join-Path $root "UI\$dll") $OutputDir -Force
    }
    $hashes = @{}
    foreach ($name in @('WIS.UI.exe', 'WIS.Launcher.exe', 'HidSharp.dll', 'LibreHardwareMonitorLib.dll')) {
        $hashes[$name] = (Get-FileHash -Algorithm SHA256 (Join-Path $OutputDir $name)).Hash.ToLowerInvariant()
    }
    $versions = Get-Content (Join-Path $root 'version.json') -Raw | ConvertFrom-Json
    $version = $versions.version
    $componentVersions = @{}
    foreach ($component in @('iso', 'launcher', 'ui', 'servicehub')) {
        $componentVersions[$component] = $versions.components.$component.version
    }
    foreach ($exe in @('WIS.UI.exe', 'WIS.Launcher.exe')) {
        $actual = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $OutputDir $exe)).FileVersion
        $component = if ($exe -eq 'WIS.UI.exe') { 'ui' } else { 'launcher' }
        $expected = $componentVersions[$component] + '.0'
        if ($actual -ne $expected) { throw "Binarna verzija ne odgovara source verziji: $exe ($actual)" }
    }
    $receipt = @{ source_commit = $commit; version = $version; component_versions = $componentVersions; windows_powershell = '5.1'; validation = 'passed'; files = $hashes; vm_e2e = 'not_run' }
    $receipt | ConvertTo-Json -Depth 4 | Set-Content -Encoding UTF8 (Join-Path $OutputDir 'build-attestation.json')
    Write-Host 'Windows build/status/parse potvrda sacuvana. VM E2E ostaje zaseban korak.'
} finally { Pop-Location }
