# DESIGN.md - Servis Hub Enterprise Design System

## 1. Visual Theme & Atmosphere
* **Mood & Vibe:** Modern Enterprise Dark Mode, Linear/Vercel aesthetic, dark slate background, high data density, subtle borders, telemetry cockpit
* **Design Philosophy:** Content-first clarity, consistent rhythm, high contrast readability, and purposeful micro-interactions.
* **Surface Hierarchy:** Subtle elevation with soft border highlights rather than heavy drop shadows.

## 2. Color Palette & Roles
* **Primary Brand:** `#38bdf8` (Used for primary CTA buttons, active tabs, progress bars)
* **Secondary / Accent:** `#3B82F6` (Hover states, badges, highlight tags)
* **Canvas / Background:** `#0F172A` (Main viewport background)
* **Surface / Container:** `#1E293B` (Card backgrounds, modals, sidebars)
* **Text Primary:** `#F8FAFC` (Headings, primary body text)
* **Text Muted:** `#94A3B8` (Subtitles, placeholders, secondary metadata)
* **Border / Divider:** `#334155` (Subtle 1px boundaries)
* **Success:** `#10B981` (Completed states, green alerts)
* **Warning:** `#F59E0B` (Cautionary indicators)
* **Error / Destructive:** `#EF4444` (Validation errors, danger buttons)

## 3. Typography Rules
* **Font Family:** `Inter, -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, monospace`
* **Display / Hero:** 36px | Bold (700) | Line-height: 1.2
* **Heading 1:** 28px | SemiBold (600) | Line-height: 1.3
* **Heading 2:** 22px | SemiBold (600) | Line-height: 1.35
* **Heading 3:** 18px | Medium (500) | Line-height: 1.4
* **Body Regular:** 15px | Normal (400) | Line-height: 1.5
* **Body Small / Caption:** 13px | Normal (400) | Line-height: 1.4
* **Button / Action:** 14px | SemiBold (600) | Letter-spacing: 0.02em

## 4. Component Stylings
* **Corner Radius (Shapes):** Default `12px`, Small `6px`, Pill `9999px`.
* **Primary Buttons:** Background `#38bdf8`, Text `#FFFFFF`, Padding `10px 18px`, Radius `12px`, smooth transition 150ms.
* **Secondary Buttons:** Background `transparent`, Border `1px solid #334155`, Text `#F8FAFC`.
* **Input Fields:** Background `#1E293B`, Border `1px solid #334155`, Focus ring `2px solid #38bdf8`, Radius `8px`.
* **Cards / Panels:** Background `#1E293B`, Border `1px solid #334155`, Padding `20px`, Radius `12px`.

## 5. Layout & Spacing Principles
* **Base Grid Unit:** 4px baseline grid (4px, 8px, 12px, 16px, 24px, 32px, 48px, 64px).
* **Container Widths:** Mobile max `428px`, Tablet max `768px`, Desktop max `1280px`.
* **Content Padding:** Mobile `16px`, Desktop `24px` to `32px`.

## 6. Constraints & Guardrails (Do's and Don'ts)
* **DO** maintain strict WCAG AA contrast ratio (minimum 4.5:1 for normal text).
* **DO** use consistent padding inside all list items and interactive components.
* **DO NOT** mix rounded and sharp corners inconsistently.
* **DO NOT** use unstyled default browser controls.
* **DO NOT** hardcode inline random color values outside the palette tokens.
