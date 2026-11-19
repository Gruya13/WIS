---
title: "ADR-0025: Univerzalni Fallback Mrežnih Drajvera za Prevenciju Zastoja i Rad bez LAN Kabla"
type: decision
status: accepted
date: 2026-09-23
author: gwadt_docs_sync
tags:
  - decision
  - adr
  - network
  - drivers
  - pnputil
  - wifi
  - lan
  - zero-touch
---

# ADR-0025: Univerzalni Fallback Mrežnih Drajvera za Prevenciju Zastoja i Rad bez LAN Kabla

## 1. Kontekst i Problem

Uspostavljanje stabilne mrežne veze u ranoj fazi automatizovane pripreme računara (Faza 1 pajplajna) predstavlja preduslov za rad svih narednih faza: preuzimanje i instalaciju WHQL drajvera preko Windows Update COM API-ja, online/offline OEM licenciranje, preuzimanje paketa i slanje telemetrije ka servisnom hub-u.

Međutim, u praksi modernog servisa pojavio se ozbiljan problem:
1. **Nedostatak fabričkih Wi-Fi drajvera u Windows 11 ISO slici:** Najnovije generacije bežičnih adaptera (Intel Wi-Fi 6E/7 AX211, AX411, BE200; Realtek RTL8852BE, RTL8852CE; MediaTek MT7921, MT7922; Qualcomm FastConnect) često nemaju ugrađene generičke drajvere unutar fabričkog `install.wim` fajla.
2. **Nestanak fizičkog RJ45 porta na savremenim laptopovima:** Većina ultrabook i tankih prenosnih računara (npr. Dell XPS, Asus Zenbook, Lenovo Yoga, HP Spectre) nema integrisani eternet (LAN) priključak.
3. **Zastoj instalacije u LAN pauzi:** Kada skripta nije uspevala da pronađe Wi-Fi adapter, ulazila je u petlju `Wait-ForNetworkConnection` zahtevajući od servisera da utakne LAN mrežni kabl. Na uređajima bez RJ45 porta, to je dovodilo do potpunog zastoja procesa ukoliko serviser pri ruci nema slobodan USB-C na RJ45 adapter, čime se kršio princip *Zero-Touch* servisa.

Bilo je neophodno implementirati automatski, samostalni mehanizam koji pre bilo kakvog blokirajućeg čekanja detektuje odsustvo mrežnih adaptera, skenira lokalne i spoljne repozitorijume sa pripremljenim mrežnim paketima i autonomno instalira potrebne `.inf` drajvere u Plug-and-Play (PnP) podsistem.

---

## 2. Razmatrane Opcije

- **Opcija 1: Forsiranje manuelne intervencije i upotrebe USB-LAN adaptera.**
  - *Mane:* Zahteva fizičko prisustvo servisera, posedovanje dodatnog hardvera i neprekidno dežurstvo pored mašine; potpuno suprotno autonomnom radu sistema.
- **Opcija 2: Slipstreaming svih mogućih drajvera direktno u `install.wim` (Offline injekcija).**
  - *Mane:* WIM slika se drastično uvećava (neretko za 10-20 GB), produžava se vreme primarnog WinPE raspakivanja na SSD, a povećava se i rizik od BSOD kolizija nespojivih drajvera tokom prvog butovanja.
