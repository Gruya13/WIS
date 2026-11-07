# WIS Autopilot Forensic Auditor Standalone (v2.0)

Samostalni izvršni alat (`WIS-AutopilotAuditor.exe`) za brzu i determinističku proveru da li je računar vezan za Microsoft Intune / Microsoft Entra ID (Azure AD) kroz **Microsoft Autopilot (Zero Touch Deployment)** sistem.

Razvijen je u okviru projekta **WIS Service - Windows Installation Assistant (WIS)** kao nezavisni prenosivi alat za servisere, tehničare i kupce polovnih/prenamenskih računara.

---

## 🚀 Ključne karakteristike

1. **Nula zavisnosti (Zero-Dependency Single-File):**
   - Kompajliran za **.NET Framework 4.8**, koji je fabrički ugrađen u svaku verziju Windows 10 (od v1809) i Windows 11.
   - Ne zahteva instalaciju .NET Core / .NET 8 runtime-a.
   - Veličina izvršnog fajla je svega **~440 KB**.
2. **Bez PowerShell-a:**
   - Celokupna inspekcija (WMI/CIM, Registar, Kriptografski sertifikati, Event Log, TLS socket) se izvršava **100% nativno** u C# kodu.
   - Nema usporavanja od 3-4 sekunde pri startu PowerShell procesa.
   - Zaobilazi bezbednosne restrikcije poput `ExecutionPolicy Restricted`.
3. **Dual-Mode interfejs (GUI + CLI):**
   - **Dvoklik u Windows Exploreru:** Otvara moderan, responzivan WPF prozor sa tamnom temom, jasnim statusnim bedžom, karticama hardvera i tenanta, dugmetom za ponovno skeniranje i izvoz izveštaja.
   - **Pokretanje iz Command Prompt-a / PowerShell-a:** Preko `AttachConsole` tehnologije automatski ispisuje rezultate u postojeću konzolu ili vraća čist JSON.
   - **WinPE Otpornost:** Ako se pokrene u minimalnom WinPE okruženju gde WPF grafički podsistem nije dostupan, automatski se prebacuje u konzolni režim bez pucanja programa!
4. **5-Stupanjska Deterministička Matrica:**
   - 🔴 **`LOCKED_TENANT`:** Uređaj je aktivno vezan u Microsoft oblaku i ima lokalne tragove u registru.
   - 🔴 **`HIDDEN_LOCK`:** Disk je bio formatiran/obrisan (nema lokalnih tragova), ali je hardverski hash i dalje zaključan u Microsoft oblaku! *(Najopasnija zamka za servisere)*.
   - 🟡 **`RELEASED_LOCAL_TRACES`:** Oblak je potvrdio da je uređaj slobodan (Event 807), ali na disku postoje ostaci prethodne firme (preporučuje se clean install).
   - 🟢 **`CONFIRMED_CLEAN`:** Microsoft ZTD potvrdio deregistraciju + nema lokalnih tragova (100% bezbedan za rad i preprodaju).
   - 🔵 **`UNVERIFIED_CLEAN` / `OFFLINE`:** Uređaj je offline ili servis nije dao eksplicitan odgovor.
5. **Izvoz Izveštaja i Hardware Hash-a:**
   - 📄 **HTML servisni izveštaj:** Stilizovan dokument spreman za štampu ili prilog uz račun kupcu.
   - 📋 **JSON izveštaj:** Standardizovana struktura za automatizovane skripte.
   - 🔑 **Hardware Hash CSV:** Generiše CSV format za registraciju novog uređaja u Microsoft Intune portal.

---

## 💻 Upotreba

### 1. Grafički režim (GUI)
Jednostavno prebacite `WIS-AutopilotAuditor.exe` na USB fleš i pokrenite dvoklikom na ciljnom računaru (prihvatite UAC administratorski zahtev).

### 2. Komandna linija (CLI)
```cmd
# Pregled opcija
WIS-AutopilotAuditor.exe --help

# Direktna analiza u konzoli
WIS-AutopilotAuditor.exe --cli

# Vraćanje čistog JSON izveštaja na stdout (za skripte)
WIS-AutopilotAuditor.exe --json

# Automatski izvoz HTML izveštaja
WIS-AutopilotAuditor.exe --export-html C:\servis\izvestaj.html

# Izvoz 4K Hardware Hash-a u CSV za Intune
WIS-AutopilotAuditor.exe --export-hash C:\servis\autopilot_hash.csv
```

### 3. CLI Exit Kodovi
| Exit Code | Stanje | Značenje |
|---|---|---|
| `0` | `CONFIRMED_CLEAN` | Računar je 100% čist i potvrđeno slobodan u Microsoft oblaku |
| `1` | `LOCKED_TENANT` / `HIDDEN_LOCK` | Računar je zaključan na korporativnu firmu |
| `2` | `RELEASED_LOCAL_TRACES` / `UNVERIFIED_LOCAL_TRACES` | Pronađeni lokalni registarski/certifikacioni ostaci |
| `3` | `OFFLINE` / `UNVERIFIED` | Nema internet konekcije ili oblak nije odgovorio |

---

## 🛠️ Kompilacija

Izvorni kod se kompajlira pomoću Mono Roslyn kompajlera (`csc.exe`) na Linuxu ili `csc.exe` / `dotnet` na Windowsu:

```bash
python3 Tools/AutopilotAuditor/Build-AutopilotAuditor.py
```

Generisani artefakti:
- `Tools/AutopilotAuditor/WIS-AutopilotAuditor.exe`
- `.workspace/artifacts/tools/WIS-AutopilotAuditor.exe`
