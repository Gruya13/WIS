<#
.MODULE
    WIS.ScreenCapture.ps1
.DESCRIPTION
    Klijentski radnik za periodicno slanje frejmova ekrana (1 Hz Screen Capture Worker)
    ka centralnom WIS Service ServisHub-u tokom automatizovane instalacije (WinPE / Windows).
    Podrzava snimanje ekrana preko .NET GDI+ API-ja, bicubic skaliranje sa ocuvanjem proporcija,
    JPEG kompresiju u memoriji (bez habanja SSD-a) i ultra-lagano slanje preko HTTP POST-a.
.COMPATIBILITY
    Windows PowerShell 5.1+ (WinPE 10/11, Windows 10, Windows 11, Windows Server)
#>

[CmdletBinding(DefaultParameterSetName="Start")]
param(
    [Parameter(ParameterSetName="Start", Position=0)]
    [string]$HubUrl = "",

    [Parameter(ParameterSetName="Start")]
    [string]$DeviceId = "",

    [Parameter(ParameterSetName="Start")]
    [int]$IntervalMs = 1000,

    [Parameter(ParameterSetName="Start")]
    [int]$Quality = 70,

    [Parameter(ParameterSetName="Start")]
    [int]$MaxWidth = 1280,

    [Parameter(ParameterSetName="Start")]
    [Parameter(ParameterSetName="Stop")]
    [string]$StopEventName = "WIS_ScreenCapture_Stop",

    [Parameter(ParameterSetName="Start")]
    [switch]$RunAsJob,

    [Parameter(ParameterSetName="Start")]
    [int]$MaxFrames = 0,

    [Parameter(ParameterSetName="Start")]
    [int]$ParentPid = 0,

    [Parameter(ParameterSetName="Start")]
    [string]$OwnerSessionId = "",

    [Parameter(ParameterSetName="Start")]
    [Parameter(ParameterSetName="Stop")]
    [string]$LocalSetupDir = "C:\setup",

    [Parameter(ParameterSetName="Stop")]
    [switch]$Stop,

    [switch]$NoAutoStart
)

# Normalizacija ako je pozicioni switch argument (npr. "-RunAsJob" ili "-Stop") završio u $HubUrl
if ($HubUrl -and $HubUrl.Trim().StartsWith("-")) {
    $posFlag = $HubUrl.Trim()
    $HubUrl = ""
    if ($posFlag -match '^(?i)-+Stop$') {
        $Stop = [switch]::Present
    } elseif ($posFlag -match '^(?i)-+RunAsJob$') {
        $RunAsJob = [switch]::Present
    } elseif ($posFlag -match '^(?i)-+NoAutoStart$') {
        $NoAutoStart = [switch]::Present
    }
}

# ------------------------------------------------------------------------------
# GLOBALNO / SKRIPTNO STANJE I INICIJALIZACIJA .NET SKLOPA
# ------------------------------------------------------------------------------

$script:CachedJpegCodec = $null
$script:LastScreenFrameError = ""
$script:LastScreenFrameStatusCode = 0
$Global:WIS_ScreenCapture_Running = $false
$Global:WIS_ScreenCapture_StopRequested = $false

try {
    Add-Type -AssemblyName System.Drawing -ErrorAction SilentlyContinue
    Add-Type -AssemblyName System.Windows.Forms -ErrorAction SilentlyContinue
} catch {}

# ------------------------------------------------------------------------------
# 1. POMOCNA FUNKCIJA: GET-SERVISJPEGCODECINFO
# ------------------------------------------------------------------------------

function Get-ServisJpegCodecInfo {
    <#
    .SYNOPSIS
        Pronalazi i kesira GDI+ ImageCodecInfo za JPEG format.
    #>
    [CmdletBinding()]
    param()

    if ($script:CachedJpegCodec) {
        return $script:CachedJpegCodec
    }

    try {
        $codecs = [System.Drawing.Imaging.ImageCodecInfo]::GetImageEncoders()
        foreach ($c in $codecs) {
            if ($c.FormatID -eq [System.Drawing.Imaging.ImageFormat]::Jpeg.Guid) {
                $script:CachedJpegCodec = $c
                return $c
            }
        }
    } catch {}

    return $null
}

# ------------------------------------------------------------------------------
# 2. POMOCNA FUNKCIJA: CAPTURE-SERVISSCREENFRAME
# ------------------------------------------------------------------------------

