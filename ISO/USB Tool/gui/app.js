// ==============================================================================
// GWADT USB Tool - Frontend Application Logic (app.js)
// ==============================================================================

document.addEventListener("DOMContentLoaded", () => {
  // DOM Elementi
  const devBanner = document.getElementById("deviceBanner");
  const bannerTitle = document.getElementById("bannerTitle");
  const bannerSubtitle = document.getElementById("bannerSubtitle");
  const chkDevMode = document.getElementById("chkDevMode");

  const gwadtBadge = document.getElementById("gwadtBadge");
  const pipelineStatusBadge = document.getElementById("pipelineStatusBadge");
  const devModel = document.getElementById("devModel");
  const devMount = document.getElementById("devMount");
  const devLabel = document.getElementById("devLabel");
  const devFs = document.getElementById("devFs");
  const storageText = document.getElementById("storageText");
  const storageBar = document.getElementById("storageBar");

  const heartbeatStatus = document.getElementById("heartbeatStatus");
  if (heartbeatStatus) {
    const currentPort = window.location.port || "8092";
    heartbeatStatus.textContent = `Povezano sa lokalnim serverom na portu ${currentPort}`;
  }

  // Tab 2: Ažuriranje Programa (Pre-Stage) Elementi
  const selectIsoSource = document.getElementById("selectIsoSource");
  const btnRescanIso = document.getElementById("btnRescanIso");
  const customIsoContainer = document.getElementById("customIsoContainer");
  const inputCustomIso = document.getElementById("inputCustomIso");
  const officialLinksList = document.getElementById("officialLinksList");
  const btnStartPrepare = document.getElementById("btnStartPrepare");
  const prepareProgSection = document.getElementById("prepareProgSection");
  const prepareStepText = document.getElementById("prepareStepText");
  const preparePercentText = document.getElementById("preparePercentText");
  const prepareProgressBar = document.getElementById("prepareProgressBar");
  const prepareStatusText = document.getElementById("prepareStatusText");
  const prepareElapsedText = document.getElementById("prepareElapsedText");
  const prepareLogs = document.getElementById("prepareLogs");

  // Markeri
  const markerWim = document.getElementById("m_wim");
  const markerUnattend = document.getElementById("m_unattend");
  const markerFirstlogon = document.getElementById("m_firstlogon");
  const markerScript = document.getElementById("m_script");
  const markerProgrami = document.getElementById("m_programi");
  const markerZakrpe = document.getElementById("m_zakrpe");

  // Verzije
  const versionStatusBadge = document.getElementById("versionStatusBadge");
  const verUsb = document.getElementById("verUsb");
  const verUsbSub = document.getElementById("verUsbSub");
  const verStaging = document.getElementById("verStaging");
  const verStagingSub = document.getElementById("verStagingSub");
  const verOnline = document.getElementById("verOnline");
  const verOnlineSub = document.getElementById("verOnlineSub");
  const actionRecBox = document.getElementById("actionRecBox");
  const actionRecText = document.getElementById("actionRecText");

  // Dugmad
  const btnUpgradeUsb = document.getElementById("btnUpgradeUsb");
  const btnQuickOnlineUpdate = document.getElementById("btnQuickOnlineUpdate");
  const btnSyncScripts = document.getElementById("btnSyncScripts");
  const btnCheckOnline = document.getElementById("btnCheckOnline");
  const btnRefreshStatus = document.getElementById("btnRefreshStatus");
  const btnClearDirty = document.getElementById("btnClearDirty");
  const btnEjectUsb = document.getElementById("btnEjectUsb");

  // Elementi za automatsko preuzimanje sa interneta
  const onlineBuildTargetBadge = document.getElementById("onlineBuildTargetBadge");
  const btnTriggerOnlineBuild = document.getElementById("btnTriggerOnlineBuild");
  const chkAutoSyncUsb = document.getElementById("chkAutoSyncUsb");
  const onlineProgSection = document.getElementById("onlineProgSection");
  const onlineStepText = document.getElementById("onlineStepText");
  const onlinePercentText = document.getElementById("onlinePercentText");
  const onlineProgressBar = document.getElementById("onlineProgressBar");
  const onlineSpeedText = document.getElementById("onlineSpeedText");
  const onlineElapsedText = document.getElementById("onlineElapsedText");
  const onlineLogs = document.getElementById("onlineLogs");

  let latestOnlineData = null;
  let onlineBuildPollInterval = null;
  let isOnlineBuilding = false;

  // Upgrade Panel
  const upgradeSection = document.getElementById("upgradeSection");
  const upgradeTitle = document.getElementById("upgradeTitle");
  const upgradeSpeedBadge = document.getElementById("upgradeSpeedBadge");
  const upgradeStepText = document.getElementById("upgradeStepText");
  const upgradePercentText = document.getElementById("upgradePercentText");
  const upgradeProgressBar = document.getElementById("upgradeProgressBar");
  const upgradeCopiedText = document.getElementById("upgradeCopiedText");
  const upgradeTotalText = document.getElementById("upgradeTotalText");
  const upgradeEtaText = document.getElementById("upgradeEtaText");
  const upgradeElapsedText = document.getElementById("upgradeElapsedText");
  const consoleLogs = document.getElementById("consoleLogs");
  const btnClearLogs = document.getElementById("btnClearLogs");

  // Tab 3: Ventoy ISO Generator Elementi
  const isoTargetBuildBadge = document.getElementById("isoTargetBuildBadge");
  const isoDetectedBuildText = document.getElementById("isoDetectedBuildText");
  const inputIsoOutputDir = document.getElementById("inputIsoOutputDir");
  const chkNoPrompt = document.getElementById("chkNoPrompt");
  const btnStartBuildIso = document.getElementById("btnStartBuildIso");
  const isoBuildProgSection = document.getElementById("isoBuildProgSection");
  const isoStepText = document.getElementById("isoStepText");
  const isoPercentText = document.getElementById("isoPercentText");
  const isoProgressBar = document.getElementById("isoProgressBar");
  const isoStatusText = document.getElementById("isoStatusText");
  const isoElapsedText = document.getElementById("isoElapsedText");
  const isoBuildLogs = document.getElementById("isoBuildLogs");
  const btnClearIsoLogs = document.getElementById("btnClearIsoLogs");
  const isoResultCard = document.getElementById("isoResultCard");
  const isoResultPath = document.getElementById("isoResultPath");
  const isoResultSize = document.getElementById("isoResultSize");
  const isoResultSha256 = document.getElementById("isoResultSha256");
  const isoResultMd5 = document.getElementById("isoResultMd5");
  const btnCopyIsoPath = document.getElementById("btnCopyIsoPath");
  const isoErrorBanner = document.getElementById("isoErrorBanner");
  const isoErrorText = document.getElementById("isoErrorText");

  let isoBuildPollInterval = null;
  let isIsoBuilding = false;

  // Web Audio API zvucni signal (Chime)
  function playChime() {
    try {
      const AudioCtx = window.AudioContext || window.webkitAudioContext;
      if (!AudioCtx) return;
      const ctx = new AudioCtx();
      if (ctx.state === "suspended") {
        ctx.resume();
      }
      const now = ctx.currentTime;

      // Ton 1: D5 (587.33 Hz)
      const osc1 = ctx.createOscillator();
      const gain1 = ctx.createGain();
      osc1.type = "sine";
      osc1.frequency.setValueAtTime(587.33, now);
      gain1.gain.setValueAtTime(0.15, now);
      gain1.gain.exponentialRampToValueAtTime(0.001, now + 0.3);
      osc1.connect(gain1);
      gain1.connect(ctx.destination);
      osc1.start(now);
      osc1.stop(now + 0.3);

      // Ton 2: A5 (880.00 Hz)
      const osc2 = ctx.createOscillator();
      const gain2 = ctx.createGain();
      osc2.type = "sine";
      osc2.frequency.setValueAtTime(880.00, now + 0.15);
      gain2.gain.setValueAtTime(0.2, now + 0.15);
      gain2.gain.exponentialRampToValueAtTime(0.001, now + 0.65);
      osc2.connect(gain2);
      gain2.connect(ctx.destination);
      osc2.start(now + 0.15);
      osc2.stop(now + 0.65);
    } catch (e) {
      console.warn("[app.js] Audio signal nije uspeo:", e);
    }
  }

  // Tabovi i Tabele
  const patchesTableBody = document.getElementById("patchesTableBody");
  const patchesCountBadge = document.getElementById("patchesCountBadge");
  const diagLogView = document.getElementById("diagLogView");
  const btnFetchUsbLogs = document.getElementById("btnFetchUsbLogs");

  let activeUsbDrive = null;
  let isUpgrading = false;
  let statusPollInterval = null;
  let upgradePollInterval = null;

  function getCsrfToken() {
    const meta = document.querySelector('meta[name="csrf-token"]');
    return meta ? (meta.getAttribute("content") || "") : "";
  }

  function buildPostHeaders() {
    return {
      "Content-Type": "application/json",
      "X-USB-Tool-Token": getCsrfToken()
    };
  }

  function buildUsbTargetPayload(extra = {}) {
    if (!activeUsbDrive) return Object.assign({}, extra);
    return Object.assign({
      device_id: activeUsbDrive.device_id || "",
      device: activeUsbDrive.device || "",
      mountpoint: activeUsbDrive.mountpoint || "",
      serial: activeUsbDrive.serial || "",
      uuid: activeUsbDrive.uuid || "",
      model: activeUsbDrive.model || ""
    }, extra);
  }

  // ---------------------------------------------------------------------------
  // 1. INICIJALIZACIJA I POLLING STANJA
  // ---------------------------------------------------------------------------
  function fetchStatus() {
    const devModeParam = chkDevMode.checked ? "?dev_mode=1" : "";
    fetch(`/api/status${devModeParam}`)
      .then(res => res.json())
      .then(data => {
        renderStatus(data);
      })
      .catch(err => {
        console.warn("[app.js] Greška pri dohvatu statusa:", err);
      });
  }

  function renderStatus(data) {
    activeUsbDrive = data.active_drive || null;
    const isDevRepo = Boolean(activeUsbDrive && activeUsbDrive.is_dev_repo);

    // Ažuriranje Banera
    if (activeUsbDrive) {
      devBanner.className = "banner banner-connected";
      if (isDevRepo) {
        bannerTitle.textContent = `Povezan Lokalni Repozitorijum (Razvojni Režim): ${activeUsbDrive.mountpoint}`;
        bannerSubtitle.textContent = "Dozvoljen isključivo ne-destruktivni test kopiranja skripti (Eject i Clear Dirty su blokirani).";
      } else {
        bannerTitle.textContent = `Pronađen USB Uređaj: ${activeUsbDrive.model} (${activeUsbDrive.device})`;
        bannerSubtitle.textContent = `Montiran na: ${activeUsbDrive.mountpoint || 'Nije montiran'} | Kapacitet: ${activeUsbDrive.size_human}`;
      }
      btnEjectUsb.disabled = isDevRepo;
      if (btnClearDirty) btnClearDirty.disabled = isDevRepo;
    } else {
      devBanner.className = "banner banner-waiting";
      bannerTitle.textContent = "Čekam ubacivanje USB fleš diska...";
      bannerSubtitle.textContent = "Ubacite USB fleš u bilo koji port računara radi automatske detekcije.";
      btnEjectUsb.disabled = true;
      if (btnClearDirty) btnClearDirty.disabled = true;
    }

    // Ažuriranje Kartice Uređaja
    if (activeUsbDrive) {
      devModel.textContent = activeUsbDrive.model;
      devMount.textContent = `${activeUsbDrive.device} ➔ ${activeUsbDrive.mountpoint || 'Nije montiran'}`;
      devLabel.textContent = activeUsbDrive.label || "Bez Naziva";
      devFs.textContent = activeUsbDrive.fstype || "auto";

      const usedPct = activeUsbDrive.used_percent || 0;
      storageBar.style.width = `${usedPct}%`;
      storageText.textContent = `Zauzeto ${usedPct}% (Slobodno: ${activeUsbDrive.free_human || 'N/A'})`;

      // GWADT Inspekcija
      const insp = data.inspection || {};
      if (insp.is_gwadt) {
        gwadtBadge.className = "badge badge-success";
        gwadtBadge.textContent = "GWADT Medijum Detektovan";
      } else {
        gwadtBadge.className = "badge badge-warning";
        gwadtBadge.textContent = "Nije GWADT Sistem";
      }

      // Integritet Skripti (Pajplajn)
      const pipe = (insp && insp.pipeline_integrity) ? insp.pipeline_integrity : null;
      if (pipe) {
        pipelineStatusBadge.textContent = pipe.status_badge || "Provera...";
        pipelineStatusBadge.className = `badge ${pipe.badge_class || 'badge-gray'}`;
        pipelineStatusBadge.title = pipe.status_message || "";
      } else {
        pipelineStatusBadge.className = "badge badge-gray";
        pipelineStatusBadge.textContent = "Nema Podataka";
      }

      // Markeri
      const m = insp.markers || {};
      setMarker(markerWim, m.wim);
      setMarker(markerUnattend, m.autounattend);
      setMarker(markerFirstlogon, m.firstlogon);
      setMarker(markerScript, m.deploy_script);
      setMarker(markerProgrami, m.programi);
      setMarker(markerZakrpe, m.zakrpe);
    } else {
      devModel.textContent = "-";
      devMount.textContent = "-";
      devLabel.textContent = "-";
      devFs.textContent = "-";
      storageBar.style.width = "0%";
      storageText.textContent = "Nema priključenog diska";
      gwadtBadge.className = "badge badge-gray";
      gwadtBadge.textContent = "Nema Uređaja";
      pipelineStatusBadge.className = "badge badge-gray";
      pipelineStatusBadge.textContent = "Nema Uređaja";

      [markerWim, markerUnattend, markerFirstlogon, markerScript, markerProgrami, markerZakrpe].forEach(el => {
        el.className = "marker-chip marker-off";
      });
    }

    // Ažuriranje Kartice Verzija
    const comp = data.comparison || {};
    const staging = data.staging || {};
    const online = data.online || {};

    // 1. USB Verzija
    if (data.inspection && data.inspection.wim && data.inspection.wim.exists) {
      if (data.inspection.wim.build > 0) {
        verUsb.textContent = `Build ${data.inspection.wim.build}.${data.inspection.wim.spbuild}`;
      } else {
        verUsb.textContent = data.inspection.wim.version_str || "WIM Slika Prisutna";
      }
      verUsbSub.textContent = `${data.inspection.wim.edition} (${data.inspection.wim.size_mb} MB)`;
    } else if (activeUsbDrive) {
      verUsb.textContent = "Nema WIM-a";
      verUsbSub.textContent = "Potrebna inicijalizacija";
    } else {
      verUsb.textContent = "Nema Uređaja";
      verUsbSub.textContent = "Ubacite USB fleš";
    }

    // 2. Program / Staging Verzija
    if (staging.has_staging) {
      verStaging.textContent = `Build ${staging.build}.${staging.spbuild}`;
      verStagingSub.textContent = `${staging.edition} (${staging.size_mb} MB)`;
    } else {
      verStaging.textContent = "Nema Slike";
      verStagingSub.textContent = "Potrebna priprema";
    }

    // 3. Online Verzija
    latestOnlineData = online;
    if (online.version_str) {
      verOnline.textContent = `Build ${online.build_num}.${online.sp_num}`;
      verOnlineSub.textContent = online.checked_at || "Microsoft Catalog";
      if (onlineBuildTargetBadge) {
        onlineBuildTargetBadge.textContent = `Najnovije: Build ${online.build_num}.${online.sp_num}`;
        onlineBuildTargetBadge.className = "badge badge-accent";
      }
    } else {
      verOnline.textContent = "Offline";
      verOnlineSub.textContent = "Nema pristupa internetu";
      if (onlineBuildTargetBadge) {
        onlineBuildTargetBadge.textContent = "Offline";
        onlineBuildTargetBadge.className = "badge badge-gray";
      }
    }

    // Bedž i Preporuka
    versionStatusBadge.textContent = comp.status_badge || "Stanje";
    if (comp.status_code === "UP_TO_DATE") {
      versionStatusBadge.className = "badge badge-success";
    } else if (comp.status_code === "USB_UPGRADE_AVAILABLE") {
      versionStatusBadge.className = "badge badge-warning";
    } else if (comp.status_code === "PROGRAM_UPDATE_AVAILABLE") {
      versionStatusBadge.className = "badge badge-info";
    } else {
      versionStatusBadge.className = "badge badge-gray";
    }

    actionRecText.textContent = comp.action_text || "Sistem je spreman.";

    // Ažuriranje ciljanog OS Build-a za Ventoy ISO tab
    let detectedIsoBuild = "10.0.26200.8037";
    if (staging && staging.has_staging && staging.version_str) {
      detectedIsoBuild = staging.version_str;
    } else if (staging && staging.build) {
      detectedIsoBuild = `10.0.${staging.build}.${staging.spbuild || 0}`;
    } else if (online && online.version_str) {
      detectedIsoBuild = online.version_str;
    }
    if (isoDetectedBuildText) {
      isoDetectedBuildText.textContent = `${detectedIsoBuild} (v2.5.0)`;
    }
    if (isoTargetBuildBadge) {
      isoTargetBuildBadge.textContent = `${detectedIsoBuild} / v2.5.0`;
    }

    // Stanje Brzog Online Dugmeta
    if (!isOnlineBuilding && btnQuickOnlineUpdate) {
      if (comp.program_update_available || (online.build_num && (!staging.build || online.build_num > staging.build))) {
        btnQuickOnlineUpdate.style.display = "inline-flex";
        btnQuickOnlineUpdate.innerHTML = `<span class="icon">&#127760;</span> Preuzmi Najnoviji Build ${online.build_num || ''} sa Interneta`;
      } else {
        btnQuickOnlineUpdate.style.display = "none";
      }
    }

    // Stanje Dugmadi
    if (!isUpgrading) {
      if (activeUsbDrive && activeUsbDrive.mountpoint) {
        btnSyncScripts.disabled = false;
        
        // Provera usaglašenosti skripti (Pajplajna)
        const pipe = (data.inspection && data.inspection.pipeline_integrity) ? data.inspection.pipeline_integrity : null;
        if (pipe && !pipe.is_synced) {
          btnSyncScripts.className = "btn btn-warning pulse-btn";
          btnSyncScripts.innerHTML = `<span class="icon">&#9888;</span> Ažuriraj Skripte na USB-u (Pajplajn Zastareo)`;
          btnSyncScripts.title = pipe.status_message || "Skripte na USB-u nisu usklađene sa programom. Kliknite za 1s osvežavanje!";
        } else {
          btnSyncScripts.className = "btn btn-outline";
          btnSyncScripts.innerHTML = `<span class="icon">&#9889;</span> Osveži Samo Skripte (1s)`;
          btnSyncScripts.title = "Sinhronizuje samo skripte i XML konfiguraciju za samo 1s";
        }

        if (comp.usb_upgrade_available) {
          btnUpgradeUsb.disabled = false;
          btnUpgradeUsb.className = "btn btn-primary btn-large";
          btnUpgradeUsb.innerHTML = `<span class="icon">&#128640;</span> Nadogradi USB na Build ${staging.build}.${staging.spbuild}`;
        } else if (data.inspection && data.inspection.wim && data.inspection.wim.exists) {
          btnUpgradeUsb.disabled = false;
          btnUpgradeUsb.className = "btn btn-outline btn-large";
          btnUpgradeUsb.innerHTML = `<span class="icon">&#8635;</span> Pregazi / Osveži WIM na USB-u`;
        } else {
          btnUpgradeUsb.disabled = false;
          btnUpgradeUsb.className = "btn btn-primary btn-large";
          btnUpgradeUsb.innerHTML = `<span class="icon">&#128640;</span> Instaliraj GWADT Sistem na USB`;
        }
      } else {
        btnUpgradeUsb.disabled = true;
        btnSyncScripts.disabled = true;
        btnUpgradeUsb.innerHTML = `<span class="icon">&#128640;</span> Nadogradi USB na Novu Verziju`;
      }
    }
  }

  function setMarker(element, isPresent) {
    if (!element) return;
    if (isPresent) {
      element.className = "marker-chip marker-on";
    } else {
      element.className = "marker-chip marker-off";
    }
  }

  // ---------------------------------------------------------------------------
  // 2. RAD SA NADOGRADNJOM (UPGRADE)
  // ---------------------------------------------------------------------------
  btnUpgradeUsb.addEventListener("click", () => {
    if (!activeUsbDrive || !activeUsbDrive.mountpoint) {
      alert("Nije izabran validan USB medijum!");
      return;
    }

    const confirmMsg = `Da li ste sigurni da želite da nadogradite USB fleš (${activeUsbDrive.model})?\n\n` +
      `Lokacija: ${activeUsbDrive.mountpoint}\n` +
      `Ova operacija će osvežiti Windows 11 WIM sliku i sinhronizovati sve deploy skripte.`;

    if (!confirm(confirmMsg)) return;

    isUpgrading = true;
    btnUpgradeUsb.disabled = true;
    btnSyncScripts.disabled = true;
    upgradeSection.classList.remove("hidden");
    upgradeTitle.textContent = "Nadogradnja USB Fleša u Toku...";
    consoleLogs.textContent = "";

    fetch("/api/upgrade-usb", {
      method: "POST",
      headers: buildPostHeaders(),
      body: JSON.stringify(buildUsbTargetPayload({ force: true }))
    })
      .then(res => res.json())
      .then(resData => {
        if (!resData.success) {
          alert("Greška: " + resData.message);
          isUpgrading = false;
          upgradeSection.classList.add("hidden");
          fetchStatus();
        } else {
          startUpgradePolling();
        }
      })
      .catch(err => {
        alert("Neuspešno slanje zahteva za nadogradnju: " + err);
        isUpgrading = false;
        upgradeSection.classList.add("hidden");
      });
  });

  function startUpgradePolling() {
    if (upgradePollInterval) clearInterval(upgradePollInterval);

    upgradePollInterval = setInterval(() => {
      fetch("/api/upgrade-progress")
        .then(res => res.json())
        .then(prog => {
          renderUpgradeProgress(prog);
          if (prog.status === "success" || prog.status === "error") {
            clearInterval(upgradePollInterval);
            isUpgrading = false;
            fetchStatus();
            if (prog.status === "success") {
              upgradeTitle.textContent = "Nadogradnja USB Fleša Uspešno Završena!";
              alert(`ČESTITAMO: ${prog.message || 'USB fleš je 100% nadograđen i spreman za terenski rad!'}`);
            } else {
              upgradeTitle.textContent = "Greška Tokom Nadogradnje USB Fleša!";
            }
          }
        })
        .catch(err => {
          console.warn("[app.js] Greška u upgrade poll-u:", err);
        });
    }, 600);
  }

  function renderUpgradeProgress(prog) {
    const pct = prog.percent || 0.0;
    upgradeProgressBar.style.width = `${pct}%`;
    upgradePercentText.textContent = `${pct.toFixed(1)}%`;
    upgradeStepText.textContent = prog.current_step || "Obrada...";
    upgradeSpeedBadge.textContent = `${(prog.speed_mb_s || 0).toFixed(1)} MB/s`;

    upgradeCopiedText.textContent = `${prog.copied_mb || 0} MB`;
    upgradeTotalText.textContent = `${prog.total_mb || 0} MB`;

    const etaSec = prog.eta_seconds || 0;
    upgradeEtaText.textContent = etaSec > 0 ? `${Math.floor(etaSec / 60)}m ${etaSec % 60}s` : "Pri kraju";
    upgradeElapsedText.textContent = `${prog.elapsed_seconds || 0}s`;

    // Logovi
    if (prog.logs && prog.logs.length > 0) {
      consoleLogs.textContent = prog.logs.join("\n");
      consoleLogs.scrollTop = consoleLogs.scrollHeight;
    }
  }

  btnClearLogs.addEventListener("click", () => {
    consoleLogs.textContent = "";
  });

  // ---------------------------------------------------------------------------
  // 3. BRZA SINHRONIZACIJA SKRIPTI
  // ---------------------------------------------------------------------------
  btnSyncScripts.addEventListener("click", () => {
    if (!activeUsbDrive || !activeUsbDrive.mountpoint) return;

    btnSyncScripts.disabled = true;
    btnSyncScripts.innerHTML = `<span class="icon spin">&#8987;</span> Sinhronizujem...`;

    fetch("/api/sync-scripts", {
      method: "POST",
      headers: buildPostHeaders(),
      body: JSON.stringify(buildUsbTargetPayload())
    })
      .then(res => res.json())
      .then(resData => {
        alert(resData.message || "Skripte su uspešno osvežene.");
        btnSyncScripts.disabled = false;
        btnSyncScripts.innerHTML = `<span class="icon">&#9889;</span> Osveži Samo Skripte (1s)`;
        fetchStatus();
      })
      .catch(err => {
        alert("Greška pri sinhronizaciji skripti: " + err);
        btnSyncScripts.disabled = false;
        btnSyncScripts.innerHTML = `<span class="icon">&#9889;</span> Osveži Samo Skripte (1s)`;
      });
  });

  // ---------------------------------------------------------------------------
  // 4. ONLINE PROVERA & DEMONTIRANJE (EJECT)
  // ---------------------------------------------------------------------------
  btnCheckOnline.addEventListener("click", () => {
    btnCheckOnline.disabled = true;
    btnCheckOnline.innerHTML = `<span class="icon spin">&#8987;</span> Proveravam...`;

    fetch("/api/check-online", {
      method: "POST",
      headers: buildPostHeaders()
    })
      .then(res => res.json())
      .then(resData => {
        btnCheckOnline.disabled = false;
        btnCheckOnline.innerHTML = `<span class="icon">&#127760;</span> Proveri Online`;
        fetchStatus();
      })
      .catch(err => {
        btnCheckOnline.disabled = false;
        btnCheckOnline.innerHTML = `<span class="icon">&#127760;</span> Proveri Online`;
        alert("Greška pri povezivanju sa Microsoft servisom: " + err);
      });
  });

  btnRefreshStatus.addEventListener("click", () => {
    fetchStatus();
    fetchPatches();
  });

  chkDevMode.addEventListener("change", () => {
    fetchStatus();
  });

  btnEjectUsb.addEventListener("click", () => {
    if (!activeUsbDrive || activeUsbDrive.is_dev_repo) return;
    if (!confirm(`Da li ste sigurni da želite da bezbedno demontirate ${activeUsbDrive.model}?`)) return;

    fetch("/api/eject-usb", {
      method: "POST",
      headers: buildPostHeaders(),
      body: JSON.stringify(buildUsbTargetPayload())
    })
      .then(res => res.json())
      .then(resData => {
        alert(resData.message || "Uređaj je demontiran.");
        fetchStatus();
      })
      .catch(err => alert("Greška pri demontiranju: " + err));
  });

  if (btnClearDirty) {
    btnClearDirty.addEventListener("click", () => {
      if (!activeUsbDrive || activeUsbDrive.is_dev_repo) {
        alert("Nije izabran fizički USB uređaj za popravku fajlsistema!");
        return;
      }
      const devName = activeUsbDrive.device || activeUsbDrive.mountpoint;
      const confirmMsg = `Da li želite da popravite fajl sistem i očistite dirty bit za ${devName}?`;
      if (!confirm(confirmMsg)) return;

      btnClearDirty.disabled = true;
      btnClearDirty.textContent = "⏳ Popravljam...";

      fetch("/api/clear-dirty", {
        method: "POST",
        headers: buildPostHeaders(),
        body: JSON.stringify(buildUsbTargetPayload())
      })
        .then(res => res.json())
        .then(resData => {
          alert(resData.message || (resData.success ? "Dirty bit uspešno očišćen!" : "Greška pri čišćenju."));
          fetchStatus();
        })
        .catch(err => alert("Greška: " + err))
        .finally(() => {
          btnClearDirty.disabled = Boolean(!activeUsbDrive || activeUsbDrive.is_dev_repo);
          btnClearDirty.innerHTML = '<span class="icon">&#129529;</span> Očisti Dirty Bit';
        });
    });
  }

  // ---------------------------------------------------------------------------
  // 5. LISTA ZAKRPA & TABOVI
  // ---------------------------------------------------------------------------
  function fetchPatches() {
    fetch("/api/patches")
      .then(res => res.json())
      .then(data => {
        renderPatches(data.patches || []);
      })
      .catch(err => console.warn("Neuspešno učitavanje zakrpa:", err));
  }

  function renderPatches(patches) {
    patchesCountBadge.textContent = `${patches.length} paketa`;
    if (!patches || patches.length === 0) {
      patchesTableBody.innerHTML = `<tr><td colspan="4" class="text-center text-muted">Nema preuzetih .msu/.cab zakrpa u folderu Zakrpe/</td></tr>`;
      return;
    }

    let rowsHtml = "";
    patches.forEach(p => {
      rowsHtml += `
        <tr>
          <td><strong style="color: var(--accent-cyan);">${p.kb}</strong></td>
          <td><code>${p.filename}</code></td>
          <td><span class="badge badge-gray">${p.type}</span></td>
          <td>${p.size_mb} MB</td>
        </tr>
      `;
    });
    patchesTableBody.innerHTML = rowsHtml;
  }

  // Učitavanje loga sa USB-a
  btnFetchUsbLogs.addEventListener("click", () => {
    if (!activeUsbDrive || !activeUsbDrive.mountpoint) {
      alert("Nema priključenog USB diska!");
      return;
    }

    diagLogView.textContent = "Učitavam servisni log...";
    fetch(`/api/usb-log?mountpoint=${encodeURIComponent(activeUsbDrive.mountpoint)}`)
      .then(res => res.json())
      .then(data => {
        if (data.log) {
          diagLogView.textContent = data.log;
        } else {
          diagLogView.textContent = data.message || "Na ovom USB-u još uvek nema kreiranog servis_deploy.log fajla.";
        }
      })
      .catch(err => {
        diagLogView.textContent = "Greška pri čitanju loga: " + err;
      });
  });

  // Tab menjač
  document.querySelectorAll(".tab-btn").forEach(btn => {
    btn.addEventListener("click", () => {
      document.querySelectorAll(".tab-btn").forEach(b => b.classList.remove("active"));
      document.querySelectorAll(".tab-pane").forEach(p => p.classList.remove("active"));

      btn.classList.add("active");
      const targetPane = document.getElementById(btn.dataset.tab);
      if (targetPane) targetPane.classList.add("active");
    });
  });

  // ---------------------------------------------------------------------------
  // 6. ISO IZVORI I AŽURIRANJE PROGRAMA (PRE-STAGE)
  // ---------------------------------------------------------------------------
  let preparePollInterval = null;
  let isPreparing = false;

  function fetchIsoSources(silent = false) {
    if (!silent) {
      selectIsoSource.innerHTML = '<option value="">Pretražujem lokalne diskove...</option>';
    }
    fetch("/api/iso-sources")
      .then(res => res.json())
      .then(data => {
        renderIsoSources(data);
      })
      .catch(err => {
        console.warn("Greška pri dohvatu ISO izvora:", err);
        selectIsoSource.innerHTML = '<option value="custom">✏️ Ručni unos lokalne putanje ili URL-a...</option>';
      });
  }

  function renderIsoSources(data) {
    selectIsoSource.innerHTML = "";
    const locals = data.local_sources || [];
    
    if (locals.length > 0) {
      locals.forEach(src => {
        const opt = document.createElement("option");
        opt.value = src.path;
        opt.textContent = `[Pronađeno na disku] ${src.name} (${src.size_mb} MB) - ${src.dir}`;
        selectIsoSource.appendChild(opt);
      });
    }

    const customOpt = document.createElement("option");
    customOpt.value = "custom";
    customOpt.textContent = "✏️ Ručni unos putanje do fajla na disku ili web URL-a...";
    selectIsoSource.appendChild(customOpt);

    if (locals.length === 0) {
      customOpt.selected = true;
      customIsoContainer.style.display = "block";
    } else {
      customIsoContainer.style.display = "none";
    }

    if (data.official_sources && data.official_sources.length > 0 && officialLinksList) {
      let linksHtml = "";
      data.official_sources.forEach(s => {
        linksHtml += `<li><strong>${s.title}:</strong> <a href="${s.url}" target="_blank" rel="noopener">${s.url}</a> (${s.note})</li>`;
      });
      officialLinksList.innerHTML = linksHtml;
    }
  }

  selectIsoSource.addEventListener("change", () => {
    if (selectIsoSource.value === "custom") {
      customIsoContainer.style.display = "block";
      inputCustomIso.focus();
    } else {
      customIsoContainer.style.display = "none";
    }
  });

  btnRescanIso.addEventListener("click", () => {
    btnRescanIso.disabled = true;
    btnRescanIso.innerHTML = `<span class="icon spin">&#8987;</span> Pretražujem...`;
    fetchIsoSources(false);
    setTimeout(() => {
      btnRescanIso.disabled = false;
      btnRescanIso.innerHTML = `<span class="icon">&#8635;</span> Osveži Listu`;
    }, 1000);
  });

  btnStartPrepare.addEventListener("click", () => {
    let chosenSource = selectIsoSource.value;
    if (chosenSource === "custom") {
      chosenSource = inputCustomIso.value.trim();
    }

    if (!chosenSource) {
      alert("Molimo izaberite ili unesite putanju do ISO/ESD fajla ili URL!");
      return;
    }

    if (!confirm(`Pokrenuti pripremu nove WIM slike u programu?\n\nIzvor: ${chosenSource}\n\nOva radnja će pripremiti WIM sliku u lokalno skladište programa bez uticaja na USB.`)) {
      return;
    }

    isPreparing = true;
    btnStartPrepare.disabled = true;
    btnStartPrepare.innerHTML = `<span class="icon spin">&#8987;</span> Priprema u toku...`;
    prepareProgSection.classList.remove("hidden");
    prepareLogs.textContent = "";

    fetch("/api/prepare-program", {
      method: "POST",
      headers: buildPostHeaders(),
      body: JSON.stringify({ source: chosenSource })
    })
      .then(res => res.json())
      .then(data => {
        if (!data.success) {
          alert("Greška pri pokretanju pripreme: " + data.message);
          isPreparing = false;
          btnStartPrepare.disabled = false;
          btnStartPrepare.innerHTML = `<span class="icon">&#9889;</span> Pripremi Sliku u Programu (Pre-Stage)`;
        } else {
          startPreparePolling();
        }
      })
      .catch(err => {
        alert("Greška pri slanju zahteva: " + err);
        isPreparing = false;
        btnStartPrepare.disabled = false;
        btnStartPrepare.innerHTML = `<span class="icon">&#9889;</span> Pripremi Sliku u Programu (Pre-Stage)`;
      });
  });

  function startPreparePolling() {
    if (preparePollInterval) clearInterval(preparePollInterval);

    preparePollInterval = setInterval(() => {
      fetch("/api/prepare-progress")
        .then(res => res.json())
        .then(prog => {
          renderPrepareProgress(prog);
          if (prog.status === "success" || prog.status === "error") {
            clearInterval(preparePollInterval);
            isPreparing = false;
            btnStartPrepare.disabled = false;
            btnStartPrepare.innerHTML = `<span class="icon">&#9889;</span> Pripremi Sliku u Programu (Pre-Stage)`;
            fetchStatus();
            if (prog.status === "success") {
              alert("USPEH: Nova Windows 11 WIM slika je uspešno pripremljena u lokalnom kešu računara!\n\nDa biste je upisali na USB fleš, kliknite na dugme 'Nadogradi USB na Novu Verziju'.");
            } else {
              alert("GREŠKA: Priprema slike nije uspela. Proverite logove.");
            }
          }
        })
        .catch(err => {
          console.warn("Greška u prepare polling-u:", err);
        });
    }, 600);
  }

  function renderPrepareProgress(prog) {
    const pct = prog.percent || 0.0;
    prepareProgressBar.style.width = `${pct}%`;
    preparePercentText.textContent = `${pct.toFixed(1)}%`;
    prepareStepText.textContent = prog.current_step || "Obrada...";
    prepareStatusText.textContent = prog.status || "u toku";
    prepareElapsedText.textContent = `${prog.elapsed_seconds || 0}s`;

    if (prog.logs && prog.logs.length > 0) {
      prepareLogs.textContent = prog.logs.join("\n");
      prepareLogs.scrollTop = prepareLogs.scrollHeight;
    }
  }

  // ---------------------------------------------------------------------------
  // 7. AUTOMATSKO PREUZIMANJE I SKLAPANJE SA INTERNETA (UUP & MICROSOFT CDN)
  // ---------------------------------------------------------------------------
  function startOnlineBuild() {
    const targetVer = (latestOnlineData && latestOnlineData.version_str) ? latestOnlineData.version_str : "najnoviju verziju";
    const targetUuid = (latestOnlineData && latestOnlineData.uuid) ? latestOnlineData.uuid : null;
    const shouldSyncUsb = chkAutoSyncUsb ? chkAutoSyncUsb.checked : true;
    const usbTargetMount = (activeUsbDrive && activeUsbDrive.mountpoint && !activeUsbDrive.is_dev_repo) ? activeUsbDrive.mountpoint : null;

    let confirmMsg = `Pokrenuti automatsko preuzimanje i sklapanje sa Microsoft servera?\n\n` +
      `Ciljani Build: ${targetVer}\n\n` +
      `Sistem će samostalno preuzeti zvanične pakete preko aria2c i složiti novu Windows 11 Pro WIM sliku na vašem računaru.`;

    if (shouldSyncUsb && usbTargetMount) {
      confirmMsg += `\n\nNakon sklapanja, nova slika će AUTOMATSKI biti preneta na ubačeni USB fleš (${usbTargetMount}).`;
    }

    if (!confirm(confirmMsg)) return;

    // Otvaramo tab za ažuriranje programa ako već nije otvoren
    const tabProgramBtn = document.querySelector('.tab-btn[data-tab="tabProgramUpdate"]');
    if (tabProgramBtn) tabProgramBtn.click();

    isOnlineBuilding = true;
    if (btnTriggerOnlineBuild) {
      btnTriggerOnlineBuild.disabled = true;
      btnTriggerOnlineBuild.innerHTML = `<span class="icon spin">&#8987;</span> Preuzimanje sa Interneta u toku...`;
    }
    if (btnQuickOnlineUpdate) {
      btnQuickOnlineUpdate.disabled = true;
      btnQuickOnlineUpdate.innerHTML = `<span class="icon spin">&#8987;</span> Preuzimam sa Interneta...`;
    }

    onlineProgSection.classList.remove("hidden");
    onlineLogs.textContent = "";

    const onlinePayload = (shouldSyncUsb && usbTargetMount)
      ? buildUsbTargetPayload({
          uuid: targetUuid,
          auto_sync_usb: true,
          edition: "professional"
        })
      : {
          uuid: targetUuid,
          auto_sync_usb: false,
          mountpoint: null,
          edition: "professional"
        };

    fetch("/api/download-online-build", {
      method: "POST",
      headers: buildPostHeaders(),
      body: JSON.stringify(onlinePayload)
    })
      .then(res => res.json())
      .then(data => {
        if (!data.success) {
          alert("Greška pri pokretanju online preuzimanja: " + data.message);
          resetOnlineBuildUI();
        } else {
          startOnlineBuildPolling();
        }
      })
      .catch(err => {
        alert("Neuspešno slanje zahteva ka serveru: " + err);
        resetOnlineBuildUI();
      });
  }

  function resetOnlineBuildUI() {
    isOnlineBuilding = false;
    if (btnTriggerOnlineBuild) {
      btnTriggerOnlineBuild.disabled = false;
      btnTriggerOnlineBuild.innerHTML = `<span class="icon">&#128640;</span> Preuzmi i Sklopi Najnoviji Build sa Interneta`;
    }
    if (btnQuickOnlineUpdate) {
      btnQuickOnlineUpdate.disabled = false;
      const bNum = (latestOnlineData && latestOnlineData.build_num) ? latestOnlineData.build_num : '';
      btnQuickOnlineUpdate.innerHTML = `<span class="icon">&#127760;</span> Preuzmi Najnoviji Build ${bNum} sa Interneta`;
    }
  }

  function startOnlineBuildPolling() {
    if (onlineBuildPollInterval) clearInterval(onlineBuildPollInterval);

    onlineBuildPollInterval = setInterval(() => {
      fetch("/api/online-build-progress")
        .then(res => res.json())
        .then(prog => {
          renderOnlineBuildProgress(prog);
          if (prog.status === "success" || prog.status === "error") {
            clearInterval(onlineBuildPollInterval);
            resetOnlineBuildUI();
            fetchStatus();

            if (prog.status === "success") {
              let succMsg = `ČESTITAMO! ${prog.message || 'Nova slika je uspešno pripremljena u programu!'}`;
              if (prog.auto_synced_usb) {
                succMsg += `\n\nUSB fleš je takođe 100% automatski nadograđen na novu verziju!`;
              } else {
                succMsg += `\n\nVAŽNA NAPOMENA: Nova verzija je uspešno sačuvana na računaru.\nDa biste je preneli na USB fleš, kliknite na dugme 'Nadogradi USB na Novu Verziju'.`;
              }
              alert(succMsg);
            } else {
              alert("GREŠKA tokom online preuzimanja i sklapanja: " + (prog.message || "Proverite logove u konzoli."));
            }
          }
        })
        .catch(err => {
          console.warn("Greška u online build poll-u:", err);
        });
    }, 700);
  }

  function renderOnlineBuildProgress(prog) {
    const pct = prog.percent || 0.0;
    onlineProgressBar.style.width = `${pct}%`;
    onlinePercentText.textContent = `${pct.toFixed(1)}%`;
    onlineStepText.textContent = prog.current_step || "Obrada...";
    onlineSpeedText.textContent = `${(prog.speed_mb_s || 0).toFixed(1)} MB/s`;
    onlineElapsedText.textContent = `${prog.elapsed_seconds || 0}s`;

    if (prog.logs && prog.logs.length > 0) {
      onlineLogs.textContent = prog.logs.join("\n");
      onlineLogs.scrollTop = onlineLogs.scrollHeight;
    }
  }

  if (btnTriggerOnlineBuild) {
    btnTriggerOnlineBuild.addEventListener("click", startOnlineBuild);
  }
  if (btnQuickOnlineUpdate) {
    btnQuickOnlineUpdate.addEventListener("click", startOnlineBuild);
  }

  // ---------------------------------------------------------------------------
  // 8. VENTOY ISO GENERATOR
  // ---------------------------------------------------------------------------
  function startBuildIso() {
    if (isIsoBuilding) return;

    const outputDir = inputIsoOutputDir ? inputIsoOutputDir.value.trim() : "";
    const noPrompt = chkNoPrompt ? chkNoPrompt.checked : true;

    isIsoBuilding = true;
    if (btnStartBuildIso) {
      btnStartBuildIso.disabled = true;
      btnStartBuildIso.innerHTML = `<span class="icon spin">&#8987;</span> Generisanje ISO Slike u Toku...`;
    }

    if (isoBuildProgSection) isoBuildProgSection.classList.remove("hidden");
    if (isoResultCard) isoResultCard.classList.add("hidden");
    if (isoErrorBanner) isoErrorBanner.classList.add("hidden");

    if (isoProgressBar) isoProgressBar.style.width = "0%";
    if (isoPercentText) isoPercentText.textContent = "0.0%";
    if (isoStepText) isoStepText.textContent = "Inicijalizacija ISO generatora...";
    if (isoStatusText) isoStatusText.textContent = "Priprema...";
    if (isoElapsedText) isoElapsedText.textContent = "0s";
    if (isoBuildLogs) isoBuildLogs.textContent = "";

    fetch("/api/build-iso", {
      method: "POST",
      headers: buildPostHeaders(),
      body: JSON.stringify({
        output_dir: outputDir,
        no_prompt: noPrompt
      })
    })
      .then(res => res.json())
      .then(data => {
        if (!data.success) {
          showIsoError(data.message || "Greška pri pokretanju izgradnje ISO slike.");
          resetIsoBuildUI();
        } else {
          startIsoBuildPolling();
        }
      })
      .catch(err => {
        showIsoError("Greška u mrežnoj komunikaciji ka serveru: " + err);
        resetIsoBuildUI();
      });
  }

  function resetIsoBuildUI() {
    isIsoBuilding = false;
    if (btnStartBuildIso) {
      btnStartBuildIso.disabled = false;
      btnStartBuildIso.innerHTML = `<span class="icon">&#128230;</span> Generiši Ventoy ISO Sliku`;
    }
  }

  function showIsoError(msg) {
    if (isoErrorBanner) {
      if (isoErrorText) isoErrorText.textContent = msg;
      isoErrorBanner.classList.remove("hidden");
    }
  }

  function startIsoBuildPolling() {
    if (isoBuildPollInterval) clearInterval(isoBuildPollInterval);

    isoBuildPollInterval = setInterval(() => {
      fetch("/api/build-iso/status")
        .then(res => res.json())
        .then(prog => {
          renderIsoBuildProgress(prog);

          if (prog.status === "done" || prog.status === "error") {
            clearInterval(isoBuildPollInterval);
            resetIsoBuildUI();

            if (prog.status === "done") {
              playChime();
              renderIsoResult(prog);
            } else if (prog.status === "error") {
              showIsoError(prog.error || prog.step || "Došlo je do greške tokom generisanja ISO slike.");
            }
          }
        })
        .catch(err => {
          console.warn("[app.js] Greška pri dohvatanju ISO build statusa:", err);
        });
    }, 1000);
  }

  function renderIsoBuildProgress(prog) {
    const pct = typeof prog.progress === "number" ? prog.progress : 0;
    if (isoProgressBar) isoProgressBar.style.width = `${pct}%`;
    if (isoPercentText) isoPercentText.textContent = `${pct.toFixed(1)}%`;
    if (isoStepText) isoStepText.textContent = prog.step || "Obrada...";
    if (isoStatusText) isoStatusText.textContent = prog.status || "building";
    if (isoElapsedText) isoElapsedText.textContent = `${prog.duration || 0}s`;

    if (prog.logs && Array.isArray(prog.logs) && prog.logs.length > 0) {
      if (isoBuildLogs) {
        isoBuildLogs.textContent = prog.logs.join("\n");
        isoBuildLogs.scrollTop = isoBuildLogs.scrollHeight;
      }
    }
  }

  function renderIsoResult(prog) {
    if (!isoResultCard) return;

    if (isoResultPath) isoResultPath.textContent = prog.output_file || "-";
    if (isoResultSize) {
      const gb = typeof prog.iso_size_gb === "number" ? prog.iso_size_gb.toFixed(2) : "0.00";
      const mb = typeof prog.iso_size_mb === "number" ? prog.iso_size_mb.toFixed(0) : "0";
      isoResultSize.textContent = `${gb} GB (${mb} MB)`;
    }
    if (isoResultSha256) isoResultSha256.textContent = prog.sha256 || "N/A";
    if (isoResultMd5) isoResultMd5.textContent = prog.md5 || "N/A";

    isoResultCard.classList.remove("hidden");
  }

  // Povezivanje događaja za Ventoy ISO
  if (btnStartBuildIso) {
    btnStartBuildIso.addEventListener("click", startBuildIso);
  }

  if (btnClearIsoLogs) {
    btnClearIsoLogs.addEventListener("click", () => {
      if (isoBuildLogs) isoBuildLogs.textContent = "";
    });
  }

  if (btnCopyIsoPath) {
    btnCopyIsoPath.addEventListener("click", () => {
      const textToCopy = isoResultPath ? isoResultPath.textContent : "";
      if (!textToCopy || textToCopy === "-") return;

      if (navigator.clipboard && window.isSecureContext) {
        navigator.clipboard.writeText(textToCopy).then(() => {
          showCopySuccess();
        }).catch(() => {
          fallbackCopyText(textToCopy);
        });
      } else {
        fallbackCopyText(textToCopy);
      }
    });
  }

  function showCopySuccess() {
    if (!btnCopyIsoPath) return;
    const origHtml = btnCopyIsoPath.innerHTML;
    btnCopyIsoPath.innerHTML = `<span class="icon">&#10004;</span> Putanja Kopirana!`;
    btnCopyIsoPath.classList.add("btn-success");
    setTimeout(() => {
      btnCopyIsoPath.innerHTML = origHtml;
      btnCopyIsoPath.classList.remove("btn-success");
    }, 2000);
  }

  function fallbackCopyText(text) {
    const textArea = document.createElement("textarea");
    textArea.value = text;
    textArea.style.position = "fixed";
    textArea.style.left = "-999999px";
    document.body.appendChild(textArea);
    textArea.focus();
    textArea.select();
    try {
      document.execCommand("copy");
      showCopySuccess();
    } catch (e) {
      console.warn("Fallback kopiranje neuspešno:", e);
    }
    document.body.removeChild(textArea);
  }

  // Provera da li je ISO izgradnja već u toku pri otvaranju stranice
  function checkInitialIsoBuildStatus() {
    fetch("/api/build-iso/status")
      .then(res => res.json())
      .then(prog => {
        if (prog && prog.active) {
          isIsoBuilding = true;
          if (btnStartBuildIso) {
            btnStartBuildIso.disabled = true;
            btnStartBuildIso.innerHTML = `<span class="icon spin">&#8987;</span> Generisanje ISO Slike u Toku...`;
          }
          if (isoBuildProgSection) isoBuildProgSection.classList.remove("hidden");
          renderIsoBuildProgress(prog);
          startIsoBuildPolling();
        }
      })
      .catch(err => {
        console.warn("[app.js] Nije moguće dohvatiti inicijalni ISO status:", err);
      });
  }

  // Pokretanje
  fetchStatus();
  fetchPatches();
  fetchIsoSources(true);
  checkInitialIsoBuildStatus();
  statusPollInterval = setInterval(fetchStatus, 2500);
});
