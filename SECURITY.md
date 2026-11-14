# Politika Bezbednosti (Security Policy)

## Podržane Verzije

| Verzija | Podržana |
| ------- | -------- |
| 3.0.x   | :white_check_mark: |
| 2.17.x–2.19.x | :warning: (nasleđeno, nadogradnja na 3.0.0) |
| < 2.17  | :x: |

## Bezbednosni Principi WIS Ekosistema

1. **Kriptografski potpisani OTA paketi (`RSA-2048` + `SHA-256`):**
   - `WIS.Launcher.exe` i `ServiceHub` verifikuju RSA-2048 PKCS#1 v1.5 digitalni potpis (`payload_meta.json`) i SHA-256 kontrolni zbir pre raspakivanja ili izvršavanja bilo kog `wis-payload.zip` paketa.
   - Javni ključ za verifikaciju ugrađen je u `Launcher/PayloadManager.cs` i `ServiceHub/servishub/config.py`, dok se privatni ključ (`wis-release-private.pem`) čuva isključivo van repozitorijuma.

2. **Zaštita Servisnih Tajni i Anti-Leak Higijena:**
   - Akreditivi za servisni Wi-Fi i SMB skladište čuvaju se isključivo lokalno u `Wifi-Servis.ini` (koji je strogo izuzet u `.gitignore`). U repozitorijumu se nalazi samo šablon `Wifi-Servis.example.ini`.
   - Tokom Faze 6 (`WIS.Cleanup.psm1`), sistem automatski briše privremene Wi-Fi profile iz Windows WLAN skladišta, zatvara aktivne SMB sesije, briše `Wifi-Servis.ini` i izvršava samouništenje radnog direktorijuma `C:\setup` pre gašenja i predaje računara klijentu.

3. **ZFS i Sistemski Safety Guardrails:**
   - Modul za pripremu USB medijuma (`ServiceHub/servishub/wis/usb.py`) poseduje višeslojnu zaštitu koja fizički blokira upis na sistemske diskove i aktivne ZFS pool-ove.

## Prijava Bezbednosnih Propusta

Ukoliko uočite bezbednosni propust u kodu, molimo vas da ga ne objavljujete kroz javni GitHub Issue, već da kontaktirate održavaoce repozitorijuma direktno radi koordinisanog otklanjanja.