function Capture-ServisScreenFrame {
    <#
    .SYNOPSIS
        Hvata primarni ekran radne stanice, skalira ga i vraca kompresovane JPEG bajtove.
    .PARAMETER MaxWidth
        Maksimalna sirina u pikselima (visina se proporcionalno prilagodjava).
    .PARAMETER Quality
        Kvalitet JPEG kompresije (1-100). Preporuceno: 65-75.
    .OUTPUTS
        [byte[]] JPEG binarni podaci u RAM-u (MemoryStream).
    #>
    [CmdletBinding()]
    param(
        [int]$MaxWidth = 1280,
        [int]$Quality = 70
    )

    $srcBmp = $null
    $srcGfx = $null
    $destBmp = $null
    $destGfx = $null
    $memStream = $null
    $encoderParam = $null
    $encoderParams = $null

    try {
        # Ocitavanje granica primarnog ekrana
        $bounds = $null
        try {
            if ([System.Windows.Forms.Screen]::PrimaryScreen) {
                $bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
            }
        } catch {}

        if (-not $bounds -or $bounds.Width -le 0 -or $bounds.Height -le 0) {
            try {
                $vs = [System.Windows.Forms.SystemInformation]::VirtualScreen
                if ($vs.Width -gt 0 -and $vs.Height -gt 0) {
                    $bounds = $vs
                }
            } catch {}
        }

        if (-not $bounds -or $bounds.Width -le 0 -or $bounds.Height -le 0) {
            $bounds = New-Object System.Drawing.Rectangle(0, 0, 1920, 1080)
        }

        # Hvatanje ekrana preko GDI+ CopyFromScreen
        $srcBmp = New-Object System.Drawing.Bitmap($bounds.Width, $bounds.Height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $srcGfx = [System.Drawing.Graphics]::FromImage($srcBmp)
        $srcGfx.CopyFromScreen($bounds.X, $bounds.Y, 0, 0, $bounds.Size, [System.Drawing.CopyPixelOperation]::SourceCopy)

        $targetBmp = $srcBmp

        # Skaliranje sa ocuvanjem proporcija (HighQualityBicubic) ako prelazi MaxWidth
        if ($MaxWidth -gt 0 -and $bounds.Width -gt $MaxWidth) {
            $ratio = [double]$MaxWidth / [double]$bounds.Width
            $destWidth = [int]$MaxWidth
            $destHeight = [int][Math]::Round($bounds.Height * $ratio)
            if ($destHeight -lt 1) { $destHeight = 1 }

            $destBmp = New-Object System.Drawing.Bitmap($destWidth, $destHeight, [System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
            $destGfx = [System.Drawing.Graphics]::FromImage($destBmp)
            $destGfx.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $destGfx.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
            $destGfx.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $destGfx.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
            $destGfx.DrawImage($srcBmp, 0, 0, $destWidth, $destHeight)

            $targetBmp = $destBmp
        }

        # Preuzimanje JPEG kodeka
        $codec = Get-ServisJpegCodecInfo
        if (-not $codec) {
            throw "JPEG ImageCodecInfo nije pronadjen."
        }

        # JPEG Encoder sa EncoderQuality parametrom ([System.Drawing.Imaging.Encoder]::Quality)
        $qClamped = [Math]::Max(10, [Math]::Min(100, $Quality))
        $encoderQuality = [System.Drawing.Imaging.Encoder]::Quality
        $encoderParams = New-Object System.Drawing.Imaging.EncoderParameters(1)
        $encoderParam = New-Object System.Drawing.Imaging.EncoderParameter($encoderQuality, [long]$qClamped)
        $encoderParams.Param[0] = $encoderParam

        # Cuvanje direktno u MemoryStream (bez dodirivanja diska)
        $memStream = New-Object System.IO.MemoryStream
        $targetBmp.Save($memStream, $codec, $encoderParams)
        return $memStream.ToArray()
    }
    finally {
        # Striktno oslobadjanje svih GDI+ i .NET resursa
        if ($encoderParam) { try { $encoderParam.Dispose() } catch {} }
        if ($encoderParams) { try { $encoderParams.Dispose() } catch {} }
        if ($memStream) { try { $memStream.Dispose() } catch {} }
        if ($destGfx) { try { $destGfx.Dispose() } catch {} }
        if ($destBmp) { try { $destBmp.Dispose() } catch {} }
        if ($srcGfx) { try { $srcGfx.Dispose() } catch {} }
        if ($srcBmp) { try { $srcBmp.Dispose() } catch {} }
    }
}

# ------------------------------------------------------------------------------
# 3. POMOCNA FUNKCIJA: SEND-SERVISSCREENFRAME
# ------------------------------------------------------------------------------

function Resolve-ServisScreenCaptureHubUrl {
    <#
    .SYNOPSIS
        Razrešava bazni URL ServisHub-a iz eksplicitnog parametra, deploy-state.json, Wifi-Servis.ini ili podrazumevane adrese.
    #>
    [CmdletBinding()]
    param(
        [string]$CandidateHubUrl = "",
        [string]$LocalSetupDir = "C:\setup",
        [switch]$ForceRefresh
    )

    if ($CandidateHubUrl -and -not $CandidateHubUrl.Trim().StartsWith("-")) {
        $clean = $CandidateHubUrl.Trim().TrimEnd('/')
        if ($clean -notmatch '^https?://') {
            $clean = "http://$clean"
        }
        return $clean
    }

    # 1. Provera deploy-state.json (hub_url)
    $statePaths = @(
        (Join-Path $LocalSetupDir "deploy-state.json"),
        "C:\setup\deploy-state.json"
    ) | Select-Object -Unique
    foreach ($sp in $statePaths) {
        if ($sp -and (Test-Path $sp -ErrorAction SilentlyContinue)) {
            try {
                $raw = Get-Content -Path $sp -Raw -Encoding UTF8 -ErrorAction SilentlyContinue
                if ($raw) {
                    $obj = ConvertFrom-Json -InputObject $raw -ErrorAction SilentlyContinue
                    if ($obj -and $obj.hub_url -and -not [string]::IsNullOrWhiteSpace("$($obj.hub_url)")) {
                        $stUrl = "$($obj.hub_url)".Trim().TrimEnd('/')
                        if (-not $stUrl.StartsWith("-")) {
                            if ($stUrl -notmatch '^https?://') { $stUrl = "http://$stUrl" }
                            return $stUrl
                        }
                    }
                }
            } catch {}
        }
    }

    # 2. Provera Wifi-Servis.ini ([ServisHub] / [GdsServer])
    $iniPaths = @(
        (Join-Path $LocalSetupDir "Wifi-Servis.ini"),
        (Join-Path $PSScriptRoot "Wifi-Servis.ini"),
        (Join-Path (Split-Path $PSScriptRoot -Parent) "Wifi-Servis.ini"),
        "C:\setup\Wifi-Servis.ini"
    ) | Select-Object -Unique
    foreach ($ip in $iniPaths) {
        if ($ip -and (Test-Path $ip -ErrorAction SilentlyContinue)) {
            try {
                $iniRaw = Get-Content -Path $ip -Raw -ErrorAction SilentlyContinue
                if ($iniRaw -match '(?mi)^\s*\[(ServisHub|GdsServer)\]([\s\S]*?)(?:^\s*\[|\Z)') {
                    $sec = $matches[2]
                    $port = 8080
                    if ($sec -match '(?mi)^\s*(?:ServerPort|Port)\s*=\s*([0-9]+)') {
                        $port = [int]$matches[1].Trim()
                    }
                    if ($sec -match '(?mi)^\s*(?:HubUrl|Url)\s*=\s*([^\r\n#;]+)') {
                        $u = $matches[1].Trim().TrimEnd('/')
                        if ($u -and $u -ne "auto" -and -not $u.StartsWith("-")) {
                            if ($u -notmatch '^https?://') { $u = "http://$u" }
                            return $u
                        }
                    }
                    if ($sec -match '(?mi)^\s*(?:ServerHost|Host)\s*=\s*([^\r\n#;]+)') {
                        $hToken = (($matches[1] -split ',') | ForEach-Object { $_.Trim() } | Where-Object { $_ -and $_ -ne "auto" }) | Select-Object -First 1
                        if ($hToken) {
                            if ($hToken -match '^https?://') {
                                return $hToken.TrimEnd('/')
                            }
                            return "http://${hToken}:${port}"
                        }
                    }
                }
            } catch {}
        }
    }

    # 3. Globalna promenljiva ili Find-ServisHub
    if ($Global:ServisHubUrl -and -not $ForceRefresh -and -not "$($Global:ServisHubUrl)".Trim().StartsWith("-")) {
        return "$($Global:ServisHubUrl)".Trim().TrimEnd('/')
    }
    if (Get-Command Find-ServisHub -ErrorAction SilentlyContinue) {
        try {
            $found = if ($ForceRefresh) { Find-ServisHub -LocalSetupDir $LocalSetupDir -ForceRefresh } else { Find-ServisHub -LocalSetupDir $LocalSetupDir }
            if ($found -and -not "$found".Trim().StartsWith("-")) {
                return "$found".Trim().TrimEnd('/')
            }
        } catch {}
    }

    return "http://192.168.170.254:8080"
}

function Save-ServisScreenCaptureState {
    <#
    .SYNOPSIS
        Upisuje dijagnostičko stanje Screen Capture radnika u C:\setup\.screen_capture_state.json.
    #>
    [CmdletBinding()]
    param(
        [int]$WorkerPid = $PID,
        [string]$StartedAt = "",
        [string]$HubUrl = "",
        [string]$SessionId = "",
        [int]$FramesSent = 0,
        [bool]$FirstFrameAccepted = $false,
        [string]$LastError = "",
        [string]$LocalSetupDir = "C:\setup"
    )
    try {
        if (-not $StartedAt) { $StartedAt = (Get-Date).ToString("o") }
        $payload = [ordered]@{
            pid                  = [int]$WorkerPid
            started_at           = $StartedAt
            hub_url              = [string]$HubUrl
            session_id           = [string]$SessionId
            frames_sent          = [int]$FramesSent
            first_frame_accepted = [bool]$FirstFrameAccepted
            last_error           = [string]$LastError
            updated_at           = (Get-Date).ToString("o")
        }
        $json = ConvertTo-Json -InputObject $payload -Depth 4 -Compress
        $utf8NoBom = New-Object System.Text.UTF8Encoding($false)

        $stateFiles = @(
            (Join-Path $LocalSetupDir ".screen_capture_state.json"),
            "C:\setup\.screen_capture_state.json"
        ) | Select-Object -Unique

        foreach ($sf in $stateFiles) {
            try {
                $dir = Split-Path $sf -Parent
                if ($dir -and -not (Test-Path $dir)) {
                    New-Item -Path $dir -ItemType Directory -Force -ErrorAction SilentlyContinue | Out-Null
                }
                [System.IO.File]::WriteAllText($sf, $json, $utf8NoBom)
            } catch {}
        }
    } catch {}
}

function Test-ServisScreenCaptureProcess {
    <#
    .SYNOPSIS
        Proverava da li zadati PID zaista pripada aktivnom PowerShell procesu koji izvršava WIS.ScreenCapture.ps1.
    #>
    [CmdletBinding()]
    param(
        [int]$TargetPid
    )
    if ($TargetPid -le 0) { return $false }
    try {
        $proc = Get-Process -Id $TargetPid -ErrorAction SilentlyContinue
        if (-not $proc) { return $false }
        if ($proc.ProcessName -notmatch '(?i)powershell|pwsh') { return $false }
        try {
            $cimProc = Get-CimInstance Win32_Process -Filter "ProcessId = $TargetPid" -ErrorAction SilentlyContinue
            if ($cimProc -and $cimProc.CommandLine) {
                if ($cimProc.CommandLine -match '(?i)WIS\.ScreenCapture\.ps1|Start-ServisScreenCapture') {
                    return $true
                }
                return $false
            }
        } catch {}
        return $true
    } catch {
        return $false
    }
}

function Send-ServisScreenFrame {
    <#
    .SYNOPSIS
        Salje JPEG frejm na ServisHub endpoint preko HTTP POST poziva.
    .PARAMETER HubUrl
        Adresa ServisHuba (npr. http://192.168.170.254:8080).
    .PARAMETER DeviceId
        ID uredjaja ili ime racunara.
    .PARAMETER FrameBytes
        Niz bajtova JPEG slike.
    .PARAMETER TimeoutMs
        Maksimalno vreme cekanja na odgovor u ms. Podrazumevano: 800.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory=$true)]
        [string]$HubUrl,

        [Parameter(Mandatory=$true)]
        [string]$DeviceId,

        [Parameter(Mandatory=$true)]
        [byte[]]$FrameBytes,

        [int]$TimeoutMs = 800
    )

    if (-not $FrameBytes -or $FrameBytes.Length -eq 0) {
        $script:LastScreenFrameError = "Empty frame bytes"
        $script:LastScreenFrameStatusCode = 0
        return $false
    }

    if ([string]::IsNullOrWhiteSpace($HubUrl) -or $HubUrl.Trim().StartsWith("-")) {
        $script:LastScreenFrameError = "Invalid HubUrl: $HubUrl"
        $script:LastScreenFrameStatusCode = 0
        return $false
    }

    $cleanHubUrl = $HubUrl.Trim().TrimEnd('/')
    if ($cleanHubUrl -notmatch '^https?://') {
        $cleanHubUrl = "http://$cleanHubUrl"
    }
    $cleanDeviceId = [System.Uri]::EscapeDataString($DeviceId)
    $endpoint = "$cleanHubUrl/api/devices/$cleanDeviceId/screen-frame"

    # Unix epoch timestamp u sekundama
    $epoch = New-Object DateTime(1970, 1, 1, 0, 0, 0, [DateTimeKind]::Utc)
    $unixSec = [int64](([DateTime]::UtcNow - $epoch).TotalSeconds)

    $reqStream = $null
    $resp = $null

    try {
        $req = [System.Net.HttpWebRequest]::Create($endpoint)
        $req.Method = "POST"
        $req.ContentType = "image/jpeg"
        $req.Headers.Add("X-Client-Timestamp", [string]$unixSec)
        $req.Timeout = $TimeoutMs
        $req.ReadWriteTimeout = $TimeoutMs
        $req.KeepAlive = $true
        $req.ServicePoint.Expect100Continue = $false
        $req.ContentLength = $FrameBytes.Length

        $reqStream = $req.GetRequestStream()
        $reqStream.Write($FrameBytes, 0, $FrameBytes.Length)
        $reqStream.Flush()
        $reqStream.Close()
        $reqStream.Dispose()
        $reqStream = $null

        $resp = $req.GetResponse()
        $statusCode = 200
        try {
            $statusCode = [int]$resp.StatusCode
        } catch {
            $statusCode = 200
        }
        $resp.Close()
        $resp.Dispose()
        $resp = $null

        $script:LastScreenFrameStatusCode = $statusCode
        if ($statusCode -eq 200) {
            $script:LastScreenFrameError = ""
            return $true
        } else {
            $script:LastScreenFrameError = "HTTP status $statusCode"
            return ($statusCode -ge 200 -and $statusCode -lt 300)
        }
    }
    catch [System.Net.WebException] {
        $script:LastScreenFrameError = $_.Exception.Message
        $script:LastScreenFrameStatusCode = 0
        if ($_.Exception -and $_.Exception.Response) {
            try {
                $script:LastScreenFrameStatusCode = [int]$_.Exception.Response.StatusCode
            } catch {}
            try {
                $_.Exception.Response.Close()
                $_.Exception.Response.Dispose()
            } catch {}
        }
        return $false
    }
    catch {
        $script:LastScreenFrameError = $_.Exception.Message
        $script:LastScreenFrameStatusCode = 0
        return $false
    }
    finally {
        if ($reqStream) { try { $reqStream.Dispose() } catch {} }
        if ($resp) { try { $resp.Dispose() } catch {} }
    }
}

# ------------------------------------------------------------------------------
# 4. GLAVNA FUNKCIJA: START-SERVISSCREENCAPTURE
# ------------------------------------------------------------------------------

function Start-ServisScreenCapture {
    <#
    .SYNOPSIS
        Pokrece 1 Hz petlju za hvatanje i slanje frejmova ekrana ka centralnom ServisHubu.
    .PARAMETER HubUrl
        Bazni URL ServisHuba (npr. http://192.168.170.254:8080). Ako nije naveden ili počinje sa '-', vrsi se auto-otkrivanje.
    .PARAMETER DeviceId
        Jedinstveni identifikator uredjaja (podrazumevano: $env:COMPUTERNAME).
    .PARAMETER IntervalMs
        Interval izmedju slanja frejmova u ms (podrazumevano: 1000 ms = 1 Hz).
    .PARAMETER Quality
        JPEG kompresioni kvalitet (podrazumevano: 70).
    .PARAMETER MaxWidth
        Maksimalna sirina frejma pre slanja (podrazumevano: 1280 piksela).
    .PARAMETER StopEventName
        Naziv sinhronizacionog EventWaitHandle objekta (podrazumevano: "WIS_ScreenCapture_Stop").
    .PARAMETER RunAsJob
        Pokrece worker kao pozadinski PowerShell posao (Background Job).
    .PARAMETER MaxFrames
        Maksimalan broj frejmova pre zaustavljanja (0 = beskonacna petlja dok se ne zatrazi stop).
    .PARAMETER ParentPid
        Opcioni PID roditeljskog procesa. Ako je zadat (> 0), worker samostalno izlazi čim roditeljski proces prestane da postoji.
    .PARAMETER OwnerSessionId
        Opcioni identifikator sesije vlasnika (npr. UI Master ili FirstLogon).
    .PARAMETER LocalSetupDir
        Putanja do radnog setup direktorijuma (podrazumevano: "C:\setup").
    #>
    [CmdletBinding()]
    param(
        [string]$HubUrl = "",
        [string]$DeviceId = "",
        [int]$IntervalMs = 1000,
        [int]$Quality = 70,
        [int]$MaxWidth = 1280,
        [string]$StopEventName = "WIS_ScreenCapture_Stop",
        [switch]$RunAsJob,
        [int]$MaxFrames = 0,
        [int]$ParentPid = 0,
        [string]$OwnerSessionId = "",
        [string]$LocalSetupDir = "C:\setup"
    )

    # Normalizacija ako je u $HubUrl greškom prosleđen pozicioni switch argument (npr. "-RunAsJob" ili "-Stop")
    if ($HubUrl -and $HubUrl.Trim().StartsWith("-")) {
        $flagArg = $HubUrl.Trim()
        $HubUrl = ""
        if ($flagArg -match '^(?i)-+Stop$') {
            return (Stop-ServisScreenCapture -StopEventName $StopEventName -LocalSetupDir $LocalSetupDir)
        } elseif ($flagArg -match '^(?i)-+RunAsJob$') {
            $RunAsJob = [switch]::Present
        }
    }

    # Automatsko otkrivanje HubUrl adrese ako nije zadata ili je bila flag
    $HubUrl = Resolve-ServisScreenCaptureHubUrl -CandidateHubUrl $HubUrl -LocalSetupDir $LocalSetupDir

    # Automatsko definisanje DeviceId ako nije zadat
    if (-not $DeviceId -or $DeviceId.Trim().StartsWith("-")) {
        if ($Global:FleetDeviceId) {
            $DeviceId = $Global:FleetDeviceId
        } elseif ($env:COMPUTERNAME) {
            $DeviceId = $env:COMPUTERNAME
        } else {
            try { $DeviceId = [System.Net.Dns]::GetHostName() } catch { $DeviceId = "UNKNOWN-PC" }
        }
    }

    if (-not $OwnerSessionId) {
        $OwnerSessionId = if ($ParentPid -gt 0) { "PID-$ParentPid" } else { "PID-$PID" }
    }

    # Pozadinski rezim (PowerShell Background Job) kada se funkcija pozove direktno sa -RunAsJob
    if ($RunAsJob) {
        $jobName = "WIS_ScreenCapture_Worker"
        try {
            Get-Job -Name "$jobName*" -ErrorAction SilentlyContinue | Stop-Job -ErrorAction SilentlyContinue | Remove-Job -Force -ErrorAction SilentlyContinue
        } catch {}

        $scriptFilePath = $null
        if ($PSCommandPath -and (Test-Path $PSCommandPath)) {
            $scriptFilePath = $PSCommandPath
        } elseif ($PSScriptRoot -and (Test-Path (Join-Path $PSScriptRoot "WIS.ScreenCapture.ps1"))) {
            $scriptFilePath = Join-Path $PSScriptRoot "WIS.ScreenCapture.ps1"
        }

        $job = Start-Job -Name $jobName -ArgumentList $HubUrl, $DeviceId, $IntervalMs, $Quality, $MaxWidth, $StopEventName, $MaxFrames, $ParentPid, $OwnerSessionId, $LocalSetupDir, $scriptFilePath -ScriptBlock {
            param($hUrl, $dId, $intMs, $qual, $maxW, $evtName, $maxF, $pPid, $sessId, $setupDir, $sFile)
            if ($sFile -and (Test-Path $sFile)) {
                . $sFile -NoAutoStart
            }
            Start-ServisScreenCapture `
                -HubUrl $hUrl `
                -DeviceId $dId `
                -IntervalMs $intMs `
                -Quality $qual `
                -MaxWidth $maxW `
                -StopEventName $evtName `
                -MaxFrames $maxF `
                -ParentPid $pPid `
                -OwnerSessionId $sessId `
                -LocalSetupDir $setupDir
        }
        return $job
    }

    # Single-instance zaštita: provera postojećeg PID fajla i imenovanog Mutex-a
    $pidFiles = @(
        (Join-Path $LocalSetupDir ".screen_capture.pid"),
        "C:\setup\.screen_capture.pid"
    ) | Select-Object -Unique

    foreach ($pf in $pidFiles) {
        if ($pf -and (Test-Path $pf -ErrorAction SilentlyContinue)) {
            try {
                $rawPid = (Get-Content -Path $pf -Raw -ErrorAction SilentlyContinue).Trim()
                $existingPid = 0
                if ([int]::TryParse($rawPid, [ref]$existingPid) -and $existingPid -gt 0 -and $existingPid -ne $PID) {
                    if (Test-ServisScreenCaptureProcess -TargetPid $existingPid) {
                        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                            Write-ServisLog "Screen Capture worker je već aktivan za sesiju (PID: $existingPid). Preskačem duplikat." -Level "INFO" -Component "ScreenCapture"
                        }
                        return
                    }
                }
            } catch {}
        }
    }

    $singleInstanceMutex = $null
    $mutexAcquired = $false
    foreach ($mutexName in @("Global\WIS_ScreenCapture_SingleInstance_Mutex", "Local\WIS_ScreenCapture_SingleInstance_Mutex")) {
        try {
            $singleInstanceMutex = New-Object System.Threading.Mutex($false, $mutexName)
            try {
                $mutexAcquired = $singleInstanceMutex.WaitOne(0, $false)
            } catch [System.Threading.AbandonedMutexException] {
                $mutexAcquired = $true
            }
            break
        } catch {
            if ($singleInstanceMutex) {
                try { $singleInstanceMutex.Dispose() } catch {}
                $singleInstanceMutex = $null
            }
        }
    }

    if ($singleInstanceMutex -and -not $mutexAcquired) {
        try { $singleInstanceMutex.Dispose() } catch {}
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Screen Capture single-instance Mutex je već zauzet. Preskačem duplikat." -Level "INFO" -Component "ScreenCapture"
        }
        return
    }

    # Upis trenutnog PID-a u C:\setup\.screen_capture.pid
    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    foreach ($pf in $pidFiles) {
        try {
            $pfDir = Split-Path $pf -Parent
            if ($pfDir -and -not (Test-Path $pfDir)) {
                New-Item -Path $pfDir -ItemType Directory -Force -ErrorAction SilentlyContinue | Out-Null
            }
            [System.IO.File]::WriteAllText($pf, [string]$PID, $utf8NoBom)
        } catch {}
    }

    # Sinhronizacioni EventWaitHandle
    $hasNamedEvent = $false
    $stopEvent = $null
    try {
        $stopEvent = New-Object System.Threading.EventWaitHandle($false, [System.Threading.EventResetMode]::ManualReset, $StopEventName)
        $stopEvent.Reset() | Out-Null
        $hasNamedEvent = $true
    } catch {}

    # Priprema i čišćenje starih stop markera pre ulaska u petlju
    $stopFlagPath = Join-Path ([System.IO.Path]::GetTempPath()) "$StopEventName.stop"
    $setupStopFiles = @(
        (Join-Path $LocalSetupDir ".screen_capture.stop"),
        "C:\setup\.screen_capture.stop"
    ) | Select-Object -Unique
    foreach ($sf in (@($stopFlagPath) + $setupStopFiles)) {
        if ($sf -and (Test-Path $sf -ErrorAction SilentlyContinue)) {
            Remove-Item -Path $sf -Force -ErrorAction SilentlyContinue
        }
    }

    $Global:WIS_ScreenCapture_Running = $true
    $Global:WIS_ScreenCapture_StopRequested = $false

    $startedAtIso = (Get-Date).ToString("o")
    $frameCount = 0
    $firstFrameAccepted = $false
    $lastError = ""
    $consecutiveFailures = 0
    $backoffScheduleMs = @(1000, 2000, 5000, 10000, 15000)

    Save-ServisScreenCaptureState `
        -WorkerPid $PID `
        -StartedAt $startedAtIso `
        -HubUrl $HubUrl `
        -SessionId $OwnerSessionId `
        -FramesSent 0 `
        -FirstFrameAccepted $false `
        -LastError "" `
        -LocalSetupDir $LocalSetupDir

    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
        Write-ServisLog "Pokrenut 1 Hz Screen Capture worker na Hub: $HubUrl (Uredjaj: $DeviceId, PID: $PID, Session: $OwnerSessionId)" -Level "INFO" -Component "ScreenCapture"
    }

    try {
        while ($true) {
            # 1. Provera svih uslova za zaustavljanje pre novog frejma
            if ($hasNamedEvent -and $stopEvent) {
                if ($stopEvent.WaitOne(0)) { break }
            }
            if (Test-Path $stopFlagPath) { break }
            if ($Global:WIS_ScreenCapture_StopRequested) { break }
            if ($MaxFrames -gt 0 -and $frameCount -ge $MaxFrames) { break }

            # Provera .screen_capture.stop i .stage_completed markera
            if ((Test-Path (Join-Path $LocalSetupDir ".screen_capture.stop")) -or (Test-Path "C:\setup\.screen_capture.stop")) { break }
            if ((Test-Path (Join-Path $LocalSetupDir ".stage_completed")) -or (Test-Path "C:\setup\.stage_completed")) { break }

            # Provera da li je roditeljski proces još uvek živ (ako je zadat -ParentPid)
            if ($ParentPid -gt 0) {
                $parentProc = Get-Process -Id $ParentPid -ErrorAction SilentlyContinue
                if (-not $parentProc) { break }
            }

            # Provera deploy-state.json za deployment_status == "DONE"
            $isDeployDone = $false
            foreach ($dsp in @((Join-Path $LocalSetupDir "deploy-state.json"), "C:\setup\deploy-state.json")) {
                if ($dsp -and (Test-Path $dsp -ErrorAction SilentlyContinue)) {
                    try {
                        $rawDs = Get-Content -Path $dsp -Raw -Encoding UTF8 -ErrorAction SilentlyContinue
                        if ($rawDs -and $rawDs -match '"deployment_status"\s*:\s*"DONE"') {
                            $isDeployDone = $true
                            break
                        }
                    } catch {}
                }
            }
            if ($isDeployDone) { break }

            $sw = [System.Diagnostics.Stopwatch]::StartNew()

            # 2. Hvatanje i slanje frejma
            $sent = $false
            try {
                $frameBytes = Capture-ServisScreenFrame -MaxWidth $MaxWidth -Quality $Quality
                if ($frameBytes -and $frameBytes.Length -gt 0) {
                    $sent = Send-ServisScreenFrame -HubUrl $HubUrl -DeviceId $DeviceId -FrameBytes $frameBytes -TimeoutMs 800
                    if ($sent) {
                        $frameCount++
                        $consecutiveFailures = 0
                        if ($script:LastScreenFrameStatusCode -eq 200) {
                            $firstFrameAccepted = $true
                        }
                        $lastError = ""
                    } else {
                        $consecutiveFailures++
                        $lastError = if ($script:LastScreenFrameError) { $script:LastScreenFrameError } else { "Send-ServisScreenFrame failed" }
                    }
                } else {
                    $consecutiveFailures++
                    $lastError = "Capture-ServisScreenFrame returned empty bytes"
                }
            } catch {
                $consecutiveFailures++
                $lastError = $_.Exception.Message
            }

            # Ažuriranje .screen_capture_state.json
            Save-ServisScreenCaptureState `
                -WorkerPid $PID `
                -StartedAt $startedAtIso `
                -HubUrl $HubUrl `
                -SessionId $OwnerSessionId `
                -FramesSent $frameCount `
                -FirstFrameAccepted $firstFrameAccepted `
                -LastError $lastError `
                -LocalSetupDir $LocalSetupDir

            # Failover: ako je veza pukla 5 ili više puta zaredom, pokušaj osvežavanje Hub adrese
            if ($consecutiveFailures -ge 5) {
                try {
                    $refreshedHub = Resolve-ServisScreenCaptureHubUrl -LocalSetupDir $LocalSetupDir -ForceRefresh
                    if ($refreshedHub) { $HubUrl = $refreshedHub }
                } catch {}
            }

            $sw.Stop()
            $elapsed = $sw.ElapsedMilliseconds

            # Određivanje ciljanog intervala spavanja (1 Hz kada Hub radi, stepenasti backoff 1s -> 2s -> 5s -> 10s -> max 15s kada ne radi)
            $targetDelayMs = $IntervalMs
            if ($consecutiveFailures -gt 0) {
                $backoffIdx = [Math]::Min($consecutiveFailures - 1, $backoffScheduleMs.Length - 1)
                $targetDelayMs = [Math]::Max($IntervalMs, $backoffScheduleMs[$backoffIdx])
            }
            $sleepMs = $targetDelayMs - $elapsed

            # 3. Precizna pauza sa trenutnim odzivom na bilo koji stop signal ili završetak sesije
            if ($sleepMs -gt 0) {
                $rem = $sleepMs
                while ($rem -gt 0) {
                    if ($Global:WIS_ScreenCapture_StopRequested) { break }
                    if (Test-Path $stopFlagPath) { break }
                    if ((Test-Path (Join-Path $LocalSetupDir ".screen_capture.stop")) -or (Test-Path "C:\setup\.screen_capture.stop")) { break }
                    if ((Test-Path (Join-Path $LocalSetupDir ".stage_completed")) -or (Test-Path "C:\setup\.stage_completed")) { break }
                    if ($ParentPid -gt 0 -and -not (Get-Process -Id $ParentPid -ErrorAction SilentlyContinue)) { break }

                    $step = [Math]::Min(200, $rem)
                    if ($hasNamedEvent -and $stopEvent) {
                        $wasSignaled = $stopEvent.WaitOne([int]$step)
                        if ($wasSignaled) { break }
                    } else {
                        Start-Sleep -Milliseconds $step
                    }
                    $rem -= $step
                }
            }
        }
    }
    finally {
        $Global:WIS_ScreenCapture_Running = $false

        Save-ServisScreenCaptureState `
            -WorkerPid $PID `
            -StartedAt $startedAtIso `
            -HubUrl $HubUrl `
            -SessionId $OwnerSessionId `
            -FramesSent $frameCount `
            -FirstFrameAccepted $firstFrameAccepted `
            -LastError $lastError `
            -LocalSetupDir $LocalSetupDir

        # Čišćenje .screen_capture.pid samo ako pripada ovom procesu
        foreach ($pf in $pidFiles) {
            if ($pf -and (Test-Path $pf -ErrorAction SilentlyContinue)) {
                try {
                    $curPidText = (Get-Content -Path $pf -Raw -ErrorAction SilentlyContinue).Trim()
                    if ($curPidText -eq "$PID") {
                        Remove-Item -Path $pf -Force -ErrorAction SilentlyContinue
                    }
                } catch {}
            }
        }

        foreach ($sf in (@($stopFlagPath) + $setupStopFiles)) {
            if ($sf -and (Test-Path $sf -ErrorAction SilentlyContinue)) {
                Remove-Item -Path $sf -Force -ErrorAction SilentlyContinue
            }
        }

        if ($stopEvent) {
            try {
                $stopEvent.Close()
                $stopEvent.Dispose()
            } catch {}
        }

        if ($singleInstanceMutex) {
            if ($mutexAcquired) {
                try { $singleInstanceMutex.ReleaseMutex() } catch {}
            }
            try {
                $singleInstanceMutex.Close()
                $singleInstanceMutex.Dispose()
            } catch {}
        }

        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Screen Capture worker zaustavljen. Ukupno poslato frejmova: $frameCount (Prvi frejm prihvaćen: $firstFrameAccepted)" -Level "INFO" -Component "ScreenCapture"
        }
    }
}

