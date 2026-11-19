# ADR-0014: Automatizovano Offline Ažuriranje Windows 11 Slike (Slipstreaming)

- **Status:** Prihvaćeno
- **Datum:** 2026-09-14
- **Autor:** GWADT Multi-Agent Sistem (`gwadt_orchestrator`, `gwadt_windows_admin`, `gwadt_powershell_expert`, `gwadt_module_architect`, `gwadt_docs_sync`)
- **Kontekst:** Smanjenje vremena čekanja i internet zagušenja tokom automatizovanog puštanja računara u rad

---

## 1. Kontekst i Problem

Nakon što čista instalacija Windows 11 operativnog sistema završi raspakivanje WIM slike i OOBE fazu, klijentska mašina pokreće `Install-ServisApps.ps1`.
U dosadašnjem toku:
1. **Dugotrajno preuzimanje preko mreže:** Modul `GWADT.Update.psm1` morao je na svakoj mašini ponaosob da preuzme kumulativno mesečno ažuriranje (LCU - Latest Cumulative Update) veličine 2–4 GB, Servicing Stack Update (SSU) i .NET Cumulative Update.
2. **Utrošak vremena i resursa:** Instalacija LCU paketa na slabijim ili starijim laptopovima trošila je 20 do 45 minuta procesorskog vremena uz 1–2 obavezna restarta sistema pre nego što uopšte započne instalacija drajvera i aplikacija.
3. **Zagušenje internet linka:** Pri istovremenoj instalaciji 3 do 5 mašina na serviserskom stolu, download gigabajtnih istovetnih paketa blokirao je lokalni internet protok.

---

## 2. Razmatrane Opcije

1. **Opcija A: WSUS / Lokalni mrežni keš na radnoj stanici:**
   - Podizanje lokalnog HTTP/WSUS keša u servisu.
   - *Nedostaci:* Zahteva namensku infrastrukturu, konfiguraciju lokalnih GPO polisa preko skripte i dalje zahteva dugotrajnu fazu raspakivanja i instalacije paketa na klijentskom računaru.
2. **Opcija B: Preuzimanje gotovog mesečnog ISO-a (UUP Dump):**
   - Periodično preuzimanje kompletne sveže ISO slike sa ugrađenim zakrpama.
   - *Prednosti:* Potpuno čist fabrički WIM bez ostataka starih komponenti.
   - *Nedostaci:* Zahteva preuzimanje celog ISO-a od 5–6 GB svaki put umesto samo razlike (delti).
3. **Opcija C: Automatizovani Offline DISM Servicing (Slipstreaming) na fleš medijumu (Izabrano rešenje):**
   - Namenska PowerShell 5.1 administratorska skripta `Skripte/Update-WimImage.ps1` i 1-klik lanser `Osvezi-WimSliku.bat`.
   - Serviser u folder `Zakrpe/` ubaci samo najnovije `.msu` pakete.
   - Skripta montira `sources\install.wim`, ugrađuje pakete, dodaje osnovne drajvere diskova (`Za disk 10+ gen`), pokreće `/ResetBase` čišćenje i snima izmene.

---

## 3. Doneta Odluka

Usvojena je **Opcija C** uz preporuku kombinovanja sa **Opcijom B** za velika polugodišnja izdanja.

### Ključne komponente rešenja:
1. **`Skripte/Update-WimImage.ps1`:**
   - Automatski detektuje indeks za **Windows 11 Pro** (standard definisan u `autounattend.xml`).
   - Ugrađuje sve `.msu` i `.cab` pakete iz foldera `Zakrpe/`.
   - Opciono ugrađuje Intel VMD / IRST drajvere iz foldera `Za disk 10+ gen`.
   - **Obavezna optimizacija:** Izvršava `dism.exe /Image:<MountDir> /Cleanup-Image /StartComponentCleanup /ResetBase` čime se brišu zastarele verzije binarnih datoteka i sprečava nekontrolisano uvećanje WIM fajla.
   - Zaštićeni `try/catch/finally` mehanizam koji sprečava zaključavanje privremenih mount direktorijuma u slučaju greške.
2. **`Osvezi-WimSliku.bat`:**
   - 1-klik prečica u korenu USB diska koja automatski zahteva administratorske privilegije (UAC RunAs).
3. **`Zakrpe/`:**
   - Namenski standardizovan folder sa pratećim `README.txt` uputstvom za servisere.

---

## 4. Posledice i Dobici

- **Ušteda vremena po računaru:** Vreme čekanja u Fazi 2 (Windows Update) smanjeno za **20 do 45 minuta**, jer računar počinje život na najnovijem bezbednosnom i servisnom nivou.
- **Smanjen broj restarta:** Sistem ne mora da prolazi kroz višestruke cikluse restarta zbog kumulativnih zakrpa.
- **Integritet skripti:** Postojeći pipeline (`autounattend.xml`, `FirstLogon.cmd`, `Install-ServisApps.ps1`) ostaje **100% kompatibilan i nepromenjen**.
- **Održavanje:** Preporučena dinamika osvežavanja slike na fleš disku je jednom u 2 do 3 meseca.

---

## 5. Povezane Beleške
- [[operations/media-maintenance]]
- [[architecture/modules]]
- [[architecture/hardware-bypass]]
- [[index]]
