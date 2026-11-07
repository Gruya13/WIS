# Cist statusni ugovor; bez pristupa registru, mreznim profilima ili instalaciji.
function Get-WisDiagnosticsContract {
    [CmdletBinding()]
    param([string]$Status = 'UNKNOWN', [bool]$SensorAvailable = $false,
          [double]$MaxCpuTemp = 0, [bool]$EmergencyAbort = $false)
    $normalized = $Status.Trim().ToUpperInvariant()
    $critical = $EmergencyAbort -or $MaxCpuTemp -ge 95 -or $normalized -eq 'THERMAL_CRITICAL'
    $diagnostics = if ($critical) { 'THERMAL_CRITICAL' }
        elseif ($normalized -eq 'ERROR') { 'ERROR' }
        elseif ($normalized -eq 'CANCELLED') { 'CANCELLED' }
        elseif (-not $SensorAvailable -or $MaxCpuTemp -le 0 -or $normalized -eq 'UNKNOWN') { 'SENSOR_UNAVAILABLE' }
        elseif ($normalized -eq 'WARN') { 'WARN' }
        elseif ($normalized -eq 'PASS') { 'OK' }
        else { 'NOT_VERIFIED' }
    [PSCustomObject]@{
        diagnostics_status = $diagnostics
        cooling_verified = ($diagnostics -eq 'OK')
        manual_check_required = ($diagnostics -ne 'OK')
        thermal_critical = [bool]$critical
        diagnostics_warning = switch ($diagnostics) {
            'OK' { '' }
            'THERMAL_CRITICAL' { 'Kriticno pregrevanje: predaja je blokirana.' }
            'SENSOR_UNAVAILABLE' { 'Temperatura nije izmerena; obavezna rucna provera hladjenja.' }
            'WARN' { 'Povisena temperatura ili throttling; potrebna servisna provera.' }
            default { 'Dijagnostika nije uspesno zavrsena; potrebna rucna provera.' }
        }
    }
}
Export-ModuleMember -Function Get-WisDiagnosticsContract
