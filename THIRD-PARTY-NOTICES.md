# Third-Party Notices and Open-Source Attributions

The **WIS (Windows Installation Assistant)** project is licensed under the [MIT License](LICENSE).
To ensure deterministic, offline-capable builds (`UI/Build-UI.py`, `UI/HwTest/Build-HwTest.py`, and `ISO/Skripte/Build-WIS.ps1`), the repository includes only the managed library below under its respective license:

---

## 1. ZXing.Net (`UI/HwTest/zxing.dll`)
- **Project:** ZXing.Net (https://github.com/micjahn/ZXing.Net)
- **License:** Apache License 2.0 (**Apache-2.0**)
- **Usage in WIS:** Embedded as a managed assembly resource (`Assets.zxing.dll`) in `WIS.UI.exe` and `WIS.HwTest.exe` for QR code decoding (`WIS-CAMTEST-v1`) during interactive camera hardware verification (`UI/HwTest/Tests/CameraTestWindow.cs`).
- **License Text:** https://www.apache.org/licenses/LICENSE-2.0

---

## Removed Dependencies (`WIS v3.1.0`)
- **`LibreHardwareMonitorLib.dll` (MPL-2.0) and `HidSharp.dll` (Apache-2.0):** Completely removed from the repository, build contracts, signed OTA manifests (`REQUIRED_310_COMPONENTS`), and runtime payloads starting with **WIS v3.1.0** in order to permanently eliminate any exposure to the `WinRing0` kernel driver vulnerability (**CVE-2020-14979** / BYOVD). Thermal telemetry relies exclusively on hardened native Windows WMI/ACPI, performance counters, SMART, and OEM WMI (`Lenovo_SensorData`, `HP_ThermalSensor`, Dell `DCIM_NumericSensor`) providers.
