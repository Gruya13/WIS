---
title: "Mrežno Povezivanje, Servisni Wi-Fi i Anti-Leak Sanitizacija (Network & Wi-Fi Automation)"
type: architecture
created: 2026-09-14
updated: 2026-11-11
tags:
  - architecture
  - network
  - wifi
  - smb
  - security
---

# Mrežno Povezivanje, Servisni Wi-Fi i Anti-Leak Sanitizacija: WIS (v3.0.0)

Pouzdan i brz pristup servisnoj mreži (`192.168.170.0/24`) i internetu ključan je za preuzimanje potpisanog `wis-payload.zip` paketa sa `ServiceHub`-a, striming drajvera i programa sa TrueNAS servera (`192.168.170.254`) i izvršavanje Windows Update zakrpa. **WIS** obezbeđuje dvoslojno automatsko povezivanje uz strogo **Anti-Leak brisanje svih servisnih tajni** pre predaje računara klijentu.

---

## ⚙️ 1. Konfiguracioni Fajl: `Wifi-Servis.ini`

Parametri servisne bežične mreže, `ServiceHub` servera i TrueNAS SMB deljenja definisani su u `Wifi-Servis.ini`:
- **`[ServisWifi]`:** Podržava više SSID naziva odvojenih zarezom (primarni 5 GHz i rezervni 2.4 GHz) i lozinku.
- **`[ServisHub]`:** Port `8080` i UDP Discovery port `8888`.
- **`[ServisNas]` / `[WindowsUpdate]` / `[Printer]`:** Definiše TrueNAS host (`192.168.170.254` / `truenas.servis`), servisno deljenje `ShareName=Servis`, deljenje aplikacija `ProgramiShare=Programi`, poddirektorijum zakrpa `UpdatesSubfolder=Updates\Win11`, bazu drajvera `DriversSubDir=Drivers\Harvested\Models`, `[WindowsUpdate] Mode=All` i sekciju `[Printer]` za Star TSP700II termalni štampač (`Transport=Usb|Queue|Network`, `Emulation=StarLine|EscPos`, port `9100`, širina `576` tačaka, `ShopName=Tehno kutak`). Podaci za SMB nalog ostaju van Git-a (`.example.ini` koristi `Password=CHANGE_ME`).

---

## ⚡ 2. Dvoslojni Instant Wi-Fi Auto-Connect (Zero-Wait)

Da bi klijentski računar imao aktivnu IP adresu već u prvoj sekundi ulaska na radnu površinu ([[decisions/ADR-0120-network-hardware-apps-ui-redesign]], [[decisions/ADR-0122-ultra-lean-solid-lzms-iso-architecture]], [[decisions/ADR-0136-comprehensive-review-remediation-and-signed-payload]]):
1. **Sloj 1 — `SetupComplete.ps1` (SYSTEM kontekst pre OOBE-a):**
   - Pokreće servis `wlansvc`, aktivira sve mrežne adaptere (`Enable-NetAdapter`), čita `C:\setup\Wifi-Servis.ini` i generiše WLAN XML profil uz obavezni XML escaping specijalnih karaktera (`[System.Security.SecurityElement]::Escape()`).
   - Uvozi profil (`netsh wlan add profile ... user=all`) i pokreće povezivanje u pozadini.
2. **Sloj 2 — `FirstLogon.ps1` (12s Fast-Exit) i `WIS.Launcher.exe` (`WifiHelper.cs`):**
   - `FirstLogon.ps1` proverava IPv4 konektivnost i izlazi za `< 1 s` ako je mreža već aktivna, ili čeka do 12 sekundi uz aktivno spajanje.
   - `WIS.Launcher.exe` poseduje sopstveni `WifiHelper.cs` i reaktivni mrežni osluškivač (`NetworkChange.NetworkAddressChanged` + 1.5s tajmer) koji automatski nastavlja rad čim se priključi LAN kabl ili USB mrežni adapter.

---

## 🔧 3. Univerzalni PnP Fallback Mrežnih Drajvera i Otključavanje Mrežnog Steka (`WIS.Network.psm1` & `WIS.Core.psm1`)

