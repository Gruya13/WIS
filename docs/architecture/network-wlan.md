---
title: "Mrežno Povezivanje, Servisni Wi-Fi i Anti-Leak Sanitizacija (Network & Wi-Fi Automation)"
type: architecture
created: 2026-09-14
updated: 2026-10-26
tags:
  - architecture
  - network
  - wifi
  - smb
  - security
---

# Mrežno Povezivanje, Servisni Wi-Fi i Anti-Leak Sanitizacija: WIS (v2.17.0)

Pouzdan i brz pristup servisnoj mreži (`192.168.170.0/24`) i internetu ključan je za preuzimanje potpisanog `wis-payload.zip` paketa sa `ServiceHub`-a, striming drajvera i programa sa TrueNAS servera (`192.168.170.254`) i izvršavanje Windows Update zakrpa. **WIS** obezbeđuje dvoslojno automatsko povezivanje uz strogo **Anti-Leak brisanje svih servisnih tajni** pre predaje računara klijentu.

---

## ⚙️ 1. Konfiguracioni Fajl: `Wifi-Servis.ini`

Parametri servisne bežične mreže, `ServiceHub` servera i TrueNAS SMB deljenja definisani su u `Wifi-Servis.ini`:
- **`[ServisWifi]`:** Podržava više SSID naziva odvojenih zarezom (primarni 5 GHz i rezervni 2.4 GHz) i lozinku.
- **`[ServisHub]`:** Port `8080` i UDP Discovery port `8888`.
- **`[NasCache]` / `[Programi]`:** Definiše TrueNAS host (`192.168.170.254` / `truenas.servis`), sistemsko deljenje `ShareName=WIS`, deljenje aplikacija `ProgramiShare=Programi` i servisne SMB akreditive (`Username=admin`, `Password=<SMB_PASSWORD>`), kao i `[WindowsUpdate] Mode=DriversOnly`.

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

## 🔧 3. Univerzalni PnP Fallback Mrežnih Drajvera i Otključavanje Mrežnog Steka (`WIS.Network.psm1`)

1. **`Install-NetDriverFallback`:** Ukoliko mrežni adapter nema ugrađen drajver u Windows 11 slici, modul automatski skenira lokalnu bazu `C:\setup\Drivers\Net` (Intel, Realtek, MediaTek) i instalira `.inf` pakete preko `pnputil.exe`.
2. **Windows 11 24H2 SMB Hardening i Autentifikacija:** Otključava `LanmanWorkstation` registre (`AllowInsecureGuestAuth = 1`, `RequireSecuritySignature = 0`) i autentifikuje SMB sesije ka `\\192.168.170.254\WIS` i `\\192.168.170.254\Programi`.
3. **`Optimize-ServisNetworkStack`:** Postavlja `DisableBandwidthThrottling = 1`, `MaxCmds = 2048`, `EnableLargeMtu = $true` i TCP `InternetCustom` profil preko neblokirajućih poziva, omogućavajući pun gigabitni protok (**110–122 MB/s**).

---

## 🔒 4. Stroga Anti-Leak Sanitizacija Servisnih Tajni u Fazi 6 (`Clear-ServisWifiSecrets`)

U skladu sa strogim bezbednosnim pravilom projekta (`AGENTS.md` Pravilo 4 i [[decisions/ADR-0136-comprehensive-review-remediation-and-signed-payload]]), **servisni Wi-Fi profili i lozinke nikada ne smeju ostati na klijentskom računaru nakon završetka servisa**:
- U Fazi 6 (`Phase6-Finalize.ps1` / `WIS.Cleanup.psm1`), funkcija `Clear-ServisWifiSecrets`:
  1. Izvršava `netsh wlan delete profile` za sve SSID mreže definisane u `Wifi-Servis.ini`, trajno ih uklanjajući iz Windows WLAN registra.
  2. Trajno briše `C:\setup\Wifi-Servis.ini` i sve privremene `.xml` profile sa klijentskog diska (uz zaštitu priključenih USB diskova).
  3. Raskida sve aktivne SMB konekcije ka TrueNAS serveru (`net.exe use * /delete /y`).
- **Anti-Leak Gate:** Ukoliko verifikaciona provera u Fazi 6 detektuje da je `Wifi-Servis.ini` ili servisni WLAN profil preostao na sistemu, završetak servisa i predaja računara se **blokiraju** (`handover_ready = false`, `exit 2`).

---

## 🔗 Povezane Beleške
- Pregled Arhitekture: [[architecture/overview]]
- Pajplajn Izvršavanja: [[architecture/pipeline]]
- Sanacija Bezbednosnih Rizika i Anti-Leak Ugovor: [[decisions/ADR-0136-comprehensive-review-remediation-and-signed-payload]]
- Pristup Zaključanim NAS Deljenjima WIS i Programi: [[istorija_izrade]] (WIS-TASK-146)
