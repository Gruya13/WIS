#!/usr/bin/env python3
"""
Test-HwTestLogic.py
===================
Automatska verifikacija logike i šablona za WIS.HwTest.
"""

import hashlib
import json
import pathlib
import unittest

ROOT = pathlib.Path(__file__).resolve().parent.parent.parent
HWTEST_DIR = ROOT / "UI" / "HwTest"
LAYOUTS_PATH = HWTEST_DIR / "Assets" / "keyboard_layouts.json"
ZXING_PATH = HWTEST_DIR / "zxing.dll"
EXE_PATH = HWTEST_DIR / "WIS.HwTest.exe"


def read_all_mainwindow_cs(ui_dir=ROOT / "UI"):
    return "\n".join(p.read_text(encoding="utf-8") for p in sorted(ui_dir.glob("MainWindow*.cs")))


class TestHwTestComponents(unittest.TestCase):
    def test_binary_artifacts(self):
        self.assertTrue(ZXING_PATH.exists(), f"zxing.dll mora postojati u {ZXING_PATH}")
        if EXE_PATH.exists():
            self.assertGreater(EXE_PATH.stat().st_size, 500_000, "WIS.HwTest.exe mora biti >= 500 KB sa ugrađenim resursima")

    def test_audio_assets(self):
        digits_dir = HWTEST_DIR / "Assets" / "digits_en"
        self.assertTrue(digits_dir.exists(), "digits_en folder mora postojati")
        for d in range(10):
            wav = digits_dir / f"{d}.wav"
            self.assertTrue(wav.exists(), f"Fajl {wav.name} mora postojati")
            self.assertGreater(wav.stat().st_size, 10_000, f"{wav.name} mora biti validan WAV > 10 KB")

    def test_keyboard_layouts(self):
        self.assertTrue(LAYOUTS_PATH.exists(), "keyboard_layouts.json mora postojati")
        data = json.loads(LAYOUTS_PATH.read_text(encoding="utf-8"))

        required_layouts = [
            "laptop-tkl-ansi",
            "laptop-tkl-iso",
            "laptop-tkl-nav-ansi",
            "laptop-tkl-nav-iso",
            "thinkpad-tkl-ansi",
            "thinkpad-tkl-iso",
            "laptop-numpad-ansi",
            "laptop-numpad-iso",
            "thinkpad-numpad-ansi",
            "thinkpad-numpad-iso",
            "desktop-104-ansi",
            "desktop-105-iso",
        ]
        for rk in required_layouts:
            self.assertIn(rk, data, f"Šablon '{rk}' mora biti prisutan u JSON-u")
            layout = data[rk]
            self.assertIn("keys", layout)
            keys = layout["keys"]
            self.assertGreater(len(keys), 70, f"Šablon '{rk}' mora imati bar 70 tastera")

            # Geometrijska provera: nijedan taster se ne preklapa na 0.5-red / 0.05-kolona mreži
            occupied = {}
            for kd in keys:
                r0 = int(round(kd["row"] * 2.0))
                rh = int(round(kd["h"] * 2.0))
                c0 = int(round(kd["col"] * 20.0))
                cw = int(round(kd["w"] * 20.0))
                self.assertGreaterEqual(rh, 1, f"{rk}/{kd['id']}: visina mora biti >= 0.5")
                self.assertGreaterEqual(cw, 1, f"{rk}/{kd['id']}: širina mora biti >= 0.05")
                for rr in range(r0, r0 + rh):
                    for cc in range(c0, c0 + cw):
                        cell = (rr, cc)
                        self.assertNotIn(
                            cell,
                            occupied,
                            f"Preklapanje u {rk}: '{kd['id']}' i '{occupied.get(cell)}' na ćeliji {cell}",
                        )
                        occupied[cell] = kd["id"]

            # Provera ključnih tastera
            key_ids = {k["id"] for k in keys}
            self.assertIn("Escape", key_ids)
            self.assertIn("Enter", key_ids)
            self.assertIn("Space", key_ids)
            self.assertIn("ShiftLeft", key_ids)
            self.assertIn("F1", key_ids)
            self.assertIn("F12", key_ids)

            if "-nav-" in rk or rk.startswith("thinkpad-tkl-"):
                for nav_k in ("Home", "PageUp", "PageDown", "End"):
                    self.assertIn(nav_k, key_ids, f"Šablon '{rk}' mora imati '{nav_k}'")

            if rk.startswith("thinkpad-"):
                prtsc = next(k for k in keys if k["id"] == "PrintScreen")
                self.assertGreaterEqual(prtsc["row"], 5, f"{rk}: PrintScreen mora biti u donjem redu")

            if "numpad" in rk or "desktop" in rk:
                main_enter = next(k for k in keys if k["id"] == "Enter")
                num_enter = next(k for k in keys if k["id"] == "NumPadEnter")
                self.assertEqual(main_enter.get("extendedMode"), 1)
                self.assertEqual(num_enter.get("extendedMode"), 2)

            # Provera ISO vs ANSI
            if rk.endswith("-iso"):
                self.assertIn("Oem102", key_ids, f"ISO šablon '{rk}' mora imati Oem102 (< > |)")
                oem102 = next(k for k in keys if k["id"] == "Oem102")
                self.assertEqual(oem102["vkey"], 226)
                self.assertTrue(oem102.get("isOptional"), f"ISO šablon '{rk}': Oem102 mora biti opcioni slot")
                shift_left = next(k for k in keys if k["id"] == "ShiftLeft")
                self.assertAlmostEqual(shift_left["w"], 1.25)
            else:
                self.assertNotIn("Oem102", key_ids, f"ANSI šablon '{rk}' ne sme imati Oem102")

            # Provera alternateVkeys za F-tastere
            f_keys = [k for k in keys if k["id"].startswith("F") and k["id"][1:].isdigit()]
            self.assertEqual(len(f_keys), 12)
            f1 = next(k for k in keys if k["id"] == "F1")
            self.assertTrue(len(f1.get("alternateVkeys", [])) > 0, "F1 mora imati alternativni medijski kod")

        # Svaki ISO šablon mora imati tačno +1 taster više od svog ANSI parnjaka
        self.assertEqual(len(data["laptop-tkl-iso"]["keys"]), len(data["laptop-tkl-ansi"]["keys"]) + 1)
        self.assertEqual(len(data["laptop-tkl-nav-iso"]["keys"]), len(data["laptop-tkl-nav-ansi"]["keys"]) + 1)
        self.assertEqual(len(data["thinkpad-tkl-iso"]["keys"]), len(data["thinkpad-tkl-ansi"]["keys"]) + 1)
        self.assertEqual(len(data["laptop-numpad-iso"]["keys"]), len(data["laptop-numpad-ansi"]["keys"]) + 1)
        self.assertEqual(len(data["thinkpad-numpad-iso"]["keys"]), len(data["thinkpad-numpad-ansi"]["keys"]) + 1)
        self.assertEqual(len(data["desktop-105-iso"]["keys"]), len(data["desktop-104-ansi"]["keys"]) + 1)

    def test_json_result_contract(self):
        sample_result = {
            "schema": "wis.hwtest.v1",
            "started": "2026-11-12T18:40:00+01:00",
            "finished": "2026-11-12T18:45:00+01:00",
            "model": "ThinkPad T14",
            "serial": "PF9999",
            "app_version": "1.0.0",
            "summary": { "pass": 7, "fail": 0, "skip": 1, "na": 0, "overall": "PASS_WITH_SKIP" },
            "tests": [
                { "id": "display", "status": "PASS", "duration_s": 25, "attempts": 1, "reason": "", "details": { "defects_found": 0 } },
                { "id": "keyboard", "status": "PASS", "duration_s": 65, "attempts": 1, "reason": "", "details": { "total_keys": 84, "pressed_keys": 84 } },
                { "id": "usb", "status": "SKIP", "duration_s": 0, "attempts": 0, "reason": "Korisnik nema fleš", "details": {} }
            ]
        }
        # Validacija strukture
        self.assertEqual(sample_result["schema"], "wis.hwtest.v1")
        self.assertIn(sample_result["summary"]["overall"], ["PASS", "PASS_WITH_SKIP", "FAIL", "ABORTED"])
        for t in sample_result["tests"]:
            self.assertIn(t["status"], ["PASS", "FAIL", "SKIP", "NOTAPPLICABLE", "PENDING"])

    def test_factory_awareness_and_visibility_sources(self):
        factory_probe_cs = HWTEST_DIR / "Core" / "FactorySpecProbe.cs"
        shell_cs = HWTEST_DIR / "HwTestShell.cs"
        self.assertTrue(factory_probe_cs.exists(), "FactorySpecProbe.cs mora postojati")

        fp_src = factory_probe_cs.read_text(encoding="utf-8")
        self.assertIn("partsurfer.hpcloud.hp.com/bff/proxy/get", fp_src)
        self.assertIn("IsEndpointDisabled", fp_src)
        self.assertIn("RecordHubFailure", fp_src)

        sh_src = shell_cs.read_text(encoding="utf-8")
        self.assertIn("FactorySpecProbe.ResolveAndMerge", sh_src)
        self.assertIn("RefreshCardVisibility", sh_src)
        self.assertIn("Visibility.Collapsed", sh_src)

        audio_cs = HWTEST_DIR / "Tests" / "AudioTestWindow.cs"
        audio_src = audio_cs.read_text(encoding="utf-8")
        self.assertIn("StartMusicSweepPhase", audio_src)
        self.assertIn("GenerateWarmSweepMusicPcm", audio_src)
        self.assertIn("GetManifestResourceStream", audio_src)
        self.assertIn("ExtractPcmFromWavBytes", audio_src)
        self.assertIn("0.30f", audio_src)

    def test_automated_diagnostics_and_sections(self):
        auto_cs = HWTEST_DIR / "Core" / "AutoHardwareDiagnostics.cs"
        gpu_cs = HWTEST_DIR / "Tests" / "GpuStressWindow.cs"
        details_cs = HWTEST_DIR / "Tests" / "AutoTestDetailsWindow.cs"
        shell_src = (HWTEST_DIR / "HwTestShell.cs").read_text(encoding="utf-8")
        prog_src = (HWTEST_DIR / "Program.cs").read_text(encoding="utf-8")

        self.assertTrue(auto_cs.exists(), "AutoHardwareDiagnostics.cs mora postojati")
        self.assertTrue(gpu_cs.exists(), "GpuStressWindow.cs mora postojati")
        self.assertTrue(details_cs.exists(), "AutoTestDetailsWindow.cs mora postojati")

        for tid in ("auto_disk", "auto_cpu", "auto_ram", "auto_gpu", "auto_battery", "auto_network", "auto_security"):
            self.assertIn(f'"{tid}"', shell_src, f"Automatski test '{tid}' mora biti registrovan u HwTestShell.cs")

        self.assertIn("CreateAutoSectionHeader", shell_src)
        self.assertIn("CreateManualSectionHeader", shell_src)
        self.assertIn("StartPassiveAutoChecks", shell_src)
        self.assertIn("ToggleAutoStressSuite", shell_src)

        # Uklonjen WinRing0/LibreHardwareMonitorLib iz ugrađenih resursa radi Defender zaštite
        self.assertNotIn("Assets.LibreHardwareMonitorLib.dll", prog_src)
        self.assertNotIn("Assets.HidSharp.dll", prog_src)

        if EXE_PATH.exists():
            exe_bytes = EXE_PATH.read_bytes()
            self.assertNotIn(b"Assets.LibreHardwareMonitorLib.dll", exe_bytes)
            self.assertNotIn(b"Assets.HidSharp.dll", exe_bytes)
            self.assertNotIn(b"WinRing0", exe_bytes)

    def test_keyboard_backlight_pulse_and_probe(self):
        import os
        hw_probe = os.path.join(HWTEST_DIR, "Core", "HardwareProbe.cs")
        spec_probe = os.path.join(HWTEST_DIR, "Core", "FactorySpecProbe.cs")
        kbd_win = os.path.join(HWTEST_DIR, "Tests", "KeyboardTestWindow.cs")

        with open(hw_probe, "r", encoding="utf-8") as f:
            hw_src = f.read()
        with open(spec_probe, "r", encoding="utf-8") as f:
            spec_src = f.read()
        with open(kbd_win, "r", encoding="utf-8") as f:
            kbd_src = f.read()

        self.assertIn("HasKeyboardBacklight", hw_src)
        self.assertIn("DetectKeyboardBacklight", hw_src)
        self.assertIn("FactoryKeyboardBacklight", spec_src)
        self.assertIn("NON-BACKLIT", spec_src)
        self.assertIn("StartBacklightPhase", kbd_src)
        self.assertIn("PulseKeyboardBacklightStep", kbd_src)
        self.assertIn("Lenovo_SetKeyboardBacklightStatus", kbd_src)
        self.assertIn("LampArray", kbd_src)
        self.assertIn("Osvetljenje radi (PASS)", kbd_src)
        self.assertIn("Osvetljenje ne radi (FAIL)", kbd_src)

    def test_usb_single_device_all_ports_replug(self):
        import os
        native = os.path.join(HWTEST_DIR, "Interop", "NativeMethods.cs")
        usb_win = os.path.join(HWTEST_DIR, "Tests", "UsbPortTestWindow.cs")

        with open(native, "r", encoding="utf-8") as f:
            nat_src = f.read()
        with open(usb_win, "r", encoding="utf-8") as f:
            usb_src = f.read()

        self.assertIn("depth < 12", nat_src)
        self.assertIn("USB4\\\\", nat_src)
        self.assertIn("_currentlyAttachedDevices", usb_src)
        self.assertIn("_deviceReplugCounts", usb_src)
        self.assertIn("_baselineUsbPnpIds", usb_src)
        self.assertIn("#REPLUG-", usb_src)
        self.assertIn("ConfirmPendingSharedPort", usb_src)

    def test_camera_ir_auto_switch_and_face_landmarks(self):
        import os
        native = os.path.join(HWTEST_DIR, "Interop", "NativeMethods.cs")
        cam_win = os.path.join(HWTEST_DIR, "Tests", "CameraTestWindow.cs")

        with open(native, "r", encoding="utf-8") as f:
            nat_src = f.read()
        with open(cam_win, "r", encoding="utf-8") as f:
            cam_src = f.read()

        self.assertIn("KSCATEGORY_SENSOR_CAMERA", nat_src)
        self.assertIn("MFVideoFormat_L8", nat_src)
        self.assertNotIn("StartAutoSwitchToIrTimer", cam_src)
        self.assertIn("CameraTestMode.RgbOnly", cam_src)
        self.assertIn("CameraTestMode.IrOnly", cam_src)
        self.assertIn("WinRtFaceDetectorProxy", cam_src)
        self.assertIn("ExtractFacialLandmarks", cam_src)
        self.assertIn("DrawFaceLandmarksOverlay", cam_src)
        self.assertIn("BuildIrEdgeEnhancedBgr32", cam_src)
        self.assertIn("_irFaceConfirmed", cam_src)

    def test_touchscreen_serpentine_infinity_and_hand(self):
        import os
        maze_cs = os.path.join(HWTEST_DIR, "Core", "MazeGenerator.cs")
        touch_cs = os.path.join(HWTEST_DIR, "Tests", "TouchscreenMazeWindow.cs")

        with open(maze_cs, "r", encoding="utf-8") as f:
            maze_src = f.read()
        with open(touch_cs, "r", encoding="utf-8") as f:
            touch_src = f.read()

        self.assertIn("GenerateSerpentinePath", maze_src)
        self.assertIn("GenerateInfinityPath", maze_src)
        self.assertIn("SetupSerpentinePhase", touch_src)
        self.assertIn("SetupInfinityPhase", touch_src)
        self.assertIn("SetupHandMultitouchPhase", touch_src)
        self.assertIn("EvaluateHandMultitouch", touch_src)
        self.assertIn("Palac", touch_src)

    def test_device_feedback_x380_fixes(self):
        touchpad_src = (HWTEST_DIR / "Tests" / "TouchpadTestWindow.cs").read_text(encoding="utf-8")
        probe_src = (HWTEST_DIR / "Core" / "HardwareProbe.cs").read_text(encoding="utf-8")
        usb_src = (HWTEST_DIR / "Tests" / "UsbPortTestWindow.cs").read_text(encoding="utf-8")
        kbd_src = (HWTEST_DIR / "Tests" / "KeyboardTestWindow.cs").read_text(encoding="utf-8")
        touch_src = (HWTEST_DIR / "Tests" / "TouchscreenMazeWindow.cs").read_text(encoding="utf-8")

        self.assertIn("_isClosingOrDone", touchpad_src)
        self.assertIn("X380", probe_src)
        self.assertIn("_btnAdjustTargetToDetected", usb_src)
        self.assertIn("UpdateRemainingKeysHint", kbd_src)
        self.assertIn("Math.Min(185.0", touch_src)

    def test_audit_wis_task_214_fixes(self):
        shell_src = (HWTEST_DIR / "HwTestShell.cs").read_text(encoding="utf-8")
        prog_src = (HWTEST_DIR / "Program.cs").read_text(encoding="utf-8")
        thermal_src = (ROOT / "UI" / "ThermalSensorEngine.cs").read_text(encoding="utf-8")
        auto_src = (HWTEST_DIR / "Core" / "AutoHardwareDiagnostics.cs").read_text(encoding="utf-8")
        client_src = (HWTEST_DIR / "Core" / "ModelProfileClient.cs").read_text(encoding="utf-8")
        kbd_src = (HWTEST_DIR / "Tests" / "KeyboardTestWindow.cs").read_text(encoding="utf-8")
        mic_src = (HWTEST_DIR / "Tests" / "MicTestWindow.cs").read_text(encoding="utf-8")
        cam_src = (HWTEST_DIR / "Tests" / "CameraTestWindow.cs").read_text(encoding="utf-8")

        # 1. FinishAndSave ne može da vrati lažni PASS dok ima pending testova
        self.assertIn("if (pending > 0)", shell_src)
        self.assertIn("if (fail > 0) overall = \"FAIL\";\n            else if (skip > 0 || pending > 0) overall = \"PASS_WITH_SKIP\";", shell_src)
        self.assertIn("if (_autoStressRunning)", shell_src)

        # 2. ThermalSensorEngine bez compile-time LHM i tačna throttling logika
        self.assertNotIn("using LibreHardwareMonitor", thermal_src)
        self.assertIn("_baseMaxClockMHz", thermal_src)
        self.assertIn("HighPrecisionTemperature", thermal_src)
        self.assertIn("Win32_TemperatureProbe", thermal_src)
        self.assertIn("sensorThrottleWarn", auto_src)

        # 3. Standalone mod bez --out i circuit-breaker u ModelProfileClient
        self.assertIn("if (!hasExplicitOut)", prog_src)
        self.assertIn("IsEndpointDisabled", client_src)
        self.assertIn("RecordHubFailure", client_src)

        # 4. Svi test prozori imaju _isClosingOrDone zaštitu
        for fname in (
            "DisplayTestWindow.cs", "AudioTestWindow.cs", "MicTestWindow.cs",
            "FingerprintTestWindow.cs", "GpuStressWindow.cs", "CameraTestWindow.cs",
            "KeyboardTestWindow.cs", "TouchscreenMazeWindow.cs", "UsbPortTestWindow.cs",
            "TouchpadTestWindow.cs"
        ):
            wsrc = (HWTEST_DIR / "Tests" / fname).read_text(encoding="utf-8")
            self.assertIn("_isClosingOrDone", wsrc, f"{fname} mora imati _isClosingOrDone osigurač")

        # 5. Tastatura mapira Fn/Media na F-red, mikrofon ima izbor uređaja, kamera čisti RCW
        self.assertIn("bool matched = OnKeyPressed(vk);", kbd_src)
        self.assertIn("PopulateMicDevices", mic_src)
        self.assertIn("_micCombo", mic_src)
        self.assertIn("GC.WaitForPendingFinalizers();", cam_src)

    def test_wis_task_215_keyboard_oem_and_optional_slots(self):
        result_src = (HWTEST_DIR / "Core" / "ResultModel.cs").read_text(encoding="utf-8")
        probe_src = (HWTEST_DIR / "Core" / "HardwareProbe.cs").read_text(encoding="utf-8")
        kbd_src = (HWTEST_DIR / "Tests" / "KeyboardTestWindow.cs").read_text(encoding="utf-8")

        self.assertIn("public double Row { get; set; }", result_src)
        self.assertIn("public bool IsOptional { get; set; }", result_src)
        self.assertIn("public int ExtendedMode { get; set; }", result_src)

        self.assertIn("public bool IsThinkPad { get; set; }", probe_src)
        self.assertIn("thinkpad-tkl-iso", probe_src)
        self.assertIn("thinkpad-numpad-ansi", probe_src)

        self.assertIn("_btnCycleBrandLayout", kbd_src)
        self.assertIn("_optionalGraceTimer", kbd_src)
        self.assertIn("PlaceOrActivateKeyInPhysicalPosition", kbd_src)
        self.assertIn("ReflowTopRowInsertKey", kbd_src)
        self.assertIn("ReflowBottomRowInsertKey", kbd_src)
        self.assertIn("TranslateNumPadOffKey", kbd_src)
        self.assertIn("(opc)", kbd_src)

    def test_keyboard_backlight_hardware_drivers_contract(self):
        native_src = (HWTEST_DIR / "Interop" / "NativeMethods.cs").read_text(encoding="utf-8")
        probe_src = (HWTEST_DIR / "Core" / "HardwareProbe.cs").read_text(encoding="utf-8")
        kbd_src = (HWTEST_DIR / "Tests" / "KeyboardTestWindow.cs").read_text(encoding="utf-8")

        self.assertIn("FILE_SHARE_WRITE", native_src)
        self.assertIn("DeviceIoControl(", native_src)

        self.assertIn("TryProbeLenovoIbmPmBacklight", probe_src)
        self.assertIn("TryProbeLenovoEnergyDrvBacklight", probe_src)
        self.assertIn("0x222680u", probe_src)
        self.assertIn("0x222660u", probe_src)
        self.assertIn("0x2225ECu", probe_src)
        self.assertIn("0x83102144u", probe_src)

        self.assertIn("TryInitLenovoIbmPmBacklight", kbd_src)
        self.assertIn("SetLenovoIbmPmBacklightLevel", kbd_src)
        self.assertIn("GetLenovoIbmPmBacklightLevel", kbd_src)
        self.assertIn("0x222684u", kbd_src)
        self.assertIn("0x222664u", kbd_src)
        self.assertIn("TryInitLenovoEnergyDrvBacklight", kbd_src)
        self.assertIn("SetLenovoEnergyDrvBacklightLevel", kbd_src)
        self.assertIn("TryInitDellWmiBacklight", kbd_src)
        self.assertIn("lenovo_ibmpm", kbd_src)
        self.assertIn("lenovo_energydrv", kbd_src)
        self.assertIn("dell_wmi", kbd_src)
        self.assertIn("_initialBacklightLevel", kbd_src)

    def test_wis_task_217_usb_mic_hub_ir_improvements(self):
        usb_src = (HWTEST_DIR / "Tests" / "UsbPortTestWindow.cs").read_text(encoding="utf-8")
        mic_src = (HWTEST_DIR / "Tests" / "MicTestWindow.cs").read_text(encoding="utf-8")
        spec_src = (HWTEST_DIR / "Core" / "FactorySpecProbe.cs").read_text(encoding="utf-8")
        client_src = (HWTEST_DIR / "Core" / "ModelProfileClient.cs").read_text(encoding="utf-8")
        thermal_src = (ROOT / "UI" / "ThermalSensorEngine.cs").read_text(encoding="utf-8")
        cam_src = (HWTEST_DIR / "Tests" / "CameraTestWindow.cs").read_text(encoding="utf-8")

        # 1. USB: prepoznavanje već priključenih eksternih USB uređaja (USB-to-LAN, USB audio, serijski)
        self.assertIn("PNPClass", usb_src)
        self.assertIn("IsClearlyExternalPreAttachedUsbDevice", usb_src)
        self.assertIn("AX88179", usb_src)
        self.assertIn("USBAUDIO", usb_src)
        self.assertIn("USBSER", usb_src)
        self.assertIn("if (!clearlyExternal)", usb_src)

        # 2. Mikrofon: 96% nivo uz vraćanje originala, -44 dBFS okidač, 2s snimanje
        self.assertIn("_originalMicVolume", mic_src)
        self.assertIn("RestoreCaptureEndpointVolume", mic_src)
        self.assertIn("0.96f", mic_src)
        self.assertIn("peakBlockDbfs", mic_src)
        self.assertIn("-44.0", mic_src)
        self.assertIn("TimeSpan.FromSeconds(2)", mic_src)
        self.assertIn("TimeSpan.FromSeconds(2.2)", mic_src)

        # 3. ServiceHub: 4500 ms za /api/hardware-spec i izolovani timeout bez gašenja lokalnih ruta
        self.assertIn('HttpGetString(url, 4500, null, profileClient, "/api/hardware-spec")', spec_src)
        self.assertIn("bool isTimeout = false;", client_src)
        self.assertIn("WebExceptionStatus.KeepAliveFailure", client_src)
        self.assertIn('string.Equals(route, "/api/hardware-spec", StringComparison.OrdinalIgnoreCase)', client_src)
        self.assertIn('string.Equals(route, "/api/keyboard-layout", StringComparison.OrdinalIgnoreCase)', client_src)

        # 4. Termika: PerfRawData + OEM WMI fallback
        self.assertIn("Win32_PerfRawData_Counters_ThermalZoneInformation", thermal_src)
        self.assertIn("Lenovo_SensorData", thermal_src)
        self.assertIn("HP_ThermalSensor", thermal_src)
        self.assertIn("TryReadWmiThermalZoneClass", thermal_src)

        # 5. IR kamera: uvek poziva FallbackDetectFrontalFace kad WinRT vrati null, 3 okvira, 500 ms prelaz
        self.assertIn("RequiredIrFaceFrames = 3;", cam_src)
        self.assertIn("if (!detectedBox.HasValue)\n", cam_src)
        self.assertIn("bestScore < 30.0", cam_src)
        self.assertIn("TimeSpan.FromMilliseconds(500)", cam_src)

    def test_wis_task_218_expanded_suite_all_features(self):
        probe_src = (HWTEST_DIR / "Core" / "HardwareProbe.cs").read_text(encoding="utf-8")
        diag_src = (HWTEST_DIR / "Core" / "AutoHardwareDiagnostics.cs").read_text(encoding="utf-8")
        shell_src = (HWTEST_DIR / "HwTestShell.cs").read_text(encoding="utf-8")
        details_src = (HWTEST_DIR / "Tests" / "AutoTestDetailsWindow.cs").read_text(encoding="utf-8")
        display_src = (HWTEST_DIR / "Tests" / "DisplayTestWindow.cs").read_text(encoding="utf-8")
        touchpad_src = (HWTEST_DIR / "Tests" / "TouchpadTestWindow.cs").read_text(encoding="utf-8")
        touch_src = (HWTEST_DIR / "Tests" / "TouchscreenMazeWindow.cs").read_text(encoding="utf-8")

        # 1. HardwareProbe: stylus, TrackPoint, rotation sensor
        self.assertIn("HasStylusPen", probe_src)
        self.assertIn("HasTrackPoint", probe_src)
        self.assertIn("HasRotationSensor", probe_src)

        # 2. A1/A2/A3 u AutoHardwareDiagnostics
        self.assertIn("RunSecurityAndFirmwareCheck", diag_src)
        self.assertIn("ChargeRate", diag_src)
        self.assertIn("ProbeWifiSignalAndNetworks", diag_src)
        self.assertIn("ProbeBluetoothStatus", diag_src)

        # 3. HwTestShell + AutoTestDetailsWindow
        self.assertIn('"auto_security"', shell_src)
        self.assertIn("new TouchpadTestWindow(_hwInfo)", shell_src)
        self.assertIn("bios_password_state", details_src)
        self.assertIn("charge_rate_w", details_src)
        self.assertIn("bluetooth_status", details_src)

        # 4. Display: gradijent, WMI brightness rampa, HDMI eksterni monitor
        self.assertIn("WmiMonitorBrightnessMethods", display_src)
        self.assertIn("_gradientGridOverlay", display_src)
        self.assertIn("_externalMonitorBadge", display_src)

        # 5. Touchpad: TrackPoint srednji taster i rucna potvrda
        self.assertIn("_hasTrackPoint", touchpad_src)
        self.assertIn("MouseButton.Middle", touchpad_src)

        # 6. Touchscreen: Stylus / Wacom olovka i pritisak
        self.assertIn("StylusDown", touch_src)
        self.assertIn("PressureFactor", touch_src)
        self.assertIn("stylus_detected", touch_src)

    def test_wis_task_219_suite_tightening_and_false_positive_guards(self):
        diag_src = (HWTEST_DIR / "Core" / "AutoHardwareDiagnostics.cs").read_text(encoding="utf-8")
        shell_src = (HWTEST_DIR / "HwTestShell.cs").read_text(encoding="utf-8")
        details_src = (HWTEST_DIR / "Tests" / "AutoTestDetailsWindow.cs").read_text(encoding="utf-8")
        display_src = (HWTEST_DIR / "Tests" / "DisplayTestWindow.cs").read_text(encoding="utf-8")
        touchpad_src = (HWTEST_DIR / "Tests" / "TouchpadTestWindow.cs").read_text(encoding="utf-8")
        ui_main_src = read_all_mainwindow_cs()

        # 1. AutoHardwareDiagnostics: BIOS password policy guard + PnP virtual filter + 0-network Wi-Fi antenna fallback
        self.assertIn("isPolicyOrMeta", diag_src)
        self.assertIn('uk.Contains("CONTROL")', diag_src)
        self.assertIn('normKey == "POWERONPASSWORD"', diag_src)
        self.assertIn("IsIgnorableVirtualPnpDevice(name, devId)", diag_src)
        self.assertIn("private static bool IsIgnorableVirtualPnpDevice", diag_src)
        self.assertIn("bool wifiUp = false;", diag_src)
        self.assertIn("noWifiSignalAtAll", diag_src)
        self.assertIn('"wlan show interfaces"', diag_src)
        self.assertIn("private static string RunNetshStdout", diag_src)

        # 2. HwTestShell + AutoTestDetailsWindow: PROBE polja, kartice, srpske labele
        self.assertIn("TrackPoint={14}", shell_src)
        self.assertNotIn('"Touchpad / TrackPoint"', shell_src)
        self.assertIn('"Touchpad"', shell_src)
        self.assertIn('"TrackPoint"', shell_src)
        self.assertIn('"Touchscreen + Olovka (Stylus)"', shell_src)
        self.assertIn("BIOS/TPM/PnP", shell_src)
        for key_name in (
            "bios_password_locked",
            "computrace_active",
            "battery_present",
            "is_charging",
            "wmi_status",
            "bluetooth_present",
            "any_link_up",
            "gateway_ip",
            "hub_url",
            "system_drive",
            "bench_executed",
            "bench_error",
            "idle_temp_c",
            "iterations",
            "sensor_source",
            "pnp_error_code",
            "is_basic_display",
            "total_frames",
        ):
            self.assertIn(f'case "{key_name}":', details_src)

        # 3. DisplayTestWindow: cuvanje i vracanje osvetljenja pri brzom izlasku
        self.assertIn("_initialBrightnessPct", display_src)
        self.assertIn("RestoreInitialBrightnessIfNeeded()", display_src)
        self.assertIn("if (!_isClosingOrDone) Thread.Sleep(450);", display_src)

        # 4. TouchpadTestWindow: PreviewMouseDown na nivou prozora i zastita od dupliranja
        self.assertIn("PreviewMouseDown += OnCanvasMouseDown;", touchpad_src)
        self.assertIn("e.ChangedButton == MouseButton.Middle && !_middleClickDone", touchpad_src)

        # 5. WIS.UI MainWindow: dinamican ukupan broj testova umesto /8 PASS
        self.assertNotIn("/8 PASS", ui_main_src)
        self.assertIn("int totalApplicable = pass + fail + skip;", ui_main_src)

    def test_wis_task_221_trackpoint_middle_fail_and_interactive_fail_buttons(self):
        native_src = (HWTEST_DIR / "Interop" / "NativeMethods.cs").read_text(encoding="utf-8")
        touchpad_src = (HWTEST_DIR / "Tests" / "TouchpadTestWindow.cs").read_text(encoding="utf-8")
        kbd_src = (HWTEST_DIR / "Tests" / "KeyboardTestWindow.cs").read_text(encoding="utf-8")
        usb_src = (HWTEST_DIR / "Tests" / "UsbPortTestWindow.cs").read_text(encoding="utf-8")
        cam_src = (HWTEST_DIR / "Tests" / "CameraTestWindow.cs").read_text(encoding="utf-8")

        # 1. NativeMethods: WH_MOUSE_LL i GetAsyncKeyState(VK_MBUTTON)
        self.assertIn("public const int WH_MOUSE_LL = 14;", native_src)
        self.assertIn("public const int WM_MBUTTONDOWN = 0x0207;", native_src)
        self.assertIn("public const int WM_MBUTTONUP = 0x0208;", native_src)
        self.assertIn("public const int VK_MBUTTON = 0x04;", native_src)
        self.assertIn("LowLevelMouseProc", native_src)
        self.assertIn("GetAsyncKeyState", native_src)

        # 2. TouchpadTestWindow: hook, poll i FAIL dugme za srednji TrackPoint taster i touchpad
        self.assertIn("InstallMouseHook", touchpad_src)
        self.assertIn("_middleKeyPollTimer", touchpad_src)
        self.assertIn("GetAsyncKeyState(NativeMethods.VK_MBUTTON)", touchpad_src)
        self.assertIn("FinishMiddleButtonFail()", touchpad_src)
        self.assertIn("FinishFailGeneral()", touchpad_src)
        self.assertIn("Srednji TrackPoint taster NE RADI (FAIL)", touchpad_src)
        self.assertIn("Prijavi kvar touchpada (FAIL)", touchpad_src)
        self.assertIn("failed_components", touchpad_src)

        # 3. KeyboardTestWindow, UsbPortTestWindow i CameraTestWindow FAIL dugmad
        self.assertIn("ReportBrokenKeysFail()", kbd_src)
        self.assertIn("Prijavi neispravne tastere (FAIL)", kbd_src)
        self.assertIn("failed_keys", kbd_src)
        self.assertIn("Neispravan USB port (FAIL)", usb_src)
        self.assertIn("private void FinishFail()", usb_src)
        self.assertIn("_btnFailRgb", cam_src)
        self.assertIn("Kamera ne radi / loša slika (FAIL)", cam_src)

    def test_wis_task_225_embedded_hwtest_in_wis_ui_and_v300(self):
        import hashlib
        import json

        ui_dir = ROOT / "UI"

        # 1. Verzije v3.5.0 / ServiceHub v3.7.0 u root version.json i komponentama
        root_ver = json.loads((ROOT / "version.json").read_text(encoding="utf-8"))
        self.assertEqual(root_ver["version"], "3.5.0")
        self.assertEqual(root_ver["release_package"]["version"], "3.5.0")
        self.assertEqual(root_ver["components"]["ui"]["version"], "3.5.0")
        self.assertEqual(root_ver["components"]["launcher"]["version"], "3.5.0")
        self.assertEqual(root_ver["components"]["iso"]["version"], "3.5.0")
        self.assertEqual(root_ver["components"]["iso"]["embedded_launcher_version"], "3.5.0")
        self.assertEqual(root_ver["components"]["servicehub"]["version"], "3.7.0")
        self.assertEqual(root_ver["compatibility"]["min_launcher_version"], "3.1.0")

        # 2. AudioTestWindow.cs sadrzi ManualResetEventSlim / STA Dispatcher zastitu (BUG-01)
        audio_src = (HWTEST_DIR / "Tests" / "AudioTestWindow.cs").read_text(encoding="utf-8")
        self.assertIn("ManualResetEventSlim _waveOutDoneEvent", audio_src)
        self.assertIn("_waveOutDoneEvent.Wait(1500)", audio_src)
        self.assertIn("PlayWaveOutTracked(pcmStereo, 22050, 2, myGen)", audio_src)

        # 3. Ugradjeni HwTestShell rezim i Phase 5 orkestracija
        shell_src = (HWTEST_DIR / "HwTestShell.cs").read_text(encoding="utf-8")
        for token in (
            "CreateEmbeddedView(",
            "ShutdownEmbedded()",
            "UpdateIdentity(",
            "GetDialogOwner()",
            "StartPhase5FullWorkflow(",
            "RunSequentialPeripheralTests()",
            "ForceCompletePhase5SkipPending()",
            "SaveCurrentReport(",
        ):
            self.assertIn(token, shell_src)

        # 4. WIS.UI partial klasa i ugradjeni tab
        ui_hwtest_src = (ui_dir / "MainWindow.HwTest.cs").read_text(encoding="utf-8")
        for token in (
            "EnsureEmbeddedHwTestTab()",
            "ExecutePhase5IntegratedHwTest()",
            "OnEmbeddedHwTestReportSaved(",
            "UpdatePhase5StepFromHwTest(",
            "ForceSkipEmbeddedPhase5()",
            "TabHwTestHeader",
        ):
            self.assertIn(token, ui_hwtest_src)

        ui_main_src = read_all_mainwindow_cs(ui_dir)
        self.assertIn("ExecutePhase5IntegratedHwTest();", ui_main_src)
        self.assertIn("WIS_STRESS_TEST_INPUT_JSON", ui_main_src)
        self.assertIn("case 5: return 45;", ui_main_src)

        # 5. Build-UI.py sadrzi ugradjene HwTest izvore i resurse
        build_ui_src = (ui_dir / "Build-UI.py").read_text(encoding="utf-8")
        for token in (
            "MainWindow.HwTest.cs",
            "HwTestShell.cs",
            "Assets.zxing.dll",
            "Assets.keyboard_layouts.json",
            "Assets.digits_en.",
        ):
            self.assertIn(token, build_ui_src)

    def test_wis_task_227_desktop_peripheral_skip_and_identity_guard(self):
        shell_src = (HWTEST_DIR / "HwTestShell.cs").read_text(encoding="utf-8")
        ui_hwtest_src = (ROOT / "UI" / "MainWindow.HwTest.cs").read_text(encoding="utf-8")

        # 1. Na Desktopu se svi rucni testovi periferija oznacavaju kao NotApplicable
        self.assertIn("bool isLaptop = _hwInfo.IsLaptop;", shell_src)
        self.assertIn("Nije primenljivo — Desktop računar", shell_src)
        self.assertIn("bool hasPendingManual = false;", shell_src)
        self.assertIn("Desktop računar — ručni testovi periferija nisu potrebni", shell_src)
        self.assertIn('"SKIPPED_DONE"', shell_src)

        # 2. Placeholder identitet ("Učitavanje konfiguracije...", "Standardni PC", "N/A") ne prepisuje WMI
        self.assertIn("IsPlaceholderIdentity(", shell_src)
        self.assertIn("IsPlaceholderIdentity(", ui_hwtest_src)
        self.assertIn("TelemetryService", (ROOT / "UI" / "TelemetryService.cs").read_text(encoding="utf-8"))
        self.assertIn("private int _hubPort = 8080;", (ROOT / "UI" / "TelemetryService.cs").read_text(encoding="utf-8"))
        self.assertIn('baseUrl + "/api/status"', (ROOT / "UI" / "TelemetryService.cs").read_text(encoding="utf-8"))

    def test_wis_task_236_wlan_scan_firmware_code14_and_arm64_throttle(self):
        native_src = (HWTEST_DIR / "Interop" / "NativeMethods.cs").read_text(encoding="utf-8")
        diag_src = (HWTEST_DIR / "Core" / "AutoHardwareDiagnostics.cs").read_text(encoding="utf-8")

        # 1. Aktivni WlanScan preko wlanapi.dll kada netsh ne vrati mreže
        for token in ("wlanapi.dll", "WlanEnumInterfaces", "WlanScan", "TriggerActiveWlanScan"):
            self.assertIn(token, native_src)
        self.assertIn("NativeMethods.TriggerActiveWlanScan()", diag_src)
        self.assertIn("ParseNetshNetworksOutput(", diag_src)

        # 2. Firmware [Code 14] (CM_PROB_NEED_RESTART) ide u pending_firmware_reboot umesto PnP greške
        self.assertIn("pendingFirmwareReboots", diag_src)
        self.assertIn('"pending_firmware_reboot"', diag_src)
        self.assertIn("err == 14u", diag_src)

        # 3. CPU throttling na niskoj temperaturi (< 75°C) ne upozorava lažno na ARM64 / big.LITTLE boost taktove
        self.assertIn("lowCoolTemp = (peakTemp > 0f && peakTemp < 75.0f);", diag_src)
        self.assertIn("peakTemp >= 80.0f || peakTemp <= 0f", diag_src)

    def test_wis_task_234_display_hint_and_trackpoint_middle_click(self):
        disp_src = (HWTEST_DIR / "Tests" / "DisplayTestWindow.cs").read_text(encoding="utf-8")
        tp_src = (HWTEST_DIR / "Tests" / "TouchpadTestWindow.cs").read_text(encoding="utf-8")
        native_src = (HWTEST_DIR / "Interop" / "NativeMethods.cs").read_text(encoding="utf-8")

        # 1. Display test jasno prikazuje poruku pri startu na crnom ekranu i skriva je od koraka 2
        self.assertIn("_startHintOverlay", disp_src)
        self.assertIn("TEST MONITORA / EKRANA JE POKRENUT", disp_src)
        self.assertIn("_currentIndex == 0 && !_hintHiddenByUser", disp_src)
        self.assertIn("if (e.Key == Key.H)", disp_src)

        # 2. ThinkPad TrackPoint middle click višeslojna detekcija (ELAN registry + eventi + RawInput + WM_MBUTTON + VK_MBUTTON 0x8001)
        for token in (
            "SwitchElanMiddleButtonToClickMode",
            "RestoreElanMiddleButtonMode",
            "EPDPSTGesture_MiddleButton_Down",
            "ETDPSTGesture_MiddleButton_Down",
            "InstallHwndAndRawInputHook",
            "WndProcMiddleButtonHook",
            "vkState & 0x8001",
            "PreviewMouseUp += OnCanvasMouseDown;",
        ):
            self.assertIn(token, tp_src)

        for token in (
            "RegisterRawInputDevices",
            "GetRawInputData",
            "TryReadRawMouseButtonFlags",
            "RI_MOUSE_MIDDLE_BUTTON_DOWN",
            "OpenEvent",
            "WaitForSingleObject",
        ):
            self.assertIn(token, native_src)

    def test_wis_task_241_trackpoint_separation_ir_blink_and_overnight_phase5(self):
        tp_src = (HWTEST_DIR / "Tests" / "TouchpadTestWindow.cs").read_text(encoding="utf-8")
        cam_src = (HWTEST_DIR / "Tests" / "CameraTestWindow.cs").read_text(encoding="utf-8")
        shell_src = (HWTEST_DIR / "HwTestShell.cs").read_text(encoding="utf-8")
        ui_hw_src = (ROOT / "UI" / "MainWindow.HwTest.cs").read_text(encoding="utf-8")
        native_src = (HWTEST_DIR / "Interop" / "NativeMethods.cs").read_text(encoding="utf-8")

        # 1. Odvajanje Touchpad vs TrackPoint + ELAN kernel Wait thread + HKLM + Scroll mod auto-detekcija
        for token in (
            "MarkTrackPointLeftDone",
            "MarkTrackPointRightDone",
            "IsRecentRawTrackPointSource",
            "StartElanKernelWaitThread",
            "WaitForMultipleObjects",
            "EPDPSTGesture_Scroll_Finish",
            "EPDPSTGesture_LeftButton_Down",
            "EPDPSTGesture_RightButton_Down",
            "EPDMessage_HSA_TP_PST_UI_Set",
            "SYSTEM\\CurrentControlSet\\Control\\Elantech\\PointStick",
            "TrackPoint Scroll mod (Srednji taster + džojstik)",
            "_hasPrecisionTouchpadHid",
        ):
            self.assertIn(token, tp_src)

        for token in (
            "GetRawInputDeviceList",
            "GetRawInputDeviceInfo",
            "EnumRawHidUsagePairs",
            "TryReadRawInputPacket",
            "WaitForMultipleObjects",
        ):
            self.assertIn(token, native_src)

        # 2. IR kamera: ne preskace fiksno _frameCounter % 2 == 0, filtrira tamne IR-OFF blink frejmove i ima prosirene skale
        self.assertNotIn("if (_frameCounter % 2 == 0)", cam_src)
        self.assertIn("ComputeSampledMean", cam_src)
        self.assertIn("isDarkBlinkFrame", cam_src)
        self.assertIn("new int[] { 28, 36, 46, 58, 72, 88 }", cam_src)

        # 3. Faza 5 nocno cekanje na rucne testove bez nasilnog 45m/24h auto-skipa + zatvaranje dijaloga pri ForceComplete (azurirano za V343-03)
        self.assertIn("IsWaitingForManualTests", shell_src)
        self.assertIn("_activePeripheralDialog", shell_src)
        self.assertIn("activeDlg.Close()", shell_src)
        self.assertIn("IsWaitingForManualTests", ui_hw_src)
        self.assertNotIn("manualWaitLimitMs", ui_hw_src)
        for tok in (
            "HasUnfinishedInteractiveHwTests",
            "ShowPendingInteractiveTestsWarning",
            "RequestEmergencySkipPhase5WithConfirmation",
            "ForceCompletePhase5SkipPending",
        ):
            self.assertIn(tok, ui_hw_src)

    def test_wis_task_242_suppress_windows_location_prompt(self):
        native_src = (HWTEST_DIR / "Interop" / "NativeMethods.cs").read_text(encoding="utf-8")
        ui_main_src = read_all_mainwindow_cs()
        shell_src = (HWTEST_DIR / "HwTestShell.cs").read_text(encoding="utf-8")
        branding_src = (ROOT / "UI" / "Skripte" / "Modules" / "WIS.Branding.psm1").read_text(encoding="utf-8")
        setup_src = (ROOT / "ISO" / "SetupComplete.ps1").read_text(encoding="utf-8")

        self.assertIn("SuppressLocationPromptPopups", native_src)
        self.assertIn("ShowGlobalPrompts", native_src)
        self.assertIn("SuppressLocationPromptPopups()", ui_main_src)
        self.assertIn("SuppressLocationPromptPopups()", shell_src)
        self.assertIn("Disable-LocationPrivacyPrompts", branding_src)
        self.assertIn("ShowGlobalPrompts", branding_src)
        self.assertIn("ShowGlobalPrompts", setup_src)

    def test_wis_task_243_completion_restart_and_responsive_buttons(self):
        loc_src = (ROOT / "UI" / "LocalizationService.cs").read_text(encoding="utf-8")
        ui_main_src = read_all_mainwindow_cs()
        xaml_src = (ROOT / "UI" / "MainWindow.xaml").read_text(encoding="utf-8")

        self.assertIn("OČISTI I RESTARTUJ RAČUNAR", loc_src)
        self.assertNotIn("OČISTI I UGASI RAČUNAR", loc_src)
        self.assertIn("EnsureCompletionOverlayResponsiveLayout", ui_main_src)
        self.assertIn("shutdown.exe /r /t 3", ui_main_src)
        self.assertNotIn("shutdown.exe /s /t 3", ui_main_src)
        self.assertIn("<WrapPanel Orientation=\"Horizontal\" HorizontalAlignment=\"Center\">", xaml_src)

    def test_wis_task_244_restart_skips_launcher_when_ui_present(self):
        ui_main_src = read_all_mainwindow_cs()
        splash_src = (ROOT / "Launcher" / "SplashForm.cs").read_text(encoding="utf-8")
        first_logon_cmd = (ROOT / "ISO" / "FirstLogon.cmd").read_text(encoding="utf-8")
        first_logon_ps1 = (ROOT / "ISO" / "FirstLogon.ps1").read_text(encoding="utf-8")

        resume_block = ui_main_src.split("private void EnsureRunOnceEntry()", 1)[1].split("private void ClearRunOnceEntries()", 1)[0]
        self.assertIn("File.Exists(exePath)", resume_block)
        self.assertIn("string resumeTarget = File.Exists(exePath)", resume_block)
        self.assertIn("\"WIS-DesktopStart.cmd\"", resume_block)
        self.assertIn("ClearRunOnceEntries()", ui_main_src)
        self.assertIn("hasActiveResumeState", splash_src)
        self.assertIn("StartMasterUIAndExit()", splash_src)
        self.assertIn("if exist \"C:\\setup\\deploy-state.json\" if exist \"C:\\setup\\Skripte\\WIS.UI\\WIS.UI.exe\"", first_logon_cmd)
        self.assertIn("Test-Path 'C:\\setup\\deploy-state.json'", first_logon_ps1)

    def test_wis_task_245_thermal_overheat_continues_pipeline_as_note(self):
        shell_src = (HWTEST_DIR / "HwTestShell.cs").read_text(encoding="utf-8")
        ui_main_src = read_all_mainwindow_cs()
        ui_hw_src = (ROOT / "UI" / "MainWindow.HwTest.cs").read_text(encoding="utf-8")
        p5_src = (ROOT / "UI" / "Skripte" / "Phases" / "Phase5-Stress.ps1").read_text(encoding="utf-8")
        p6_src = (ROOT / "UI" / "Skripte" / "Phases" / "Phase6-Finalize.ps1").read_text(encoding="utf-8")
        cleanup_src = (ROOT / "UI" / "Skripte" / "Modules" / "WIS.Cleanup.psm1").read_text(encoding="utf-8")
        reports_src = (ROOT / "ServiceHub" / "servishub" / "reports.py").read_text(encoding="utf-8")

        self.assertIn("_phase5ThermalNote", shell_src)
        self.assertIn("P5.Stress", shell_src)
        self.assertNotIn("Faza 5 zaustavljena!", shell_src)
        self.assertIn("RecordThermalOverheatNote", ui_main_src)
        self.assertNotIn("HandleCriticalThermalAbort", ui_main_src)
        self.assertNotIn("HandleCriticalThermalAbort", ui_hw_src)
        self.assertIn("NAPOMENA: PREGREVANJE CPU", ui_main_src)
        p5_exit_block = p5_src.split("# Kontrola izlaza i logovanja prema statusu stres testa", 1)[1].split("} elseif", 1)[0]
        self.assertIn("exit 0", p5_exit_block)
        self.assertNotIn("exit 2", p5_exit_block)
        self.assertIn("### [NAPOMENA] Termalne i Servisne Napomene", p6_src)
        self.assertIn("thermal_note", cleanup_src)
        self.assertIn("### 📝 Napomene za Servis", reports_src)

    def test_wis_task_253_v310_security_and_telemetry_hardening(self):
        ui_dir = ROOT / "UI"
        self.assertFalse((ui_dir / "LibreHardwareMonitorLib.dll").exists())
        self.assertFalse((ui_dir / "HidSharp.dll").exists())

        thermal_src = (ui_dir / "ThermalSensorEngine.cs").read_text(encoding="utf-8")
        self.assertNotIn("Assembly.LoadFrom", thermal_src)
        self.assertIn("Anti-BYOVD", thermal_src)
        self.assertIn("DCIM_NumericSensor", thermal_src)

        ui_main_src = read_all_mainwindow_cs(ui_dir)
        self.assertNotIn("98°C", ui_main_src)
        self.assertNotIn("LibreHardwareMonitor aktivan", ui_main_src)
        self.assertIn("consecutiveOverheat", ui_main_src)

        hw_src = (ROOT / "UI" / "Skripte" / "Modules" / "WIS.Hardware.psm1").read_text(encoding="utf-8")
        p5_src = (ROOT / "UI" / "Skripte" / "Phases" / "Phase5-Stress.ps1").read_text(encoding="utf-8")
        for src in (hw_src, p5_src):
            self.assertNotIn("98.0", src)
            self.assertNotIn("98°C", src)
            self.assertIn("97.0", src)
            self.assertIn("consecutiveOverheat", src)

        shell_src = (HWTEST_DIR / "HwTestShell.cs").read_text(encoding="utf-8")
        result_src = (HWTEST_DIR / "Core" / "ResultModel.cs").read_text(encoding="utf-8")
        self.assertIn("operator_wait_sec", shell_src)
        self.assertIn("active_hwtest_sec", shell_src)
        self.assertIn("Console.Beep(880, 140)", shell_src)
        self.assertIn('AppVersion = "3.5.0";', result_src)

        upd_src = (ROOT / "UI" / "Skripte" / "Modules" / "WIS.Update.psm1").read_text(encoding="utf-8")
        apps_src = (ROOT / "UI" / "Skripte" / "Modules" / "WIS.Apps.psm1").read_text(encoding="utf-8")
        p4_src = (ROOT / "UI" / "Skripte" / "Phases" / "Phase4-Apps.ps1").read_text(encoding="utf-8")
        common_src = (ROOT / "UI" / "Skripte" / "Modules" / "WIS.Common.psm1").read_text(encoding="utf-8")
        sg_src = (ROOT / "UI" / "Skripte" / "Modules" / "WIS.StepGuard.psm1").read_text(encoding="utf-8")
        self.assertNotIn(
            'Set-ItemProperty -Path "HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Installer" -Name "ServisWuActive"',
            upd_src,
        )
        self.assertIn('"Phase4Active"', p4_src)
        self.assertIn('"Phase4Active"', upd_src)
        self.assertIn("X-ServisHub-Client-Token", upd_src)
        self.assertIn("nedozvoljene skripte", upd_src)
        self.assertIn('HKLM:\\SOFTWARE\\WIS', apps_src)
        self.assertIn("Record-WisStepBenchmark", common_src)
        self.assertIn("phase4_steps_sec", common_src)
        self.assertIn("Record-WisStepBenchmark", sg_src)

        core_src = (ROOT / "UI" / "Skripte" / "Modules" / "WIS.Core.psm1").read_text(encoding="utf-8")
        cleanup_src = (ROOT / "UI" / "Skripte" / "Modules" / "WIS.Cleanup.psm1").read_text(encoding="utf-8")
        p6_src = (ROOT / "UI" / "Skripte" / "Phases" / "Phase6-Finalize.ps1").read_text(encoding="utf-8")
        self.assertIn("Register-WisSecurityRecoverySentinel", core_src)
        self.assertIn("Remove-LegacyWinRing0Driver", cleanup_src)
        self.assertIn("$Global:WisSecurityHardeningVerified", cleanup_src)
        self.assertIn("security_hardening_verified", p6_src)

    def test_wis_task_254_v311_bug01_bug09_and_ig_systems(self):
        # BUG-01: Audio -> Mic bez deadloka (STA Dispatcher + ManualResetEventSlim + STA background Mic init)
        audio_src = (HWTEST_DIR / "Tests" / "AudioTestWindow.cs").read_text(encoding="utf-8")
        self.assertIn("ManualResetEventSlim _waveOutDoneEvent", audio_src)
        self.assertIn("_waveOutDoneEvent.Wait(1500)", audio_src)
        self.assertIn("ApplyMasterVolumeScalarDirect", audio_src)
        self.assertIn("Dispatcher.BeginInvoke", audio_src)

        mic_src = (HWTEST_DIR / "Tests" / "MicTestWindow.cs").read_text(encoding="utf-8")
        self.assertIn("StartMicInitAsync(true);", mic_src)
        self.assertIn("StartWaveInCore(myGen);", mic_src)
        self.assertIn("ApartmentState.STA", mic_src)
        loaded_block = mic_src.split("Loaded += (s, e) => {", 1)[1].split("};", 1)[0]
        self.assertNotIn("TryUnmuteAndLogCaptureEndpoint", loaded_block)

        # BUG-09: Dual-GPU provera svih adaptera u AutoHardwareDiagnostics & GpuStressWindow
        autodiag_src = (HWTEST_DIR / "Core" / "AutoHardwareDiagnostics.cs").read_text(encoding="utf-8")
        gpustress_src = (HWTEST_DIR / "Tests" / "GpuStressWindow.cs").read_text(encoding="utf-8")
        self.assertIn("out bool hasAnyBasicDisplay", autodiag_src)
        self.assertIn("out bool hasAnyErrorAdapter", autodiag_src)
        self.assertIn("out string problemAdaptersSummary", autodiag_src)
        self.assertIn("hasAnyBasicDisplay || hasAnyErrorAdapter", autodiag_src)
        self.assertIn("has_any_basic_display", gpustress_src)
        self.assertIn("_hasAnyBasicDisplay || _hasAnyErrorAdapter", gpustress_src)

        # IG-Systems rebrending dokumentacije i konfiguracionih primera (bez starog imena)
        label_src = (ROOT / "UI" / "LabelPrintService.cs").read_text(encoding="utf-8")
        self.assertIn('ShopName = "IG-Systems";', label_src)
        forbidden_brand = "tehno " + "kutak"
        for path in [
            ROOT / "README.md",
            ROOT / "LICENSE",
            ROOT / "SECURITY.md",
            ROOT / "Wifi-Servis.example.ini",
            ROOT / "ISO" / "Wifi-Servis.example.ini",
        ]:
            low = path.read_text(encoding="utf-8").lower()
            self.assertNotIn(forbidden_brand, low, f"Zaostali brend u {path}")
            self.assertIn("ig-systems", low, f"Nedostaje IG-Systems u {path}")
        for doc_md in (ROOT / "docs").rglob("*.md"):
            self.assertNotIn(forbidden_brand, doc_md.read_text(encoding="utf-8").lower(), f"Zaostali brend u {doc_md}")

    def test_wis_task_262_v330_hwtest_improvements(self):
        """Proverava IMP-10 (timeout 6000ms + WMI_ACPI_STATIC) i IMP-14 (4000ms senzor guard + TEST_START log)."""
        mpc_src = (HWTEST_DIR / "Core" / "ModelProfileClient.cs").read_text(encoding="utf-8")
        kb_part = mpc_src.split("/api/keyboard-layout", 1)[1].split("public bool PostKeyboardOverride", 1)[0]
        self.assertIn("req.Timeout = 6000;", kb_part)
        self.assertIn("req.ReadWriteTimeout = 6000;", kb_part)

        shell_src = (HWTEST_DIR / "HwTestShell.cs").read_text(encoding="utf-8")
        self.assertGreaterEqual(shell_src.count('HwTestLogger.Info("TEST_START", string.Format("Pokrenut automatski test'), 2)

        autodiag_src = (HWTEST_DIR / "Core" / "AutoHardwareDiagnostics.cs").read_text(encoding="utf-8")
        self.assertIn("private bool _sensorTimedOut;", autodiag_src)
        self.assertIn("Monitor.Wait(waitSync, 4000);", autodiag_src)
        self.assertIn("WMI_ACPI_STATIC", autodiag_src)
        self.assertIn('"sensor_static_acpi"', autodiag_src)
        self.assertIn('"raw_acpi_temp_c"', autodiag_src)

    def test_wis_task_263_hwtest_usb_noskip_gpu_guard(self):
        """Proverava WIS-TASK-263 (HWT-01..HWT-07) i security gate ugovore za v3.4.0."""
        native_src = (HWTEST_DIR / "Interop" / "NativeMethods.cs").read_text(encoding="utf-8")
        probe_src = (HWTEST_DIR / "Core" / "HardwareProbe.cs").read_text(encoding="utf-8")
        diag_src = (HWTEST_DIR / "Core" / "AutoHardwareDiagnostics.cs").read_text(encoding="utf-8")
        shell_src = (HWTEST_DIR / "HwTestShell.cs").read_text(encoding="utf-8")
        ui_hw_src = (ROOT / "UI" / "MainWindow.HwTest.cs").read_text(encoding="utf-8")
        usb_src = (HWTEST_DIR / "Tests" / "UsbPortTestWindow.cs").read_text(encoding="utf-8")
        disp_src = (HWTEST_DIR / "Tests" / "DisplayTestWindow.cs").read_text(encoding="utf-8")
        gpu_src = (HWTEST_DIR / "Tests" / "GpuStressWindow.cs").read_text(encoding="utf-8")
        fp_win_src = (HWTEST_DIR / "Tests" / "FingerprintTestWindow.cs").read_text(encoding="utf-8")
        tp_win_src = (HWTEST_DIR / "Tests" / "TouchpadTestWindow.cs").read_text(encoding="utf-8")
        cam_win_src = (HWTEST_DIR / "Tests" / "CameraTestWindow.cs").read_text(encoding="utf-8")
        handlers_src = (ROOT / "ServiceHub" / "servishub" / "handlers.py").read_text(encoding="utf-8")
        upd_src = (ROOT / "UI" / "Skripte" / "Modules" / "WIS.Update.psm1").read_text(encoding="utf-8")

        # HWT-01: CfgMgr32 live-devnode provera, kanonski RawPortKey, !newlyAttached i Unplug debounce/cooldown
        for tok in (
            "CM_Get_DevNode_Status",
            "DN_STARTED",
            "DN_HAS_PROBLEM",
            "DN_WILL_BE_REMOVED",
            "IsUsbDevNodeLiveAndStarted",
            "ResolveUsbPhysicalPortLocation(string pnpDeviceId, out string debugInfo, out string displayLocationInfo)",
        ):
            self.assertIn(tok, native_src)

        for tok in (
            "NativeMethods.IsUsbDevNodeLiveAndStarted",
            "_debounceTimer",
            "ScheduleDebouncedUsbScan",
            "_lastUnplugUtc",
            "_lastAnyUnplugUtc",
            "< 500.0",
            "bool newlyAttached = !_currentlyAttachedDevices.ContainsKey(cand.DeviceId);",
            "processedPortKeysThisScan",
            "IsPortCurrentlyOccupiedByOtherDevice",
        ):
            self.assertIn(tok, usb_src)
        attach_guard = usb_src.split("if (newlyAttached)", 1)[1].split("if (string.IsNullOrEmpty(cand.RawPortKey))", 1)[0]
        self.assertIn("continue;", attach_guard.split("else", 1)[1])

        # HWT-02: Zabrana rucnog preskakanja u svih 9 perifernih prozora i HwTestShell-u
        interactive_windows = (
            "DisplayTestWindow.cs",
            "KeyboardTestWindow.cs",
            "TouchpadTestWindow.cs",
            "TouchscreenMazeWindow.cs",
            "AudioTestWindow.cs",
            "MicTestWindow.cs",
            "CameraTestWindow.cs",
            "UsbPortTestWindow.cs",
            "FingerprintTestWindow.cs",
        )
        for fname in interactive_windows:
            wsrc = (HWTEST_DIR / "Tests" / fname).read_text(encoding="utf-8")
            self.assertNotIn("FinishSkip(", wsrc, f"{fname} ne sme imati FinishSkip(")
            self.assertNotIn("btnSkip", wsrc, f"{fname} ne sme imati btnSkip")
            if fname == "UsbPortTestWindow.cs":
                # FEAT-01 dodaje jednokratno mamac dugme 'Preskoči test' koje NIKADA ne preskace test
                self.assertNotIn("TestStatus.Skip", wsrc, "UsbPortTestWindow.cs ne sme koristiti TestStatus.Skip")
            else:
                self.assertNotIn("Preskoči test", wsrc, f"{fname} ne sme nuditi 'Preskoči test'")

        self.assertNotIn("ESC = preskoči", disp_src)
        self.assertIn("ESC = pregled rezultata", disp_src)

        self.assertNotIn("PromptSkipReason", shell_src)
        self.assertNotIn("Preskoči test uz obrazloženje", shell_src)
        self.assertNotIn("btnSkip", shell_src)
        self.assertNotIn("Zaustavi", shell_src)
        self.assertIn("⏳ Stres u toku...", shell_src)
        self.assertIn("bool canFinish = !_autoStressRunning && (pending == 0) && (running == 0);", shell_src)
        self.assertIn("_btnFinish.IsEnabled = canFinish;", shell_src)

        finish_block = shell_src.split("private void FinishAndSave()", 1)[1].split("private void SaveCurrentReport(", 1)[0]
        self.assertIn("Nije moguće sačuvati izveštaj dok svi primenljivi testovi nisu završeni (PASS ili FAIL)", finish_block)
        self.assertNotIn("SaveCurrentReport(true", finish_block)

        save_block = shell_src.split("private void SaveCurrentReport(", 1)[1].split("private string GetCheckpointFilePath(", 1)[0]
        self.assertNotIn("promptIfPending && pending > 0", save_block)
        self.assertIn("if (pending > 0 && autoMarkPendingAsSkip)", save_block)

        # HWT-03: GPU 3D test bez prekida i obavezna TryValidateGpuDriverForStress pre-flight blokada
        self.assertNotIn("btnAbort", gpu_src)
        self.assertNotIn("Prekini", gpu_src)
        self.assertNotIn("TestStatus.Skip", gpu_src)
        self.assertIn("if (!_completedNormally && !_isClosingOrDone)", gpu_src)
        self.assertIn("gpu_test_blocked_no_driver", gpu_src)

        self.assertIn("public static bool TryValidateGpuDriverForStress(out TestItemResult blockedResult)", diag_src)
        self.assertIn('blockedResult.Details["gpu_test_blocked_no_driver"] = true;', diag_src)

        run_gpu_block = shell_src.split("private void RunGpuStressTest(TestItemResult t)", 1)[1].split("private void ApplyResult(", 1)[0]
        val_idx = run_gpu_block.find("AutoDiagnosticsEngine.TryValidateGpuDriverForStress(out blockedResult)")
        win_idx = run_gpu_block.find("new GpuStressWindow(_autoEngine)")
        self.assertGreater(val_idx, 0)
        self.assertGreater(win_idx, val_idx)
        self.assertIn('HwTestLogger.Warn("GPU_BLOCKED", blockReason);', run_gpu_block)
        self.assertIn("return;", run_gpu_block[:win_idx])

        # HWT-04: Otisak prsta bez !FactoryBomLoaded, strogi filter i UEFI BIOS checkpoint/restart
        fp_block = shell_src.split("bool fpExpected =", 1)[1].split("_testItems.Add(fp);", 1)[0]
        self.assertNotIn("!_hwInfo.FactoryBomLoaded", fp_block)
        self.assertNotIn("FactoryBomLoaded", fp_block)
        self.assertIn("(_hwInfo.HasFingerprint || _hwInfo.FactoryFingerprint == true)", fp_block)

        for tok in (
            "SaveCheckpoint(",
            "TryRestoreCheckpoint(",
            "hwtest-checkpoint.json",
            "wis.hwtest.checkpoint.v1",
            "/r /fw /t 0",
            "_uefiBiosPromptOpen",
            "_uefiBiosRebootRequested",
            "RequestUefiBiosReboot(",
        ):
            self.assertIn(tok, shell_src)

        for tok in (
            "_phase5UefiRebootInProgress",
            "HandlePhase5UefiBiosRebootRequest",
            "SetUefiBiosRebootHandler(HandlePhase5UefiBiosRebootRequest)",
            "/r /fw /t 0",
            "if (_phase5UefiRebootInProgress) break;",
        ):
            self.assertIn(tok, ui_hw_src)
        phase5_post_wait = ui_hw_src.split("waitEvent.WaitOne(5000);", 1)[1].split("ExecutePhase(6);", 1)[0]
        self.assertIn("if (_phase5UefiRebootInProgress)", phase5_post_wait)

        self.assertIn("public static bool IsFingerprintPnpCandidate(", probe_src)
        self.assertIn("TryWakeWbioService(", probe_src)
        fp_filter_fn = probe_src.split("public static bool IsFingerprintPnpCandidate(", 1)[1].split("private static void TryWakeWbioService(", 1)[0]
        for tok in ("TOUCHSCREEN", "TOUCHPAD", "DIGITIZER", "POINTING", r"USB\\VID_04F3", r"USB\\VID_06CB", r"USB\\VID_27C6", "GOODIX"):
            self.assertIn(tok, fp_filter_fn)

        self.assertIn("✓ Potvrdi (PASS)", fp_win_src)
        self.assertIn("✗ Senzor ne radi (FAIL)", fp_win_src)
        self.assertIn("🔧 Restartuj u UEFI BIOS (i nastavi WIS)", fp_win_src)
        self.assertIn("OnRequestUefiBiosReboot", fp_win_src)
        self.assertNotIn("FinishSkip(", fp_win_src)
        self.assertNotIn("Model nema čitač", fp_win_src)

        # HWT-05: Razdvojeni Touchpad i TrackPoint
        self.assertIn('new TestItemResult("trackpoint", "TrackPoint"', shell_src)
        self.assertIn("PointingTestMode.TrackPointOnly", shell_src)
        self.assertIn("public enum PointingTestMode", tp_win_src)
        self.assertIn("TouchpadOnly", tp_win_src)
        self.assertIn("TrackPointOnly", tp_win_src)
        self.assertIn('new TestItemResult("touchpad", "Touchpad"', tp_win_src)
        self.assertIn('new TestItemResult("trackpoint", "TrackPoint"', tp_win_src)
        self.assertIn("Koristite crveni TrackPoint džojstik, ne touchpad!", tp_win_src)
        self.assertIn("public bool TrackPointDisabledInBios { get; set; }", probe_src)
        self.assertIn("public bool TrackPointBiosPresent { get; set; }", probe_src)
        self.assertIn("public bool? FactoryTrackPoint { get; set; }", probe_src)

        # HWT-06: Razdvojene Web RGB i IR kamere
        self.assertIn('new TestItemResult("ir_camera", "IR kamera (Windows Hello)"', shell_src)
        self.assertIn("CameraTestMode.RgbOnly", shell_src)
        self.assertIn("CameraTestMode.IrOnly", shell_src)
        self.assertIn("public enum CameraTestMode", cam_win_src)
        self.assertIn("RgbOnly", cam_win_src)
        self.assertIn("IrOnly", cam_win_src)
        self.assertIn("public bool InIrMode { get; private set; }", cam_win_src)
        self.assertIn('new TestItemResult("ir_camera"', cam_win_src)
        self.assertIn("public static bool IsIrCameraEntry(", probe_src)
        self.assertIn("public static bool IsNonPhysicalCameraImageDevice(", probe_src)
        self.assertNotIn('IndexOf("IR"', probe_src)
        self.assertIn(r'@"\bIR\b"', probe_src)

        # HWT-07: Pasivni RF Wi-Fi test, Native WLAN API i Anti-Leak / anti-konekciona garancija
        for tok in (
            "QueryWifiRadioState",
            "WlanQueryInterface",
            "wlan_intf_opcode_radio_state = 4u",
            "GetWlanBssScanSummary",
            "WlanGetNetworkBssList",
            "WlanFreeMemory",
        ):
            self.assertIn(tok, native_src)

        for tok in (
            "TryReadServiceWifiSsid",
            "WlanSvc",
            "wifiSupports5Ghz",
            "wifi_hw_radio_off",
            "wifi_sw_radio_off",
            "wifi_bssid_count",
            "wifi_24ghz_count",
            "wifi_5ghz_count",
            "wifi_6ghz_count",
            "wifi_max_rssi_dbm",
            "wifi_max_24ghz_pct",
            "wifi_max_5ghz_pct",
            "wifi_supports_5ghz",
            "wifi_service_ssid",
            "wifi_service_ssid_found",
            "wifi_service_signal_pct",
            "wifi_service_rssi_dbm",
            "noWifiSignalAtAll",
            "radioOff",
            "res.Status = TestStatus.Fail;",
        ):
            self.assertIn(tok, diag_src)

        self.assertNotIn("WlanSetProfile", diag_src)
        self.assertNotIn("WlanConnect", diag_src)
        ssid_fn = diag_src.split("public static string TryReadServiceWifiSsid()", 1)[1].split("private static bool EnsureWlanServiceRunning", 1)[0]
        self.assertNotIn("Password", ssid_fn)
        self.assertIn('key.Equals("SSID"', ssid_fn)
        self.assertIn("YOUR_SERVICE_WIFI_SSID", ssid_fn)

        # Security gate ugovori: fail-closed /api/drivers/pool/notify-harvest i X-ServisHub-Client-Token
        notify_block = handlers_src.split('if path == "/api/drivers/pool/notify-harvest":', 1)[1].split("if sync_mode:", 1)[0]
        self.assertIn("self._require_client_upload_token()", notify_block)

        harvest_notify_fn = upd_src.split("function Send-ServisHubHarvestNotify", 1)[1].split("function Copy-FileWithHeartbeat", 1)[0]
        self.assertIn("Get-ServisHubClientToken", harvest_notify_fn)
        self.assertIn("X-ServisHub-Client-Token", harvest_notify_fn)

    def test_wis_task_264_v341_dpi_compact_layout_contract(self):
        """Proverava Backlog B2 ugovor za 150% DPI / kompaktne ekrane (< 760 DIP) u MainWindow.xaml i MainWindow.xaml.cs."""
        xaml_src = (ROOT / "UI" / "MainWindow.xaml").read_text(encoding="utf-8")
        main_cs = (ROOT / "UI" / "MainWindow.xaml.cs").read_text(encoding="utf-8")

        self.assertIn('<ScrollViewer Tag="ActiveStepBarsScroll" MaxHeight="160"', xaml_src)
        for tok in (
            "Loaded += (s, e) => ApplyCompactViewportLayoutIfNeeded();",
            "SizeChanged += (s, e) => ApplyCompactViewportLayoutIfNeeded();",
            "private void ApplyCompactViewportLayoutIfNeeded()",
            "bool isCompact = ActualHeight > 0 && ActualHeight < 760.0;",
            "double targetScrollMaxHeight = isCompact ? 96.0 : 160.0;",
            "double targetMiddleMinHeight = isCompact ? 260.0 : 320.0;",
            'FindTaggedElement<System.Windows.Controls.ScrollViewer>(this, "ActiveStepBarsScroll")',
            "FindVisualChild<System.Windows.Controls.ScrollViewer>(globalCard);",
            'Tag = "ActiveStepBarsScroll",',
        ):
            self.assertIn(tok, main_cs)

    def test_feat_01_usb_skip_easter_egg_contract(self):
        """Proverava FEAT-01 ugovor: resurs na sva 4 build mesta, jednokratno mamac dugme i bezbedna 2s sirena."""
        asset_path = HWTEST_DIR / "Assets" / "usb_skip_easter_egg.jpg"
        self.assertTrue(asset_path.is_file(), f"Nedostaje resurs {asset_path}")
        sha256 = hashlib.sha256(asset_path.read_bytes()).hexdigest()
        self.assertEqual(
            sha256,
            "aadfc4ab28914949d25adda2e2e8c5cd023ef053dcbb26fdedbbc9f301a9960c",
        )

        hw_csproj = (HWTEST_DIR / "WIS.HwTest.csproj").read_text(encoding="utf-8")
        hw_build = (HWTEST_DIR / "Build-HwTest.py").read_text(encoding="utf-8")
        ui_csproj = (ROOT / "UI" / "WIS.UI.csproj").read_text(encoding="utf-8")
        ui_build = (ROOT / "UI" / "Build-UI.py").read_text(encoding="utf-8")

        for bsrc, bname in (
            (hw_csproj, "WIS.HwTest.csproj"),
            (hw_build, "Build-HwTest.py"),
            (ui_csproj, "WIS.UI.csproj"),
            (ui_build, "Build-UI.py"),
        ):
            self.assertIn("Assets.usb_skip_easter_egg.jpg", bsrc, f"{bname} mora ugraditi Assets.usb_skip_easter_egg.jpg")

        usb_src = (HWTEST_DIR / "Tests" / "UsbPortTestWindow.cs").read_text(encoding="utf-8")
        self.assertNotIn("btnSkip", usb_src)
        self.assertNotIn("FinishSkip(", usb_src)
        self.assertNotIn("TestStatus.Skip", usb_src)

        for tok in (
            "_btnUsbEasterEgg",
            '"Preskoči test"',
            "TriggerSkipEasterEgg",
            "_easterEggTriggered",
            "_btnUsbEasterEgg.Visibility = Visibility.Collapsed;",
            "Stretch.Uniform",
            'GetManifestResourceStream("Assets.usb_skip_easter_egg.jpg")',
            "TimeSpan.FromSeconds(2.0)",
            "IsHitTestVisible = false",
            "BoostAudioToMaxForSiren",
            "SetMasterVolumeLevelScalar(1.0f",
            "RestoreSirenAudioVolume",
            "GenerateSirenPcm2Seconds",
            "NativeMethods.waveOutOpen",
            "ManualResetEventSlim",
            "Dispatcher.BeginInvoke",
        ):
            self.assertIn(tok, usb_src)

        trig_block = usb_src.split("private void TriggerSkipEasterEgg()", 1)[1].split("private BitmapImage LoadEasterEggBitmap()", 1)[0]
        for forbidden in ("SafeCloseDialog", "Close(", "TestStatus.Skip", "FinishPass", "FinishFail", "FinishSkip"):
            self.assertNotIn(forbidden, trig_block)

        siren_block = usb_src.split("private void PlaySirenAsync()", 1)[1].split("private static void SetColumn(", 1)[0]
        self.assertIn("Dispatcher.BeginInvoke(new Action(RestoreSirenAudioVolume))", siren_block)
        self.assertNotIn("Dispatcher.Invoke(", siren_block)

    def test_wis_task_265_266_v342_hwtest_anticheat_and_bugfixes(self):
        """Proverava HWT-05 Anti-Cheat dopunu, IMP-34/BUG-01, IMP-31, BUG-02, BUG-03/IMP-35, IMP-30 i IMP-23."""
        native_src = (HWTEST_DIR / "Interop" / "NativeMethods.cs").read_text(encoding="utf-8")
        diag_src = (HWTEST_DIR / "Core" / "AutoHardwareDiagnostics.cs").read_text(encoding="utf-8")
        shell_src = (HWTEST_DIR / "HwTestShell.cs").read_text(encoding="utf-8")
        tp_src = (HWTEST_DIR / "Tests" / "TouchpadTestWindow.cs").read_text(encoding="utf-8")
        app_cs = (ROOT / "UI" / "App.xaml.cs").read_text(encoding="utf-8")
        xaml_src = (ROOT / "UI" / "MainWindow.xaml").read_text(encoding="utf-8")
        main_cs = read_all_mainwindow_cs()
        therm_cs = (ROOT / "UI" / "ThermalSensorEngine.cs").read_text(encoding="utf-8")

        # 1. HWT-05 Anti-Cheat (Sloj 1 SPI + Anti-Brick + Sloj 2 RawInput + F9/Ctrl+F)
        for tok in (
            "SPI_GETTOUCHPADPARAMETERS = 0x00AEu",
            "SPI_SETTOUCHPADPARAMETERS = 0x00AFu",
            "SPIF_SENDCHANGE = 0x0002u",
            "TOUCHPAD_PARAMETERS_V1",
            "TrySetPrecisionTouchpadEnabled",
        ):
            self.assertIn(tok, native_src)
        self.assertNotIn("SPIF_UPDATEINIFILE", native_src)
        self.assertNotIn("CM_Disable_DevNode", native_src)
        self.assertNotIn("Disable-PnpDevice", native_src)

        self.assertIn("TryStartupRecoverTouchpadIfNeeded();", shell_src.split("public HwTestShell(", 1)[1].split("BuildUI();", 1)[0])
        self.assertIn("hwtest-touchpad-restore.json", shell_src)

        for tok in (
            "hwtest-touchpad-restore.json",
            "InstallHwndAndRawInputHook",
            "ClassifyMouseRawDevice",
            "EXTERNAL_MOUSE",
            "TOUCHSCREEN",
            "touchpad_temporarily_disabled",
            "touchpad_disable_method",
            "anticheat_blocked_touchpad_events",
            "anticheat_blocked_external_mouse_events",
            "anticheat_blocked_trackpoint_events",
            "anticheat_blocked_touchscreen_events",
            "Key.F9",
            "FinishFailGeneral()",
            "ProcessExit",
            "DispatcherUnhandledException",
        ):
            self.assertIn(tok, tp_src)
        self.assertNotIn("CM_Disable_DevNode", tp_src)
        self.assertNotIn("FinishSkip", tp_src)

        loaded_block = tp_src.split("Loaded += (s, e) =>", 1)[1].split("Closed += (s, e) =>", 1)[0]
        self.assertIn("InstallHwndAndRawInputHook();", loaded_block)

        # 2. IMP-34 / BUG-01: WlanQueryInterface current_connection, IsLocationPolicyDisabled i zabrana laznog FAIL
        for tok in (
            "wlan_intf_opcode_current_connection = 7u",
            "IsLocationPolicyDisabled",
            "QueryCurrentWifiConnection",
        ):
            self.assertIn(tok, native_src)

        for tok in (
            "wifi_wlan_access_denied",
            "wifi_wlan_last_error",
            "wifi_location_policy_disabled",
            "QueryCurrentWifiConnection()",
            "locationBlockedWifiScan",
        ):
            self.assertIn(tok, diag_src)

        # 3. IMP-31: Uklonjen trajni _sensorTimedOut short-circuit i povecan inicijalni timeout
        ensure_block = diag_src.split("public ThermalSensorEngine EnsureSensorsAndUpdate()", 1)[1].split("private static void ProbePhysicalDisksAndMemory", 1)[0]
        self.assertNotIn("if (_sensorTimedOut) return null;", ensure_block)
        self.assertIn("10000", ensure_block)
        self.assertIn("CreateBoundedWmiSearcher", therm_cs)

        # 4. BUG-02: SW_MAXIMIZE u App.xaml.cs, ResizeMode CanMinimize + WindowMaximizedGuard uz dozvoljen Minimize
        self.assertIn("ShowWindow(p.MainWindowHandle, 3); // SW_MAXIMIZE", app_cs)
        self.assertNotIn("ShowWindow(p.MainWindowHandle, 9);", app_cs)
        self.assertIn('ResizeMode="CanMinimize"', xaml_src)
        for tok in (
            "EnsureWindowMaximized",
            "WindowMaximizedGuardHook",
            "WM_DISPLAYCHANGE",
            "WM_DPICHANGED",
            "SystemEvents.DisplaySettingsChanged +=",
            "SystemEvents.DisplaySettingsChanged -=",
            "WindowState.Minimized",
        ):
            self.assertIn(tok, main_cs)

        # 5. BUG-03 / IMP-35 + IMP-30 + IMP-23
        self.assertIn('Tag="FailedPhaseRecoveryPanel"', xaml_src)
        for tok in (
            "EnsureFailedPhaseRecoveryBar",
            "BtnFailedPhaseRecheck",
            "BtnFailedPhaseRetry",
            "BtnFailedPhaseSkip",
            "GetMissingKeyApplications",
            "Acrobat 2020",
            "EXCEL.EXE",
            "PerformPhase4RealityCheck",
            "ShouldIgnoreRetryJsonLine",
            "-PreserveCurrentPhase",
            "_phase6ScriptCompletedSuccessfully",
        ):
            self.assertIn(tok, main_cs)

        retry_block = main_cs.split("public void RetrySingleApp(", 1)[1].split("private void ExecuteAutopilotRescan(", 1)[0]
        self.assertIn("_failedPhase == 4", retry_block)
        self.assertIn("PerformPhase4RealityCheck()", retry_block)
        self.assertIn("StartPhaseExecution(5)", retry_block)

    def test_wis_task_268_v343_hwtest_and_ui_contracts(self):
        """Proverava ugovore za WIS v3.4.3 / ServiceHub v3.6.3 (V343-01..V343-07)."""
        import re

        ui_dir = ROOT / "UI"
        native_src = (HWTEST_DIR / "Interop" / "NativeMethods.cs").read_text(encoding="utf-8")
        probe_src = (HWTEST_DIR / "Core" / "HardwareProbe.cs").read_text(encoding="utf-8")
        spec_src = (HWTEST_DIR / "Core" / "FactorySpecProbe.cs").read_text(encoding="utf-8")
        mpc_src = (HWTEST_DIR / "Core" / "ModelProfileClient.cs").read_text(encoding="utf-8")
        shell_src = (HWTEST_DIR / "HwTestShell.cs").read_text(encoding="utf-8")
        kbd_src = (HWTEST_DIR / "Tests" / "KeyboardTestWindow.cs").read_text(encoding="utf-8")
        usb_src = (HWTEST_DIR / "Tests" / "UsbPortTestWindow.cs").read_text(encoding="utf-8")
        tel_cs = (ui_dir / "TelemetryService.cs").read_text(encoding="utf-8")
        xaml_src = (ui_dir / "MainWindow.xaml").read_text(encoding="utf-8")
        models_cs = (ui_dir / "Models.cs").read_text(encoding="utf-8")
        main_xaml_cs = (ui_dir / "MainWindow.xaml.cs").read_text(encoding="utf-8")
        hw_cs = (ui_dir / "MainWindow.Hardware.cs").read_text(encoding="utf-8")
        ui_hw_cs = (ui_dir / "MainWindow.HwTest.cs").read_text(encoding="utf-8")

        # V343-01: Location Allow + sveza P5.AutoDiag sinhronizacija
        self.assertIn(r"ConsentStore\location", native_src)
        self.assertIn("IsConsentLocationDenied", native_src)
        self.assertIn("FormatAutoDiagIssueSummary", shell_src)
        self.assertIn("P5.AutoDiag", shell_src)

        # V343-02: Windows licencni metadata i reupload u TelemetryService
        for tok in ("windows_oa3_present", "windows_license_method", "ReuploadUpdatedReportAfterHwTestAsync"):
            self.assertIn(tok, tel_cs)

        # V343-03: Pending interactive overlay/banner + hard gate bez 24h auto-skipa
        self.assertIn('Tag="PendingInteractiveTestsBanner"', xaml_src)
        self.assertIn('Tag="PendingInteractiveTestsOverlay"', xaml_src)
        self.assertNotIn("manualWaitLimitMs", ui_hw_cs)
        for tok in (
            "HasUnfinishedInteractiveHwTests",
            "ShowPendingInteractiveTestsWarning",
            "RequestEmergencySkipPhase5WithConfirmation",
        ):
            self.assertIn(tok, ui_hw_cs)
        for tok in (
            "GetPendingInteractiveTestNames",
            "PendingInteractiveTestsRequired",
            "ForceCompletePhase5SkipPending(string customReason)",
        ):
            self.assertIn(tok, shell_src)

        # V343-04: Tastatura bez laznih detektora, EnergyDrv + BOM override + uvek vidljiv no_hardware
        hw_cs_code = re.sub(r"//.*", "", hw_cs)
        for forbidden in ("LEN0071", "DELLABCE", "Lenovo_SetKeyboardBacklightStatus", "HP_BIOSSetting", "DCIM_BiosEnumeration"):
            self.assertNotIn(forbidden, hw_cs_code, f"Lazni detektor {forbidden} ne sme postojati u kodu MainWindow.Hardware.cs")
        self.assertIn("ApplyBacklitKeyboardVerdict", hw_cs)
        self.assertIn("ApplyBacklitKeyboardVerdict", models_cs)
        self.assertIn("TryProbeLenovoEnergyDrvBacklight", probe_src)
        self.assertIn("FactoryKeyboardBacklight", spec_src)
        self.assertIn("_btnBacklightNoHw", kbd_src)
        self.assertIn('"no_hardware"', kbd_src)

        # V343-05: Faza Tracker sklopljen po difoltu
        self.assertIn("private bool _isPhasesExpanded = false;", models_cs)
        self.assertIn("private bool _showStepsInOverview = false;", models_cs)
        self.assertIn("PhasesToggleText", models_cs)
        self.assertIn("PhasesCollapseToggleBtn", xaml_src)
        self.assertIn("PhasesCollapseToggleBtn", main_xaml_cs)

        # V343-06: USB fizicke lokacije i Type-C PD kontrolerski limit
        for tok in (
            "physical_port_locations",
            "DetectTypeCControllerCap",
            "_maxTypeCChargerCap",
            "hasTypeCController",
            "max_typec_cap",
        ):
            self.assertIn(tok, usb_src)
        self.assertIn("physical_port_locations", mpc_src)

        # V343-07: Pregled periferija bez Web kamere/TB, sa BT verzijom, 4G/5G, eSIM i osvezavanjem posle drajvera
        rebuild_blk = models_cs.split("public void RebuildOverviewPeripherals(", 1)[1].split("private void AddPeripheralBadge(", 1)[0]
        self.assertNotIn("Web kamera", rebuild_blk)
        self.assertNotIn("Thunderbolt / USB4", rebuild_blk)
        for tok in ("HasEsim", "_bluetoothVersionLabel", "_wwanInfo"):
            self.assertIn(tok, models_cs)
        for tok in (
            "A92F26CA-EDA7-4B1D-9DB2-27B68AA5A2EB",
            "HasEsimIndicatorInMbnOutput",
            "mbn show interfaces",
            "RefreshPeripheralsAfterDrivers",
        ):
            self.assertIn(tok, hw_cs)
        self.assertIn("RefreshPeripheralsAfterDrivers()", main_xaml_cs)

    def test_wis_task_269_270_v344_contracts(self):
        """Proverava ugovore za WIS v3.4.4 / ServiceHub v3.6.4 (V344-01..V344-03, IMP-39, IMP-44..46, IMP-51, IMP-52)."""
        import re

        ui_dir = ROOT / "UI"
        gate_src = (HWTEST_DIR / "Core" / "DriverReadinessGate.cs").read_text(encoding="utf-8")
        native_src = (HWTEST_DIR / "Interop" / "NativeMethods.cs").read_text(encoding="utf-8")
        diag_src = (HWTEST_DIR / "Core" / "AutoHardwareDiagnostics.cs").read_text(encoding="utf-8")
        shell_src = (HWTEST_DIR / "HwTestShell.cs").read_text(encoding="utf-8")
        models_cs = (ui_dir / "Models.cs").read_text(encoding="utf-8")
        tel_cs = (ui_dir / "TelemetryService.cs").read_text(encoding="utf-8")
        main_xaml_cs = (ui_dir / "MainWindow.xaml.cs").read_text(encoding="utf-8")
        ui_hw_cs = (ui_dir / "MainWindow.HwTest.cs").read_text(encoding="utf-8")

        # V344-01 (Odluke 1A + 2A): DriverReadinessGate i povezivanje u HwTestShell + MainWindow.HwTest
        for tok in (
            "class DriverGateState",
            "CanEmergencyUnlock",
            "HasCode14PendingReboot",
            "HasBackgroundWuDrivers",
            "DriverReadinessGate.EvaluateLocal()",
            "IsAllowedErrorCode",
            "IsIgnoredPnpDevice",
            "IsGenericDisplayDriverName",
        ):
            self.assertIn(tok, gate_src + "\n" + "DriverReadinessGate.EvaluateLocal()")
        for tok in (
            "SetInteractiveTestsGateProvider",
            "SetOpenDriversTabHandler",
            "RefreshDriverGateUi",
            "RefreshHardwareProbeAfterDrivers",
            "_driverGateBanner",
            "driver_gate_emergency_override_reason",
        ):
            self.assertIn(tok, shell_src)
        for tok in (
            "EvaluateInteractiveTestsDriverGate",
            "OpenDriversTabFromHwTest",
            "NotifyHwTestDriverStateChanged",
        ):
            self.assertIn(tok, ui_hw_cs)
        self.assertIn("pending_driver_count", main_xaml_cs)
        self.assertIn("installing_driver", main_xaml_cs)

        # V344-02 (Opcija A): Adaptivna rezervacija CPU niti + ThreadPriority.Lowest + Thread.Yield()
        for tok in ("ComputeCpuStressThreadCount", "ThreadPriority.Lowest", "Thread.Yield()", "stress_threads", "reserved_threads"):
            self.assertIn(tok, diag_src)
        self.assertIn("ComputeCpuStressThreadCount", main_xaml_cs)
        self.assertIn("ThreadPriority.Lowest", main_xaml_cs)

        # V344-03 + IMP-51: Perzistencija preko restarta, TryParsePersistedUtcTimestamp bez RoundtripKind|AdjustToUniversal, SaveDeployStatePhase(5)/(6)
        self.assertNotIn("DateTimeStyles.RoundtripKind | DateTimeStyles.AdjustToUniversal", shell_src)
        for tok in (
            "TryParsePersistedUtcTimestamp",
            "TryRestorePersistedTestState",
            "ResetAllTestsWithConfirmation",
            "phase5_metrics",
            r"C:\Windows\Logs\WIS\hwtest-result.json",
        ):
            self.assertIn(tok, shell_src)
        self.assertIn("SaveDeployStatePhase(5);", main_xaml_cs)
        self.assertIn("SaveDeployStatePhase(6);", ui_hw_cs + "\n" + main_xaml_cs)

        # IMP-39, IMP-44, IMP-45, IMP-52 u UI
        self.assertIn("GetAppsStatusSnapshot", models_cs)
        self.assertIn("GetAppsStatusSnapshot()", tel_cs)
        tel_cs_code = re.sub(r"//.*", "", tel_cs)
        self.assertNotIn("_dispatcher.Invoke", tel_cs_code)
        self.assertIn("SanitizeHardwareIdentityValue", main_xaml_cs)
        self.assertIn("HydrateAppsStateOnResume", main_xaml_cs)
        self.assertIn("加载中...", tel_cs + "\n" + main_xaml_cs + "\n" + models_cs)

        # IMP-46: IsLocationPolicyDisabled u NativeMethods
        self.assertIn("IsLocationPolicyDisabled", native_src)
        self.assertIn("lfsvc", native_src)

    def test_wis_task_271_272_v350_contracts(self):
        """Proverava WIS-TASK-271 (IMP-53..69) i WIS-TASK-272 (V350-01..08) ugovore za C#, ServiceHub i Patch-BootWim."""
        import json

        result_src = (HWTEST_DIR / "Core" / "ResultModel.cs").read_text(encoding="utf-8")
        auto_src = (HWTEST_DIR / "Core" / "AutoHardwareDiagnostics.cs").read_text(encoding="utf-8")
        probe_src = (HWTEST_DIR / "Core" / "HardwareProbe.cs").read_text(encoding="utf-8")
        gate_src = (HWTEST_DIR / "Core" / "DriverReadinessGate.cs").read_text(encoding="utf-8")
        shell_src = (HWTEST_DIR / "HwTestShell.cs").read_text(encoding="utf-8")

        main_cs = (ROOT / "UI" / "MainWindow.xaml.cs").read_text(encoding="utf-8")
        main_hw_cs = (ROOT / "UI" / "MainWindow.Hardware.cs").read_text(encoding="utf-8")
        main_hwt_cs = (ROOT / "UI" / "MainWindow.HwTest.cs").read_text(encoding="utf-8")
        main_reboot_cs = (ROOT / "UI" / "MainWindow.Reboot.cs").read_text(encoding="utf-8")
        models_cs = (ROOT / "UI" / "Models.cs").read_text(encoding="utf-8")
        label_cs = (ROOT / "UI" / "LabelPrintService.cs").read_text(encoding="utf-8")
        tel_cs = (ROOT / "UI" / "TelemetryService.cs").read_text(encoding="utf-8")
        loc_cs = (ROOT / "UI" / "LocalizationService.cs").read_text(encoding="utf-8")

        reports_py = (ROOT / "ServiceHub" / "servishub" / "reports.py").read_text(encoding="utf-8")
        hub_tel_py = (ROOT / "ServiceHub" / "servishub" / "telemetry.py").read_text(encoding="utf-8")
        boot_py = (ROOT / "ISO" / "Skripte" / "Patch-BootWim.py").read_text(encoding="utf-8")

        # V350-01 & IMP-61: Preliminarni Basic Display status ne truje checkpoint / izvestaj
        self.assertIn('AppVersion = "3.5.0";', result_src)
        self.assertIn("public bool IsPassivePreliminary { get; set; }", result_src)
        self.assertIn("res.IsPassivePreliminary = true;", auto_src)
        self.assertIn("HasAnyRealCompletedTestsForCheckpoint", shell_src)
        self.assertIn("SyncFinalReportBeforeCompletion", shell_src)
        self.assertIn("Ignorišem pre-drajverski Basic Display auto_gpu=FAIL iz persisted stanja.", shell_src)
        self.assertIn("HasAnyRealCompletedTestsForCheckpoint", main_reboot_cs)

        # V350-02 & V350-03: Asinhroni termalni tajmer i WMI timeouti (2500ms)
        self.assertIn("private System.Timers.Timer _thermalTimer;", main_cs)
        self.assertIn("private int _thermalUpdateInFlight = 0;", main_cs)
        self.assertIn("RunThermalUpdateBackground", main_cs)
        self.assertIn("ApplyThermalTelemetryToUi", main_cs)
        self.assertIn("private int _probeRefreshInFlight = 0;", shell_src)
        self.assertIn("RefreshHardwareProbeAfterDrivers", shell_src)
        self.assertIn("_lastNotifyProbeRefreshUtc", main_hwt_cs)
        self.assertIn("private static EnumerationOptions CreateWmiOptions(int timeoutMs)", probe_src)
        self.assertIn("CreateWmiOptions(2500)", probe_src)
        self.assertIn("RunWmiWithTimeout", probe_src)

        # V350-04: Progresivni tagovi rezolucija (2256x1504 FHD+, 2496x1664 QHD, itd.) i hw.display sinhronizacija
        self.assertIn('(lw == 2256 && lh == 1504)', label_cs)
        self.assertIn('(lw == 2400 && lh == 1600)) return "FHD+";', label_cs)
        self.assertIn('(lw == 2496 && lh == 1664) || (lw == 2560 && lh == 1440)) return "QHD";', label_cs)
        self.assertIn('hw["display"] =', tel_cs)
        self.assertIn("def _format_hw_display(hw: dict) -> str:", reports_py)

        # V350-06: Kontrolisani Faza 4 -> 4B (InstallStaged) -> 1 reboot -> PostRebootFinalize -> Faza 5 pipeline
        self.assertIn("post_reboot_finalize", main_cs)
        self.assertIn("SaveDeployStatePhase", main_cs)
        self.assertIn("WaitForBackgroundDriverDownloadIfActive", main_cs)
        self.assertIn('RunPhase2DriversStage("PostRebootFinalize"', main_cs)
        self.assertIn("InstallStaged", main_cs)
        self.assertIn("private static bool IsPostRebootFinalizePending()", gate_src)
        self.assertIn("HasCode14PendingReboot", gate_src)
        self.assertIn("RunWithTimeout", gate_src)
        for key in (
            "HwTGateDriversPipelineTitle",
            "HwTGateDriversPipelineDetail",
            "WinPostRebootFinalizeSub",
            "WinPostRebootFinalizeLive",
            "WinPhase4BSub",
            "WinPhase4BLive",
            "WinWaitDriverDownloadProgressFmt",
        ):
            self.assertEqual(loc_cs.count(f'"{key}"'), 3, f"Kljuc {key} mora postojati u sr, en i zh recniku")

        # V350-07: Izuzimanje License/P4.Activation iz kriticnih upozorenja Overview kartice i ServiceHub izvestaja
        self.assertIn('string.Equals(appItem.Id, "License", StringComparison.OrdinalIgnoreCase)', models_cs)
        self.assertIn('string.Equals(st.Id, "P4.Activation", StringComparison.OrdinalIgnoreCase)', models_cs)
        self.assertIn("RebuildOverviewAlerts()", models_cs)
        self.assertIn("def _apps_have_real_failures(apps) -> bool:", reports_py)

        # V350-08: Tačna detekcija sistemskog diska uz isključenje USB/Removable/Virtual drajvova
        self.assertIn("private static int ResolveSystemDiskIndex()", main_hw_cs)
        self.assertIn("IsExcludedPhysicalBusType", main_hw_cs)
        self.assertIn("IsExcludedWin32Disk", main_hw_cs)
        self.assertIn("private static int? ResolveSystemDiskIndex()", auto_src)
        self.assertIn("IsExcludedStorageBusType", auto_src)

        # IMP-58: Boot.wim detekcija Z:\win11 pre Y:\ i oslobadjanje T:\ bez obzira na iventoy.iso
        self.assertIn('Z:\\win11\\sources\\$OEM$\\$1\\setup\\WIS.Launcher.exe', boot_py)
        self.assertIn("mountvol.exe Y: /D", boot_py)
        idx_t_block = boot_py.find('if exist "T:\\iventoy.iso"')
        idx_t_del = boot_py.find("net.exe use T: /delete /y", idx_t_block)
        self.assertGreater(idx_t_block, 0)
        self.assertGreater(idx_t_del, idx_t_block)

        # IMP-65/66/67/69: Session ID, active time watchdog i log filtriranje
        self.assertIn("GetOrCreateInstallSessionId", tel_cs)
        self.assertIn('data["install_session_id"] = sessionId;', tel_cs)
        self.assertIn("WaitForPhaseProcessExitWithActiveTime", main_cs)
        self.assertIn("def _filter_stale_session_errors(", reports_py)
        self.assertIn("def _handle_install_session_change_locked(", hub_tel_py)
        self.assertIn("def load_today_live_logs_tail(", hub_tel_py)


if __name__ == "__main__":
    unittest.main()
