---
title: "Zaobilaženje Hardverskih Zahteva, WinPE Pre-Boot i Ultra-Lean ISO (Hardware Bypass & WinPE)"
type: architecture
created: 2026-09-14
updated: 2026-11-11
tags:
  - architecture
  - unattend
  - bypass
  - winpe
  - vmd
  - windows-11
---

# Zaobilaženje Hardverskih Zahteva, WinPE Pre-Boot i Ultra-Lean ISO: WIS (v2.19.0)

Kako bi se Windows 11 24H2 (Build `10.0.26200.8037`) instalirao na sve generacije računara u servisu (od starijih procesora bez TPM 2.0 do najnovijih Intel Gen 11–14 i Core Ultra laptopova sa VMD kontrolerima i Intel I219-LM PXE mrežom), **WIS** primenjuje višeslojnu WinPE Pre-Boot i Unattend arhitekturu ([[decisions/ADR-0184-winpe-i219lm-driverstore-guard-and-parallel-pxe]]).

---

## ⚡ 1. Univerzalni Dual-Index `boot.wim` (~582 MiB), LAN-Only Net Staging i `000_wis_net1ix64.inf_guard`

Alat `ISO/Skripte/Patch-BootWim.py` (1,313 linija, **32 automatske provere**) zakrpljuje oba indeksa u `ISO/sources/boot.wim` (**609,758,089 B / ~582 MiB** — Index 1 WinPE i Index 2 Windows Setup):
- **Intel RST VMD Skladišni Drajveri (`X:\Drivers\VMD\` i `ISO/Za disk 10+ gen/`):**
  - `01_RST_v20_Gen12-14_CoreUltra` (`v20.0.0.1038`): Za Intel 12, 13. i 14. generaciju i Core Ultra (`DEV_7D0B`, `DEV_A77F`, `DEV_467F`).
  - `02_RST_v19_Gen11-13` (`v19.5.2.1049`): Za Intel 11. generaciju Tiger Lake (`PCI\VEN_8086&DEV_9A0B`) — sprečava nestajanje NVMe diskova kada bi noviji v20 drajver pregazio Tiger Lake kontroler.
  - `03_RST_v18_Legacy_Optane` (`v18.6.1.1016`): Za Intel 10. generaciju i Optane memoriju.
- **LAN-Only Mrežni Staging u WinPE (`X:\Drivers\Net\`, `15` fajlova, `7.57 MiB`):**
  - Radi bržeg PXE preuzimanja `boot.wim`-a (`582 MiB` umesto `639 MiB`), iz WinPE-a je izostavljeno `137.49 MiB` Wi-Fi drajvera (`Intel_LAN`, `Realtek_LAN` i `Realtek_USB_LAN` ostaju aktivni u WinPE-u, dok Wi-Fi paketi ostaju u `$OEM$\$1\setup\Drivers\Net` za instalirani OS).
- **DriverStore Guard za Intel I219-LM (`DEV_15FB` — `000_wis_net1ix64.inf_guard`):**
  - `VTOYPE.EXE PNPHELP` skenira `FileRepository\*` abecedno i na prvi pogodak učitava `drvload e1d.inf` preko već aktivnog inbox servisa `e1i68x64.sys` (`net1ix64.inf`), što obara PXE mrežu na ~3 min (`DEV_15FB`).
  - `Patch-BootWim.py` (`prepare_driverstore_staging`) ugrađuje abecedno prvi paket `000_wis_net1ix64.inf_guard` (`238,786 B`, sadrži `DEV_15FB`, bez `DEV_15FC`/`DEV_0D4F`) i briše konfliktne `/Windows/System32/ventoy` kuke.
- **WinPE Turbo Akceleracija & Bezbedne WinPE Skripte (`WIS-AcceleratePE.cmd` 208 lin., `WIS-ConnectNAS.cmd` 125 lin.):**
  - Aktivira High Performance CPU profil i proširuje FBWF RAM keš (`WinPECacheThreshold`) na **512 MB**; u `WIS-AcceleratePE.cmd` koristi `ping.exe -n N 127.0.0.1` umesto u WinPE nepostojećeg `timeout.exe` i ravnu `goto` logiku bez `()` zagrada i `findstr.exe`.
  - **WinPE SMB 3.1.1 Akcelerator (`105–122 MB/s`):** Ako `IPv4` već radi (`HAS_IPV4=1`), preskače čekanje (`0s`); inače čeka DHCP do `10s`, upisuje `192.168.170.254 truenas.servis`, mapira `Z:` (`\WIS`) i `Y:` (`\WIS\win11`), a iVentoy `T:\iventoy.iso` (`Y:`) oslobađa tek nakon potvrde `Z:\win11\sources\install.*`.
- **iVentoy PXE Infrastruktura & Paralelni Limit:** Watchdog u `entrypoint.sh` proverava servise na svake **3 sekunde**, uz evidentirani limit besplatne verzije `iVentoy 1.0.43 Free` od **20 registrovanih MAC adresa**.

---

## 🛑 2. Prolaz 1: `windowsPE` i Čisti `LabConfig` Registar + `Order 7/8`

U prolazu `windowsPE` datoteke `ISO/autounattend.xml` (`180` linija) uklonjen je `PnpCustomizationsWinPE` (eliminacija `0x80070103 - 0x40031`), a komande su podešene tako da uvek vraćaju nulti exit kod (`Order 1–6` su deklarativni `LabConfig` ključevi, dok `Order 7` pokreće `load_vmd.cmd & ver >nul`, a `Order 8` pokreće `WIS-AcceleratePE.cmd & ver >nul` kako bi se sprečio `0x800700FF - 0x40030`):

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
    <RunSynchronousCommand wcm:action="add">
        <Order>7</Order>
        <Path>cmd.exe /c "call X:\Windows\System32\load_vmd.cmd &amp; ver &gt;nul"</Path>
    </RunSynchronousCommand>
    <RunSynchronousCommand wcm:action="add">
        <Order>8</Order>
        <Path>cmd.exe /c "call X:\Windows\System32\WIS-AcceleratePE.cmd &amp; ver &gt;nul"</Path>
    </RunSynchronousCommand>
</RunSynchronous>
```

---

## 🎯 3. Dual-Edition Solid LZMS `install.esd`, `ei.cfg` i Zlatni Lean ISO (`v2.18.0`, `6.07 GB`)

1. **Dual-Edition Solid LZMS (`ISO/sources/install.esd`, `5,344,805,734 B` / `4.98 GiB` / `5.34 GB`):**
   - Pomoću `ISO/Skripte/Export-SlimWim.py` (`295` linija) fabrički WIM sa 11 edicija sveden je na tačno 2 edicije (**Index 1: Windows 11 Home**, **Index 2: Windows 11 Pro**) i kompresovan u Solid LZMS format (`lzms:100`).
   - Ukupna veličina hibridnog UDF (`ECMA-167`) + UEFI (`0xEF`) + BIOS (`0x00`) Zlatnog Lean ISO medijuma (`WIS-Win11-Pro-10.0.26200.8037-Lean.iso`, `v2.18.0`) iznosi **6.07 GB (`6,522,753,024 B`, SHA-256 `80ef4593...`)**.
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
- Intel I219-LM DriverStore Guard, LAN-Only WinPE i Paralelni PXE: [[decisions/ADR-0184-winpe-i219lm-driverstore-guard-and-parallel-pxe]]
- Ultra-Lean Solid LZMS ISO: [[decisions/ADR-0122-ultra-lean-solid-lzms-iso-architecture]]
- Eliminacija Grešaka 0x80070103 i 0x800700FF: [[decisions/ADR-0123-fix-setup-0x80070103-winpe-pnp-removal]], [[decisions/ADR-0126-eliminate-winpe-setup-0x800700ff]]