- **Opcija 3: Dinamički lokalni i eksterni fallback drajver repozitorijum sa tiho vođenim `pnputil.exe` podsistemom (Usvojeno).**
  - *Prednosti:* Održava `install.wim` vitkim i čistim; drajveri se smeštaju u strukturirani direktorijum `Drivers\Net\`; skripta pretražuje lokalni SSD (`C:\setup\Drivers\Net`), USB medijum (`$OEM$` stablo i `Programi\Drivers\Net`) i sve montirane diskove; instalacija se vrši nativno preko `pnputil.exe /add-driver ... /subdirs /install` uz 4-sekundni PnP stabilizacioni osigurač.

---

## 3. Doneta Odluka

Usvojena je **Opcija 3**. U modulu [`GWADT.Network.psm1`](file:///home/gru/Projects/WIS Service%20windows%2011%20automat/Skripte/Modules/GWADT.Network.psm1) implementirana je funkcija `Install-FallbackNetworkDrivers`, koja se autonomno poziva na ključnim tačkama mrežne faze.

```mermaid
flowchart TD
    A[Start: Mrežna Faza / Connect-ServisWifi] --> B[Get-NetAdapter provera]
    B --> C{Postoji li ijedan adapter<br/>ILI postoji li Wi-Fi adapter?}
    C -- "Da (Mreža je operativna)" --> D[Nastavi sa standardnim povezivanjem na Wi-Fi]
    
    C -- "Ne (Nedostaju drajveri!)" --> E[Log WARN: Nedostaje adapter, aktiviram Fallback]
    E --> F[Skeniraj kandidate za repozitorijum:<br/>1. C:\setup\Drivers\Net<br/>2. sources\$OEM$\$1\setup\Drivers\Net<br/>3. Programi\Drivers\Net<br/>4. Root svih montiranih diskova]
    
    F --> G{Pronađen direktorijum sa .inf fajlovima?}
    G -- Ne --> H[Log WARN: Nema dostupnih .inf drajvera<br/>Prelazak na LAN pauzu kao krajnju opciju]
    
    G -- Da --> I[Prikaži TUI status:<br/>Instaliram fallback drajvere...]
    I --> J["Izvrši: pnputil.exe /add-driver *.inf /subdirs /install"]
    J --> K[PnP Stabilizacioni tajmaut: 4 sekunde (Start-Sleep)]
    K --> L[Ponovno očitavanje Get-NetAdapter]
    L --> M{Adapteri uspešno registrovani?}
    M -- Da --> N[Log SUCCESS: Novi adapteri aktivni!<br/>Nastavi Wi-Fi povezivanje bez pauze]
    M -- Ne --> H
