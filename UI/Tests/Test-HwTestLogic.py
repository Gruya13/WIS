#!/usr/bin/env python3
"""
Test-HwTestLogic.py
===================
Automatska verifikacija logike i šablona za WIS.HwTest.
"""

import json
import pathlib
import unittest

ROOT = pathlib.Path(__file__).resolve().parent.parent.parent
HWTEST_DIR = ROOT / "UI" / "HwTest"
LAYOUTS_PATH = HWTEST_DIR / "Assets" / "keyboard_layouts.json"
ZXING_PATH = HWTEST_DIR / "zxing.dll"
EXE_PATH = HWTEST_DIR / "WIS.HwTest.exe"

class TestHwTestComponents(unittest.TestCase):
    def test_binary_artifacts(self):
        self.assertTrue(EXE_PATH.exists(), f"WIS.HwTest.exe mora postojati u {EXE_PATH}")
        self.assertGreater(EXE_PATH.stat().st_size, 500_000, "WIS.HwTest.exe mora biti >= 500 KB sa ugrađenim resursima")
        self.assertTrue(ZXING_PATH.exists(), f"zxing.dll mora postojati u {ZXING_PATH}")

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
        self.assertIn("StartAutoSwitchToIrTimer", cam_src)
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
        self.assertIn("bestScore < 42.0", cam_src)
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
        ui_main_src = (ROOT / "UI" / "MainWindow.xaml.cs").read_text(encoding="utf-8")

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
        self.assertIn('"Touchpad / TrackPoint"', shell_src)
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

        # 1. Verzije v3.0.0 u root version.json i komponentama
        root_ver = json.loads((ROOT / "version.json").read_text(encoding="utf-8"))
        self.assertEqual(root_ver["version"], "3.0.0")
        self.assertEqual(root_ver["release_package"]["version"], "3.0.0")
        self.assertEqual(root_ver["components"]["ui"]["version"], "3.0.0")
        self.assertEqual(root_ver["components"]["launcher"]["version"], "3.0.0")
        self.assertEqual(root_ver["components"]["iso"]["version"], "3.0.0")
        self.assertEqual(root_ver["components"]["iso"]["embedded_launcher_version"], "3.0.0")
        self.assertEqual(root_ver["components"]["servicehub"]["version"], "3.3.0")

        # 2. AudioTestWindow.cs ne sme biti menjan
        audio_sha = hashlib.sha256((HWTEST_DIR / "Tests" / "AudioTestWindow.cs").read_bytes()).hexdigest()
        self.assertEqual(audio_sha, "503fd688d9a409d75180f60742e6125a76597b71d062bde385fb2de37c1a535c")

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

        ui_main_src = (ui_dir / "MainWindow.xaml.cs").read_text(encoding="utf-8")
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


if __name__ == "__main__":
    unittest.main()
