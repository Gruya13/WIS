"""
servishub.web.dashboard
=======================
Ugrađeni Modern Enterprise Dark-Mode Web Dashboard (HTML5, CSS3, Vanilla JS, SSE).
Implementira Linear/Vercel estetiku, Collapsible Sidebar, Hibridni Cockpit sa 1 Hz ogledalom ekrana
i Zero-Dependency arhitekturu usklađenu sa Google Stitch DESIGN.md i PRD/RFC specifikacijama.

HTML, CSS i JS izvori nalaze se odvojeno u ``servishub/web/static/`` radi čitljivosti,
lintovanja i direktnog serviranja, a ``HTML_DASHBOARD`` ih spaja bez spoljnih zavisnosti.
"""

import os
from servishub.version import VERSION

_STATIC_DIR = os.path.join(os.path.dirname(__file__), "static")


def _load_dashboard_html() -> str:
    index_path = os.path.join(_STATIC_DIR, "index.html")
    css_path = os.path.join(_STATIC_DIR, "dashboard.css")
    js_path = os.path.join(_STATIC_DIR, "dashboard.js")

    with open(index_path, "r", encoding="utf-8") as f:
        html_tpl = f.read()
    with open(css_path, "r", encoding="utf-8") as f:
        css_src = f.read()
    with open(js_path, "r", encoding="utf-8") as f:
        js_src = f.read()

    html = html_tpl.replace("<!-- __DASHBOARD_CSS__ -->", f"<style>\n{css_src}    </style>")
    html = html.replace("<!-- __DASHBOARD_JS__ -->", f"<script>\n{js_src}    </script>")
    return html.replace("__WIS_VERSION__", VERSION)


HTML_DASHBOARD = _load_dashboard_html()
