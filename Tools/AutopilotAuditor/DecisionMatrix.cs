using System;

namespace WIS.AutopilotAuditor
{
    public static class DecisionMatrix
    {
        public static AuditorResult Evaluate(HardwareDetails hw, LocalForensicsDetails local, CloudZtdDetails cloud)
        {
            AuditorResult res = new AuditorResult();
            res.Timestamp = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
            res.Hardware = hw;
            res.LocalForensics = local;
            res.CloudZtd = cloud;

            bool localHas = local.HasTraces;
            bool cloudChecked = cloud.OnlineChecked;
            bool cloudHas = cloud.IsRegistered;
            bool cloudFree = cloud.ConfirmedFree;

            // Dodeljivanje tenanta
            if (!string.IsNullOrEmpty(local.TenantDomain))
                res.TenantDomain = local.TenantDomain;
            else if (!string.IsNullOrEmpty(cloud.ProfileTenantDomain))
                res.TenantDomain = cloud.ProfileTenantDomain;

            if (!string.IsNullOrEmpty(local.TenantId))
                res.TenantId = local.TenantId;
            else if (!string.IsNullOrEmpty(cloud.ProfileTenantId))
                res.TenantId = cloud.ProfileTenantId;

            // Deterministička procena stanja
            if (cloudChecked && cloudHas)
            {
                if (localHas)
                {
                    res.MatrixCode = MatrixStatus.LOCKED_TENANT;
                    res.StatusLevel = StatusSeverity.CRITICAL;
                    res.IsLocked = true;
                    res.StatusTitle = "🔴 KRITIČNO - ZAKLJUČAN U ORGANIZACIJI (TENANT LOCK)";
                    res.StatusDescription = string.Format("Uređaj se aktivno nalazi u Microsoft Autopilot bazi ({0}) i poseduje tragove u operativnom sistemu.", !string.IsNullOrEmpty(res.TenantDomain) ? res.TenantDomain : "Nepoznat domen");
                    res.RecommendedAction = "FORMATIRANJE DISKA NE REŠAVA PROBLEM! Čim se operativni sistem ponovo instalira i spoji na mrežu, OOBE će automatski zaključati računar. IT administrator navedene firme mora obrisati hardver iz svog Intune/Autopilot portala pre prodaje.";
                }
                else
                {
                    res.MatrixCode = MatrixStatus.HIDDEN_LOCK;
                    res.StatusLevel = StatusSeverity.CRITICAL;
                    res.IsLocked = true;
                    res.StatusTitle = "🔴 SKRIVENA OPASNOST - ZAKLJUČAN U OBLAKU (DISK JE OBRISAN)";
                    res.StatusDescription = "Lokalni operativni sistem je čist (nema registry tragova), ali je hardverski otisak (hash) ovog računara i dalje zaključan u Microsoft bazi!";
                    res.RecommendedAction = "Računar ne sme ići u dalju prodaju niti prenamenu dok ga prethodni vlasnik tenanta ne oslobodi iz Microsoft Intune portala.";
                }
            }
            else if (cloudChecked && cloudFree)
            {
                if (localHas)
                {
                    res.MatrixCode = MatrixStatus.RELEASED_LOCAL_TRACES;
                    res.StatusLevel = StatusSeverity.WARNING;
                    res.IsLocked = false;
                    res.StatusTitle = "🟡 BEZBEDNO - OSLOBOĐEN U OBLAKU (POSTOJE SAMO LOKALNI ZAOSTACI)";
                    res.StatusDescription = "Uređaj je u prošlosti pripadao firmi, ali je USPEŠNO DEREGISTROVAN iz Microsoft Autopilot baze u oblaku (Microsoft Event 807).";
                    res.RecommendedAction = "UREĐAJ JE BEZBEDAN ZA PRENAMENU I PRODAJU. Preporučuje se čista instalacija Windowsa (Clean install) kako bi se uklonili svi lokalni sertifikati i registarski ostaci.";
                }
                else
                {
                    res.MatrixCode = MatrixStatus.CONFIRMED_CLEAN;
                    res.StatusLevel = StatusSeverity.OK;
                    res.IsLocked = false;
                    res.StatusTitle = "🟢 100% ČISTO - UREĐAJ JE POTPUNO SLOBODAN (POTVRĐENO U OBLAKU)";
                    res.StatusDescription = "Računar nema lokalnih tragova korporativnog upravljanja, a Microsoft ZTD servis eksplicitno potvrđuje da hardver nije registrovan.";
                    res.RecommendedAction = "Računar je u potpunosti čist i bezbedan za rad bilo kog novog korisnika.";
                }
            }
            else if (cloudChecked && !cloudHas && !cloudFree)
            {
                if (localHas)
                {
                    res.MatrixCode = MatrixStatus.UNVERIFIED_LOCAL_TRACES;
                    res.StatusLevel = StatusSeverity.WARNING;
                    res.IsLocked = false;
                    res.StatusTitle = "🟡 UPOZORENJE - LOKALNI TRAGOVI (STATUS U OBLAKU NIJE POTVRĐEN)";
                    res.StatusDescription = string.Format("Pronađeni su lokalni tragovi ({0}), ali Microsoft ZTD oblak nije vratio eksplicitnu potvrdu u predviđenom roku.", !string.IsNullOrEmpty(res.TenantDomain) ? res.TenantDomain : "Nepoznat domen");
                    res.RecommendedAction = "Proverite mrežnu vezu i kliknite 'Ponovi proveru' radi dobijanja 100% potvrde iz oblaka.";
                }
                else
                {
                    res.MatrixCode = MatrixStatus.UNVERIFIED_CLEAN;
                    res.StatusLevel = StatusSeverity.INFO;
                    res.IsLocked = false;
                    res.StatusTitle = "🔵 USLOVNO ČISTO - NEMA LOKALNIH TRAGOVA (STATUS U OBLAKU NIJE POTVRĐEN)";
                    res.StatusDescription = "Lokalni sistem nema tragova, ali Windows OOBE modul nije dobio eksplicitan odgovor iz Microsoft oblaka.";
                    res.RecommendedAction = "Ako je uređaj otkupljen iz korporativnog izvora, preporučuje se pokretanje OOBE ekrana (Sysprep) pre konačne isporuke kupcu.";
                }
            }
            else
            {
                // OFFLINE
                if (localHas)
                {
                    res.MatrixCode = MatrixStatus.OFFLINE_LOCAL_TRACES;
                    res.StatusLevel = StatusSeverity.WARNING;
                    res.IsLocked = false;
                    res.StatusTitle = "🟡 OFFLINE - DETEKTOVANI LOKALNI TRAGOVI (CLOUD NIJE DOSTUPAN)";
                    res.StatusDescription = string.Format("Pronađeni su tragovi organizacije u registru ({0}). Zbog nedostatka internet veze, status u oblaku nije proveren.", !string.IsNullOrEmpty(res.TenantDomain) ? res.TenantDomain : "Nepoznat domen");
                    res.RecommendedAction = "Povežite računar na internet i ponovite proveru radi utvrđivanja da li je računar oslobođen u oblaku.";
                }
                else
                {
                    res.MatrixCode = MatrixStatus.OFFLINE_CLEAN_LOCAL;
                    res.StatusLevel = StatusSeverity.INFO;
                    res.IsLocked = false;
                    res.StatusTitle = "🔵 USLOVNO ČISTO - NEMA LOKALNIH TRAGOVA (OFFLINE REŽIM)";
                    res.StatusDescription = "Lokalni sistem nema tragova, ali je računar bio OFFLINE. Ako je disk bio formatiran, status u oblaku ostaje nepoznat.";
                    res.RecommendedAction = "Za stopostotnu sigurnost, povežite računar na mrežu i kliknite 'Ponovi proveru'.";
                }
            }

            return res;
        }
    }
}
