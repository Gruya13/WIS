        let currentModalDevice = null;
        let eventSource = null;
        let wisBurnEventSource = null;
        let activeTab = 'fleet';
        let updatesCachedPackages = [];
        let fleetCachedDevices = {};
        let selectedCockpitDeviceId = null;
        let currentFleetFilter = 'all';
        let mirrorTimer = null;
        let currentMirrorDevId = null;
        let isPollingFrame = false;
        let isScreenStreamActive = false;
        let lastRenderedCockpitId = null;

        function getAdminToken() {
            try {
                return sessionStorage.getItem('servisHubAdminToken') || '';
            } catch (e) {
                return '';
            }
        }

        function setAdminToken(tok) {
            try {
                if (tok) {
                    sessionStorage.setItem('servisHubAdminToken', tok);
                } else {
                    sessionStorage.removeItem('servisHubAdminToken');
                }
            } catch (e) {}
        }

        function promptAdminToken(forceAsk) {
            let tok = forceAsk ? '' : getAdminToken();
            if (!tok) {
                const entered = window.prompt('Unesite ServisHub Admin Token (SERVIS_HUB_ADMIN_TOKEN):', '');
                if (entered === null) return '';
                tok = entered.trim();
                if (tok) setAdminToken(tok);
            }
            return tok;
        }

        async function adminFetch(url, options) {
            const opts = Object.assign({}, options || {});
            const baseHeaders = Object.assign({}, opts.headers || {});
            const tok = getAdminToken();
            if (tok) {
                baseHeaders['X-ServisHub-Token'] = tok;
            }
            opts.headers = baseHeaders;

            let res = await fetch(url, opts);
            if (res.status === 401) {
                setAdminToken('');
                const newTok = promptAdminToken(true);
                if (!newTok) {
                    return res;
                }
                const retryHeaders = Object.assign({}, baseHeaders, { 'X-ServisHub-Token': newTok });
                res = await fetch(url, Object.assign({}, opts, { headers: retryHeaders }));
            }
            return res;
        }

        // =====================================================================
        // Collapsible Sidebar & Sat
        // =====================================================================
        function toggleSidebar() {
            const sidebar = document.querySelector('.sidebar');
            const toggleBtn = document.getElementById('sidebar-toggle-btn');
            if (!sidebar) return;
            sidebar.classList.toggle('collapsed');
            const isCollapsed = sidebar.classList.contains('collapsed');
            if (toggleBtn) {
                toggleBtn.textContent = isCollapsed ? '▶' : '◀';
            }
            try {
                localStorage.setItem('servishub_sidebar_collapsed', isCollapsed ? '1' : '0');
            } catch(e) {}
        }

        try {
            if (localStorage.getItem('servishub_sidebar_collapsed') === '1') {
                const sidebar = document.querySelector('.sidebar');
                const toggleBtn = document.getElementById('sidebar-toggle-btn');
                if (sidebar) sidebar.classList.add('collapsed');
                if (toggleBtn) toggleBtn.textContent = '▶';
            }
        } catch(e) {}

        function updateClock() {
            const clockEl = document.getElementById('clock-display');
            if (clockEl) {
                const now = new Date();
                clockEl.textContent = now.toTimeString().substring(0, 8);
            }
        }
        setInterval(updateClock, 1000);
        updateClock();

        // 1 Hz Mirror Clock & Puls
        setInterval(function() {
            const mirrorStandbyTime = document.getElementById('mirror-standby-time');
            if (mirrorStandbyTime) {
                const now = new Date();
                mirrorStandbyTime.textContent = `${now.toTimeString().substring(0, 8)} [1 Hz SYNC]`;
            }
            const liveMirrorTime = document.getElementById('live-mirror-time');
            if (liveMirrorTime) {
                const now = new Date();
                liveMirrorTime.textContent = `${now.toTimeString().substring(0, 8)} [1 Hz TELEMETRY]`;
            }
            const liveTs = document.getElementById('mirror-live-timestamp');
            if (liveTs) {
                const now = new Date();
                liveTs.textContent = `${now.toTimeString().substring(0, 8)} [1 Hz LIVE]`;
            }
        }, 1000);

        // =====================================================================
        // Tab Navigacija
        // =====================================================================
        function switchTab(tabId) {
            activeTab = tabId;
            document.querySelectorAll('.nav-tab').forEach(b => b.classList.remove('active'));
            document.querySelectorAll('.tab-content').forEach(s => s.classList.remove('active'));

            const targetBtn = document.getElementById(`tab-btn-${tabId}`);
            const targetSection = document.getElementById(`tab-${tabId}`);
            if (targetBtn) targetBtn.classList.add('active');
            if (targetSection) targetSection.classList.add('active');

            if (tabId === 'wis') {
                stopCockpitScreenMirror();
                loadWisIsoInfo();
                loadWisUsbList();
                loadBenchmarkSummary();
            } else if (tabId === 'updates') {
                stopCockpitScreenMirror();
                loadUpdatesStatus();
                loadPredictiveStatus();
            } else if (tabId === 'fleet') {
                updateFleet();
                if (selectedCockpitDeviceId && (!mirrorTimer || currentMirrorDevId !== selectedCockpitDeviceId)) {
                    startCockpitScreenMirror(selectedCockpitDeviceId);
                }
            }
        }

        function setFleetFilter(filterType) {
            currentFleetFilter = filterType;
            document.querySelectorAll('.filter-chip').forEach(c => c.classList.remove('active'));
            const chip = document.getElementById(`filter-chip-${filterType}`);
            if (chip) chip.classList.add('active');
            filterFleetCards();
        }

        // =====================================================================
        // Pomoćne funkcije (XSS escape, formater, Markdown renderer)
        // =====================================================================
        function escapeHtml(str) {
            if (str === null || str === undefined) return '';
            return String(str)
                .replace(/&/g, '&amp;')
                .replace(/</g, '&lt;')
                .replace(/>/g, '&gt;')
                .replace(/"/g, '&quot;')
                .replace(/'/g, '&#039;');
        }

        function formatBytes(bytes) {
            if (!bytes || bytes <= 0) return '0 B';
            const k = 1024;
            const sizes = ['B', 'KB', 'MB', 'GB', 'TB'];
            const i = Math.floor(Math.log(bytes) / Math.log(k));
            return parseFloat((bytes / Math.pow(k, i)).toFixed(2)) + ' ' + sizes[i];
        }

        function renderMarkdown(mdText) {
            if (!mdText || !mdText.trim()) {
                return '<div style="color:var(--text-muted); font-style:italic;">Dnevnik intervencija je trenutno prazan.</div>';
            }
            const lines = mdText.split('\n');
            let html = [];
            let inList = false;

            for (let rawLine of lines) {
                let line = escapeHtml(rawLine);
                if (line.startsWith('### ')) {
                    if (inList) { html.push('</ul>'); inList = false; }
                    html.push(`<h4 class="md-h3">${formatInline(line.substring(4))}</h4>`);
                } else if (line.startsWith('## ')) {
                    if (inList) { html.push('</ul>'); inList = false; }
                    html.push(`<h3 class="md-h2">${formatInline(line.substring(3))}</h3>`);
                } else if (line.startsWith('# ')) {
                    if (inList) { html.push('</ul>'); inList = false; }
                    html.push(`<h2 class="md-h1">${formatInline(line.substring(2))}</h2>`);
                } else if (line.trim() === '---') {
                    if (inList) { html.push('</ul>'); inList = false; }
                    html.push('<hr class="md-hr">');
                } else if (line.trim().startsWith('- ') || line.trim().startsWith('* ')) {
                    if (!inList) { html.push('<ul class="md-ul">'); inList = true; }
                    let itemContent = line.trim().substring(2);
                    html.push(`<li>${formatInline(itemContent)}</li>`);
                } else if (line.trim() === '') {
                    if (inList) { html.push('</ul>'); inList = false; }
                    html.push('<div class="md-space"></div>');
                } else {
                    if (inList) { html.push('</ul>'); inList = false; }
                    html.push(`<p class="md-p">${formatInline(line)}</p>`);
                }
            }
            if (inList) html.push('</ul>');
            return html.join('');
        }

        function formatInline(str) {
            return str
                .replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>')
                .replace(/\*([^*]+)\*/g, '<em>$1</em>')
                .replace(/`([^`]+)`/g, '<code class="md-code">$1</code>');
        }

        // =====================================================================
        // TAB 1: Radionička Flota & Telemetrija
        // =====================================================================
        function updateFleet() {
            fetch('/api/devices')
                .then(res => res.json())
                .then(devices => {
                    fleetCachedDevices = devices;
                    renderFleet(devices);
                })
                .catch(err => console.error("Greška pri osvežavanju flote:", err));
        }

        function selectCockpitDevice(devKey) {
            if (mirrorTimer) {
                clearInterval(mirrorTimer);
                mirrorTimer = null;
            }
            selectedCockpitDeviceId = devKey;
            lastRenderedCockpitId = null;
            isScreenStreamActive = false;

            document.querySelectorAll('.card').forEach(c => c.classList.remove('card-selected'));
            const selectedCard = document.getElementById(`card-${devKey}`);
            if (selectedCard) selectedCard.classList.add('card-selected');

            if (fleetCachedDevices && fleetCachedDevices[devKey]) {
                renderCockpitDetail(devKey, fleetCachedDevices[devKey]);
            }
            startCockpitScreenMirror(devKey);
        }

        // WIS_STATUS_CONTRACT_START
        function deviceIsCompleted(d) {
            if (d.diagnostics_status === 'THERMAL_CRITICAL' || d.deployment_status === 'NEEDS_ATTENTION' || d.status_label === 'NEEDS_ATTENTION') return false;
            return ['Zavrseno', 'Završeno', 'Servisiran (AGY)'].includes(d.status_label) ||
                ['DONE', 'READY', 'COMPLETED', 'SUCCESS'].includes(d.deployment_status || d.status);
        }
        function deviceLiveStatus(d) {
            const base = d.live_status || "Inicijalizacija sistema...";
            if (deviceIsCompleted(d) || d.diagnostics_status === 'THERMAL_CRITICAL') {
                const diagnostic = d.diagnostics_summary || (d.cooling_verified !== true ? 'Hlađenje nije potvrđeno; potrebna ručna provera.' : 'Hlađenje provereno prema rezultatu klijenta.');
                return base + ' | ' + diagnostic;
            }
            return base;
        }
        // WIS_STATUS_CONTRACT_END

        function renderFleet(devices) {
            const grid = document.getElementById('fleet-grid');
            const devKeys = Object.keys(devices);

            document.getElementById('stat-total').textContent = devKeys.length;
            const fleetCountBadge = document.getElementById('fleet-count-badge');
            if (fleetCountBadge) fleetCountBadge.textContent = `${devKeys.length} računara`;

            let activeCount = 0;
            let doneCount = 0;

            if (devKeys.length === 0) {
                grid.innerHTML = `
                    <div class="empty-fleet">
                        <h2>Čekam povezivanje klijentskih računara...</h2>
                        <p>Čim se na nekom klijentu u servisu pokrene instalacija i uspostavi mreža, ovde će se pojaviti kartica uređaja.</p>
                    </div>`;
                document.getElementById('stat-active').textContent = 0;
                document.getElementById('stat-done').textContent = 0;
                stopCockpitScreenMirror();
                lastRenderedCockpitId = null;
                renderEmptyCockpit();
                return;
            }

            // Ako trenutno izabrani uređaj više ne postoji ili nije postavljen, selektuj prvi
            if (!selectedCockpitDeviceId || !devices[selectedCockpitDeviceId]) {
                selectedCockpitDeviceId = devKeys[0];
            }

            let html = '';
            for (const key of devKeys) {
                const d = devices[key];
                const isOnline = d.is_online;
                const phase = d.phase || 1;
                const prog = (d.worker_progress !== undefined) ? d.worker_progress : 0;
                const statusLabel = d.status_label || "Aktivno";

                if (statusLabel === 'Aktivno' || statusLabel === 'Restart u toku') activeCount++;
                if (deviceIsCompleted(d)) doneCount++;

                let statusClass = 'status-active';
                if (statusLabel === 'Servisiran (AGY)') statusClass = 'status-agy-done';
                else if (statusLabel === 'Kvar (AGY)') statusClass = 'status-agy-fail';
                else if (statusLabel === 'Restart u toku') statusClass = 'status-reboot';
                else if (deviceIsCompleted(d)) statusClass = 'status-done';
                else if (statusLabel === 'Upozorenje') statusClass = 'status-warn';
                else if (!isOnline) statusClass = 'status-offline';

                if (deviceIsCompleted(d) && d.cooling_verified !== true) statusClass = 'status-warn';
                const devTitle = d.hw && (d.hw.manufacturer || d.hw.model) ?
                    `${d.hw.manufacturer || ''} ${d.hw.model || ''}`.trim() : d.computer_name;
                const serial = d.hw && d.hw.serial ? `S/N: ${d.hw.serial}` : (d.client_ip || '');
                const liveStatus = deviceLiveStatus(d);

                // Autopilot bedž
                const ap = d.autopilot || {};
                const apStatus = ap.status || (d.hw && d.hw.autopilot_status) || 'Nepoznato';
                let apBadge = '';
                if (apStatus === 'Autopilot') {
                    apBadge = `<span class="header-badge" style="background:rgba(239,68,68,0.25); color:#ef4444; border:1px solid #ef4444; font-weight:bold;">🚨 ! AUTOPILOT REGISTROVAN (${escapeHtml(ap.tenant || 'Zaključan')})</span>`;
                } else if (apStatus === 'AadMdm') {
                    apBadge = `<span class="header-badge" style="background:rgba(234,179,8,0.2); color:#eab308; border:1px solid #eab308;">⚠️ ~ AAD/MDM</span>`;
                } else if (apStatus === 'Clean') {
                    apBadge = `<span class="header-badge" style="background:rgba(34,197,94,0.2); color:#22c55e; border:1px solid #22c55e;">✓ * ČISTO</span>`;
                }

                // SMART / Hardware Health bedž
                const smart = d.smart || (d.tab_data && d.tab_data.smart);
                let smartBadge = '';
                if (smart) {
                    const cat = smart.Category || smart.category || 'OK';
                    const ssdPct = (smart.SsdHealth !== undefined && smart.SsdHealth !== null) ? smart.SsdHealth : (smart.ssd_health || 100);
                    if (cat === 'CRITICAL') {
                        smartBadge = `<span class="header-badge" style="background:rgba(239,68,68,0.25); color:#ef4444; border:1px solid #ef4444; font-weight:bold;">🚨 SMART: KRITIČNO (${ssdPct}%)</span>`;
                    } else if (cat === 'WARNING') {
                        smartBadge = `<span class="header-badge" style="background:rgba(234,179,8,0.2); color:#eab308; border:1px solid #eab308;">⚠️ SMART: ${ssdPct}%</span>`;
                    } else {
                        smartBadge = `<span class="header-badge" style="background:rgba(34,197,94,0.15); color:#22c55e; border:1px solid rgba(34,197,94,0.4);">✓ SMART: ${ssdPct}%</span>`;
                    }
                }

                // OEM & Garancija Bedževi
                const warranty = d.warranty || (d.oem && d.oem.warranty) || (d.tab_data && d.tab_data.warranty) || (d.latest_report && d.latest_report.warranty);
                let warrantyBadge = '';
                if (warranty) {
                    const inWarr = warranty.in_warranty || (warranty.days_remaining && warranty.days_remaining > 0) || (warranty.status_label && String(warranty.status_label).toLowerCase().includes('u garanciji'));
                    const days = warranty.days_remaining;
                    const sLevel = String(warranty.service_level || '').toLowerCase();
                    const isPremier = sLevel.includes('premier') || sLevel.includes('onsite');

                    if (inWarr) {
                        let wText = '🛡️ U Garanciji';
                        if (isPremier && days !== undefined && days !== null) {
                            wText = `🛡️ Premier Onsite (${days}d)`;
                        } else if (isPremier) {
                            wText = `🛡️ Premier Onsite`;
                        } else if (days !== undefined && days !== null && days > 0) {
                            wText = `🛡️ U Garanciji (${days}d)`;
                        }
                        warrantyBadge = `<span class="header-badge" style="background:rgba(34,197,94,0.15); color:var(--green); border:1px solid rgba(34,197,94,0.4); font-weight:600;">${escapeHtml(wText)}</span>`;
                    } else {
                        warrantyBadge = `<span class="header-badge" style="background:rgba(239,68,68,0.15); color:#ef4444; border:1px solid rgba(239,68,68,0.4);">🛡️ Van Garancije</span>`;
                    }
                }

                // Izvor Drajvera Bedž
                let driverSourceBadge = '';
                const rawDrvSrc = d.driver_source || (d.oem && d.oem.source ? d.oem.source : null) || (d.drivers && d.drivers.source) || '';
                const drvSrcLower = String(rawDrvSrc).toLowerCase();
                const isLenovo = (d.hw && d.hw.manufacturer && String(d.hw.manufacturer).toLowerCase().includes('lenovo')) || (devTitle && devTitle.toLowerCase().includes('lenovo'));

                if (rawDrvSrc) {
                    if (drvSrcLower.includes('sccm') || drvSrcLower.includes('lenovo')) {
                        driverSourceBadge = `<span class="header-badge" style="background:rgba(56,189,248,0.15); color:var(--cyan); border:1px solid rgba(56,189,248,0.4);">📦 Lenovo SCCM ZFS Keš</span>`;
                    } else if (drvSrcLower.includes('catalog') || drvSrcLower.includes('ms') || drvSrcLower.includes('microsoft')) {
                        driverSourceBadge = `<span class="header-badge" style="background:rgba(217,70,239,0.15); color:var(--magenta); border:1px solid rgba(217,70,239,0.4);">🌐 Microsoft Catalog</span>`;
                    } else {
                        driverSourceBadge = `<span class="header-badge" style="background:rgba(56,189,248,0.15); color:var(--cyan); border:1px solid rgba(56,189,248,0.4);">📦 ${escapeHtml(rawDrvSrc)}</span>`;
                    }
                } else if (isLenovo) {
                    driverSourceBadge = `<span class="header-badge" style="background:rgba(56,189,248,0.15); color:var(--cyan); border:1px solid rgba(56,189,248,0.4);">📦 Lenovo SCCM ZFS Keš</span>`;
                }

                // Pauza, USB i sesija status bedževi
                let sessionBadge = '';
                if (d.is_paused) {
                    sessionBadge += `<span class="header-badge" style="background:rgba(234,179,8,0.25); color:#eab308; border:1px solid #eab308; font-weight:bold;">⏸️ PAUZIRANO</span>`;
                }
                if (d.usb_free) {
                    sessionBadge += `<span class="header-badge" style="background:rgba(34,197,94,0.2); color:#22c55e; border:1px solid #22c55e;">💾 USB SLOBODAN</span>`;
                }
                if (d.session_id && d.last_seen_sec_ago > 30) {
                    sessionBadge += `<span class="header-badge" style="background:rgba(239,68,68,0.2); color:#ef4444; border:1px solid #ef4444;">STALE</span>`;
                }

                // Faze 1..5
                const pList = [
                    { n: 1, name: "1. Mreža" },
                    { n: 2, name: "2. Update" },
                    { n: 3, name: "3. Optimizacija" },
                    { n: 4, name: "4. Programi" },
                    { n: 5, name: "5. Završetak" }
                ];
                let phasesHtml = pList.map(p => {
                    let cls = '';
                    if (phase > p.n) cls = 'completed';
                    else if (phase === p.n) cls = 'active';
                    return `<div class="phase-step ${cls}">${p.name}</div>`;
                }).join('');

                // Aplikacije bedževi
                const apps = d.apps || {};
                const appKeys = ['Chrome', 'WinRAR', 'Office', 'Acrobat'];
                let appsHtml = appKeys.map(ak => {
                    const st = apps[ak] || 'Pending';
                    let cls = '';
                    if (st === 'Completed' || st === 'OK') cls = 'ok';
                    else if (st === 'Running') cls = 'running';
                    else if (st === 'Failed') cls = 'failed';
                    return `<div class="app-badge ${cls}">${ak}</div>`;
                }).join('');

                let netBadge = '';
                if (d.network && d.network.both_connected) {
                    netBadge = '<span class="header-badge" style="color:var(--green); border-color:var(--green);">⚡ Dual-Homing (LAN + Wi-Fi)</span>';
                } else if (d.network && d.network.lan_connected) {
                    netBadge = `<span class="header-badge" style="color:var(--green);">🔌 LAN (${escapeHtml(d.network.lan_speed || 'Kabl')})</span>`;
                } else if (d.network && d.network.wifi_connected) {
                    netBadge = `<span class="header-badge" style="color:var(--yellow);">📶 Wi-Fi (${escapeHtml(d.network.wifi_ssid || 'Bežično')})</span>`;
                } else if (d.client_ip) {
                    const isWlan = d.client_ip.startsWith("192.168.180.");
                    const isLan = d.client_ip.startsWith("192.168.88.");
                    const subLabel = isWlan ? "WLAN" : (isLan ? "LAN" : "IP");
                    netBadge = `<span class="header-badge">${subLabel}: ${escapeHtml(d.client_ip)}</span>`;
                }

                // Sažetak AGY intervencije ili live status
                const isCompleted = (deviceIsCompleted(d));
                let liveActionHtml = '';
                if (d.summary || statusLabel.includes('AGY')) {
                    const boxBorder = statusLabel === 'Kvar (AGY)' ? 'var(--red)' : 'var(--purple)';
                    const boxColor = statusLabel === 'Kvar (AGY)' ? 'var(--red)' : 'var(--purple)';
                    liveActionHtml = `
                        <div class="agy-summary-box" style="border-left-color: ${boxBorder};">
                            <div class="agy-summary-label" style="color: ${boxColor};">🛠️ Sažetak AGY Dijagnostike / Intervencije</div>
                            <div class="agy-summary-text" title="${escapeHtml(d.summary || '')}">
                                ${escapeHtml(d.summary || 'Servisna sesija uspešno evidentirana.')}
                            </div>
                        </div>`;
                } else if (isCompleted) {
                    liveActionHtml = `
                        <div class="live-action-box" style="border-left-color: var(--green);">
                            <div class="live-action-label" style="color: var(--green);">Status servisa</div>
                            <div class="live-action-text" style="color: #4ade80;">
                                ⏱️ Trajanje: <strong>${escapeHtml(d.duration_formatted || 'Završeno')}</strong>
                            </div>
                        </div>`;
                } else {
                    liveActionHtml = `
                        <div class="live-action-box">
                            <div class="live-action-label">Trenutna aktivnost (Faza ${phase})</div>
                            <div class="live-action-text" title="${escapeHtml(liveStatus)}">${escapeHtml(liveStatus)}</div>
                        </div>`;
                }

                let reportBtnHtml = '';
                if (isCompleted) {
                    reportBtnHtml = `
                        <button class="btn-report" onclick="event.stopPropagation(); openReportModal('${escapeHtml(key)}', '${escapeHtml(d.computer_name || key)}')">
                            <span>📋</span> Servisni Karton
                        </button>`;
                }

                const historyBtnHtml = `
                    <button class="btn-history" onclick="event.stopPropagation(); openHistoryModal('${escapeHtml(d.device_tag || key)}', '${escapeHtml(d.computer_name || key)}')">
                        <span>📜</span> Istorija / Dnevnik
                    </button>`;

                const isCardSelected = (key === selectedCockpitDeviceId);
                const selectedClass = isCardSelected ? 'card-selected' : '';

                html += `
                <div class="card ${selectedClass}" id="card-${escapeHtml(key)}" onclick="selectCockpitDevice('${escapeHtml(key)}')">
                    <div class="card-header">
                        <div class="card-title-group">
                            <div class="card-dev-name">
                                <span>💻 ${escapeHtml(devTitle)}</span>
                                ${apBadge}
                                ${smartBadge}
                                ${sessionBadge}
                            </div>
                            <div class="card-sub">${escapeHtml(d.computer_name || key)} | ${escapeHtml(serial)} ${netBadge}</div>
                            <div class="card-meta-badges">
                                ${warrantyBadge}
                                ${driverSourceBadge}
                            </div>
                        </div>
                        <div class="status-pill ${statusClass}">${escapeHtml(statusLabel)}</div>
                    </div>
                    <div class="card-body">
                        <div class="phases-row">
                            ${phasesHtml}
                        </div>
                        ${liveActionHtml}
                        <div class="progress-container">
                            <div class="progress-header">
                                <span>Napredak aplikacija / servisa:</span>
                                <span style="font-weight:700; color:#fff;">${prog}%</span>
                            </div>
                            <div class="progress-bar-bg">
                                <div class="progress-bar-fill" style="width: ${prog}%"></div>
                            </div>
                        </div>
                        <div class="apps-badge-grid">
                            ${appsHtml}
                        </div>
                    </div>
                    <div class="card-footer">
                        <span style="color:var(--text-muted); font-size:0.75rem;">Zadnji ping: ${d.last_seen_sec_ago || 0}s pre ${d.session_id ? `| Sesija: <span style="font-family:var(--font-mono); color:var(--cyan);">${escapeHtml(d.session_id.substring(0, 10))}...</span>` : ''}</span>
                        <div class="card-footer-actions">
                            ${historyBtnHtml}
                            ${reportBtnHtml}
                            <button class="btn-log" onclick="event.stopPropagation(); openLogModal('${escapeHtml(key)}', '${escapeHtml(d.computer_name || key)}', 'Faza ${phase}')">
                                <span>📟</span> Log Uživo
                            </button>
                        </div>
                    </div>
                </div>`;
            }

            document.getElementById('stat-active').textContent = activeCount;
            document.getElementById('stat-done').textContent = doneCount;
            grid.innerHTML = html;

            filterFleetCards();

            // Ažuriraj Cockpit desni panel sa detaljima izabranog računara
            if (selectedCockpitDeviceId && devices[selectedCockpitDeviceId]) {
                renderCockpitDetail(selectedCockpitDeviceId, devices[selectedCockpitDeviceId]);
                if (!mirrorTimer || currentMirrorDevId !== selectedCockpitDeviceId) {
                    startCockpitScreenMirror(selectedCockpitDeviceId);
                }
            }
        }

        function filterFleetCards() {
            const query = (document.getElementById('fleet-search-input')?.value || '').trim().toLowerCase();
            const cards = document.querySelectorAll('.fleet-grid .card');

            cards.forEach(card => {
                const text = card.textContent.toLowerCase();
                let matchesQuery = !query || text.includes(query);
                let matchesFilter = true;

                if (currentFleetFilter === 'active') {
                    matchesFilter = text.includes('aktivno') || text.includes('restart u toku');
                } else if (currentFleetFilter === 'done') {
                    matchesFilter = text.includes('zavrseno') || text.includes('servisiran (agy)');
                } else if (currentFleetFilter === 'agy') {
                    matchesFilter = text.includes('agy');
                }

                card.style.display = (matchesQuery && matchesFilter) ? 'flex' : 'none';
            });
        }

        // =====================================================================
        // Detaljni Hibridni Cockpit sa 1 Hz ogledalom ekrana
        // =====================================================================
        function renderEmptyCockpit() {
            const pane = document.getElementById('cockpit-detail-pane');
            if (!pane) return;
            pane.innerHTML = `
                <div class="cockpit-card">
                    <div class="cockpit-view-header">
                        <div class="cockpit-title-group">
                            <h2><span>⚡</span> Radionički Telemetry Cockpit</h2>
                            <div class="cockpit-title-sub">Čekam povezivanje radnih stanica na lokalnu radioničku mrežu</div>
                        </div>
                        <span class="header-badge" style="color:var(--accent); border-color:var(--accent);">Standby Režim</span>
                    </div>
                    <div class="screen-mirror-frame">
                        <div class="screen-mirror-topbar">
                            <span><span class="pulse-dot"></span> 1 Hz OGLEDALO EKRANA RADNE STANICE</span>
                            <span style="color:var(--accent);">Standby Režim</span>
                        </div>
                        <div class="screen-mirror-viewport">
                            <div class="mirror-standby-container">
                                <div class="mirror-content-top">
                                    <span style="color:var(--accent); font-weight:700;">[WIS SERVICE COCKPIT]</span>
                                    <span style="color:var(--success);">UDP DISCOVERY AKTIVAN (PORT 8888)</span>
                                </div>
                                <div class="mirror-content-mid" style="text-align:center;">
                                    <div style="font-size:1.1rem; color:#fff; font-weight:700;">Čekam heartbeat klijenta...</div>
                                    <div style="font-size:0.75rem; color:var(--text-muted); margin-top:4px;">Čim se klijent javi sa PXE/USB instalacije, ovde se aktivira 1 Hz ogledalo ekrana.</div>
                                </div>
                                <div class="mirror-content-bottom">
                                    <span>LAN: 10 GbE TrueNAS ZFS Keš Spreman</span>
                                    <span id="mirror-standby-time">1 Hz SYNC</span>
                                </div>
                            </div>
                        </div>
                    </div>
                </div>`;
        }

        // =====================================================================
        // 1 Hz Screen Mirror Polling & Upravljanje Strimom
        // =====================================================================
        function startCockpitScreenMirror(devKey) {
            if (mirrorTimer) {
                clearInterval(mirrorTimer);
                mirrorTimer = null;
            }
            if (!devKey) return;
            currentMirrorDevId = devKey;
            isPollingFrame = false;

            // Odmah pokreni prvo učitavanje frejma
            pollCockpitScreen();

            // 1 Hz interval (1000ms)
            mirrorTimer = setInterval(pollCockpitScreen, 1000);
        }

        function stopCockpitScreenMirror() {
            if (mirrorTimer) {
                clearInterval(mirrorTimer);
                mirrorTimer = null;
            }
            isPollingFrame = false;
            currentMirrorDevId = null;
            isScreenStreamActive = false;
        }

        function pollCockpitScreen() {
            if (!selectedCockpitDeviceId) return;
            if (activeTab !== 'fleet') return;

            const devKey = selectedCockpitDeviceId;
            if (isPollingFrame) return;

            isPollingFrame = true;
            const testImg = new Image();
            const frameUrl = `/api/devices/${encodeURIComponent(devKey)}/screen-frame.jpg?t=${Date.now()}`;

            testImg.onload = function() {
                isPollingFrame = false;
                if (selectedCockpitDeviceId !== devKey) return;

                isScreenStreamActive = true;
                const liveContainer = document.getElementById('mirror-live-container');
                const standbyContainer = document.getElementById('mirror-standby-container');
                const liveImg = document.getElementById('cockpit-screen-img');
                const badge = document.getElementById('screen-mirror-badge');

                if (liveImg) {
                    liveImg.src = frameUrl;
                }
                if (liveContainer) liveContainer.style.display = 'flex';
                if (standbyContainer) standbyContainer.style.display = 'none';

                if (badge) {
                    badge.textContent = '🟢 1 Hz LIVE';
                    badge.style.color = 'var(--success)';
                    badge.style.borderColor = 'var(--success)';
                }

                const resLabel = document.getElementById('mirror-live-res');
                if (resLabel && testImg.naturalWidth && testImg.naturalHeight) {
                    resLabel.textContent = `${testImg.naturalWidth}×${testImg.naturalHeight}`;
                }
                const tsLabel = document.getElementById('mirror-live-timestamp');
                if (tsLabel) {
                    const now = new Date();
                    tsLabel.textContent = `${now.toTimeString().substring(0, 8)} [1 Hz LIVE]`;
                }
            };

            testImg.onerror = function() {
                isPollingFrame = false;
                if (selectedCockpitDeviceId !== devKey) return;

                isScreenStreamActive = false;
                const liveContainer = document.getElementById('mirror-live-container');
                const standbyContainer = document.getElementById('mirror-standby-container');
                const badge = document.getElementById('screen-mirror-badge');

                if (liveContainer) liveContainer.style.display = 'none';
                if (standbyContainer) standbyContainer.style.display = 'flex';

                if (badge) {
                    badge.textContent = 'Status: Čeka se klijentski strim';
                    badge.style.color = 'var(--accent)';
                    badge.style.borderColor = 'var(--accent)';
                }
            };

            testImg.src = frameUrl;
        }

        function updateCockpitMetricsInPlace(devKey, d, devTitle, serial, currentPhaseName, liveStatus, prog, appSummaryText, statusLabel, cpuName, cpuLoadDisplay, cpuBarWidth, rawTemp, tempDisplay, tempStatus, tempBarWidth, tempBarColor, ramUsageText, ramPctDisplay, ramBarWidth, smartHeaderSub, smartDisplay, smartBarWidth, smartBarColor, wLabel, drvDisplay, isCompleted) {
            const el = id => document.getElementById(id);

            const durEl = el('cockpit-duration-badge');
            if (durEl) durEl.textContent = `⏱️ ${d.duration_formatted || 'U toku'}`;
            const statEl = el('cockpit-status-pill');
            if (statEl) statEl.textContent = statusLabel;

            const phaseEl = el('cockpit-phase-label');
            if (phaseEl) phaseEl.textContent = currentPhaseName;
            const liveStatEl = el('cockpit-live-status');
            if (liveStatEl) liveStatEl.textContent = liveStatus;
            const progBar = el('cockpit-prog-bar');
            if (progBar) progBar.style.width = `${prog}%`;
            const progNum = el('cockpit-prog-num');
            if (progNum) progNum.textContent = `${prog}%`;
            const appsEl = el('cockpit-apps-summary');
            if (appsEl) appsEl.textContent = appSummaryText;

            const cpuPctEl = el('cockpit-gauge-cpu-pct');
            if (cpuPctEl) cpuPctEl.textContent = cpuLoadDisplay;
            const cpuNameEl = el('cockpit-gauge-cpu-name');
            if (cpuNameEl) {
                cpuNameEl.textContent = cpuName;
                cpuNameEl.title = cpuName;
            }
            const cpuBar = el('cockpit-gauge-cpu-bar');
            if (cpuBar) cpuBar.style.width = cpuBarWidth;

            const tempStatEl = el('cockpit-gauge-temp-status');
            if (tempStatEl) {
                tempStatEl.textContent = tempStatus;
                tempStatEl.style.color = rawTemp !== null ? (rawTemp > 80 ? 'var(--danger)' : 'var(--green)') : 'var(--text-muted)';
            }
            const tempValEl = el('cockpit-gauge-temp-val');
            if (tempValEl) {
                tempValEl.textContent = tempDisplay;
                tempValEl.style.color = rawTemp !== null ? (rawTemp > 80 ? 'var(--danger)' : 'var(--green)') : 'var(--text-secondary)';
            }
            const tempBar = el('cockpit-gauge-temp-bar');
            if (tempBar) {
                tempBar.style.width = tempBarWidth;
                tempBar.style.background = tempBarColor;
            }

            const ramPctEl = el('cockpit-gauge-ram-pct');
            if (ramPctEl) ramPctEl.textContent = ramPctDisplay;
            const ramValEl = el('cockpit-gauge-ram-val');
            if (ramValEl) ramValEl.textContent = ramUsageText;
            const ramBar = el('cockpit-gauge-ram-bar');
            if (ramBar) ramBar.style.width = ramBarWidth;

            const smartCatEl = el('cockpit-gauge-smart-cat');
            if (smartCatEl) smartCatEl.textContent = smartHeaderSub;
            const smartValEl = el('cockpit-gauge-smart-val');
            if (smartValEl) smartValEl.textContent = smartDisplay;
            const smartBar = el('cockpit-gauge-smart-bar');
            if (smartBar) {
                smartBar.style.width = smartBarWidth;
                smartBar.style.background = smartBarColor;
            }

            const oemValEl = el('cockpit-gauge-oem-val');
            if (oemValEl) oemValEl.textContent = wLabel;
            const oemDrvEl = el('cockpit-gauge-oem-drv');
            if (oemDrvEl) oemDrvEl.textContent = drvDisplay;
        }

        function renderCockpitDetail(devKey, d) {
            const pane = document.getElementById('cockpit-detail-pane');
            if (!pane) return;

            const devTitle = d.hw && (d.hw.manufacturer || d.hw.model) ?
                `${d.hw.manufacturer || ''} ${d.hw.model || ''}`.trim() : d.computer_name;
            const serial = d.hw && d.hw.serial ? `S/N: ${d.hw.serial}` : (d.client_ip || 'N/A');
            const phase = d.phase || 1;
            const prog = (d.worker_progress !== undefined) ? d.worker_progress : 0;
            const liveStatus = deviceLiveStatus(d);
            const statusLabel = d.status_label || "Aktivno";

            // Faza opis
            const phaseNames = {
                1: "1. Mreža & WinPE Inicijalizacija",
                2: "2. Windows 11 Offline & OEM Drajveri",
                3: "3. Optimizacija & Postavke Sistema",
                4: "4. Instalacija Aplikacija i Alata",
                5: "5. Stres test",
                6: "6. Finalizacija & Servisni Izveštaj"
            };
            const currentPhaseName = phaseNames[phase] || `Faza ${phase}`;

            // SMART telemetrija (Strict No-Mock: bez izmišljanja 100% ili kategorije ako podaci ne postoje)
            const hasSmart = !!(d.smart || (d.tab_data && d.tab_data.smart));
            const smart = d.smart || (d.tab_data && d.tab_data.smart) || {};
            const ssdPct = (smart.SsdHealth !== undefined && smart.SsdHealth !== null) ? smart.SsdHealth : (smart.ssd_health !== undefined && smart.ssd_health !== null ? smart.ssd_health : null);
            const diskTemp = (smart.Temperature !== undefined && smart.Temperature !== null) ? smart.Temperature : ((smart.temperature !== undefined && smart.temperature !== null) ? smart.temperature : null);
            const smartCat = smart.Category || smart.category || (hasSmart ? 'ČISTO' : 'N/A (Čeka telemetriju)');
            let smartHeaderSub = smartCat;
            if (diskTemp !== null) {
                smartHeaderSub += ` (${diskTemp} °C)`;
            }
            let smartDisplay = ssdPct !== null ? `${ssdPct}%` : 'N/A (Čeka telemetriju)';
            if (ssdPct !== null && diskTemp !== null) {
                smartDisplay = `${ssdPct}% (NVMe: ${diskTemp} °C)`;
            }
            const smartBarWidth = ssdPct !== null ? `${Math.min(100, Math.max(0, ssdPct))}%` : '0%';
            const smartBarColor = ssdPct !== null ? (ssdPct < 50 ? 'var(--danger)' : (ssdPct < 80 ? 'var(--warning)' : 'var(--success)')) : 'var(--border)';

            // Termika / Temperatura procesora (Strict No-Mock: ako nema podatka, prikaz '-- °C')
            const rawTemp = (d.cpu_temp !== undefined && d.cpu_temp !== null) ? d.cpu_temp : ((d.temp !== undefined && d.temp !== null) ? d.temp : ((d.temperature !== undefined && d.temperature !== null) ? d.temperature : null));
            const tempDisplay = rawTemp !== null ? `${rawTemp} °C` : '-- °C';
            const tempStatus = rawTemp !== null ? (rawTemp > 80 ? 'Visoka temperatura' : 'Senzor radne stanice aktivan') : 'N/A (Čeka telemetriju)';
            const tempBarWidth = rawTemp !== null ? `${Math.min(100, Math.max(0, rawTemp))}%` : '0%';
            const tempBarColor = rawTemp !== null ? (rawTemp > 80 ? 'var(--danger)' : (rawTemp > 70 ? 'var(--warning)' : 'var(--success)')) : 'var(--border)';

            // CPU telemetrija (sa progres barom)
            const cpuName = d.hw?.cpu || 'N/A (Čeka telemetriju)';
            const cpuPct = (d.cpu_percent !== undefined && d.cpu_percent !== null) ? d.cpu_percent : ((d.cpu_usage !== undefined && d.cpu_usage !== null) ? d.cpu_usage : null);
            const cpuLoadDisplay = cpuPct !== null ? `${cpuPct}%` : 'N/A (Čeka telemetriju)';
            const cpuBarWidth = cpuPct !== null ? `${Math.min(100, Math.max(0, cpuPct))}%` : '0%';

            // RAM telemetrija (sa progres barom i ram_used / ram_total)
            let ramUsageText = '';
            if (d.ram_used !== undefined && d.ram_used !== null && d.ram_total !== undefined && d.ram_total !== null) {
                const uStr = String(d.ram_used).toLowerCase().includes('gb') ? String(d.ram_used) : `${d.ram_used} GB`;
                const tStr = String(d.ram_total).toLowerCase().includes('gb') ? String(d.ram_total) : `${d.ram_total} GB`;
                ramUsageText = `${uStr} / ${tStr}`;
            } else if (d.ram_used !== undefined && d.ram_used !== null) {
                ramUsageText = String(d.ram_used).toLowerCase().includes('gb') ? String(d.ram_used) : `${d.ram_used} GB`;
            } else if (d.hw?.ram) {
                ramUsageText = d.hw.ram;
            } else {
                ramUsageText = 'N/A (Čeka telemetriju)';
            }
            const ramPct = (d.ram_percent !== undefined && d.ram_percent !== null) ? d.ram_percent : ((d.memory_percent !== undefined && d.memory_percent !== null) ? d.memory_percent : null);
            const ramPctDisplay = ramPct !== null ? `${ramPct}%` : 'N/A (Čeka telemetriju)';
            const ramBarWidth = ramPct !== null ? `${Math.min(100, Math.max(0, ramPct))}%` : '0%';

            // Garancija & Drajveri info za cockpit (Očuvaj ključne oznake za testove)
            const warranty = d.warranty || (d.oem && d.oem.warranty) || (d.tab_data && d.tab_data.warranty) || (d.latest_report && d.latest_report.warranty) || {};
            const inWarr = warranty.in_warranty || (warranty.days_remaining && warranty.days_remaining > 0);
            const wDays = warranty.days_remaining ? ` (${warranty.days_remaining}d)` : '';
            const sLevel = String(warranty.service_level || '').toLowerCase();
            const isPremier = sLevel.includes('premier') || sLevel.includes('onsite');
            let wLabel = 'Van Garancije';
            if (inWarr) {
                wLabel = isPremier ? `Premier Onsite${wDays}` : `U Garanciji${wDays}`;
            }

            const rawDrvSrc = d.driver_source || (d.oem && d.oem.source) || 'Lenovo SCCM ZFS Keš';
            const isLenovo = (d.hw && d.hw.manufacturer && String(d.hw.manufacturer).toLowerCase().includes('lenovo'));
            const drvDisplay = isLenovo ? 'Lenovo SCCM ZFS Keš' : (String(rawDrvSrc).toLowerCase().includes('catalog') ? 'Microsoft Catalog' : escapeHtml(rawDrvSrc));

            const isCompleted = (deviceIsCompleted(d));

            // Realne instalirane aplikacije iz telemetrije
            const completedApps = Object.entries(d.apps || {})
                .filter(([_, st]) => st === 'Completed' || st === 'OK')
                .map(([name]) => name);
            const appSummaryText = completedApps.length > 0 ? completedApps.join(', ') : (Object.keys(d.apps || {}).length > 0 ? Object.keys(d.apps).join(', ') : 'N/A (Čeka telemetriju)');

            // In-place ažuriranje ako je isti računar već nacrtan
            if (lastRenderedCockpitId === devKey && document.getElementById('cockpit-screen-img')) {
                updateCockpitMetricsInPlace(devKey, d, devTitle, serial, currentPhaseName, liveStatus, prog, appSummaryText, statusLabel, cpuName, cpuLoadDisplay, cpuBarWidth, rawTemp, tempDisplay, tempStatus, tempBarWidth, tempBarColor, ramUsageText, ramPctDisplay, ramBarWidth, smartHeaderSub, smartDisplay, smartBarWidth, smartBarColor, wLabel, drvDisplay, isCompleted);
                return;
            }

            lastRenderedCockpitId = devKey;

            pane.innerHTML = `
                <div class="cockpit-card">
                    <div class="cockpit-view-header">
                        <div class="cockpit-title-group">
                            <h2><span>⚡</span> ${escapeHtml(devTitle)}</h2>
                            <div class="cockpit-title-sub">
                                Host: <strong>${escapeHtml(d.computer_name || devKey)}</strong> | ${escapeHtml(serial)} | IP: ${escapeHtml(d.client_ip || 'Lokalno')}
                            </div>
                        </div>
                        <div style="display:flex; gap:8px; align-items:center;">
                            <span id="cockpit-duration-badge" class="header-badge" style="color:var(--accent); border-color:var(--accent);">⏱️ ${escapeHtml(d.duration_formatted || 'U toku')}</span>
                            <span id="cockpit-status-pill" class="status-pill status-active">${escapeHtml(statusLabel)}</span>
                        </div>
                    </div>

                    <!-- 1 Hz Ogledalo Ekrana Radne Stanice -->
                    <div class="screen-mirror-frame">
                        <div class="screen-mirror-topbar">
                            <span style="display:flex; align-items:center; gap:8px;">
                                <span class="pulse-dot"></span>
                                <strong>1 Hz OGLEDALO EKRANA RADNE STANICE</strong>
                            </span>
                            <span id="screen-mirror-badge" class="header-badge" style="color:${isScreenStreamActive ? 'var(--success)' : 'var(--accent)'}; border-color:${isScreenStreamActive ? 'var(--success)' : 'var(--accent)'}; font-weight:600; font-size:0.75rem;">
                                ${isScreenStreamActive ? '🟢 1 Hz LIVE' : 'Status: Čeka se klijentski strim'}
                            </span>
                        </div>
                        <div class="screen-mirror-viewport" id="screen-mirror-viewport">
                            <!-- a) Active Live Feed -->
                            <div id="mirror-live-container" class="mirror-live-container" style="display: ${isScreenStreamActive ? 'flex' : 'none'};">
                                <div class="mirror-live-img-box">
                                    <img id="cockpit-screen-img" class="cockpit-screen-img" src="${isScreenStreamActive ? `/api/devices/${encodeURIComponent(devKey)}/screen-frame.jpg?t=${Date.now()}` : ''}" alt="1 Hz Live Screen Mirror" />
                                    <div class="mirror-live-overlay-footer">
                                        <span class="mirror-live-stream-label">
                                            <span class="pulse-dot"></span> 1 Hz LIVE STREAM
                                        </span>
                                        <span id="mirror-live-res" class="mirror-live-res-label">1920×1080</span>
                                        <span id="mirror-live-timestamp" class="mirror-live-ts">1 Hz SYNC</span>
                                    </div>
                                </div>
                            </div>

                            <!-- b) Standby Režim -->
                            <div id="mirror-standby-container" class="mirror-standby-container" style="display: ${isScreenStreamActive ? 'none' : 'flex'};">
                                <div class="mirror-content-top">
                                    <span style="color:var(--accent); font-weight:700;">[TK-UEFI] ${escapeHtml(d.computer_name || devKey)}</span>
                                    <span id="cockpit-phase-label" style="color:var(--success); font-weight:700;">${escapeHtml(currentPhaseName)}</span>
                                </div>

                                <div class="mirror-content-mid" style="text-align:center; padding: 18px 14px;">
                                    <div style="font-size:0.92rem; color:var(--accent); font-weight:600; margin-bottom:8px; line-height:1.4;">
                                        Povezano na uređaj. Čeka se klijentski video/skrinšot strim sa radne stanice (Pogledaj: docs/BACKEND_IMPLEMENTATION_ROADMAP.md)
                                    </div>
                                    <div style="font-size:0.85rem; color:#f8fafc; font-weight:600; display:flex; align-items:center; justify-content:center; gap:8px; margin-top:8px;">
                                        <span style="color:var(--accent);">▶</span>
                                        <span id="cockpit-live-status">${escapeHtml(liveStatus)}</span>
                                    </div>
                                    <div class="progress-bar-bg" style="height:8px; margin: 10px auto; max-width: 480px;">
                                        <div class="progress-bar-fill" id="cockpit-prog-bar" style="width: ${prog}%"></div>
                                    </div>
                                    <div style="display:flex; justify-content:center; gap:24px; font-size:0.75rem; color:#94a3b8; flex-wrap:wrap;">
                                        <span>Napredak faze: <strong id="cockpit-prog-num" style="color:#fff;">${prog}%</strong></span>
                                        <span>Aplikacije: <strong id="cockpit-apps-summary" style="color:var(--accent);">${escapeHtml(appSummaryText)}</strong></span>
                                    </div>
                                </div>

                                <div class="mirror-content-bottom">
                                    <span>LAN Discovery: ${escapeHtml(d.client_ip || 'LAN')} | Keš: ZFS Aktivan</span>
                                    <span id="live-mirror-time" style="color:var(--accent); font-weight:700;">1 Hz TELEMETRY</span>
                                </div>
                            </div>
                        </div>
                    </div>

                    <!-- Hardverske Metrike & Instrumenti (Strict No-Mock) -->
                    <div class="telemetry-gauges-grid">
                        <div class="gauge-card">
                            <div class="gauge-header">
                                <span>CPU Procesor</span>
                                <span id="cockpit-gauge-cpu-pct" style="color:var(--accent); font-size:0.75rem; font-weight:700;">${escapeHtml(cpuLoadDisplay)}</span>
                            </div>
                            <div class="gauge-value" id="cockpit-gauge-cpu-name" style="font-size:0.92rem; overflow:hidden; text-overflow:ellipsis; white-space:nowrap;" title="${escapeHtml(cpuName)}">${escapeHtml(cpuName)}</div>
                            <div class="gauge-bar-bg">
                                <div class="gauge-bar-fill" id="cockpit-gauge-cpu-bar" style="width: ${cpuBarWidth}; background:var(--accent);"></div>
                            </div>
                        </div>

                        <div class="gauge-card">
                            <div class="gauge-header">
                                <span>Termika CPU</span>
                                <span id="cockpit-gauge-temp-status" style="color:${rawTemp !== null ? (rawTemp > 80 ? 'var(--danger)' : 'var(--green)') : 'var(--text-muted)'}; font-size:0.72rem;">${escapeHtml(tempStatus)}</span>
                            </div>
                            <div class="gauge-value" id="cockpit-gauge-temp-val" style="font-size:1.15rem; color:${rawTemp !== null ? (rawTemp > 80 ? 'var(--danger)' : 'var(--green)') : 'var(--text-secondary)'};">${tempDisplay}</div>
                            <div class="gauge-bar-bg">
                                <div class="gauge-bar-fill" id="cockpit-gauge-temp-bar" style="width: ${tempBarWidth}; background:${tempBarColor};"></div>
                            </div>
                        </div>

                        <div class="gauge-card">
                            <div class="gauge-header">
                                <span>Radna Memorija</span>
                                <span id="cockpit-gauge-ram-pct" style="color:var(--accent); font-size:0.75rem; font-weight:700;">${escapeHtml(ramPctDisplay)}</span>
                            </div>
                            <div class="gauge-value" id="cockpit-gauge-ram-val" style="font-size:0.95rem;">${escapeHtml(ramUsageText)}</div>
                            <div class="gauge-bar-bg">
                                <div class="gauge-bar-fill" id="cockpit-gauge-ram-bar" style="width: ${ramBarWidth}; background:var(--accent);"></div>
                            </div>
                        </div>

                        <div class="gauge-card">
                            <div class="gauge-header">
                                <span>NVMe SMART Zdravlje</span>
                                <span id="cockpit-gauge-smart-cat" style="color:${ssdPct !== null && ssdPct < 50 ? 'var(--danger)' : 'var(--success)'}; font-size:0.72rem;">${escapeHtml(smartHeaderSub)}</span>
                            </div>
                            <div class="gauge-value" id="cockpit-gauge-smart-val" style="color:${ssdPct !== null && ssdPct < 50 ? 'var(--danger)' : 'var(--success)'}; font-size:1.0rem;">${escapeHtml(smartDisplay)}</div>
                            <div class="gauge-bar-bg">
                                <div class="gauge-bar-fill" id="cockpit-gauge-smart-bar" style="width: ${smartBarWidth}; background:${smartBarColor};"></div>
                            </div>
                        </div>

                        <div class="gauge-card">
                            <div class="gauge-header">
                                <span>OEM Garancija & Drajveri</span>
                                <span id="cockpit-gauge-oem-drv" style="color:var(--purple);">${drvDisplay}</span>
                            </div>
                            <div class="gauge-value" id="cockpit-gauge-oem-val" style="font-size:0.95rem; color:var(--text-primary);">${wLabel}</div>
                            <div class="gauge-bar-bg">
                                <div class="gauge-bar-fill" style="width: 100%; background:var(--purple);"></div>
                            </div>
                        </div>
                    </div>

                    <!-- Brze Radnje (Quick Action Toolbar) -->
                    <div class="cockpit-action-toolbar">
                        <button class="btn-cockpit btn-log" onclick="openLogModal('${escapeHtml(devKey)}', '${escapeHtml(d.computer_name || devKey)}', 'Faza ${phase}')">
                            <span>📟</span> Log Uživo (SSE)
                        </button>
                        <button class="btn-cockpit btn-history" onclick="openHistoryModal('${escapeHtml(d.device_tag || devKey)}', '${escapeHtml(d.computer_name || devKey)}')">
                            <span>📜</span> Istorija / Dnevnik
                        </button>
                        ${isCompleted ? `
                        <button class="btn-cockpit btn-report" onclick="openReportModal('${escapeHtml(devKey)}', '${escapeHtml(d.computer_name || devKey)}')">
                            <span>📋</span> Servisni Karton
                        </button>` : ''}
                        <button class="btn-cockpit btn-warning" onclick="sendDeviceCommand('${escapeHtml(devKey)}', 'reboot_uefi', 'Restart u UEFI/BIOS')">
                            <span>⚡</span> Restart u UEFI/BIOS
                        </button>
                        <button class="btn-cockpit btn-secondary" onclick="sendDeviceCommand('${escapeHtml(devKey)}', 'reboot_pxe', 'Mrežni But (PXE)')">
                            <span>🔄</span> Mrežni But (PXE)
                        </button>
                        <a href="/api/devices/${encodeURIComponent(devKey)}/download" target="_blank" class="btn-cockpit btn-refresh" style="margin-left:auto;">
                            <span>💾</span> Preuzmi Log
                        </a>
                    </div>
                </div>
            `;
        }

        // =====================================================================
        // TAB 2: WIS Centar (ISO & USB Builder)
        // =====================================================================
        function loadWisIsoInfo() {
            const badge = document.getElementById('wis-iso-status-badge');
            badge.textContent = "Učitavanje...";
            badge.style.color = "var(--text-muted)";
            badge.style.borderColor = "var(--border)";

            fetch('/api/wis/iso/info')
                .then(res => res.json())
                .then(info => {
                    const dlBtn = document.getElementById('btn-download-iso');
                    if (info.available) {
                        badge.textContent = "✓ Dostupno";
                        badge.style.color = "var(--green)";
                        badge.style.borderColor = "var(--green)";
                        dlBtn.classList.remove('disabled');
                        dlBtn.removeAttribute('disabled');
                        dlBtn.style.pointerEvents = "auto";
                        dlBtn.style.opacity = "1";
                    } else {
                        badge.textContent = "❌ Nije pronađeno";
                        badge.style.color = "var(--red)";
                        badge.style.borderColor = "var(--red)";
                        dlBtn.classList.add('disabled');
                        dlBtn.setAttribute('disabled', 'true');
                        dlBtn.style.pointerEvents = "none";
                        dlBtn.style.opacity = "0.5";
                    }

                    document.getElementById('wis-iso-filename').textContent = info.filename || 'N/A';
                    document.getElementById('wis-iso-version').textContent = info.version ? `v${info.version}` : 'N/A';

                    let sizeText = '-';
                    if (info.size_mb) {
                        sizeText = info.size_mb >= 1024 ?
                            `${(info.size_mb / 1024).toFixed(2)} GB (${info.size_mb} MB)` :
                            `${info.size_mb} MB`;
                    }
                    document.getElementById('wis-iso-size').textContent = sizeText;
                    document.getElementById('wis-iso-modified').textContent = info.modified_at ? info.modified_at.substring(0, 19).replace('T', ' ') : 'N/A';
                    document.getElementById('wis-iso-sha256').textContent = info.sha256 || 'Nije izračunato';
                    document.getElementById('wis-iso-path').textContent = info.path || 'N/A';
                })
                .catch(err => {
                    console.error("Greška pri dohvatanju WIS ISO info:", err);
                    badge.textContent = "Greška";
                    badge.style.color = "var(--red)";
                });
        }

        function loadWisUsbList() {
            const select = document.getElementById('wis-usb-select');
            select.innerHTML = '<option value="">-- Tražim priključene USB diskove... --</option>';

            fetch('/api/wis/usb/list')
                .then(res => res.json())
                .then(data => {
                    const devices = data.devices || [];
                    select.innerHTML = '';

                    if (devices.length === 0) {
                        select.innerHTML = '<option value="">-- Nema pronađenih USB diskova u sistemu --</option>';
                        validateUsbForm();
                        return;
                    }

                    const defaultOpt = document.createElement('option');
                    defaultOpt.value = '';
                    defaultOpt.textContent = '-- Izaberite USB disk sa liste --';
                    select.appendChild(defaultOpt);

                    devices.forEach(dev => {
                        const opt = document.createElement('option');
                        opt.value = dev.device_path;
                        let sizeLabel = dev.size_formatted || (dev.size_bytes ? formatBytes(dev.size_bytes) : '');
                        let title = `${dev.model || 'USB Medij'} (${dev.device_path}) - ${sizeLabel}`;

                        if (!dev.is_safe) {
                            title += ` [Blokirano: ${dev.blocked_reason || 'Nije bezbedno'}]`;
                            opt.disabled = true;
                        }
                        opt.textContent = title;
                        opt.dataset.safe = dev.is_safe ? "true" : "false";
                        select.appendChild(opt);
                    });

                    validateUsbForm();
                })
                .catch(err => {
                    console.error("Greška pri dohvatanju USB liste:", err);
                    select.innerHTML = '<option value="">Greška pri očitavanju USB diskova</option>';
                    validateUsbForm();
                });
        }

        function validateUsbForm() {
            const select = document.getElementById('wis-usb-select');
            const confirmWipe = document.getElementById('wis-confirm-wipe');
            const burnBtn = document.getElementById('btn-burn-usb');

            const selectedOpt = select.options[select.selectedIndex];
            const isDeviceSelected = select.value && selectedOpt && selectedOpt.dataset.safe === "true";
            const isConfirmed = confirmWipe.checked;

            if (isDeviceSelected && isConfirmed) {
                burnBtn.removeAttribute('disabled');
            } else {
                burnBtn.setAttribute('disabled', 'true');
            }
        }

        function startUsbBurn() {
            const select = document.getElementById('wis-usb-select');
            const devPath = select.value;
            if (!devPath) return;

            const confirmMsg = `DA LI STE SIGURNI?\n\nPokrećete formatiranje i pripremu diska: ${devPath}\nSVI PODACI SA OVOG DISKA BIĆE NEPOVRATNO OBRISANI!\n\nNastaviti?`;
            if (!confirm(confirmMsg)) {
                return;
            }

            const burnBtn = document.getElementById('btn-burn-usb');
            const confirmCheckbox = document.getElementById('wis-confirm-wipe');
            burnBtn.setAttribute('disabled', 'true');
            select.setAttribute('disabled', 'true');
            confirmCheckbox.setAttribute('disabled', 'true');

            const progBox = document.getElementById('wis-burn-progress-box');
            progBox.style.display = 'flex';
            document.getElementById('wis-burn-phase').textContent = "Pokretanje...";
            document.getElementById('wis-burn-pct').textContent = "0%";
            document.getElementById('wis-burn-bar-fill').style.width = "0%";
            document.getElementById('wis-burn-message').textContent = `Slanje komande za narezivanje na ${devPath}...`;

            adminFetch('/api/wis/usb/burn', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({
                    device_path: devPath,
                    confirm_wipe: true
                })
            })
            .then(res => {
                if (!res.ok) {
                    return res.json().then(j => { throw new Error(j.error || "HTTP " + res.status); });
                }
                return res.json();
            })
            .then(data => {
                connectWisBurnStream();
            })
            .catch(err => {
                document.getElementById('wis-burn-phase').textContent = "Greška";
                document.getElementById('wis-burn-phase').style.color = "var(--red)";
                document.getElementById('wis-burn-message').textContent = `Greška pri pokretanju: ${err.message}`;
                burnBtn.removeAttribute('disabled');
                select.removeAttribute('disabled');
                confirmCheckbox.removeAttribute('disabled');
            });
        }

        function connectWisBurnStream() {
            if (wisBurnEventSource) {
                wisBurnEventSource.close();
            }

            wisBurnEventSource = new EventSource('/api/wis/usb/progress');
            wisBurnEventSource.onmessage = function(e) {
                try {
                    const data = JSON.parse(e.data);
                    updateWisBurnProgress(data);
                    if (data.phase === 'completed' || data.phase === 'error') {
                        wisBurnEventSource.close();
                        wisBurnEventSource = null;
                        document.getElementById('wis-usb-select').removeAttribute('disabled');
                        document.getElementById('wis-confirm-wipe').removeAttribute('disabled');
                        validateUsbForm();
                        loadWisUsbList();
                    }
                } catch (err) {
                    console.error("Greška pri obradi SSE poruke napretka:", err);
                }
            };

            wisBurnEventSource.onerror = function() {
                document.getElementById('wis-burn-message').textContent = "Veza sa serverom za napredak je zatvorena ili prekinuta.";
            };
        }

        function updateWisBurnProgress(data) {
            const phaseEl = document.getElementById('wis-burn-phase');
            const pctEl = document.getElementById('wis-burn-pct');
            const barEl = document.getElementById('wis-burn-bar-fill');
            const msgEl = document.getElementById('wis-burn-message');

            const pct = data.percent !== undefined ? data.percent : 0;
            pctEl.textContent = `${pct}%`;
            barEl.style.width = `${pct}%`;
            msgEl.textContent = data.message || `Faza: ${data.phase}`;

            if (data.phase === 'completed') {
                phaseEl.textContent = "Završeno";
                phaseEl.style.color = "var(--green)";
                barEl.style.background = "var(--green)";
            } else if (data.phase === 'error') {
                phaseEl.textContent = "Greška";
                phaseEl.style.color = "var(--red)";
                barEl.style.background = "var(--red)";
            } else {
                phaseEl.textContent = data.phase.toUpperCase();
                phaseEl.style.color = "var(--cyan)";
                barEl.style.background = "linear-gradient(90deg, var(--cyan), var(--magenta))";
            }
        }

        // =====================================================================
        // TAB 3: Windows 11 Zakrpe
        // =====================================================================
        function loadUpdatesStatus() {
            fetch('/api/updates/status')
                .then(res => res.json())
                .then(data => {
                    const statusText = document.getElementById('updates-status-text');
                    const zfsBadge = document.getElementById('updates-zfs-badge');
                    const dirText = document.getElementById('updates-directory-text');
                    const countEl = document.getElementById('updates-total-count');
                    const sizeEl = document.getElementById('updates-total-size');
                    const kbContainer = document.getElementById('updates-kb-list');

                    dirText.textContent = data.directory || '/home/gru/Storage/Updates/Win11';
                    countEl.textContent = data.total_packages || 0;
                    sizeEl.textContent = data.total_size_mb ? `${data.total_size_mb} MB` : '0 MB';

                    if (data.available) {
                        statusText.textContent = "Dostupno";
                        statusText.style.color = "var(--green)";
                        zfsBadge.style.display = "inline-flex";
                    } else {
                        statusText.textContent = "Nedostupno";
                        statusText.style.color = "var(--red)";
                        zfsBadge.style.display = "none";
                    }

                    const kbList = data.kb_list || [];
                    if (kbList.length === 0) {
                        kbContainer.innerHTML = '<span style="color:var(--text-muted); font-size:0.8rem;">Nema prepoznatih KB</span>';
                    } else {
                        kbContainer.innerHTML = kbList.map(kb => `<span class="kb-pill">${escapeHtml(kb)}</span>`).join('');
                    }

                    updatesCachedPackages = data.packages || [];
                    document.getElementById('updates-table-count').textContent = `${updatesCachedPackages.length} paketa`;
                    filterUpdatesTable();
                })
                .catch(err => {
                    console.error("Greška pri očitavanju statusa zakrpi:", err);
                });
        }

        function filterUpdatesTable() {
            const query = (document.getElementById('updates-search').value || '').trim().toLowerCase();
            const tbody = document.getElementById('updates-table-body');

            const filtered = updatesCachedPackages.filter(p => {
                if (!query) return true;
                const fn = (p.filename || '').toLowerCase();
                const kb = (p.kb || '').toLowerCase();
                return fn.includes(query) || kb.includes(query);
            });

            if (filtered.length === 0) {
                tbody.innerHTML = `
                    <tr>
                        <td colspan="5" style="text-align:center; padding:30px; color:var(--text-muted);">
                            ${updatesCachedPackages.length === 0 ? 'Nema pronađenih .msu / .cab paketa u skladištu.' : 'Nema paketa koji odgovaraju pretrazi.'}
                        </td>
                    </tr>`;
                return;
            }

            let html = '';
            filtered.forEach(p => {
                const typeClass = p.file_type === 'msu' ? 'type-msu' : 'type-cab';
                const kbPill = p.kb ? `<span class="kb-pill">${escapeHtml(p.kb)}</span>` : '<span style="color:var(--text-muted);">-</span>';
                const modDate = p.modified_at ? p.modified_at.substring(0, 19).replace('T', ' ') : 'N/A';

                html += `
                    <tr>
                        <td><span class="type-pill ${typeClass}">${escapeHtml(p.file_type)}</span></td>
                        <td>${kbPill}</td>
                        <td style="font-family:var(--font-mono); font-weight:600; color:#fff;" title="${escapeHtml(p.path || '')}">${escapeHtml(p.filename)}</td>
                        <td>${p.size_mb} MB</td>
                        <td style="color:var(--text-muted); font-size:0.8rem;">${escapeHtml(modDate)}</td>
                    </tr>`;
            });

            tbody.innerHTML = html;
        }

        // =====================================================================
        // Modali (Log Uživo, Servisni Karton, Istorija & Dnevnik)
        // =====================================================================
        function openLogModal(deviceId, compName, phaseLabel) {
            currentModalDevice = deviceId;
            document.getElementById('modal-client-name').textContent = compName;
            document.getElementById('modal-phase-badge').textContent = phaseLabel;
            const term = document.getElementById('terminal-content');
            term.innerHTML = '<div class="log-line DEBUG">Učitavam postojeće logove...</div>';
            document.getElementById('log-modal').classList.add('open');

            fetch(`/api/devices/${encodeURIComponent(deviceId)}/logs`)
                .then(res => res.json())
                .then(lines => {
                    term.innerHTML = '';
                    lines.forEach(l => appendLogLine(l));
                    term.scrollTop = term.scrollHeight;
                });

            if (eventSource) { eventSource.close(); }
            eventSource = new EventSource(`/api/stream/${encodeURIComponent(deviceId)}`);
            eventSource.onmessage = function(e) {
                appendLogLine(e.data);
                term.scrollTop = term.scrollHeight;
            };

            document.getElementById('btn-download-log').onclick = function() {
                window.open(`/api/devices/${encodeURIComponent(deviceId)}/download`);
            };
        }

        function closeLogModal() {
            document.getElementById('log-modal').classList.remove('open');
            if (eventSource) {
                eventSource.close();
                eventSource = null;
            }
            currentModalDevice = null;
        }

        function openReportModal(deviceId, compName) {
            currentModalDevice = deviceId;
            document.getElementById('report-client-name').textContent = compName;
            const content = document.getElementById('report-modal-content');
            content.innerHTML = '<div class="log-line DEBUG">Učitavam servisni karton...</div>';
            document.getElementById('report-modal').classList.add('open');

            fetch(`/api/devices/${encodeURIComponent(deviceId)}/report`)
                .then(res => {
                    if (!res.ok) throw new Error("Status " + res.status + ": Servisni karton još nije dostupan.");
                    return res.json();
                })
                .then(data => {
                    document.getElementById('report-duration-badge').textContent = `⏱️ ${escapeHtml(data.duration_formatted || 'Završeno')}`;
                    content.innerHTML = renderReportDetails(data);
                })
                .catch(err => {
                    content.innerHTML = `<div class="log-line ERROR" style="color:var(--red); font-size:1rem; padding:10px;">⚠️ ${escapeHtml(err.message)}</div>`;
                });

            document.getElementById('btn-download-report').onclick = function() {
                window.open(`/api/devices/${encodeURIComponent(deviceId)}/report`);
            };
        }

        function closeReportModal() {
            document.getElementById('report-modal').classList.remove('open');
        }

        function renderReportDetails(data) {
            const apps = data.apps || {};
            let appBadges = Object.keys(apps).map(k => {
                const st = apps[k];
                const color = (st === 'Completed' || st === 'OK') ? 'var(--green)' : (st === 'Failed' ? 'var(--red)' : 'var(--yellow)');
                return `<span class="header-badge" style="color:${color}; border-color:${color}; margin: 2px;">${escapeHtml(k)}: ${escapeHtml(st)}</span>`;
            }).join(' ');

            const hw = data.hw || {};
            const ap = data.autopilot || {};
            const bm = data.benchmark || null;
            let bmHtml = '';
            if (bm && typeof bm === 'object') {
                const pd = bm.phase_durations_sec || {};
                const pBadges = Object.keys(pd).map(k => `<span class="tool-pill">${escapeHtml(k)}: ${escapeHtml(pd[k])}s</span>`).join(' ');
                bmHtml = `
                    <div style="background:#131c2d; padding:14px; border-radius:var(--radius-sm); border:1px solid var(--border);">
                        <div style="font-weight:700; color:var(--accent); margin-bottom:8px;">📊 WIS Benchmark Metrike:</div>
                        <div style="display:flex; gap:12px; flex-wrap:wrap; margin-bottom:6px;">
                            <span><strong>Cache Hit Ratio:</strong> ${escapeHtml(bm.cache_hit_ratio_pct ?? 0)}%</span>
                            <span><strong>LAN Throughput:</strong> ${escapeHtml(bm.lan_throughput_mbps ?? 0)} Mbps</span>
                            <span><strong>WAN Throughput:</strong> ${escapeHtml(bm.wan_throughput_mbps ?? 0)} Mbps</span>
                        </div>
                        ${pBadges ? `<div>${pBadges}</div>` : ''}
                    </div>`;
            }

            return `
                <div style="display:flex; flex-direction:column; gap:16px; font-family:var(--font-mono); font-size:0.82rem;">
                    <div style="background:#131c2d; padding:14px; border-radius:var(--radius-sm); border:1px solid var(--border); display:grid; grid-template-columns: repeat(auto-fit, minmax(220px, 1fr)); gap:12px;">
                        <div><strong>💻 Računar:</strong> ${escapeHtml(data.computer_name || 'N/A')}</div>
                        <div><strong>🔢 Serijski broj:</strong> ${escapeHtml(data.serial_number || hw.serial || 'N/A')}</div>
                        <div><strong>⏱️ Trajanje:</strong> ${escapeHtml(data.duration_formatted || 'N/A')} (${data.duration_seconds || 0}s)</div>
                        <div><strong>🏢 Model:</strong> ${escapeHtml(hw.manufacturer || '')} ${escapeHtml(hw.model || '')}</div>
                        <div><strong>🛡️ Autopilot:</strong> ${escapeHtml(ap.status || 'Nepoznato')} ${ap.tenant ? '(' + escapeHtml(ap.tenant) + ')' : ''}</div>
                        <div><strong>📅 Datum servisa:</strong> ${escapeHtml(data.timestamp || 'N/A')}</div>
                    </div>
                    ${bmHtml}
                    ${appBadges ? `
                    <div style="background:#131c2d; padding:14px; border-radius:var(--radius-sm); border:1px solid var(--border);">
                        <div style="font-weight:700; color:var(--accent); margin-bottom:8px;">📦 Status Aplikacija:</div>
                        <div>${appBadges}</div>
                    </div>` : ''}
                    <div style="background:#131c2d; padding:14px; border-radius:var(--radius-sm); border:1px solid var(--border);">
                        <div style="font-weight:700; color:var(--accent); margin-bottom:8px;">📄 Arhivirani Servisni Karton (JSON):</div>
                        <pre style="background:#090e17; padding:12px; border-radius:4px; overflow-x:auto; max-height:350px; color:#d1d5db; font-size:0.8rem;">${escapeHtml(JSON.stringify(data, null, 2))}</pre>
                    </div>
                </div>
            `;
        }

        function openHistoryModal(deviceId, compName) {
            currentModalDevice = deviceId;
            document.getElementById('history-client-name').textContent = compName;
            const content = document.getElementById('history-modal-content');
            content.innerHTML = '<div class="log-line DEBUG">Učitavam servisnu istoriju i dnevnik intervencija...</div>';
            document.getElementById('history-modal').classList.add('open');

            fetch(`/api/devices/${encodeURIComponent(deviceId)}/history`)
                .then(res => {
                    if (!res.ok) throw new Error("HTTP " + res.status + " pri preuzimanju istorije.");
                    return res.json();
                })
                .then(data => {
                    renderHistoryDetails(data, compName);
                })
                .catch(err => {
                    content.innerHTML = `<div class="log-line ERROR" style="color:var(--red); font-size:1rem; padding:10px;">⚠️ Greška: ${escapeHtml(err.message)}</div>`;
                });
        }

        function closeHistoryModal() {
            document.getElementById('history-modal').classList.remove('open');
        }

        function renderHistoryDetails(data, compName) {
            const content = document.getElementById('history-modal-content');
            const interventions = data.interventions || [];
            const total = data.total_interventions !== undefined ? data.total_interventions : interventions.length;
            document.getElementById('history-total-badge').textContent = `${total} Intervencija`;

            const isReg = data.is_registered;
            const regBadge = isReg ?
                '<span class="header-badge" style="color:var(--green); border-color:var(--green);">✓ Registrovan u bazi</span>' :
                '<span class="header-badge" style="color:var(--text-muted); border-color:var(--border);">Neregistrovan</span>';

            let metaHtml = `
                <div class="history-meta-grid">
                    <div><strong>💻 Oznaka:</strong> ${escapeHtml(data.device_tag || compName)}</div>
                    <div><strong>🔢 Serijski broj:</strong> ${escapeHtml(data.serial_number || 'N/A')}</div>
                    <div><strong>🏢 Model:</strong> ${escapeHtml((data.manufacturer || '') + ' ' + (data.model || '')).trim() || 'N/A'}</div>
                    <div><strong>📅 Prvi put viđen:</strong> ${escapeHtml(data.first_seen ? data.first_seen.substring(0, 19).replace('T', ' ') : 'N/A')}</div>
                    <div><strong>⏱️ Poslednji servis:</strong> ${escapeHtml(data.last_seen ? data.last_seen.substring(0, 19).replace('T', ' ') : 'N/A')}</div>
                    <div><strong>🛡️ Baza:</strong> ${regBadge}</div>
                </div>
            `;

            let interventionsHtml = '<div style="margin-top:16px;">';
            interventionsHtml += `<h4 style="color:var(--purple); margin-bottom:10px; display:flex; align-items:center; gap:8px;"><span>🛠️</span> Prethodne Servisne Intervencije (${interventions.length}):</h4>`;

            if (interventions.length === 0) {
                interventionsHtml += '<div style="color:var(--text-muted); font-size:0.82rem; padding:12px; background:#131c2d; border-radius:var(--radius-sm); border:1px solid var(--border);">Nema zabeleženih pojedinačnih intervencija za ovaj računar.</div>';
            } else {
                interventionsHtml += '<div class="interventions-list">';
                interventions.slice().reverse().forEach(item => {
                    const st = String(item.status || 'COMPLETED').toUpperCase();
                    const isCritical = (st === 'CRITICAL_ISSUE' || st === 'ERROR' || st === 'FAILED');
                    const stColor = isCritical ? 'var(--red)' : 'var(--green)';
                    const stIcon = isCritical ? '🚨' : '✅';
                    const tools = (item.tools_used || []).map(t => `<span class="tool-pill">${escapeHtml(t)}</span>`).join('');

                    interventionsHtml += `
                        <div class="intervention-card" style="border-left: 3px solid ${stColor};">
                            <div class="intervention-header">
                                <span style="font-weight:700; color:#fff;">${stIcon} ${escapeHtml(item.date || 'Datum N/A')}</span>
                                <span class="header-badge" style="color:${stColor}; border-color:${stColor};">${escapeHtml(st)}</span>
                            </div>
                            <div class="intervention-summary">${escapeHtml(item.summary || 'Nema sažetka')}</div>
                            ${item.serviser_id ? `<div style="font-size:0.75rem; color:var(--text-muted); margin-top:4px;">👤 Serviser: <strong>${escapeHtml(item.serviser_id)}</strong></div>` : ''}
                            ${tools ? `<div style="margin-top:6px;">${tools}</div>` : ''}
                        </div>`;
                });
                interventionsHtml += '</div>';
            }
            interventionsHtml += '</div>';

            let dnevnikHtml = `
                <div style="margin-top:16px;">
                    <h4 style="color:var(--accent); margin-bottom:10px; display:flex; align-items:center; gap:8px;"><span>📄</span> Servisni Dnevnik (SERVISNI_DNEVNIK.md):</h4>
                    <div class="dnevnik-markdown-viewer">
                        ${renderMarkdown(data.dnevnik_markdown || '')}
                    </div>
                </div>`;

            content.innerHTML = `
                <div style="display:flex; flex-direction:column; gap:12px;">
                    ${metaHtml}
                    ${interventionsHtml}
                    ${dnevnikHtml}
                </div>`;
        }

        function appendLogLine(text) {
            const term = document.getElementById('terminal-content');
            if (!term) return;
            const div = document.createElement('div');
            div.className = 'log-line';
            if (text.includes('[SUCCESS]')) div.classList.add('SUCCESS');
            else if (text.includes('[WARN]')) div.classList.add('WARN');
            else if (text.includes('[ERROR]') || text.includes('[CRITICAL]')) div.classList.add('ERROR');
            else if (text.includes('[STEP]')) div.classList.add('STEP');
            else if (text.includes('[DEBUG]')) div.classList.add('DEBUG');
            div.textContent = text;
            term.appendChild(div);
        }

        // =====================================================================
        // Daljinsko Upravljanje Radnim Stanicama (Remote BIOS / UEFI Reboot)
        // =====================================================================
        let pendingCommandTarget = null;

        function showToast(message, type = 'success', duration = 5000) {
            let container = document.getElementById('toast-container');
            if (!container) {
                container = document.createElement('div');
                container.id = 'toast-container';
                container.className = 'toast-container';
                document.body.appendChild(container);
            }
            const toast = document.createElement('div');
            toast.className = `toast toast-${type}`;
            let icon = 'ℹ️';
            if (type === 'success') icon = '✅';
            else if (type === 'error') icon = '❌';
            else if (type === 'warning') icon = '⚠️';

            toast.innerHTML = `<span style="font-size:1.1rem;">${icon}</span><span style="flex:1;">${escapeHtml(message)}</span>`;
            container.appendChild(toast);

            setTimeout(() => {
                toast.style.opacity = '0';
                toast.style.transform = 'translateX(100%)';
                setTimeout(() => toast.remove(), 300);
            }, duration);
        }

        function sendDeviceCommand(devKey, action, actionLabel) {
            pendingCommandTarget = { devKey: devKey, action: action, actionLabel: actionLabel };
            const confirmMsg = `Da li ste sigurni da želite da pošaljete komandu '${actionLabel}' na radnu stanicu '${devKey}'?`;
            const modal = document.getElementById('command-confirm-modal');
            const msgEl = document.getElementById('command-confirm-message');
            if (msgEl) {
                msgEl.innerHTML = `Da li ste sigurni da želite da pošaljete komandu '<strong>${escapeHtml(actionLabel)}</strong>' na radnu stanicu '<strong>${escapeHtml(devKey)}</strong>'?`;
            }
            if (modal) {
                modal.classList.add('open');
            } else {
                if (confirm(confirmMsg)) {
                    executeConfirmedCommand();
                }
            }
        }

        function closeCommandConfirmModal() {
            pendingCommandTarget = null;
            const modal = document.getElementById('command-confirm-modal');
            if (modal) modal.classList.remove('open');
        }

        function executeConfirmedCommand() {
            if (!pendingCommandTarget) return;
            const devKey = pendingCommandTarget.devKey;
            const action = pendingCommandTarget.action;
            const actionLabel = pendingCommandTarget.actionLabel;
            closeCommandConfirmModal();

            adminFetch('/api/devices/' + encodeURIComponent(devKey) + '/command', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ action: action, params: {} })
            })
            .then(res => {
                if (!res.ok) {
                    return res.json().then(data => {
                        throw new Error(data.error || ("HTTP " + res.status));
                    }).catch(e => {
                        throw new Error(e.message || ("HTTP " + res.status));
                    });
                }
                return res.json();
            })
            .then(data => {
                showToast(`✅ Komanda [${actionLabel}] uspešno zadata. Biće izvršena pri sledećem heartbeat-u.`, 'success');
            })
            .catch(err => {
                showToast(`❌ Greška pri slanju komande [${actionLabel}]: ${err.message}`, 'error');
            });
        }

        // =====================================================================
        // WIS Benchmark & Predictive Cache (Pre-Arrival Pre-Warming)
        // =====================================================================
        function loadBenchmarkSummary() {
            fetch('/api/benchmark/summary')
                .then(res => res.json())
                .then(data => {
                    const srEl = document.getElementById('bm-success-rate');
                    const cntEl = document.getElementById('bm-install-counts');
                    const hrEl = document.getElementById('bm-cache-hit-ratio');
                    const svEl = document.getElementById('bm-cache-saved');
                    const lanEl = document.getElementById('bm-lan-throughput');
                    const wanEl = document.getElementById('bm-wan-throughput');
                    const avgEl = document.getElementById('bm-avg-duration');
                    const pctEl = document.getElementById('bm-percentiles');
                    const bnEl = document.getElementById('bm-bottleneck-badge');
                    const phGrid = document.getElementById('bm-phases-grid');

                    const cm = data.cache_metrics || {};
                    if (srEl) srEl.textContent = `${data.success_rate_pct !== null && data.success_rate_pct !== undefined ? data.success_rate_pct : 0}%`;
                    if (cntEl) cntEl.textContent = `${data.successful_installations || 0} / ${data.total_installations || 0} uspešnih`;
                    if (hrEl) hrEl.textContent = (cm.cache_hit_ratio_pct === null || cm.cache_hit_ratio_pct === undefined) ? 'N/A' : `${cm.cache_hit_ratio_pct}%`;
                    if (svEl) svEl.textContent = `Ušteđeno ${cm.saved_wan_mb || 0} MB WAN (${cm.total_hits || 0} hit / ${cm.total_misses || 0} miss)`;
                    if (lanEl) lanEl.textContent = `${cm.avg_lan_throughput_mbps || 0} Mbps`;
                    if (wanEl) wanEl.textContent = `WAN: ${cm.avg_wan_throughput_mbps || 0} Mbps`;
                    if (avgEl) avgEl.textContent = `${data.avg_duration_seconds || 0}s`;
                    if (pctEl) pctEl.textContent = `P50: ${data.p50_duration_seconds || 0}s | P90: ${data.p90_duration_seconds || 0}s`;
                    if (bnEl) bnEl.textContent = `Usko grlo: ${data.bottleneck_phase || 'N/A'}`;

                    if (phGrid && data.phase_averages_sec) {
                        const pa = data.phase_averages_sec;
                        const phaseNames = {
                            Phase1: 'Faza 1 (Mreža)',
                            Phase2: 'Faza 2 (Drajveri)',
                            Phase3: 'Faza 3 (Optimiz.)',
                            Phase4: 'Faza 4 (Programi)',
                            Phase5: 'Faza 5 (Stres)',
                            Phase6: 'Faza 6 (Završetak)'
                        };
                        phGrid.innerHTML = Object.keys(phaseNames).map(pk => `
                            <div class="gauge-card">
                                <div class="gauge-header">${escapeHtml(phaseNames[pk])}</div>
                                <div class="gauge-value" style="${pk === data.bottleneck_phase ? 'color:var(--warning);' : ''}">${pa[pk] || 0}s</div>
                            </div>
                        `).join('');
                    }
                })
                .catch(err => console.error("Greška pri učitavanju benchmark statistike:", err));
        }

        function loadPredictiveStatus() {
            fetch('/api/predictive/status')
                .then(res => res.json())
                .then(data => {
                    const models = data.known_models || [];
                    const jobs = data.jobs || [];
                    const mBadge = document.getElementById('pw-models-count');
                    const jBadge = document.getElementById('pw-jobs-count');
                    const mBody = document.getElementById('pw-dna-models-body');
                    const jBody = document.getElementById('pw-jobs-body');

                    if (mBadge) mBadge.textContent = `${models.length} modela`;
                    if (jBadge) jBadge.textContent = `${jobs.length} zadataka`;

                    if (mBody) {
                        if (models.length === 0) {
                            mBody.innerHTML = '<tr><td colspan="3" style="text-align:center; color:var(--text-muted);">Nema naučenih Model DNA profila.</td></tr>';
                        } else {
                            mBody.innerHTML = models.slice(0, 15).map(m => `
                                <tr>
                                    <td style="font-weight:600; color:#fff;">${escapeHtml((m.manufacturer ? m.manufacturer + ' ' : '') + m.model)}</td>
                                    <td><span class="kb-pill">${m.known_hwids_count || 0} HWID</span></td>
                                    <td>${escapeHtml(m.preferred_os_build || '-')}</td>
                                </tr>
                            `).join('');
                        }
                    }

                    if (jBody) {
                        if (jobs.length === 0) {
                            jBody.innerHTML = '<tr><td colspan="3" style="text-align:center; color:var(--text-muted);">Nema pre-arrival zadataka.</td></tr>';
                        } else {
                            jBody.innerHTML = jobs.slice(0, 15).map(j => `
                                <tr>
                                    <td style="font-weight:600; color:#fff;">${escapeHtml(j.model || j.serial_number || j.sku || 'Uređaj')}</td>
                                    <td><span class="kb-pill">${j.readiness_score_pct || 0}%</span></td>
                                    <td style="font-size:0.78rem;">Keš: ${j.drivers_cached_hits || 0}/${j.drivers_total_hwids || 0} | WU: ${j.updates_available_count || 0}</td>
                                </tr>
                            `).join('');
                        }
                    }
                })
                .catch(err => console.error("Greška pri učitavanju Predictive Cache statusa:", err));
        }

        function runPredictivePrewarm() {
            const mfg = (document.getElementById('pw-manufacturer').value || '').trim();
            const model = (document.getElementById('pw-model').value || '').trim();
            const serial = (document.getElementById('pw-serial').value || '').trim();
            const osBuild = (document.getElementById('pw-os-build').value || '24H2').trim();
            const resBox = document.getElementById('pw-result-box');

            if (!model && !serial) {
                showToast('Unesite bar Model ili Serijski broj uređaja.', 'warning');
                return;
            }

            if (resBox) {
                resBox.style.display = 'block';
                resBox.textContent = 'Pre-warming u toku: provera OEM BOM-a, garancije, Model DNA drajvera i Win11 zakrpi...';
            }

            fetch('/api/predictive/prewarm', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({
                    manufacturer: mfg,
                    model: model,
                    serial_number: serial,
                    os_build: osBuild
                })
            })
            .then(res => res.json().then(d => ({ ok: res.ok, data: d })))
            .then(({ ok, data }) => {
                if (!ok) throw new Error(data.error || 'Greška pri pre-warm zahtevu');
                if (resBox) {
                    resBox.textContent = `[OK] Spremnost: ${data.readiness_score_pct}% | Model: ${data.model || 'N/A'} | HWID Keš: ${data.drivers_cached_hits}/${data.drivers_total_hwids} (preuzeto: ${data.drivers_downloaded}) | Win11 Zakrpe (${data.os_build}): ${data.updates_available_count}`;
                }
                showToast(`✅ Predictive Cache spreman (${data.readiness_score_pct}%) za ${data.model || data.serial_number}`, 'success');
                loadPredictiveStatus();
            })
            .catch(err => {
                if (resBox) resBox.textContent = `[GREŠKA] ${err.message}`;
                showToast(`❌ ${err.message}`, 'error');
            });
        }

        // =====================================================================
        // Inicijalizacija i periodično osvežavanje
        // =====================================================================
        function updateServerVitals() {
            fetch('/api/health')
                .then(res => res.json())
                .then(data => {
                    const upEl = document.getElementById('stat-uptime');
                    if (upEl && data.uptime_sec !== undefined) {
                        const sec = data.uptime_sec;
                        const hours = Math.floor(sec / 3600);
                        const mins = Math.floor((sec % 3600) / 60);
                        const s = sec % 60;
                        let text = '';
                        if (hours > 0) text += `${hours}h `;
                        if (mins > 0 || hours > 0) text += `${mins}m `;
                        text += `${s}s`;
                        upEl.textContent = text;
                    }
                })
                .catch(() => {});
        }

        setInterval(function() {
            if (activeTab === 'fleet') {
                updateFleet();
            }
        }, 2000);

        setInterval(updateServerVitals, 5000);
        updateServerVitals();
        updateFleet();