1. **`PreOOBE-Network.ps1` (144 linije) & `Install-NetDriverFallback` (`WIS.Network.psm1`):** Pre prvog OOBE ekrana `PreOOBE-Network.ps1` omogućava mrežne adaptere, pokreće `wlansvc` i privremeno spaja servisni Wi-Fi (XML escaping + brisanje privremenog XML profila u `finally` bloku) kako bi `SetupComplete.ps1` uhvatio Pre-OOBE Autopilot ZTD status. Kasnije u instaliranom sistemu, ukoliko mrežni adapter nema ugrađen drajver, `Install-NetDriverFallback` skenira `C:\setup\Drivers\Net` i instalira `.inf` pakete preko `pnputil.exe` (u WinPE `boot.wim` čuvaju se samo LAN drajveri — `15` fajlova, `7.57 MiB` + `000_wis_net1ix64.inf_guard`, [[decisions/ADR-0184-winpe-i219lm-driverstore-guard-and-parallel-pxe]]).
2. **Windows 11 24H2 SMB Hardening i `Error 1219` Zaštita u Fazi 4:** Otključava `LanmanWorkstation` registre (`AllowInsecureGuestAuth = 1`, `RequireSecuritySignature = 0`) i autentifikuje SMB sesije ka `\\192.168.170.254\Servis` (sa fallback-om na `\WIS`) i `\\192.168.170.254\Programi`. `Phase4-Apps.ps1` eksplicitno uvozi `WIS.Core.psm1` kako bi `Sync-ProgramiFromNas` očistio prethodne sesije i sprečio Windows SMB `Error 1219` pri prebacivanju resursa ili pod `SYSTEM` nalogom ([[decisions/ADR-0185-phase4-office-odt-bom-free-and-local-nvme-sourcepath]]).
3. **`Optimize-ServisNetworkStack`:** Postavlja `DisableBandwidthThrottling = 1`, `MaxCmds = 2048`, `EnableLargeMtu = $true` i TCP `InternetCustom` profil preko neblokirajućih poziva, omogućavajući pun gigabitni protok (**110–122 MB/s**).

---

## 🔒 4. Stroga Anti-Leak Sanitizacija Servisnih Tajni u Fazi 6 (`Clear-ServisWifiSecrets`)

U skladu sa strogim bezbednosnim pravilom projekta (`AGENTS.md` Pravilo 4 i [[decisions/ADR-0136-comprehensive-review-remediation-and-signed-payload]]), **servisni Wi-Fi profili i lozinke nikada ne smeju ostati na klijentskom računaru nakon završetka servisa**:
- U Fazi 6 (`Phase6-Finalize.ps1` / `WIS.Cleanup.psm1`), nakon slanja završnog servisnog kartona ka `ServiceHub`-u (`Send-FleetFinalReport`), funkcija `Clear-ServisWifiSecrets`:
  1. Izvršava `netsh wlan delete profile` za sve SSID mreže definisane u `Wifi-Servis.ini`, trajno ih uklanjajući iz Windows WLAN registra *(operativna napomena: na uređajima priključenim isključivo preko servisnog Wi-Fi-ja brisanje profila odmah gasi bežičnu vezu po završetku Faze 6, što je namerno bezbednosno ponašanje)*.
  2. Trajno briše `C:\setup\Wifi-Servis.ini` i sve privremene `.xml` profile sa klijentskog diska (uz zaštitu priključenih USB diskova).
  3. Raskida sve aktivne SMB konekcije ka TrueNAS serveru (`net.exe use * /delete /y`).
- **Anti-Leak Gate:** Ukoliko verifikaciona provera u Fazi 6 detektuje da je `Wifi-Servis.ini` ili servisni WLAN profil preostao na sistemu, završetak servisa i predaja računara se **blokiraju** (`handover_ready = false`, `exit 2`).

---

## 🔗 Povezane Beleške
- Pregled Arhitekture: [[architecture/overview]]
- Pajplajn Izvršavanja: [[architecture/pipeline]]
- Sanacija Bezbednosnih Rizika i Anti-Leak Ugovor: [[decisions/ADR-0136-comprehensive-review-remediation-and-signed-payload]]
- Pristup Zaključanim NAS Deljenjima WIS i Programi: [[istorija_izrade]] (WIS-TASK-146)
