"""
servishub.web.dashboard
=======================
Ugrađeni Modern Enterprise Dark-Mode Web Dashboard (HTML5, CSS3, Vanilla JS, SSE).
Implementira Linear/Vercel estetiku, Collapsible Sidebar, Hibridni Cockpit sa 1 Hz ogledalom ekrana
i Zero-Dependency arhitekturu usklađenu sa Google Stitch DESIGN.md i PRD/RFC specifikacijama.
"""

HTML_DASHBOARD = r"""<!DOCTYPE html>
<html lang="sr">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>Tehno-Kutak | ServisHub Centralni Sistem</title>
    <link rel="icon" href="data:image/svg+xml,<svg xmlns=%22http://www.w3.org/2000/svg%22 viewBox=%220 0 100 100%22><text y=%22.9em%22 font-size=%2290%22>⚡</text></svg>">
    <style>
        /* =====================================================================
           Google Stitch Design Tokens & CSS Variables (DESIGN.md)
           ===================================================================== */
        :root {
            /* Palette Tokens */
            --canvas: #0F172A;
            --card: #1E293B;
            --card-hover: #243248;
            --card-subtle: #162032;
            --border: #334155;
            --border-hover: #475569;
            --text-primary: #F8FAFC;
            --text-secondary: #CBD5E1;
            --text-muted: #94A3B8;
            --accent: #38bdf8;
            --accent-glow: rgba(56, 189, 248, 0.25);
            --accent-subtle: rgba(56, 189, 248, 0.08);
            --success: #10B981;
            --success-glow: rgba(16, 185, 129, 0.25);
            --warning: #F59E0B;
            --warning-glow: rgba(245, 158, 11, 0.25);
            --danger: #EF4444;
            --danger-glow: rgba(239, 68, 68, 0.25);
            --purple: #818cf8;
            --magenta: #d946ef;

            /* Tipografija */
            --font-sans: 'Inter', -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif;
            --font-mono: "Cascadia Code", Consolas, "Courier New", monospace;

            /* Radijusi & Tranzicije */
            --radius-sm: 6px;
            --radius-md: 10px;
            --radius-lg: 14px;
            --radius-full: 9999px;
            --transition-fast: 0.15s ease;
            --transition-normal: 0.25s cubic-bezier(0.4, 0, 0.2, 1);

            /* Aliases za kompatibilnost sa postojećim elementima */
            --bg-main: var(--canvas);
            --bg-card: var(--card);
            --bg-card-hover: var(--card-hover);
            --border-color: var(--border);
            --text-main: var(--text-primary);
            --cyan: var(--accent);
            --green: var(--success);
            --yellow: var(--warning);
            --red: var(--danger);
        }

        /* Reset & Osnove */
        * { box-sizing: border-box; margin: 0; padding: 0; }
        html, body {
            height: 100%;
            background-color: var(--canvas);
            color: var(--text-primary);
            font-family: var(--font-sans);
            font-size: 14px;
            line-height: 1.5;
            -webkit-font-smoothing: antialiased;
        }

        /* Prilagođeni Scrollbar */
        ::-webkit-scrollbar { width: 6px; height: 6px; }
        ::-webkit-scrollbar-track { background: var(--canvas); }
        ::-webkit-scrollbar-thumb { background: var(--border); border-radius: var(--radius-full); }
        ::-webkit-scrollbar-thumb:hover { background: var(--border-hover); }

        /* Glavni Layout: Sidebar + Main Content Wrapper */
        .app-layout {
            display: flex;
            min-height: 100vh;
            width: 100%;
            background-color: var(--canvas);
        }

        /* =====================================================================
           Collapsible Levi Sidebar (.sidebar)
           ===================================================================== */
        .sidebar {
            width: 275px;
            min-width: 275px;
            height: 100vh;
            position: sticky;
            top: 0;
            background: var(--card);
            border-right: 1px solid var(--border);
            display: flex;
            flex-direction: column;
            transition: width var(--transition-normal), min-width var(--transition-normal);
            z-index: 1000;
            user-select: none;
        }

        .sidebar.collapsed {
            width: 72px;
            min-width: 72px;
        }

        .sidebar-header {
            padding: 18px 16px;
            border-bottom: 1px solid var(--border);
            display: flex;
            align-items: center;
            justify-content: space-between;
            gap: 8px;
        }

        .sidebar-brand {
            display: flex;
            align-items: center;
            gap: 10px;
            overflow: hidden;
            text-decoration: none;
        }

        .brand-icon {
            font-size: 1.4rem;
            display: flex;
            align-items: center;
            justify-content: center;
            width: 38px;
            height: 38px;
            min-width: 38px;
            background: linear-gradient(135deg, rgba(56, 189, 248, 0.2), rgba(16, 185, 129, 0.1));
            border: 1px solid var(--accent);
            border-radius: var(--radius-md);
            box-shadow: 0 0 12px var(--accent-glow);
        }

        .brand-info {
            display: flex;
            flex-direction: column;
            overflow: hidden;
            white-space: nowrap;
            transition: opacity var(--transition-fast);
        }

        .brand-title {
            font-size: 1rem;
            font-weight: 700;
            color: var(--text-primary);
            letter-spacing: 0.3px;
        }

        .brand-version {
            font-size: 0.72rem;
            font-weight: 600;
            color: var(--accent);
            font-family: var(--font-mono);
        }

        .sidebar-toggle-btn {
            background: var(--card-subtle);
            border: 1px solid var(--border);
            color: var(--text-muted);
            width: 28px;
            height: 28px;
            min-width: 28px;
            border-radius: var(--radius-sm);
            cursor: pointer;
            display: flex;
            align-items: center;
            justify-content: center;
            font-size: 0.75rem;
            transition: all var(--transition-fast);
        }
        .sidebar-toggle-btn:hover {
            color: var(--text-primary);
            border-color: var(--accent);
            background: var(--card-hover);
        }

        .sidebar.collapsed .brand-info {
            display: none;
        }

        /* Navigacija u sidebaru */
        .sidebar-nav {
            flex: 1;
            padding: 16px 10px;
            display: flex;
            flex-direction: column;
            gap: 6px;
            overflow-y: auto;
            overflow-x: hidden;
        }

        .nav-section-title {
            font-size: 0.68rem;
            text-transform: uppercase;
            font-weight: 700;
            letter-spacing: 0.8px;
            color: var(--text-muted);
            padding: 8px 10px 4px 10px;
            white-space: nowrap;
        }
        .sidebar.collapsed .nav-section-title {
            display: none;
        }

        .nav-tab {
            background: transparent;
            color: var(--text-secondary);
            border: 1px solid transparent;
            border-radius: var(--radius-md);
            padding: 10px 14px;
            font-size: 0.88rem;
            font-weight: 600;
            cursor: pointer;
            display: flex;
            align-items: center;
            gap: 12px;
            transition: all var(--transition-fast);
            white-space: nowrap;
            text-align: left;
            width: 100%;
        }

        .nav-tab-icon {
            font-size: 1.15rem;
            min-width: 24px;
            display: flex;
            align-items: center;
            justify-content: center;
        }

        .nav-tab-label {
            flex: 1;
            overflow: hidden;
            text-overflow: ellipsis;
        }

        .sidebar.collapsed .nav-tab-label {
            display: none;
        }
        .sidebar.collapsed .nav-tab {
            justify-content: center;
            padding: 12px;
        }

        .nav-tab:hover {
            color: var(--text-primary);
            background: var(--card-hover);
            border-color: rgba(255, 255, 255, 0.05);
        }

        .nav-tab.active {
            color: var(--accent);
            background: var(--accent-subtle);
            border-color: rgba(56, 189, 248, 0.35);
            box-shadow: inset 0 0 12px rgba(56, 189, 248, 0.08);
        }

        /* Brzi Filteri Flote u sidebaru */
        .sidebar-subnav {
            margin-top: 14px;
            padding-top: 14px;
            border-top: 1px solid var(--border);
            display: flex;
            flex-direction: column;
            gap: 4px;
        }
        .sidebar.collapsed .sidebar-subnav {
            display: none;
        }

        .filter-chip {
            background: transparent;
            border: 1px solid transparent;
            border-radius: var(--radius-sm);
            color: var(--text-muted);
            font-size: 0.78rem;
            font-weight: 500;
            padding: 6px 10px;
            cursor: pointer;
            display: flex;
            align-items: center;
            gap: 8px;
            transition: all var(--transition-fast);
            width: 100%;
            text-align: left;
        }
        .filter-chip:hover {
            color: var(--text-primary);
            background: var(--card-hover);
        }
        .filter-chip.active {
            color: var(--accent);
            background: rgba(56, 189, 248, 0.06);
            border-color: rgba(56, 189, 248, 0.2);
            font-weight: 600;
        }

        /* Donji panel sidebara: Server Status & TrueNAS Info */
        .sidebar-bottom {
            padding: 12px;
            border-top: 1px solid var(--border);
            background: var(--card-subtle);
        }

        .server-status-panel {
            background: var(--card);
            border: 1px solid var(--border);
            border-radius: var(--radius-md);
            padding: 12px;
            display: flex;
            flex-direction: column;
            gap: 8px;
        }

        .server-status-header {
            display: flex;
            align-items: center;
            gap: 8px;
            font-size: 0.8rem;
            font-weight: 700;
            color: var(--text-primary);
        }

        .server-status-body {
            display: flex;
            flex-direction: column;
            gap: 4px;
            font-size: 0.72rem;
            color: var(--text-muted);
            font-family: var(--font-mono);
        }

        .server-metric-row {
            display: flex;
            justify-content: space-between;
            align-items: center;
        }

        .server-metric-val {
            color: var(--text-secondary);
            font-weight: 600;
        }

        .sidebar.collapsed .server-status-panel {
            padding: 8px;
            align-items: center;
            justify-content: center;
        }
        .sidebar.collapsed .server-status-body,
        .sidebar.collapsed .server-status-header span:not(.pulse-dot) {
            display: none;
        }

        /* Pulsirajući indikator statusa */
        .pulse-dot {
            width: 10px;
            height: 10px;
            min-width: 10px;
            border-radius: 50%;
            background-color: var(--success);
            display: inline-block;
            box-shadow: 0 0 10px var(--success);
            animation: pulseAnimation 1.8s infinite;
        }
        @keyframes pulseAnimation {
            0% { transform: scale(0.95); opacity: 0.7; }
            50% { transform: scale(1.2); opacity: 1; }
            100% { transform: scale(0.95); opacity: 0.7; }
        }

        /* =====================================================================
           Glavno Radno Područje (.main-wrapper & .main-header)
           ===================================================================== */
        .main-wrapper {
            flex: 1;
            min-width: 0;
            display: flex;
            flex-direction: column;
            background-color: var(--canvas);
            overflow-x: hidden;
        }

        .main-header {
            background: #111827;
            border-bottom: 1px solid var(--border);
            padding: 14px 28px;
            display: flex;
            align-items: center;
            justify-content: space-between;
            flex-wrap: wrap;
            gap: 16px;
            position: sticky;
            top: 0;
            z-index: 900;
        }

        .header-title {
            display: flex;
            align-items: center;
            gap: 12px;
        }

        .header-title h1 {
            font-size: 1.15rem;
            font-weight: 700;
            color: var(--accent);
            letter-spacing: 0.4px;
        }

        .header-badge {
            background: var(--card);
            color: var(--text-muted);
            padding: 3px 10px;
            border-radius: var(--radius-full);
            font-size: 0.72rem;
            font-weight: 600;
            border: 1px solid var(--border);
            display: inline-flex;
            align-items: center;
            gap: 4px;
            font-family: var(--font-mono);
        }

        .fleet-stats {
            display: flex;
            gap: 12px;
            align-items: center;
            flex-wrap: wrap;
        }

        .stat-item {
            display: flex;
            align-items: center;
            gap: 6px;
            background: var(--card);
            padding: 6px 14px;
            border-radius: var(--radius-sm);
            border: 1px solid var(--border);
            font-size: 0.82rem;
            color: var(--text-muted);
        }

        .stat-val {
            font-weight: 700;
            color: var(--text-primary);
            font-family: var(--font-mono);
        }

        .stat-clock {
            font-family: var(--font-mono);
            font-weight: 600;
            color: var(--accent);
            border-color: rgba(56, 189, 248, 0.3);
            background: rgba(56, 189, 248, 0.05);
        }

        /* Glavni kontejner sadržaja tabova */
        main {
            flex: 1;
            padding: 24px;
            max-width: 1720px;
            width: 100%;
            margin: 0 auto;
        }

        .tab-content {
            display: none;
            animation: tabFadeIn 0.15s ease-in-out;
        }
        .tab-content.active {
            display: block;
        }
        @keyframes tabFadeIn {
            from { opacity: 0; transform: translateY(4px); }
            to { opacity: 1; transform: translateY(0); }
        }

        /* =====================================================================
           TAB 1: Hibridni Cockpit Raspored (.cockpit-layout)
           ===================================================================== */
        .cockpit-layout {
            display: grid;
            grid-template-columns: minmax(460px, 1fr) minmax(520px, 1.25fr);
            gap: 24px;
            align-items: start;
        }
        @media (max-width: 1200px) {
            .cockpit-layout {
                grid-template-columns: 1fr;
            }
        }

        .cockpit-fleet-column,
        .cockpit-monitor-column {
            display: flex;
            flex-direction: column;
            gap: 16px;
        }

        .column-header {
            display: flex;
            justify-content: space-between;
            align-items: center;
            gap: 12px;
            flex-wrap: wrap;
            padding-bottom: 4px;
        }

        .column-title {
            display: flex;
            align-items: center;
            gap: 10px;
        }
        .column-title h3 {
            font-size: 1.05rem;
            font-weight: 700;
            color: var(--text-primary);
        }

        .fleet-search-box {
            position: relative;
            flex: 1;
            max-width: 320px;
        }

        .fleet-grid {
            display: flex;
            flex-direction: column;
            gap: 14px;
        }

        /* Kartica računara u listi */
        .card {
            background: var(--card);
            border: 1px solid var(--border);
            border-radius: var(--radius-md);
            overflow: hidden;
            display: flex;
            flex-direction: column;
            transition: all var(--transition-fast);
            box-shadow: 0 4px 14px rgba(0, 0, 0, 0.35);
            cursor: pointer;
            position: relative;
        }
        .card:hover {
            border-color: var(--border-hover);
            background: var(--card-hover);
            transform: translateY(-1px);
        }
        .card.card-selected {
            border-color: var(--accent);
            box-shadow: 0 0 16px var(--accent-glow), 0 4px 16px rgba(0, 0, 0, 0.4);
            background: #1b273b;
        }

        .card-header {
            padding: 12px 16px;
            background: rgba(255, 255, 255, 0.02);
            border-bottom: 1px solid var(--border);
            display: flex;
            justify-content: space-between;
            align-items: flex-start;
            gap: 10px;
        }

        .card-title-group {
            display: flex;
            flex-direction: column;
            gap: 4px;
            flex: 1;
        }

        .card-dev-name {
            font-size: 0.98rem;
            font-weight: 700;
            color: #fff;
            display: flex;
            align-items: center;
            gap: 8px;
            flex-wrap: wrap;
        }

        .card-sub {
            font-size: 0.78rem;
            color: var(--text-muted);
            font-family: var(--font-mono);
            display: flex;
            align-items: center;
            gap: 8px;
            flex-wrap: wrap;
        }

        .card-meta-badges {
            display: flex;
            gap: 6px;
            flex-wrap: wrap;
            margin-top: 4px;
        }

        .status-pill {
            padding: 4px 10px;
            border-radius: var(--radius-full);
            font-size: 0.72rem;
            font-weight: 700;
            text-transform: uppercase;
            letter-spacing: 0.4px;
            white-space: nowrap;
        }
        .status-active { background: var(--success-subtle); color: var(--success); border: 1px solid rgba(16, 185, 129, 0.4); }
        .status-reboot { background: var(--warning-subtle); color: var(--warning); border: 1px solid rgba(245, 158, 11, 0.4); }
        .status-done { background: var(--accent-subtle); color: var(--accent); border: 1px solid rgba(56, 189, 248, 0.4); }
        .status-warn { background: var(--danger-subtle); color: var(--danger); border: 1px solid rgba(239, 68, 68, 0.4); }
        .status-offline { background: rgba(148, 163, 184, 0.12); color: #94a3b8; border: 1px solid #475569; }
        .status-agy-done { background: rgba(16, 185, 129, 0.2); color: #34d399; border: 1px solid var(--success); box-shadow: 0 0 10px var(--success-glow); }
        .status-agy-fail { background: rgba(239, 68, 68, 0.2); color: #f87171; border: 1px solid var(--danger); }

        .card-body {
            padding: 14px 16px;
            display: flex;
            flex-direction: column;
            gap: 12px;
            flex: 1;
        }

        .phases-row {
            display: flex;
            gap: 4px;
            background: #0f1523;
            padding: 5px;
            border-radius: var(--radius-sm);
            border: 1px solid var(--border);
        }
        .phase-step {
            flex: 1;
            text-align: center;
            font-size: 0.7rem;
            padding: 4px 2px;
            border-radius: var(--radius-sm);
            color: #64748b;
            background: transparent;
            font-weight: 600;
            white-space: nowrap;
            overflow: hidden;
            text-overflow: ellipsis;
        }
        .phase-step.active {
            background: var(--warning);
            color: #000;
            font-weight: 700;
        }
        .phase-step.completed {
            background: rgba(16, 185, 129, 0.2);
            color: var(--success);
        }

        .live-action-box {
            background: #0c121e;
            border-left: 3px solid var(--accent);
            padding: 9px 12px;
            border-radius: 0 var(--radius-sm) var(--radius-sm) 0;
            font-size: 0.82rem;
        }
        .live-action-label {
            font-size: 0.68rem;
            color: var(--accent);
            text-transform: uppercase;
            font-weight: 700;
            margin-bottom: 2px;
        }
        .live-action-text {
            color: var(--text-primary);
            font-family: var(--font-mono);
            font-size: 0.78rem;
            white-space: nowrap;
            overflow: hidden;
            text-overflow: ellipsis;
        }

        .agy-summary-box {
            background: rgba(15, 23, 42, 0.95);
            border-left: 3px solid var(--purple);
            padding: 9px 12px;
            border-radius: 0 var(--radius-sm) var(--radius-sm) 0;
        }
        .agy-summary-label {
            font-size: 0.68rem;
            color: var(--purple);
            text-transform: uppercase;
            font-weight: 700;
            margin-bottom: 2px;
        }
        .agy-summary-text {
            color: var(--text-secondary);
            font-size: 0.8rem;
            line-height: 1.4;
            max-height: 50px;
            overflow: hidden;
            text-overflow: ellipsis;
            display: -webkit-box;
            -webkit-line-clamp: 2;
            -webkit-box-orient: vertical;
        }

        .progress-container {
            display: flex;
            flex-direction: column;
            gap: 4px;
        }
        .progress-header {
            display: flex;
            justify-content: space-between;
            font-size: 0.72rem;
            color: var(--text-muted);
        }
        .progress-bar-bg {
            height: 6px;
            background: #111827;
            border-radius: var(--radius-full);
            overflow: hidden;
            border: 1px solid rgba(255, 255, 255, 0.05);
        }
        .progress-bar-fill {
            height: 100%;
            background: linear-gradient(90deg, var(--accent), var(--success));
            border-radius: var(--radius-full);
            transition: width 0.3s ease;
        }

        .apps-badge-grid {
            display: grid;
            grid-template-columns: repeat(4, 1fr);
            gap: 6px;
        }
        .app-badge {
            font-size: 0.7rem;
            padding: 4px 6px;
            border-radius: var(--radius-sm);
            text-align: center;
            background: #162032;
            color: #94a3b8;
            display: flex;
            align-items: center;
            justify-content: center;
            gap: 4px;
            font-family: var(--font-mono);
            border: 1px solid rgba(255, 255, 255, 0.03);
        }
        .app-badge.ok { background: var(--success-subtle); color: var(--success); border-color: rgba(16, 185, 129, 0.3); font-weight: 600; }
        .app-badge.running { background: var(--warning-subtle); color: var(--warning); border-color: rgba(245, 158, 11, 0.3); }
        .app-badge.failed { background: var(--danger-subtle); color: var(--danger); border-color: rgba(239, 68, 68, 0.3); }

        .card-footer {
            padding: 10px 16px;
            border-top: 1px solid var(--border);
            background: rgba(0, 0, 0, 0.25);
            display: flex;
            justify-content: space-between;
            align-items: center;
            font-size: 0.75rem;
            flex-wrap: wrap;
            gap: 8px;
        }
        .card-footer-actions {
            display: flex;
            gap: 6px;
            align-items: center;
            flex-wrap: wrap;
        }

        /* Dugmad (Stitch Spec) */
        .btn-log, .btn-report, .btn-history, .btn-cockpit {
            padding: 5px 10px;
            border-radius: var(--radius-sm);
            font-size: 0.75rem;
            font-weight: 600;
            cursor: pointer;
            transition: all var(--transition-fast);
            display: inline-flex;
            align-items: center;
            gap: 5px;
            border: 1px solid transparent;
            text-decoration: none;
            white-space: nowrap;
        }
        .btn-log { background: #15803d; color: #fff; }
        .btn-log:hover { background: #16a34a; }
        .btn-report { background: #1d4ed8; color: #fff; }
        .btn-report:hover { background: #2563eb; }
        .btn-history { background: #7e22ce; color: #fff; }
        .btn-history:hover { background: #9333ea; }
        .btn-warning {
            background: rgba(245, 158, 11, 0.15);
            color: #fbbf24;
            border: 1px solid rgba(245, 158, 11, 0.4);
        }
        .btn-warning:hover {
            background: rgba(245, 158, 11, 0.28);
            border-color: var(--warning);
            box-shadow: 0 0 12px var(--warning-glow);
            color: #fff;
        }
        .btn-warning-solid {
            background: var(--warning);
            color: #0f172a;
            border: 1px solid transparent;
            font-weight: 700;
        }
        .btn-warning-solid:hover {
            background: #d97706;
            box-shadow: 0 0 14px var(--warning-glow);
            color: #fff;
        }
        .btn-secondary {
            background: transparent;
            border: 1px solid var(--border);
            color: var(--text-primary);
        }
        .btn-secondary:hover {
            background: var(--card-hover);
            border-color: var(--border-hover);
            color: #fff;
        }

        .btn-primary {
            background: var(--accent);
            color: #0F172A;
            border: 1px solid transparent;
            padding: 9px 18px;
            border-radius: var(--radius-sm);
            font-size: 0.88rem;
            font-weight: 700;
            cursor: pointer;
            transition: all var(--transition-fast);
            display: inline-flex;
            align-items: center;
            gap: 8px;
            text-decoration: none;
        }
        .btn-primary:hover:not(:disabled) {
            background: #7dd3fc;
            box-shadow: 0 0 14px var(--accent-glow);
        }
        .btn-primary:disabled {
            background: #334155;
            color: #64748b;
            cursor: not-allowed;
        }

        .btn-burn {
            background: linear-gradient(135deg, var(--magenta), #8b5cf6);
            color: #fff;
            border: 1px solid rgba(255, 255, 255, 0.2);
            padding: 10px 22px;
            border-radius: var(--radius-sm);
            font-size: 0.92rem;
            font-weight: 700;
            cursor: pointer;
            transition: all var(--transition-fast);
            display: inline-flex;
            align-items: center;
            justify-content: center;
            gap: 8px;
            box-shadow: 0 4px 14px rgba(217, 70, 239, 0.35);
        }
        .btn-burn:hover:not(:disabled) {
            background: linear-gradient(135deg, #e879f9, #a78bfa);
            box-shadow: 0 6px 20px rgba(217, 70, 239, 0.55);
            transform: translateY(-1px);
        }
        .btn-burn:disabled {
            background: #334155;
            color: #64748b;
            cursor: not-allowed;
            box-shadow: none;
            transform: none;
        }

        .btn-refresh {
            background: var(--card);
            color: var(--text-primary);
            border: 1px solid var(--border);
            padding: 7px 12px;
            border-radius: var(--radius-sm);
            cursor: pointer;
            transition: all var(--transition-fast);
            font-size: 0.82rem;
            display: inline-flex;
            align-items: center;
            gap: 6px;
        }
        .btn-refresh:hover {
            border-color: var(--accent);
            background: var(--card-hover);
        }

        /* =====================================================================
           Desni Cockpit Panel: 1 Hz Ogledalo Ekrana & Telemetrija
           ===================================================================== */
        .cockpit-card {
            background: var(--card);
            border: 1px solid var(--border);
            border-radius: var(--radius-md);
            overflow: hidden;
            box-shadow: 0 8px 24px rgba(0, 0, 0, 0.45);
            display: flex;
            flex-direction: column;
            gap: 16px;
            padding: 18px;
        }

        .cockpit-view-header {
            display: flex;
            justify-content: space-between;
            align-items: flex-start;
            flex-wrap: wrap;
            gap: 12px;
            border-bottom: 1px solid var(--border);
            padding-bottom: 14px;
        }

        .cockpit-title-group h2 {
            font-size: 1.25rem;
            font-weight: 700;
            color: #fff;
            display: flex;
            align-items: center;
            gap: 10px;
        }
        .cockpit-title-sub {
            font-size: 0.82rem;
            color: var(--text-muted);
            font-family: var(--font-mono);
            margin-top: 4px;
        }

        /* 1 Hz Screen Mirror Okvir */
        .screen-mirror-frame {
            background: #090e17;
            border: 2px solid #23354d;
            border-radius: var(--radius-md);
            overflow: hidden;
            box-shadow: inset 0 0 24px rgba(0, 0, 0, 0.9), 0 6px 20px rgba(0, 0, 0, 0.6);
            display: flex;
            flex-direction: column;
        }

        .screen-mirror-topbar {
            background: #111a28;
            padding: 8px 14px;
            border-bottom: 1px solid #23354d;
            display: flex;
            justify-content: space-between;
            align-items: center;
            font-size: 0.72rem;
            color: var(--text-muted);
            font-family: var(--font-mono);
        }

        .screen-mirror-viewport {
            min-height: 250px;
            background: radial-gradient(circle at 50% 30%, #101c30 0%, #060911 100%);
            padding: 0;
            position: relative;
            overflow: hidden;
            display: flex;
            flex-direction: column;
            justify-content: space-between;
            font-family: var(--font-mono);
            color: #e2e8f0;
            box-shadow: inset 0 0 40px rgba(0, 0, 0, 0.95);
        }

        /* CRT/LCD Scanline & Reflection efekt */
        .screen-mirror-viewport::after {
            content: " ";
            position: absolute;
            top: 0; left: 0; bottom: 0; right: 0;
            background: linear-gradient(rgba(18, 16, 16, 0) 50%, rgba(0, 0, 0, 0.2) 50%), linear-gradient(90deg, rgba(255, 0, 0, 0.012), rgba(0, 255, 0, 0.008), rgba(0, 0, 255, 0.012));
            background-size: 100% 3px, 3px 100%;
            pointer-events: none;
            opacity: 0.5;
            z-index: 5;
        }

        /* Standby Režim Container */
        .mirror-standby-container {
            width: 100%;
            height: 100%;
            min-height: 250px;
            padding: 16px;
            display: flex;
            flex-direction: column;
            justify-content: space-between;
            z-index: 2;
        }

        .mirror-content-top {
            display: flex;
            justify-content: space-between;
            align-items: center;
            border-bottom: 1px dashed rgba(56, 189, 248, 0.3);
            padding-bottom: 8px;
            z-index: 2;
        }

        .mirror-content-mid {
            display: flex;
            flex-direction: column;
            gap: 10px;
            margin: auto 0;
            z-index: 2;
        }

        .mirror-content-bottom {
            display: flex;
            justify-content: space-between;
            align-items: center;
            font-size: 0.7rem;
            color: #94a3b8;
            border-top: 1px dashed rgba(255, 255, 255, 0.1);
            padding-top: 8px;
            z-index: 2;
        }

        /* Active 1 Hz Live Feed Container */
        .mirror-live-container {
            width: 100%;
            height: 100%;
            min-height: 250px;
            background: #060911;
            display: flex;
            flex-direction: column;
            align-items: center;
            justify-content: center;
            position: relative;
            padding: 10px;
            z-index: 2;
        }

        .mirror-live-img-box {
            position: relative;
            width: 100%;
            display: flex;
            align-items: center;
            justify-content: center;
            overflow: hidden;
            border-radius: var(--radius-md);
            border: 1px solid var(--border);
            box-shadow: 0 4px 20px rgba(0, 0, 0, 0.6);
            background: #000;
        }

        .cockpit-screen-img {
            max-width: 100%;
            max-height: 380px;
            width: auto;
            height: auto;
            object-fit: contain;
            display: block;
            border-radius: calc(var(--radius-md) - 1px);
        }

        .mirror-live-overlay-footer {
            position: absolute;
            bottom: 8px;
            left: 12px;
            right: 12px;
            display: flex;
            justify-content: space-between;
            align-items: center;
            padding: 4px 10px;
            background: rgba(15, 23, 42, 0.82);
            backdrop-filter: blur(6px);
            border: 1px solid rgba(51, 65, 85, 0.7);
            border-radius: var(--radius-sm);
            font-size: 0.72rem;
            color: var(--text-secondary);
            font-family: var(--font-mono);
            pointer-events: none;
            z-index: 3;
        }

        .mirror-live-stream-label {
            display: flex;
            align-items: center;
            gap: 6px;
            color: var(--success);
            font-weight: 700;
        }

        .mirror-live-res-label {
            color: var(--text-muted);
            font-size: 0.7rem;
        }

        .mirror-live-ts {
            color: var(--accent);
            font-weight: 600;
        }

        /* Hardverske Metrike & Instrumenti (CPU, RAM, Disk, OEM) */
        .telemetry-gauges-grid {
            display: grid;
            grid-template-columns: repeat(auto-fit, minmax(200px, 1fr));
            gap: 12px;
        }

        .gauge-card {
            background: #131c2d;
            border: 1px solid var(--border);
            border-radius: var(--radius-sm);
            padding: 12px;
            display: flex;
            flex-direction: column;
            gap: 6px;
        }

        .gauge-header {
            font-size: 0.72rem;
            color: var(--text-muted);
            text-transform: uppercase;
            font-weight: 700;
            letter-spacing: 0.5px;
            display: flex;
            justify-content: space-between;
            align-items: center;
        }

        .gauge-value {
            font-size: 1.15rem;
            font-weight: 700;
            color: #fff;
            font-family: var(--font-mono);
        }

        .gauge-bar-bg {
            height: 5px;
            background: #1e293b;
            border-radius: var(--radius-full);
            overflow: hidden;
            margin-top: 2px;
        }

        .gauge-bar-fill {
            height: 100%;
            border-radius: var(--radius-full);
            transition: width 0.3s ease;
        }

        /* Quick Action Toolbar u Cockpitu */
        .cockpit-action-toolbar {
            display: flex;
            gap: 8px;
            align-items: center;
            flex-wrap: wrap;
            padding-top: 6px;
            border-top: 1px solid var(--border);
        }

        /* Prazno stanje flote */
        .empty-fleet {
            text-align: center;
            padding: 60px 20px;
            color: var(--text-muted);
            background: var(--card);
            border: 1px dashed var(--border);
            border-radius: var(--radius-md);
            grid-column: 1 / -1;
        }
        .empty-fleet h2 {
            color: #fff;
            margin-bottom: 8px;
            font-size: 1.15rem;
        }

        /* =====================================================================
           TAB 2: WIS Centar (ISO & USB Builder)
           ===================================================================== */
        .panel-grid-2 {
            display: grid;
            grid-template-columns: repeat(auto-fit, minmax(460px, 1fr));
            gap: 24px;
        }
        .panel-card {
            background: var(--card);
            border: 1px solid var(--border);
            border-radius: var(--radius-md);
            overflow: hidden;
            box-shadow: 0 4px 14px rgba(0, 0, 0, 0.35);
            display: flex;
            flex-direction: column;
        }
        .panel-header {
            padding: 16px 20px;
            background: rgba(255, 255, 255, 0.02);
            border-bottom: 1px solid var(--border);
            display: flex;
            justify-content: space-between;
            align-items: center;
            flex-wrap: wrap;
            gap: 10px;
        }
        .panel-title {
            font-size: 1.05rem;
            font-weight: 700;
            color: #fff;
            display: flex;
            align-items: center;
            gap: 10px;
        }
        .panel-body {
            padding: 20px;
            display: flex;
            flex-direction: column;
            gap: 18px;
            flex: 1;
        }

        .form-group {
            display: flex;
            flex-direction: column;
            gap: 8px;
        }
        .form-label {
            font-size: 0.85rem;
            font-weight: 600;
            color: var(--text-primary);
            display: flex;
            justify-content: space-between;
            align-items: center;
        }
        .select-row {
            display: flex;
            gap: 8px;
        }
        .usb-select {
            flex: 1;
            background: #090e17;
            color: var(--text-primary);
            border: 1px solid var(--border);
            border-radius: var(--radius-sm);
            padding: 10px 14px;
            font-size: 0.88rem;
            font-family: var(--font-mono);
            outline: none;
        }
        .usb-select:focus {
            border-color: var(--accent);
            box-shadow: 0 0 0 2px var(--accent-subtle);
        }
        .usb-select option:disabled {
            color: var(--danger);
            background: #1b1214;
        }

        .safety-warning-box {
            background: rgba(245, 158, 11, 0.08);
            border: 1px solid rgba(245, 158, 11, 0.35);
            border-left: 4px solid var(--warning);
            border-radius: var(--radius-sm);
            padding: 12px 14px;
            display: flex;
            align-items: flex-start;
            gap: 12px;
            font-size: 0.82rem;
            color: #fef08a;
            line-height: 1.4;
        }
        .checkbox-container {
            display: flex;
            align-items: center;
            gap: 10px;
            background: #0f172a;
            padding: 12px 14px;
            border-radius: var(--radius-sm);
            border: 1px solid var(--border);
            cursor: pointer;
            user-select: none;
        }
        .checkbox-container input[type="checkbox"] {
            width: 18px;
            height: 18px;
            accent-color: var(--magenta);
            cursor: pointer;
        }
        .checkbox-label {
            font-size: 0.88rem;
            font-weight: 600;
            color: var(--text-primary);
            cursor: pointer;
        }

        .burn-progress-box {
            background: #090e17;
            border: 1px solid var(--border);
            border-radius: var(--radius-md);
            padding: 16px;
            display: flex;
            flex-direction: column;
            gap: 12px;
        }
        .burn-header {
            display: flex;
            justify-content: space-between;
            align-items: center;
            font-size: 0.85rem;
        }
        .burn-bar-bg {
            height: 10px;
            background: #1e293b;
            border-radius: var(--radius-full);
            overflow: hidden;
        }
        .burn-bar-fill {
            height: 100%;
            background: linear-gradient(90deg, var(--accent), var(--magenta));
            border-radius: var(--radius-full);
            transition: width 0.3s ease;
            box-shadow: 0 0 12px rgba(217, 70, 239, 0.5);
        }
        .burn-log-text {
            font-family: var(--font-mono);
            font-size: 0.8rem;
            color: var(--text-secondary);
            padding: 8px 12px;
            background: #060911;
            border-radius: var(--radius-sm);
            border: 1px solid #1e293b;
        }

        /* =====================================================================
           TAB 3: Windows 11 Zakrpe
           ===================================================================== */
        .stat-overview-grid {
            display: grid;
            grid-template-columns: repeat(auto-fit, minmax(230px, 1fr));
            gap: 16px;
            margin-bottom: 24px;
        }
        .stat-card-box {
            background: var(--card);
            border: 1px solid var(--border);
            border-radius: var(--radius-md);
            padding: 16px 20px;
            display: flex;
            flex-direction: column;
            gap: 6px;
            box-shadow: 0 4px 14px rgba(0, 0, 0, 0.3);
        }
        .stat-card-label {
            font-size: 0.78rem;
            color: var(--text-muted);
            text-transform: uppercase;
            font-weight: 700;
            letter-spacing: 0.5px;
            display: flex;
            justify-content: space-between;
            align-items: center;
        }
        .stat-card-value {
            font-size: 1.45rem;
            font-weight: 700;
            color: #fff;
            font-family: var(--font-mono);
        }
        .stat-card-sub {
            font-size: 0.75rem;
            color: var(--text-muted);
            font-family: var(--font-mono);
            word-break: break-all;
        }

        .table-container {
            background: var(--card);
            border: 1px solid var(--border);
            border-radius: var(--radius-md);
            overflow: hidden;
            box-shadow: 0 4px 14px rgba(0, 0, 0, 0.35);
        }
        .table-toolbar {
            padding: 14px 18px;
            background: rgba(255, 255, 255, 0.02);
            border-bottom: 1px solid var(--border);
            display: flex;
            justify-content: space-between;
            align-items: center;
            flex-wrap: wrap;
            gap: 12px;
        }
        .search-input {
            background: #090e17;
            border: 1px solid var(--border);
            border-radius: var(--radius-sm);
            padding: 8px 14px;
            color: var(--text-primary);
            font-size: 0.82rem;
            min-width: 260px;
            outline: none;
            transition: border-color var(--transition-fast);
        }
        .search-input:focus {
            border-color: var(--accent);
            box-shadow: 0 0 0 2px var(--accent-subtle);
        }

        .data-table {
            width: 100%;
            border-collapse: collapse;
            font-size: 0.82rem;
            text-align: left;
        }
        .data-table th {
            background: #111a28;
            color: var(--text-muted);
            padding: 12px 16px;
            border-bottom: 1px solid var(--border);
            font-weight: 700;
            text-transform: uppercase;
            font-size: 0.72rem;
            letter-spacing: 0.5px;
        }
        .data-table td {
            padding: 12px 16px;
            border-bottom: 1px solid rgba(51, 65, 85, 0.6);
            color: var(--text-secondary);
        }
        .data-table tr:hover td {
            background: rgba(255, 255, 255, 0.02);
            color: #fff;
        }
        .kb-pill {
            background: var(--accent-subtle);
            color: var(--accent);
            border: 1px solid rgba(56, 189, 248, 0.35);
            padding: 2px 8px;
            border-radius: var(--radius-full);
            font-weight: 700;
            font-family: var(--font-mono);
            font-size: 0.75rem;
            display: inline-block;
        }
        .type-pill {
            padding: 3px 8px;
            border-radius: var(--radius-sm);
            font-weight: 700;
            font-size: 0.7rem;
            text-transform: uppercase;
            display: inline-block;
        }
        .type-msu { background: rgba(59, 130, 246, 0.2); color: #60a5fa; border: 1px solid #3b82f6; }
        .type-cab { background: rgba(217, 70, 239, 0.2); color: #f472b6; border: 1px solid #d946ef; }

        /* =====================================================================
           Modali (Log Uživo, Servisni Karton, Servisna Istorija)
           ===================================================================== */
        .modal-overlay {
            display: none;
            position: fixed;
            top: 0; left: 0; right: 0; bottom: 0;
            background: rgba(15, 23, 42, 0.85);
            backdrop-filter: blur(8px);
            z-index: 9999;
            align-items: center;
            justify-content: center;
            padding: 20px;
        }
        .modal-overlay.open { display: flex; }
        .modal-box {
            background: #090e17;
            border: 1px solid var(--border);
            border-radius: var(--radius-md);
            width: 100%;
            max-width: 1100px;
            height: 85vh;
            display: flex;
            flex-direction: column;
            box-shadow: 0 24px 48px rgba(0, 0, 0, 0.9);
            overflow: hidden;
        }
        .modal-header {
            padding: 14px 20px;
            background: #111a28;
            border-bottom: 1px solid var(--border);
            display: flex;
            justify-content: space-between;
            align-items: center;
            gap: 12px;
            flex-wrap: wrap;
        }
        .modal-title {
            display: flex;
            align-items: center;
            gap: 12px;
            font-size: 1.05rem;
            color: var(--accent);
            font-weight: 700;
        }
        .modal-close {
            background: transparent;
            border: none;
            color: var(--text-muted);
            font-size: 1.5rem;
            cursor: pointer;
            padding: 2px 8px;
            border-radius: var(--radius-sm);
            line-height: 1;
        }
        .modal-close:hover { color: #fff; background: var(--border); }
        .terminal-container {
            flex: 1;
            padding: 16px;
            background: #04070d;
            overflow-y: auto;
            font-family: var(--font-mono);
            font-size: 0.8rem;
            line-height: 1.55;
            color: #d1d5db;
        }
        .log-line {
            padding: 2px 0;
            word-break: break-all;
        }
        .log-line.SUCCESS { color: #34d399; }
        .log-line.WARN { color: #facc15; }
        .log-line.ERROR { color: #f87171; font-weight: 700; }
        .log-line.STEP { color: var(--accent); font-weight: 600; }
        .log-line.DEBUG { color: #64748b; }

        .history-meta-grid {
            background: #131c2d;
            padding: 14px;
            border-radius: var(--radius-sm);
            border: 1px solid var(--border);
            display: grid;
            grid-template-columns: repeat(auto-fit, minmax(220px, 1fr));
            gap: 12px;
            font-family: var(--font-mono);
            font-size: 0.82rem;
        }
        .interventions-list {
            display: flex;
            flex-direction: column;
            gap: 10px;
        }
        .intervention-card {
            background: #0d1422;
            border: 1px solid var(--border);
            border-radius: var(--radius-sm);
            padding: 12px 14px;
        }
        .intervention-header {
            display: flex;
            justify-content: space-between;
            align-items: center;
            margin-bottom: 6px;
            flex-wrap: wrap;
            gap: 6px;
        }
        .intervention-summary {
            font-size: 0.82rem;
            color: var(--text-primary);
            line-height: 1.4;
        }
        .tool-pill {
            background: #1e293b;
            color: var(--text-muted);
            border: 1px solid var(--border);
            padding: 2px 6px;
            border-radius: var(--radius-sm);
            font-size: 0.7rem;
            font-family: var(--font-mono);
            display: inline-block;
            margin-right: 4px;
        }
        .dnevnik-markdown-viewer {
            background: #060911;
            border: 1px solid var(--border);
            border-radius: var(--radius-sm);
            padding: 16px;
            color: #d1d5db;
            font-size: 0.85rem;
            line-height: 1.6;
            max-height: 400px;
            overflow-y: auto;
        }
        .md-h1 { font-size: 1.2rem; color: #fff; margin-bottom: 8px; border-bottom: 1px solid var(--border); padding-bottom: 4px; }
        .md-h2 { font-size: 1.05rem; color: var(--accent); margin: 12px 0 6px 0; }
        .md-h3 { font-size: 0.92rem; color: var(--success); margin: 10px 0 4px 0; }
        .md-p { margin-bottom: 6px; }
        .md-ul { margin-left: 20px; margin-bottom: 8px; }
        .md-code { font-family: var(--font-mono); background: #131c2d; padding: 2px 6px; border-radius: 4px; color: var(--accent); font-size: 0.8rem; }
        .md-hr { border: none; border-top: 1px solid var(--border); margin: 12px 0; }
        .md-space { height: 8px; }

        /* =====================================================================
           Toast Notifikacije & Daljinske Komande (Stitch Spec / DESIGN.md)
           ===================================================================== */
        .toast-container {
            position: fixed;
            bottom: 24px;
            right: 24px;
            z-index: 10000;
            display: flex;
            flex-direction: column;
            gap: 10px;
            pointer-events: none;
        }
        .toast {
            pointer-events: auto;
            min-width: 300px;
            max-width: 480px;
            background: #1e293b;
            border: 1px solid var(--border);
            border-radius: var(--radius-sm);
            padding: 12px 16px;
            box-shadow: 0 10px 25px rgba(0, 0, 0, 0.5);
            display: flex;
            align-items: center;
            gap: 12px;
            font-size: 0.85rem;
            color: var(--text-primary);
            transition: all var(--transition-normal);
            animation: toastSlideIn 0.25s ease-out;
        }
        @keyframes toastSlideIn {
            from { transform: translateX(100%); opacity: 0; }
            to { transform: translateX(0); opacity: 1; }
        }
        .toast.toast-success {
            border-color: rgba(16, 185, 129, 0.4);
            background: #0d281e;
            box-shadow: 0 0 16px var(--success-glow);
        }
        .toast.toast-error {
            border-color: rgba(239, 68, 68, 0.4);
            background: #2a1215;
            box-shadow: 0 0 16px var(--danger-glow);
        }
        .toast.toast-warning {
            border-color: rgba(245, 158, 11, 0.4);
            background: #281c0c;
            box-shadow: 0 0 16px var(--warning-glow);
        }
    </style>
</head>
<body>
    <div class="app-layout">
        <!-- =====================================================================
             Collapsible Levi Sidebar (.sidebar)
             ===================================================================== -->
        <aside class="sidebar">
            <div class="sidebar-header">
                <div class="sidebar-brand">
                    <span class="brand-icon">⚡</span>
                    <div class="brand-info">
                        <span class="brand-title">Tehno-Kutak</span>
                        <span class="brand-version">ServisHub v__WIS_VERSION__</span>
                    </div>
                </div>
                <button class="sidebar-toggle-btn" id="sidebar-toggle-btn" onclick="toggleSidebar()" title="Smanji / Proširi bočni meni">◀</button>
            </div>

            <nav class="sidebar-nav">
                <div class="nav-section-title">Glavna Navigacija</div>
                <button class="nav-tab active" id="tab-btn-fleet" onclick="switchTab('fleet')">
                    <span class="nav-tab-icon">🖥️</span>
                    <span class="nav-tab-label">Radionička Flota & Telemetrija</span>
                </button>
                <button class="nav-tab" id="tab-btn-wis" onclick="switchTab('wis')">
                    <span class="nav-tab-icon">💿</span>
                    <span class="nav-tab-label">WIS Centar (ISO & USB Builder)</span>
                </button>
                <button class="nav-tab" id="tab-btn-updates" onclick="switchTab('updates')">
                    <span class="nav-tab-icon">🔄</span>
                    <span class="nav-tab-label">Windows 11 Zakrpe</span>
                </button>

                <div class="sidebar-subnav">
                    <div class="nav-section-title">Brzi Filteri Flote</div>
                    <button class="filter-chip active" id="filter-chip-all" onclick="setFleetFilter('all')">
                        <span>🌐</span> <span class="nav-tab-label">Sve mašine</span>
                    </button>
                    <button class="filter-chip" id="filter-chip-active" onclick="setFleetFilter('active')">
                        <span>⚡</span> <span class="nav-tab-label">Aktivne u radu</span>
                    </button>
                    <button class="filter-chip" id="filter-chip-done" onclick="setFleetFilter('done')">
                        <span>✓</span> <span class="nav-tab-label">Završeni servisi</span>
                    </button>
                    <button class="filter-chip" id="filter-chip-agy" onclick="setFleetFilter('agy')">
                        <span>🛠️</span> <span class="nav-tab-label">AGY Intervencije</span>
                    </button>
                </div>
            </nav>

            <div class="sidebar-bottom">
                <div class="server-status-panel">
                    <div class="server-status-header">
                        <span class="pulse-dot" id="stat-server-pulse"></span>
                        <span class="nav-tab-label">Server Status: Online</span>
                    </div>
                    <div class="server-status-body">
                        <div class="server-metric-row">
                            <span>Uptime:</span>
                            <span class="server-metric-val" id="stat-uptime">99.98%</span>
                        </div>
                        <div class="server-metric-row">
                            <span>TrueNAS Keš:</span>
                            <span class="server-metric-val" style="color:var(--success);">Aktivan (10 GbE)</span>
                        </div>
                        <div class="server-metric-row">
                            <span>Skladište:</span>
                            <span class="server-metric-val" title="/home/gru/Storage">ZFS Pool</span>
                        </div>
                        <div class="server-metric-row">
                            <span>UDP Broadcast:</span>
                            <span class="server-metric-val">Port 8888</span>
                        </div>
                    </div>
                </div>
            </div>
        </aside>

        <!-- =====================================================================
             Glavno Radno Područje (.main-wrapper)
             ===================================================================== -->
        <div class="main-wrapper">
            <header class="main-header">
                <div class="header-title">
                    <span class="pulse-dot"></span>
                    <h1>Tehno-Kutak Centralni Servisni Hub</h1>
                    <span class="header-badge">ServisHub v__WIS_VERSION__</span>
                </div>
                <div class="fleet-stats">
                    <div class="stat-item">Ukupno mašina: <span class="stat-val" id="stat-total">0</span></div>
                    <div class="stat-item">Aktivno u radu: <span class="stat-val" id="stat-active" style="color:var(--green)">0</span></div>
                    <div class="stat-item">Završeno: <span class="stat-val" id="stat-done" style="color:var(--cyan)">0</span></div>
                    <div class="stat-item stat-clock">
                        <span>⏱️ LAN Sat:</span>
                        <span id="clock-display">--:--:--</span>
                    </div>
                </div>
            </header>

            <main>
                <!-- TAB 1: Radionička Flota & Telemetrija (Hibridni Cockpit) -->
                <section id="tab-fleet" class="tab-content active">
                    <div class="cockpit-layout">
                        <!-- Leva polovina: Lista i tabela mašina sa statusima -->
                        <div class="cockpit-fleet-column">
                            <div class="column-header">
                                <div class="column-title">
                                    <span>🖥️</span>
                                    <h3>Radne Stanice na Stolu</h3>
                                    <span class="header-badge" id="fleet-count-badge">0 računara</span>
                                </div>
                                <div class="fleet-search-box">
                                    <input type="text" id="fleet-search-input" class="search-input" placeholder="🔍 Pretraži mašinu, IP, S/N..." oninput="filterFleetCards()">
                                </div>
                            </div>
                            <div class="fleet-grid" id="fleet-grid">
                                <div class="empty-fleet">
                                    <h2>Čekam povezivanje klijentskih računara...</h2>
                                    <p>Čim se na nekom klijentu u servisu pokrene instalacija i uspostavi mreža, ovde će se pojaviti kartica uređaja.</p>
                                </div>
                            </div>
                        </div>

                        <!-- Desna polovina: Detaljni Cockpit sa 1 Hz ogledalom ekrana -->
                        <div class="cockpit-monitor-column" id="cockpit-detail-pane">
                            <!-- Inicijalni Cockpit Standby -->
                            <div class="cockpit-card">
                                <div class="cockpit-view-header">
                                    <div class="cockpit-title-group">
                                        <h2><span>⚡</span> Radionički Telemetry Cockpit</h2>
                                        <div class="cockpit-title-sub">Izaberite radnu stanicu sa leve liste za 1 Hz praćenje ekrana i hardvera</div>
                                    </div>
                                    <span class="header-badge" style="color:var(--accent); border-color:var(--accent);">Standby Režim</span>
                                </div>
                                <div class="screen-mirror-frame">
                                    <div class="screen-mirror-topbar">
                                        <span><span class="pulse-dot"></span> 1 Hz OGLEDALO EKRANA RADNE STANICE</span>
                                        <span>1920x1080 @ 60Hz Mirrored</span>
                                    </div>
                                    <div class="screen-mirror-viewport">
                                        <div class="mirror-standby-container">
                                            <div class="mirror-content-top">
                                                <span style="color:var(--accent); font-weight:700;">[TEHNO-KUTAK COCKPIT]</span>
                                                <span style="color:var(--success);">UDP DISCOVERY AKTIVAN (PORT 8888)</span>
                                            </div>
                                            <div class="mirror-content-mid" style="text-align:center;">
                                                <div style="font-size:1.1rem; color:#fff; font-weight:700;">Čekam heartbeat klijenta...</div>
                                                <div style="font-size:0.75rem; color:var(--text-muted);">Kada računar pošalje telemetriju, ovde se aktivira 1 Hz ogledalo ekrana i dijagnostika u realnom vremenu.</div>
                                            </div>
                                            <div class="mirror-content-bottom">
                                                <span>LAN: 10 GbE TrueNAS ZFS Keš Spreman</span>
                                                <span id="mirror-standby-time">1 Hz SYNC</span>
                                            </div>
                                        </div>
                                    </div>
                                </div>
                            </div>
                        </div>
                    </div>
                </section>

                <!-- TAB 2: WIS Centar (ISO & USB Builder) -->
                <section id="tab-wis" class="tab-content">
                    <div class="panel-grid-2">
                        <!-- Panel A: WIS ISO -->
                        <div class="panel-card">
                            <div class="panel-header">
                                <div class="panel-title">
                                    <span>💿</span> WIS Master ISO Slika
                                </div>
                                <span id="wis-iso-status-badge" class="header-badge">Provera...</span>
                            </div>
                            <div class="panel-body">
                                <div style="background:#090e17; border:1px solid var(--border); border-radius:var(--radius-sm); padding:16px; display:flex; flex-direction:column; gap:10px; font-family:var(--font-mono); font-size:0.82rem;">
                                    <div><strong>📦 Naziv datoteke:</strong> <span id="wis-iso-filename" style="color:#fff;">Učitavam...</span></div>
                                    <div><strong>🏷️ Verzija:</strong> <span id="wis-iso-version" style="color:var(--accent); font-weight:700;">-</span></div>
                                    <div><strong>📊 Veličina:</strong> <span id="wis-iso-size" style="color:#fff;">-</span></div>
                                    <div><strong>📅 Ažurirano:</strong> <span id="wis-iso-modified" style="color:var(--text-muted);">-</span></div>
                                    <div style="word-break:break-all;"><strong>🔐 SHA-256 Heš:</strong> <span id="wis-iso-sha256" style="color:#a78bfa; font-size:0.78rem;">-</span></div>
                                    <div style="word-break:break-all; font-size:0.75rem; color:var(--text-muted);"><strong>📁 Putanja:</strong> <span id="wis-iso-path">-</span></div>
                                </div>
                                <div style="display:flex; gap:10px; align-items:center; flex-wrap:wrap; margin-top:auto;">
                                    <a id="btn-download-iso" href="/api/wis/iso/download" class="btn-primary" style="display:inline-flex;">
                                        <span>⬇️</span> Preuzmi WIS ISO
                                    </a>
                                    <button class="btn-refresh" onclick="loadWisIsoInfo()">
                                        <span>🔄</span> Osveži ISO Info
                                    </button>
                                </div>
                            </div>
                        </div>

                        <!-- Panel B: UEFI USB Builder -->
                        <div class="panel-card">
                            <div class="panel-header">
                                <div class="panel-title">
                                    <span>⚡</span> Interaktivni UEFI USB Builder
                                </div>
                                <span class="header-badge" style="color:var(--magenta); border-color:var(--magenta);">Dual-Part (FAT32+NTFS)</span>
                            </div>
                            <div class="panel-body">
                                <div class="form-group">
                                    <label class="form-label" for="wis-usb-select">
                                        <span>Izaberite prenosivi USB disk:</span>
                                        <button class="btn-refresh" style="padding:2px 8px; font-size:0.72rem;" onclick="loadWisUsbList()" title="Skeniraj ponovo">🔄 Osveži Diskove</button>
                                    </label>
                                    <div class="select-row">
                                        <select id="wis-usb-select" class="usb-select" onchange="validateUsbForm()">
                                            <option value="">-- Tražim priključene USB diskove... --</option>
                                        </select>
                                    </div>
                                </div>

                                <div class="safety-warning-box">
                                    <span style="font-size:1.3rem;">⚠️</span>
                                    <div>
                                        <strong>SIGURNOSNA ZAŠTITA PODATAKA:</strong>
                                        Izabrani USB disk biće u potpunosti obrisan i formatiran u GPT layout sa UEFI boot podrškom. Sistemski i ZFS diskovi su automatski blokirani.
                                    </div>
                                </div>

                                <label class="checkbox-container">
                                    <input type="checkbox" id="wis-confirm-wipe" onchange="validateUsbForm()">
                                    <span class="checkbox-label">Potvrđujem brisanje svih podataka sa izabranog USB diska</span>
                                </label>

                                <div>
                                    <button id="btn-burn-usb" class="btn-burn" style="width:100%;" disabled onclick="startUsbBurn()">
                                        <span>⚡</span> Kreiraj Butabilni USB
                                    </button>
                                </div>

                                <!-- Progress Box za kreiranje USB-a -->
                                <div id="wis-burn-progress-box" class="burn-progress-box" style="display:none;">
                                    <div class="burn-header">
                                        <span><strong>Status narezivanja:</strong> <span id="wis-burn-phase" style="color:var(--accent); font-weight:700;">Inicijalizacija</span></span>
                                        <span id="wis-burn-pct" style="font-weight:700; color:#fff; font-family:var(--font-mono);">0%</span>
                                    </div>
                                    <div class="burn-bar-bg">
                                        <div id="wis-burn-bar-fill" class="burn-bar-fill" style="width:0%"></div>
                                    </div>
                                    <div id="wis-burn-message" class="burn-log-text">Priprema procesa...</div>
                                </div>
                            </div>
                        </div>
                    </div>
                </section>

                <!-- TAB 3: Windows 11 Zakrpe -->
                <section id="tab-updates" class="tab-content">
                    <div class="stat-overview-grid">
                        <div class="stat-card-box">
                            <div class="stat-card-label">
                                <span>Status Keša</span>
                                <span id="updates-zfs-badge" class="header-badge" style="color:var(--green); border-color:var(--green);">⚡ ZFS Keš Aktivan</span>
                            </div>
                            <div class="stat-card-value" id="updates-status-text" style="color:var(--green);">Dostupno</div>
                            <div class="stat-card-sub" id="updates-directory-text">/home/gru/Storage/Updates/Win11</div>
                        </div>
                        <div class="stat-card-box">
                            <div class="stat-card-label">Ukupno Paketa</div>
                            <div class="stat-card-value" id="updates-total-count">0</div>
                            <div class="stat-card-sub">.msu i .cab offline paketi</div>
                        </div>
                        <div class="stat-card-box">
                            <div class="stat-card-label">Ukupna Veličina</div>
                            <div class="stat-card-value" id="updates-total-size">0 MB</div>
                            <div class="stat-card-sub">Optimizovano za lokalnu mrežu</div>
                        </div>
                        <div class="stat-card-box">
                            <div class="stat-card-label">Prepoznati KB Brojevi</div>
                            <div id="updates-kb-list" style="display:flex; gap:6px; flex-wrap:wrap; margin-top:4px;">
                                <span style="color:var(--text-muted); font-size:0.8rem;">Nema paketa</span>
                            </div>
                        </div>
                    </div>

                    <div class="table-container">
                        <div class="table-toolbar">
                            <div style="display:flex; align-items:center; gap:10px;">
                                <strong style="color:#fff; font-size:0.95rem;">📦 Skladište Offline Zakrpi</strong>
                                <span id="updates-table-count" class="header-badge">0 paketa</span>
                            </div>
                            <div style="display:flex; gap:10px; align-items:center;">
                                <input type="text" id="updates-search" class="search-input" placeholder="Pretraži KB ili naziv paketa..." oninput="filterUpdatesTable()">
                                <button class="btn-refresh" onclick="loadUpdatesStatus()">🔄 Osveži</button>
                            </div>
                        </div>
                        <table class="data-table">
                            <thead>
                                <tr>
                                    <th style="width:70px;">Tip</th>
                                    <th style="width:130px;">KB Broj</th>
                                    <th>Naziv Datoteke</th>
                                    <th style="width:110px;">Veličina</th>
                                    <th style="width:170px;">Datum Izmene</th>
                                </tr>
                            </thead>
                            <tbody id="updates-table-body">
                                <tr>
                                    <td colspan="5" style="text-align:center; padding:30px; color:var(--text-muted);">Učitavanje liste zakrpi...</td>
                                </tr>
                            </tbody>
                        </table>
                    </div>
                </section>
            </main>
        </div>
    </div>

    <!-- =====================================================================
         Modali
         ===================================================================== -->
    <!-- Modal 1: Live Log Terminal -->
    <div class="modal-overlay" id="log-modal">
        <div class="modal-box">
            <div class="modal-header">
                <div class="modal-title">
                    <span>📟 Live Log Terminal:</span>
                    <span id="modal-client-name" style="color:#fff;">DESKTOP</span>
                    <span class="header-badge" id="modal-phase-badge">Faza 2</span>
                </div>
                <div style="display:flex; gap:10px; align-items:center;">
                    <button class="btn-log" id="btn-download-log" style="display:inline-flex;">Preuzmi Log</button>
                    <button class="modal-close" onclick="closeLogModal()">&times;</button>
                </div>
            </div>
            <div class="terminal-container" id="terminal-content">
                <div class="log-line DEBUG">Povezivanje na live stream loga...</div>
            </div>
        </div>
    </div>

    <!-- Modal 2: Servisni Karton -->
    <div class="modal-overlay" id="report-modal">
        <div class="modal-box">
            <div class="modal-header">
                <div class="modal-title">
                    <span>📋 Servisni Karton:</span>
                    <span id="report-client-name" style="color:#fff;">DESKTOP</span>
                    <span class="header-badge" id="report-duration-badge" style="color:var(--green); border-color:var(--green);">Završeno</span>
                </div>
                <div style="display:flex; gap:10px; align-items:center;">
                    <button class="btn-report" id="btn-download-report">Preuzmi JSON</button>
                    <button class="modal-close" onclick="closeReportModal()">&times;</button>
                </div>
            </div>
            <div class="terminal-container" id="report-modal-content" style="padding: 20px; overflow-y: auto;">
                <div class="log-line DEBUG">Učitavanje servisnog kartona...</div>
            </div>
        </div>
    </div>

    <!-- Modal 3: Servisna Istorija i Dnevnik -->
    <div class="modal-overlay" id="history-modal">
        <div class="modal-box">
            <div class="modal-header">
                <div class="modal-title">
                    <span>📜 Servisna Istorija & Dnevnik:</span>
                    <span id="history-client-name" style="color:#fff;">UREĐAJ</span>
                    <span class="header-badge" id="history-total-badge" style="color:var(--purple); border-color:var(--purple);">0 Intervencija</span>
                </div>
                <button class="modal-close" onclick="closeHistoryModal()">&times;</button>
            </div>
            <div class="terminal-container" id="history-modal-content" style="padding: 20px; overflow-y: auto;">
                <div class="log-line DEBUG">Učitavanje servisne istorije...</div>
            </div>
        </div>
    </div>

    <!-- Modal 4: Potvrda Daljinske Komande (Remote BIOS Reboot) -->
    <div class="modal-overlay" id="command-confirm-modal">
        <div class="modal-box" style="max-width: 520px; height: auto; max-height: 90vh;">
            <div class="modal-header">
                <div class="modal-title" style="color:var(--warning);">
                    <span>⚡</span> Potvrda Daljinske Komande
                </div>
                <button class="modal-close" onclick="closeCommandConfirmModal()">&times;</button>
            </div>
            <div style="padding: 24px; display: flex; flex-direction: column; gap: 16px;">
                <div id="command-confirm-message" style="font-size: 0.95rem; line-height: 1.5; color: var(--text-primary);">
                    Da li ste sigurni da želite da pošaljete komandu na radnu stanicu?
                </div>
                <div style="font-size: 0.8rem; color: var(--text-muted); background: rgba(245, 158, 11, 0.08); border: 1px solid rgba(245, 158, 11, 0.25); border-radius: var(--radius-sm); padding: 10px 14px; display: flex; align-items: center; gap: 8px;">
                    <span>⚠️</span> Radna stanica će preuzeti i primeniti zadatu komandu pri sledećem heartbeat kontaktu sa serverom.
                </div>
                <div style="display: flex; justify-content: flex-end; gap: 10px; margin-top: 8px;">
                    <button class="btn-cockpit btn-secondary" onclick="closeCommandConfirmModal()" style="padding: 8px 16px; font-size: 0.85rem;">
                        Odustani
                    </button>
                    <button id="btn-confirm-command-action" class="btn-cockpit btn-warning-solid" onclick="executeConfirmedCommand()" style="padding: 8px 18px; font-size: 0.85rem; font-weight: 700;">
                        <span>✓</span> Potvrdi i pošalji
                    </button>
                </div>
            </div>
        </div>
    </div>

    <!-- Kontejner za Toast Notifikacije -->
    <div id="toast-container" class="toast-container"></div>

    <!-- =====================================================================
         Vanilla JavaScript Logika
         ===================================================================== -->
    <script>
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
            } else if (tabId === 'updates') {
                stopCockpitScreenMirror();
                loadUpdatesStatus();
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
                                    <span style="color:var(--accent); font-weight:700;">[TEHNO-KUTAK COCKPIT]</span>
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

            fetch('/api/wis/usb/burn', {
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

            fetch('/api/devices/' + encodeURIComponent(devKey) + '/command', {
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
    </script>
</body>
</html>
"""


from servishub.version import VERSION
HTML_DASHBOARD = HTML_DASHBOARD.replace("__WIS_VERSION__", VERSION)
