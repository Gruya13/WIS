[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -ne 5 -or $PSVersionTable.PSVersion.Minor -ne 1) {
    throw 'Ova provera zahteva stvarni Windows PowerShell 5.1 parser.'
}
$workspaceRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$failed = @()
# Scan physical development sources only; installation mirrors and archives are excluded.
function Get-WisPowerShellSources([string]$directory) {
    foreach ($entry in (Get-ChildItem -LiteralPath $directory -Force)) {
        if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { continue }
        if ($entry.PSIsContainer) {
            if ($entry.Name -in @('archives', 'archive', 'sources', '.git', '__pycache__')) { continue }
            Get-WisPowerShellSources $entry.FullName
        } elseif ($entry.Extension -in @('.ps1', '.psm1')) {
            $entry
        }
    }
}
$files = @(foreach ($category in @('UI', 'ISO', 'Launcher')) {
    Get-WisPowerShellSources (Join-Path $workspaceRoot $category)
})
foreach ($file in $files) {
    $tokens = $null; $parseErrors = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile($file.FullName, [ref]$tokens, [ref]$parseErrors)
    foreach ($err in $parseErrors) { $failed += "$($file.FullName):$($err.Extent.StartLineNumber): $($err.Message)" }
}
if ($failed.Count) { throw ($failed -join "`n") }
Write-Output "PASS: Windows PowerShell 5.1 parsed $($files.Count) source files"