# ------------------------------------------------------------------------------
# 5. ZAUSTAVLJANJE: STOP-SERVISSCREENCAPTURE
# ------------------------------------------------------------------------------

function Stop-ServisScreenCapture {
    <#
    .SYNOPSIS
        Zaustavlja 1 Hz screen capture radnika preko sinhronizacionih primitiva i verifikovanog PID-a.
    .PARAMETER StopEventName
        Naziv sistemskog dogadjaja za prekid. Podrazumevano: "WIS_ScreenCapture_Stop".
    .PARAMETER LocalSetupDir
        Putanja do radnog setup direktorijuma (podrazumevano: "C:\setup").
    #>
    [CmdletBinding()]
    param(
        [string]$StopEventName = "WIS_ScreenCapture_Stop",
        [string]$LocalSetupDir = "C:\setup"
    )

    $stopped = $false

    # 1. Globalna varijabla u tekucoj sesiji
    $Global:WIS_ScreenCapture_StopRequested = $true

    # 2. Privremene stop flag datoteke (%TEMP% i C:\setup\.screen_capture.stop)
    $stopFiles = @(
        (Join-Path ([System.IO.Path]::GetTempPath()) "$StopEventName.stop"),
        (Join-Path $LocalSetupDir ".screen_capture.stop"),
        "C:\setup\.screen_capture.stop"
    ) | Select-Object -Unique

    foreach ($flagFile in $stopFiles) {
        try {
            $dir = Split-Path $flagFile -Parent
            if ($dir -and (Test-Path $dir)) {
                [System.IO.File]::WriteAllText($flagFile, (Get-Date).ToString("o"))
                $stopped = $true
            }
        } catch {}
    }

    # 3. .NET Named EventWaitHandle
    try {
        $namedEvent = [System.Threading.EventWaitHandle]::OpenExisting($StopEventName)
        $namedEvent.Set() | Out-Null
        $namedEvent.Close()
        $namedEvent.Dispose()
        $stopped = $true
    } catch {}

    # 4. Prekid bilo kog aktivnog background job-a u tekućoj sesiji
    try {
        $activeJobs = Get-Job -Name "WIS_ScreenCapture_*" -ErrorAction SilentlyContinue
        if ($activeJobs) {
            $activeJobs | Stop-Job -ErrorAction SilentlyContinue | Out-Null
            $activeJobs | Remove-Job -Force -ErrorAction SilentlyContinue | Out-Null
            $stopped = $true
        }
    } catch {}

    # 5. Provera C:\setup\.screen_capture.pid: čekanje do 2s na kooperativni izlaz i ciljani fallback samo nad verifikovanim PID-om
    $pidFiles = @(
        (Join-Path $LocalSetupDir ".screen_capture.pid"),
        "C:\setup\.screen_capture.pid"
    ) | Select-Object -Unique

    foreach ($pf in $pidFiles) {
        if ($pf -and (Test-Path $pf -ErrorAction SilentlyContinue)) {
            try {
                $rawPid = (Get-Content -Path $pf -Raw -ErrorAction SilentlyContinue).Trim()
                $targetPid = 0
                if ([int]::TryParse($rawPid, [ref]$targetPid) -and $targetPid -gt 0 -and $targetPid -ne $PID) {
                    $waitMs = 0
                    while ($waitMs -lt 2000) {
                        if (-not (Get-Process -Id $targetPid -ErrorAction SilentlyContinue)) {
                            break
                        }
                        Start-Sleep -Milliseconds 200
                        $waitMs += 200
                    }
                    # Fallback: ugasi isključivo verifikovani PID čija komandna linija pripada WIS.ScreenCapture.ps1
                    if (Test-ServisScreenCaptureProcess -TargetPid $targetPid) {
                        Stop-Process -Id $targetPid -Force -ErrorAction SilentlyContinue
                        $stopped = $true
                    }
                }
                Remove-Item -Path $pf -Force -ErrorAction SilentlyContinue
            } catch {}
        }
    }

    foreach ($flagFile in $stopFiles) {
        if ($flagFile -and (Test-Path $flagFile -ErrorAction SilentlyContinue)) {
            Remove-Item -Path $flagFile -Force -ErrorAction SilentlyContinue
        }
    }

    return $stopped
}

