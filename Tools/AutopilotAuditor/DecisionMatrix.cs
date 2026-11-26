using System;

namespace WIS.AutopilotAuditor
{
    public static class DecisionMatrix
    {
        public static AuditorResult Evaluate(HardwareDetails hw, LocalForensicsDetails local, CloudZtdDetails cloud)
        {
            AuditorResult res = new AuditorResult();
            res.Timestamp = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
            res.Hardware = hw ?? new HardwareDetails();
            res.LocalForensics = local ?? new LocalForensicsDetails();
            res.CloudZtd = cloud ?? new CloudZtdDetails();

            bool localHas = res.LocalForensics.HasTraces;
            bool cloudChecked = res.CloudZtd.OnlineChecked;
            bool cloudHas = res.CloudZtd.IsRegistered;
            bool cloudFree = res.CloudZtd.ConfirmedFree && !cloudHas;
            bool networkReachable = res.CloudZtd.NetworkReachable;

            // Dodeljivanje tenanta i organizacionog imena uređaja
            if (!string.IsNullOrEmpty(res.CloudZtd.ProfileTenantDomain))
                res.TenantDomain = res.CloudZtd.ProfileTenantDomain;
            else if (!string.IsNullOrEmpty(res.LocalForensics.TenantDomain))
                res.TenantDomain = res.LocalForensics.TenantDomain;

            if (!string.IsNullOrEmpty(res.CloudZtd.ProfileTenantId))
                res.TenantId = res.CloudZtd.ProfileTenantId;
            else if (!string.IsNullOrEmpty(res.LocalForensics.TenantId))
                res.TenantId = res.LocalForensics.TenantId;

            if (!string.IsNullOrEmpty(res.CloudZtd.ProfileDeviceName))
                res.AssignedDeviceName = res.CloudZtd.ProfileDeviceName;
            else if (!string.IsNullOrEmpty(res.LocalForensics.AssignedDeviceName))
                res.AssignedDeviceName = res.LocalForensics.AssignedDeviceName;
            else if (!string.IsNullOrEmpty(res.LocalForensics.MatchedWaadName))
                res.AssignedDeviceName = res.LocalForensics.MatchedWaadName;

            // Deterministički binarni model presude:
            // - IMA_AUTOPILOT (CRITICAL): LOCKED_TENANT ili HIDDEN_LOCK
            // - NEMA_AUTOPILOT (OK / WARNING sa tragovima): isključivo kada je confirmed_free == true && !is_registered
            // - NIJE_PROVERENO (WARNING): sva UNVERIFIED_* i OFFLINE_* stanja (iskljucivo upozorenje, nikad INFO)
            if (cloudHas)
            {
                res.BinaryVerdict = "IMA_AUTOPILOT";
                res.StatusLevel = StatusSeverity.CRITICAL;
                res.IsLocked = true;
                res.ConfirmedFree = false;

                if (localHas)
                {
                    res.MatrixCode = MatrixStatus.LOCKED_TENANT;
                    res.StatusTitle = "🔴 IMA AUTOPILOT - ZAKLJUČAN U ORGANIZACIJI (TENANT LOCK)";
                    res.StatusDescription = string.Format(
                        "Uređaj se aktivno nalazi u Microsoft Autopilot bazi ({0}) i poseduje tragove u operativnom sistemu.",
                        !string.IsNullOrEmpty(res.TenantDomain) ? res.TenantDomain : (!string.IsNullOrEmpty(res.AssignedDeviceName) ? res.AssignedDeviceName : "Nepoznat domen"));
                    res.RecommendedAction = "FORMATIRANJE DISKA NE REŠAVA PROBLEM! Čim se operativni sistem ponovo instalira i spoji na mrežu, OOBE će automatski zaključati računar. IT administrator navedene firme mora obrisati hardver iz svog Intune/Autopilot portala pre prodaje.";
                }
                else
                {
                    res.MatrixCode = MatrixStatus.HIDDEN_LOCK;
                    res.StatusTitle = "🔴 IMA AUTOPILOT - SKRIVENA OPASNOST (ZAKLJUČAN U OBLAKU)";
                    res.StatusDescription = "Lokalni operativni sistem je čist (nema registry tragova), ali je hardverski otisak (hash) ovog računara i dalje zaključan u Microsoft bazi!";
                    res.RecommendedAction = "Računar ne sme ići u dalju prodaju niti prenamenu dok ga prethodni vlasnik tenanta ne oslobodi iz Microsoft Intune portala.";
                }
            }
            else if (cloudChecked && cloudFree)
            {
                res.BinaryVerdict = "NEMA_AUTOPILOT";
                res.IsLocked = false;
                res.ConfirmedFree = true;

                if (localHas)
                {
                    res.MatrixCode = MatrixStatus.RELEASED_LOCAL_TRACES;
                    res.StatusLevel = StatusSeverity.WARNING;
                    res.StatusTitle = "🟢 NEMA AUTOPILOT - OSLOBOĐEN U OBLAKU (STARI LOKALNI TRAGOVI)";
                    res.StatusDescription = "Uređaj je u prošlosti pripadao firmi, ali je USPEŠNO DEREGISTROVAN iz Microsoft Autopilot baze u oblaku (Microsoft Event 807 - ZtdDeviceIsNotRegistered).";
                    res.RecommendedAction = "UREĐAJ JE SLOBODAN ZA PRENAMENU I PRODAJU. Preporučuje se čista instalacija Windowsa (Clean install) kako bi se uklonili svi lokalni sertifikati i registarski ostaci.";
                }
                else
                {
                    res.MatrixCode = MatrixStatus.CONFIRMED_CLEAN;
                    res.StatusLevel = StatusSeverity.OK;
                    res.StatusTitle = "🟢 NEMA AUTOPILOT - UREĐAJ JE POTPUNO SLOBODAN (EVENT 807 POTVRDA)";
                    res.StatusDescription = "Računar nema lokalnih tragova korporativnog upravljanja, a Microsoft ZTD servis eksplicitno potvrđuje (Event 807) da hardver nije registrovan.";
                    res.RecommendedAction = "Računar je u potpunosti čist i bezbedan za rad bilo kog novog korisnika.";
                }
            }
            else if (cloudChecked || networkReachable)
            {
                res.BinaryVerdict = "NIJE_PROVERENO";
                res.StatusLevel = StatusSeverity.WARNING;
                res.IsLocked = false;
                res.ConfirmedFree = false;

                if (localHas)
                {
                    res.MatrixCode = MatrixStatus.UNVERIFIED_LOCAL_TRACES;
                    res.StatusTitle = "⚠️ NIJE PROVERENO - DETEKTOVANI LOKALNI TRAGOVI (POTREBNA MREŽA U OOBE-U)";
                    res.StatusDescription = string.Format(
                        "Pronađeni su lokalni tragovi ({0}), a Microsoft ZTD oblak nije vratio eksplicitnu Event 806/807 potvrdu.",
                        !string.IsNullOrEmpty(res.TenantDomain) ? res.TenantDomain : (!string.IsNullOrEmpty(res.AssignedDeviceName) ? res.AssignedDeviceName : "lokalni registar/sertifikati"));
                    res.RecommendedAction = "NE SMATRATI UREĐAJ ČISTIM! Obezbedite stabilnu mrežu u OOBE fazi (ili proverite Event 806/807) pre predaje kupcu.";
                }
                else
                {
                    res.MatrixCode = MatrixStatus.UNVERIFIED_CLEAN;
                    res.StatusTitle = "⚠️ NIJE PROVERENO - STATUS U OBLAKU NIJE POTVRĐEN (POTREBNA MREŽA U OOBE-U)";
                    res.StatusDescription = "Lokalni sistem nema vidljivih tragova, ali Microsoft ZTD oblak (ztd.dds.microsoft.com) nije vratio eksplicitnu Event 807 potvrdu.";
                    res.RecommendedAction = "Status u oblaku NIJE potvrđen — uređaj se ne sme proglasiti slobodnim bez realne OOBE ZTD provere sa aktivnom mrežom.";
                }
            }
            else
            {
                res.BinaryVerdict = "NIJE_PROVERENO";
                res.StatusLevel = StatusSeverity.WARNING;
                res.IsLocked = false;
                res.ConfirmedFree = false;

                if (localHas)
                {
                    res.MatrixCode = MatrixStatus.OFFLINE_LOCAL_TRACES;
                    res.StatusTitle = "⚠️ NIJE PROVERENO - OFFLINE SA LOKALNIM TRAGOVIMA (POTREBNA MREŽA U OOBE-U)";
                    res.StatusDescription = string.Format(
                        "Pronađeni su tragovi organizacije u registru ({0}). Zbog nedostatka internet veze, status u Microsoft ZTD oblaku nije proveren.",
                        !string.IsNullOrEmpty(res.TenantDomain) ? res.TenantDomain : (!string.IsNullOrEmpty(res.AssignedDeviceName) ? res.AssignedDeviceName : "lokalni registar"));
                    res.RecommendedAction = "Povežite računar na internet (LAN/Wi-Fi) i ponovite OOBE ZTD proveru pre predaje kupcu.";
                }
                else
                {
                    res.MatrixCode = MatrixStatus.OFFLINE_CLEAN_LOCAL;
                    res.StatusTitle = "⚠️ NIJE PROVERENO - OFFLINE REŽIM (POTREBNA MREŽA U OOBE-U)";
                    res.StatusDescription = "Lokalni sistem nema tragova, ali je računar bio OFFLINE. Nakon brisanja diska status u Microsoft ZTD oblaku ostaje potpuno nepoznat.";
                    res.RecommendedAction = "Povežite računar na mrežu u OOBE fazi i ponovite proveru da biste dobili eksplicitnu Event 806/807 potvrdu.";
                }
            }

            return res;
        }
    }
}
