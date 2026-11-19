<#
    Provera ishoda CELOG stvarno izvrsenog Windows Setup/WIS toka u namenskoj VM.
    Pokrenuti posle instalacije sa novog test ISO-a i automatskog zavrsetka.
    Ne izvrsava instalaciju, ne menja profile, ne pravi lazne rezultate.
#>
[CmdletBinding()]
param(
    [switch]$DisposableVM,
    [Parameter(Mandatory=$true)][string]$ExpectedSourceCommit,
    [string[]]$RequiredApps = @('chrome', 'office', 'acrobat'),
    [string]$SystemLogDir = 'C:\Windows\Logs\WIS',
    [string]$OutputPath = 'C:\WIS-VM-Validation\installed-e2e.json'
)
$ErrorActionPreference = 'Stop'
if (-not $DisposableVM -or $PSVersionTable.PSEdition -ne 'Desktop' -or $PSVersionTable.PSVersion.Major -ne 5) {
    throw 'Zahteva namensku Windows VM i Windows PowerShell 5.1.'
}
$computer = Get-CimInstance Win32_ComputerSystem
if (-not $computer.HypervisorPresent -or ($computer.Model + ' ' + $computer.Manufacturer) -notmatch 'Virtual|VMware|KVM|QEMU|HVM') {
    throw 'Namenska VM nije detektovana.'
}
if ($ExpectedSourceCommit -notmatch '^[0-9a-f]{40}$') { throw 'Potreban je pun source commit.' }
New-Item (Split-Path $OutputPath -Parent) -ItemType Directory -Force | Out-Null
try {
    $state = Get-Content (Join-Path $SystemLogDir 'deploy-state.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $card = Get-Content (Join-Path $SystemLogDir 'servisni_karton.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $metadata = Get-Content (Join-Path $SystemLogDir 'payload_meta.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $build = Get-Content (Join-Path $SystemLogDir 'build-attestation.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($metadata.source_commit -ne $ExpectedSourceCommit -or $build.source_commit -ne $ExpectedSourceCommit) {
        throw 'Instalirani izvor/build ne odgovara ocekivanom commit-u.'
    }
    if ($metadata.product -ne 'WIS' -or $metadata.schema_version -ne 2 -or -not $metadata.release_id.StartsWith($ExpectedSourceCommit + '-')) {
        throw 'Arhivirani manifest nema ocekivano poreklo.'
    }
    $paths = [string[]]@($metadata.required_files.PSObject.Properties | ForEach-Object { $_.Name })
    [Array]::Sort($paths, [StringComparer]::Ordinal)
    $pairs = @($paths | ForEach-Object { $_ + ':' + ([string]$metadata.required_files.PSObject.Properties[$_].Value).ToLowerInvariant() })
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $digest = $sha.ComputeHash([Text.Encoding]::UTF8.GetBytes(($pairs -join "`n")))
        $filesDigest = ([BitConverter]::ToString($digest)).Replace('-', '').ToLowerInvariant()
        $canonical = 'WIS|v2|' + $metadata.version + '|' + $metadata.release_id + '|' + $metadata.size + '|' + $metadata.sha256 + '|' + $metadata.min_launcher_version + '|' + $filesDigest
        if ($canonical -ne $metadata.signed_payload) { throw 'Polja manifesta ne odgovaraju potpisu.' }
        $parameters = New-Object Security.Cryptography.CspParameters(24)
        $rsa = New-Object Security.Cryptography.RSACryptoServiceProvider($parameters)
        try {
            $rsa.PersistKeyInCsp = $false
            $keyPath = Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'Launcher\trusted_public_key.xml'
            $rsa.FromXmlString((Get-Content $keyPath -Raw))
            if (-not $rsa.VerifyData([Text.Encoding]::UTF8.GetBytes($canonical), $sha, [Convert]::FromBase64String($metadata.signature))) {
                throw 'RSA potpis instaliranog izdanja nije validan.'
            }
        } finally { $rsa.Dispose() }
    } finally { $sha.Dispose() }
    $actualBuildHash = (Get-FileHash (Join-Path $SystemLogDir 'build-attestation.json') -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualBuildHash -ne $metadata.required_files.'build-attestation.json') { throw 'Build dokaz ne odgovara potpisanom hash-u.' }
    if ($state.phase -ne 6 -or $state.deployment_status -ne 'DONE' -or -not $state.handover_ready) {
        throw 'Celokupna instalacija nije zavrsena.'
    }
    if (-not $state.anti_leak_verified -or $state.diagnostics_status -eq 'THERMAL_CRITICAL') {
        throw 'Zavrsna bezbednosna/dijagnosticka provera nije prosla.'
    }
    if ($state.diagnostics_status -ne 'OK' -and ($state.cooling_verified -or -not $state.manual_check_required)) {
        throw 'Nepotvrdjena dijagnostika nije oznacena za rucnu proveru.'
    }
    foreach ($app in $RequiredApps) {
        $property = @($card.apps.PSObject.Properties | Where-Object { $_.Name -ieq $app })
        if ($property.Count -ne 1 -or ($property[0].Value -ne $true -and [string]$property[0].Value -notin @('OK', 'Completed', 'Installed', 'Success'))) {
            throw "Obavezna aplikacija nije potvrdjena: $app"
        }
        # Ne veruj samo bool polju proizvodjaca kartona: proveri stvarni PE artefakt.
        $relative = switch ($app.ToLowerInvariant()) {
            'chrome' { @('Google\Chrome\Application\chrome.exe') }
            'office' { @('Microsoft Office\root\Office16\WINWORD.EXE', 'Microsoft Office\Office16\WINWORD.EXE') }
            'acrobat' { @('Adobe\Acrobat DC\Acrobat\Acrobat.exe', 'Adobe\Acrobat\Acrobat\Acrobat.exe') }
            'winrar' { @('WinRAR\WinRAR.exe') }
            default { throw "Nema nezavisne putanje za aplikaciju: $app" }
        }
        $found = $false
        foreach ($base in @($env:ProgramFiles, ${env:ProgramFiles(x86)})) {
            if (-not $base) { continue }
            foreach ($path in $relative) {
                $exe = Join-Path $base $path
                if ((Test-Path -LiteralPath $exe) -and [Diagnostics.FileVersionInfo]::GetVersionInfo($exe).FileVersion) { $found = $true }
            }
        }
        if (-not $found) { throw "Stvarna instalacija/PE verzija aplikacije nije pronadjena: $app" }
    }
    $secretPaths = @('C:\setup\Wifi-Servis.ini', 'C:\setup\wifi_profile.xml',
                     'C:\Windows\Setup\Scripts\Wifi-Servis.ini', 'C:\Windows\Setup\Scripts\wifi_profile.xml')
    foreach ($path in $secretPaths) { if (Test-Path -LiteralPath $path) { throw "Preostala servisna tajna: $path" } }
    @{ status = 'PASS'; source_commit = $ExpectedSourceCommit; whole_installation_completed = $true;
       required_apps = $RequiredApps; diagnostics_status = $state.diagnostics_status;
       cooling_verified = [bool]$state.cooling_verified; manual_check_required = [bool]$state.manual_check_required;
       anti_leak_verified = $true; timestamp = (Get-Date -Format o) } |
        ConvertTo-Json -Depth 4 | Set-Content $OutputPath -Encoding UTF8
    Write-Output 'PASS: stvarni instalacioni tok, aplikacije, dijagnostika i tajne.'
} catch {
    @{ status = 'FAIL'; source_commit = $ExpectedSourceCommit; error = $_.Exception.Message; timestamp = (Get-Date -Format o) } |
        ConvertTo-Json | Set-Content $OutputPath -Encoding UTF8
    throw
}