# ------------------------------------------------------------------------------
# 6. EXPORT / CLI AUTO-START LOGIKA
# ------------------------------------------------------------------------------

if ($ExecutionContext.SessionState.Module) {
    Export-ModuleMember -Function `
        Start-ServisScreenCapture, `
        Stop-ServisScreenCapture, `
        Capture-ServisScreenFrame, `
        Send-ServisScreenFrame, `
        Resolve-ServisScreenCaptureHubUrl, `
        Save-ServisScreenCaptureState, `
        Test-ServisScreenCaptureProcess, `
        Get-ServisJpegCodecInfo
}

# Ako je skripta pokrenuta sa komandne linije (nije dot-source-ovana niti učitana sa -NoAutoStart):
if (-not $NoAutoStart -and $MyInvocation.InvocationName -ne '.') {
    if ($Stop) {
        Stop-ServisScreenCapture -StopEventName $StopEventName -LocalSetupDir $LocalSetupDir
    } elseif ($PSBoundParameters.ContainsKey('HubUrl') -or $PSBoundParameters.ContainsKey('DeviceId') -or $PSBoundParameters.ContainsKey('RunAsJob') -or $PSBoundParameters.ContainsKey('ParentPid') -or $PSBoundParameters.ContainsKey('OwnerSessionId') -or $RunAsJob) {
        Start-ServisScreenCapture `
            -HubUrl $HubUrl `
            -DeviceId $DeviceId `
            -IntervalMs $IntervalMs `
            -Quality $Quality `
            -MaxWidth $MaxWidth `
            -StopEventName $StopEventName `
            -RunAsJob:$false `
            -MaxFrames $MaxFrames `
            -ParentPid $ParentPid `
            -OwnerSessionId $OwnerSessionId `
            -LocalSetupDir $LocalSetupDir
    }
}

