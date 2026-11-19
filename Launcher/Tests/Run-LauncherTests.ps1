#requires -Version 5.1
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$sourceDirectory = Split-Path $PSScriptRoot -Parent
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path -LiteralPath $compiler)) { throw 'Nedostaje .NET Framework C# compiler.' }
$testDirectory = Join-Path ([IO.Path]::GetTempPath()) ('WIS-launcher-tests-' + [guid]::NewGuid().ToString('N'))
$previousTestRoot = $env:WIS_TEST_SETUP_DIR
try {
    New-Item -ItemType Directory -Path $testDirectory | Out-Null
    $productionSources = @(Get-ChildItem -LiteralPath $sourceDirectory -Filter '*.cs' | ForEach-Object FullName)
    $productionSources += Join-Path $sourceDirectory 'Properties\AssemblyInfo.cs'
    & $compiler /nologo /langversion:5 /target:winexe "/out:$testDirectory\WIS.Launcher.exe" /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll $productionSources
    if ($LASTEXITCODE -ne 0) { throw "Launcher source build nije uspeo ($LASTEXITCODE)." }
    & $compiler /nologo /langversion:5 /define:LAUNCHER_TESTS "/out:$testDirectory\LauncherTests.exe" /r:System.Web.Extensions.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll (Join-Path $sourceDirectory 'PayloadManager.cs') (Join-Path $PSScriptRoot 'LauncherBehaviorTests.cs')
    if ($LASTEXITCODE -ne 0) { throw "Launcher test build nije uspeo ($LASTEXITCODE)." }
    $env:WIS_TEST_SETUP_DIR = Join-Path $testDirectory 'setup'
    & (Join-Path $testDirectory 'LauncherTests.exe')
    if ($LASTEXITCODE -ne 0) { throw "Launcher behavior test nije uspeo ($LASTEXITCODE)." }
}
finally {
    $env:WIS_TEST_SETUP_DIR = $previousTestRoot
    if (Test-Path -LiteralPath $testDirectory) { Remove-Item -LiteralPath $testDirectory -Recurse -Force }
}
