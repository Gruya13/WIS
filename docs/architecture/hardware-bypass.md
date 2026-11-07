---
title: "Zaobilaženje Hardverskih Zahteva, WinPE Pre-Boot i Ultra-Lean ISO (Hardware Bypass & WinPE)"
type: architecture
created: 2026-09-14
updated: 2026-10-26
tags:
  - architecture
  - unattend
  - bypass
  - winpe
  - vmd
  - windows-11
---

# Zaobilaženje Hardverskih Zahteva, WinPE Pre-Boot i Ultra-Lean ISO: WIS (v2.17.0)

Kako bi se Windows 11 24H2 (Build `10.0.26200.8037`) instalirao na sve generacije računara u servisu (od starijih procesora bez TPM 2.0 do najnovijih Intel Gen 11–14 i Core Ultra laptopova sa VMD kontrolerima), **WIS** primenjuje višeslojnu WinPE Pre-Boot i Unattend arhitekturu.

---

## ⚡ 1. Univerzalni Dual-Index `boot.wim` i Selektivna PCI VMD Detekcija

Alat `ISO/Skripte/Patch-BootWim.py` zakrpljuje oba indeksa u `ISO/sources/boot.wim` (`668.9 MB` — Index 1 WinPE i Index 2 Windows Setup):
- **Intel RST VMD Skladišni Drajveri (`X:\Drivers\VMD\` i `ISO/Za disk 10+ gen/`):**
  - `01_RST_v20_Gen12-14_CoreUltra` (`v20.0.0.1038`): Za Intel 12, 13. i 14. generaciju i Core Ultra (`DEV_7D0B`, `DEV_A77F`, `DEV_467F`).
  - `02_RST_v19_Gen11-13` (`v19.5.2.1049`): Za Intel 11. generaciju Tiger Lake (`PCI\VEN_8086&DEV_9A0B`) — sprečava nestajanje NVMe diskova kada bi noviji v20 drajver pregazio Tiger Lake kontroler.
  - `03_RST_v18_Legacy_Optane` (`v18.6.1.1016`): Za Intel 10. generaciju i Optane memoriju.
- **Mrežni Drajveri (`X:\Drivers\Net\`):** Intel LAN/Wi-Fi, Realtek PCIe/USB GBE i Wi-Fi, MediaTek Wi-Fi.
- **WinPE Turbo Akceleracija:** U `VentoyAutoRun.bat` i `startnet.cmd` aktivira se High Performance profil (`powercfg.exe /s 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c`) i proširuje FBWF RAM keš (`WinPECacheThreshold`) sa 32 MB na **512 MB**.
- **Idempotentni Tag Bypass (`X:\WIS_PreBoot_Done.tag`):** Sprečava dvostruku inicijalizaciju mreže i drajvera pri Ventoy/iVentoy butovanju (ušteda 20–30 sekundi).
- **WinPE SMB 3.1.1 Akcelerator (`WIS-AcceleratePE.cmd` & `WIS-ConnectNAS.cmd`):** Upisuje `192.168.170.254 truenas.servis` u `hosts`, mapira TrueNAS deljenje `\\truenas.servis\WIS` na disk `Z:` i `\\truenas.servis\WIS\win11` na disk `Y:` preko porta 445 (**105–122 MB/s**), zaobilazeći spori iVentoy HTTP/CIFS port 10445.

---

## 🛑 2. Prolaz 1: `windowsPE` i Čisti `LabConfig` Registar (Bez Skripti i PnP Kolizija)

Radi trajne eliminacije Setup grešaka **`0x80070103 - 0x40031`** (Driver Ranking kolizija više `iaStorVD.inf` verzija) i **`0x800700FF - 0x40030`** (pad SetupPlatform-a na eksternim skriptama), iz `windowsPE` prolaza u `ISO/autounattend.xml` uklonjen je `PnpCustomizationsWinPE` i sve batch komande, a zadržano je isključivo **6 deklarativnih `LabConfig` registarskih komandi**:

```xml
<RunSynchronous>
    <RunSynchronousCommand wcm:action="add">
        <Order>1</Order>
        <Path>reg add "HKLM\SYSTEM\Setup\LabConfig" /v "BypassTPMCheck" /t REG_DWORD /d 1 /f</Path>
    </RunSynchronousCommand>
    <RunSynchronousCommand wcm:action="add">
        <Order>2</Order>
        <Path>reg add "HKLM\SYSTEM\Setup\LabConfig" /v "BypassSecureBootCheck" /t REG_DWORD /d 1 /f</Path>
    </RunSynchronousCommand>
    <RunSynchronousCommand wcm:action="add">
        <Order>3</Order>
        <Path>reg add "HKLM\SYSTEM\Setup\LabConfig" /v "BypassRAMCheck" /t REG_DWORD /d 1 /f</Path>
    </RunSynchronousCommand>
    <RunSynchronousCommand wcm:action="add">
        <Order>4</Order>
        <Path>reg add "HKLM\SYSTEM\Setup\LabConfig" /v "BypassStorageCheck" /t REG_DWORD /d 1 /f</Path>
    </RunSynchronousCommand>
    <RunSynchronousCommand wcm:action="add">
        <Order>5</Order>
        <Path>reg add "HKLM\SYSTEM\Setup\LabConfig" /v "BypassCPUCheck" /t REG_DWORD /d 1 /f</Path>
    </RunSynchronousCommand>
    <RunSynchronousCommand wcm:action="add">
        <Order>6</Order>
        <Path>reg add "HKLM\SYSTEM\Setup\LabConfig" /v "BypassDiskCheck" /t REG_DWORD /d 1 /f</Path>
    </RunSynchronousCommand>
</RunSynchronous>
```

---

## 🎯 3. Dual-Edition Solid LZMS `install.esd`, `ei.cfg` i Zlatni Lean ISO (6.08 GB)

1. **Dual-Edition Solid LZMS (`ISO/sources/install.esd`, `5.34 GB`):**
   - Pomoću `ISO/Skripte/Export-SlimWim.py` fabrički WIM sa 11 edicija sveden je na tačno 2 edicije (**Index 1: Windows 11 Home**, **Index 2: Windows 11 Pro**) i kompresovan u Solid LZMS format (`lzms:100`).
   - Ukupna veličina hibridnog UDF (`ECMA-167`) + UEFI (`0xEF`) + BIOS (`0x00`) Zlatnog Lean ISO medijuma (`WIS-Win11-Pro-10.0.26200.8037-Lean.iso`) iznosi **6.08 GB (`6,531,049,472 B`)**.
2. **Konfiguracioni fajl `ISO/sources/ei.cfg` i Generic Setup Ključ:**
   - Sprečava da ugrađeni OEM Home ključ u UEFI MSDM tabeli nametne Home ediciju bez izbora, dok generički Pro ključ u `autounattend.xml` automatizuje izbor Pro edicije. Trajna aktivacija se obavlja u Fazi 4 preko OEM OA3x verifikacije.

---

## 👤 4. Prolaz 4 (`specialize`) i Prolaz 7 (`oobeSystem`)

- **Prolaz `specialize`:**
  - Postavlja `BypassNRO = 1` u `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\OOBE`.
  - Otključava Windows 11 24H2 SMB polise (`AllowInsecureGuestAuth = 1`, `RequireSecuritySignature = 0`).
  - Privremeno utišava UAC (`ConsentPromptBehaviorAdmin = 0`, `PromptOnSecureDesktop = 0`) radi 100% Zero-Touch izvršavanja (vraća se na fabričke vrednosti `5` i `1` u Fazi 6).
- **Prolaz `oobeSystem`:**
  - Kreira lokalni administratorski nalog `Korisnik`, uključuje `AutoLogon`, sakriva sve OOBE ekrane (`ProtectYourPC = 3`) i pokreće `FirstLogon.cmd`.

---

## 🔗 Povezane Beleške
- Glavni Indeks: [[index]]
- Pregled Arhitekture: [[architecture/overview]]
- Ultra-Lean Solid LZMS ISO: [[decisions/ADR-0122-ultra-lean-solid-lzms-iso-architecture]]
- Eliminacija Grešaka 0x80070103 i 0x800700FF: [[decisions/ADR-0123-fix-setup-0x80070103-winpe-pnp-removal]], [[decisions/ADR-0126-eliminate-winpe-setup-0x800700ff]]