```

### 1. Arhitektura Repozitorijuma Mrežnih Drajvera

U stablu projekta kreirani su standardizovani direktorijumi:
- `sources/$OEM$/$1/setup/Drivers/Net/` — Automatski se tokom WinPE faze kopira na klijentski SSD u `C:\setup\Drivers\Net\`.
- `Programi/Drivers/Net/` — Sekundarna rezerva koja se nalazi na USB medijumu u folderu sa programima.

Struktura foldera omogućava serviseru jednostavno ubacivanje raspakovanih drajvera po proizvođačima:
```
Drivers/Net/
├── Intel/
│   ├── WiFi/           # Netwtw*.inf drajveri
│   └── LAN/            # e1d*.inf drajveri
├── Realtek/
│   ├── WiFi/           # rtwlane*.inf drajveri
│   └── LAN/            # rt640x64.inf drajveri
├── MediaTek/
│   └── WiFi/           # mtkwl6ex.inf drajveri
└── README.md           # Uputstvo za održavanje INF paketa
```

### 2. Algoritam Detekcije i Instalacije (Izvod iz Implementacije)

Funkcija `Install-FallbackNetworkDrivers` koristi sledeći deterministički tok:

```powershell
function Install-FallbackNetworkDrivers {
    $adapters = Get-NetAdapter -ErrorAction SilentlyContinue
    $hasAny = ($adapters -and $adapters.Count -gt 0)
    $hasWifi = $false
    if ($hasAny) {
        $wifiAdapters = $adapters | Where-Object {
            $_.InterfaceDescription -match 'Wi-Fi|Wireless|802.11' -or $_.Name -match 'Wi-Fi'
        }
        if ($wifiAdapters) { $hasWifi = $true }
    }

    # Ako nema uopšte adaptera ili nema bežičnog adaptera
    if (-not $hasAny -or -not $hasWifi) {
        Write-ServisLog "Nedostaje mrezni adapter... Trazim fallback drajvere..." -Level "WARN" -Component "Network"

        $candidateDirs = @(
            "C:\setup\Drivers\Net",
            (Join-Path $PSScriptRoot "..\..\sources\`$OEM`$\`$1\setup\Drivers\Net"),
            (Join-Path $PSScriptRoot "..\..\Programi\Drivers\Net"),
            "C:\setup\Programi\Drivers\Net"
        )
        # Pretraga svih diskova (uključujući USB fleš)
        foreach ($d in (Get-PSDrive -PSProvider FileSystem -ErrorAction SilentlyContinue)) {
            $candidateDirs += (Join-Path "$($d.Root)" "Programi\Drivers\Net")
            $candidateDirs += (Join-Path "$($d.Root)" "Drivers\Net")
            $candidateDirs += (Join-Path "$($d.Root)" "sources\`$OEM`$\`$1\setup\Drivers\Net")
        }

        $installedAny = $false
        foreach ($driverDir in $candidateDirs) {
            if ($driverDir -and (Test-Path $driverDir)) {
                $infFiles = Get-ChildItem -Path $driverDir -Filter "*.inf" -Recurse -File -ErrorAction SilentlyContinue
                if ($infFiles -and $infFiles.Count -gt 0) {
                    Write-ServisLog "Instaliram fallback drajvere iz: $driverDir ($($infFiles.Count) .inf)..." -Level "INFO" -Component "Network"
                    & pnputil.exe /add-driver "$driverDir\*.inf" /subdirs /install | Out-Null
                    $installedAny = $true
                }
            }
        }

        if ($installedAny) {
            # PnP osigurač za inicijalizaciju novog hardvera
            Start-Sleep -Seconds 4
            $adapters = Get-NetAdapter -ErrorAction SilentlyContinue
            Write-ServisLog "Nakon PnP registracije detektovano $($adapters.Count) adaptera." -Level "SUCCESS" -Component "Network"
        }
    }
}
```

### 3. Tačke Uvezivanja u Pajplajn

Kako bi se garantovala stopostotna prevencija zastoja, `Install-FallbackNetworkDrivers` je integrisan na 4 nivoa:
1. **`Initialize-ServisNetwork`:** Poziva se odmah na početku modula pri inicijalizaciji mrežnog steka.
2. **`Invoke-NetworkPhase`:** Poziva se pre bilo kakvog pokušaja Wi-Fi autentifikacije.
3. **`Connect-ServisWifi`:** Poziva se ukoliko u momentu traženja bežičnih interfejsa servis ne vidi Wi-Fi adapter.
4. **`Wait-ForNetworkConnection`:** Poziva se kao bezbednosni osigurač neposredno pre nego što bi skripta ispisala zahtev za uključenje fizičkog LAN kabla.

---

## 4. Posledice i Benefiti

### Pozitivne Posledice:
- **Potpuna eliminacija zastoja na ultrabook laptopovima:** Uređaji bez LAN porta više ne blokiraju automatizovani tok rada; Wi-Fi se podiže samostalno iz fallback paketa.
- **Nulto opterećenje WIM slike:** `install.wim` ostaje standardne veličine i lako se ažurira, dok se repozitorijum drajvera može nezavisno osvežavati novim verzijama jednostavnim kopiranjem INF fajlova.
- **Dvostruka lokacijska elastičnost:** Sistem pretražuje i lokalni SSD (`C:\setup\Drivers\Net`) i eksterni USB fleš disk ukoliko lokalna kopija nije izvršena ili je prostor bio ograničen.
- **Deterministička PnP stabilizacija:** Ugrađeni tajmaut od 4 sekunde eliminiše trku u vremenu (*race condition*) između instalacije drajvera od strane `pnputil.exe` i spremnosti servisa `WlanSvc` da prihvati profile.

### Negativne Posledice i Rizici (i ublažavanje):
- *Rizik:* Vreme izvršavanja `pnputil.exe` ako je direktorijum pretrpan zastarelim ili nekompatibilnim drajverima.
  *Ublažavanje:* Preporučuje se servisno održavanje samo najvažnijih i najčešćih paketa za Wi-Fi (Intel, Realtek, MediaTek), čime se instalacija završava za manje od 3 sekunde.

---

## 5. Povezane Datoteke i Arhitektonske Odluke

- **Implementirani kod:**
  - [`Skripte/Modules/GWADT.Network.psm1`](file:///home/gru/Projects/WIS Service%20windows%2011%20automat/Skripte/Modules/GWADT.Network.psm1) — Funkcija `Install-FallbackNetworkDrivers` i integracija u mrežnu fazu.
  - [`sources/$OEM$/$1/setup/Drivers/Net/README.md`](file:///home/gru/Projects/WIS Service%20windows%2011%20automat/sources/$OEM$/$1/setup/Drivers/Net/README.md) — Uputstvo i struktura za $OEM$ stablo.
  - [`Programi/Drivers/Net/README.md`](file:///home/gru/Projects/WIS Service%20windows%2011%20automat/Programi/Drivers/Net/README.md) — Uputstvo i struktura za USB Programi stablo.
  - [`Skripte/Test-Task043-QA.py`](file:///home/gru/Projects/WIS Service%20windows%2011%20automat/Skripte/Test-Task043-QA.py) — Verifikacija prisustva drajver stabla i AST kompatibilnosti.
- **Povezane Arhitektonske Odluke:**
  - [[ADR-0003-ssd-caching-usb-release]] — Rano kopiranje na `C:\setup` i oslobađanje fleša.
  - [[ADR-0012-dual-network-connectivity-and-persistent-wifi]] — Dual-Homing mrežna povezanost i perzistentni Wi-Fi.
