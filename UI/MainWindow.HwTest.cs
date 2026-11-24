using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using WIS.HwTest;
using WIS.HwTest.Core;

namespace WIS.UI
{
    public partial class MainWindow : Window
    {
        private const string HwTestTabTag = "WisHwTestTab";

        private TabItem _hwTestTabItem = null;
        private HwTestShell _embeddedHwTestShell = null;
        private readonly object _phase5HwSyncLock = new object();
        private ManualResetEvent _phase5HwCompletedEvent = null;
        private int _phase5LastCpuDurationSec = 0;
        private float _phase5LastMaxTemp = 0f;
        private float _phase5LastMaxThrot = 0f;
        private bool _phase5LastThermalCritical = false;
        private bool _phase5LastCancelled = false;
        private volatile bool _phase5UefiRebootInProgress = false;
        private volatile bool _phase5EmergencySkipAuthorized = false;
        private int _phase6DispatchGuard = 0;
        private DateTime _lastPendingAlertSoundUtc = DateTime.MinValue;
        private bool _isSequentialPeripheralRunActive = false;
        private DateTime _lastHwTestReportSavedUtc = DateTime.MinValue;

        private static bool IsPlaceholderIdentity(string val)
        {
            if (string.IsNullOrEmpty(val)) return true;
            string t = val.Trim();
            if (t.Length == 0) return true;
            if (t.EndsWith("...", StringComparison.Ordinal) || t.EndsWith("…", StringComparison.Ordinal)) return true;
            if (t.Equals("N/A", StringComparison.OrdinalIgnoreCase)) return true;
            if (t.Equals("NA", StringComparison.OrdinalIgnoreCase)) return true;
            if (t.Equals("None", StringComparison.OrdinalIgnoreCase)) return true;
            if (t.Equals("Unknown", StringComparison.OrdinalIgnoreCase)) return true;
            if (t.Equals("Standardni PC", StringComparison.OrdinalIgnoreCase)) return true;
            if (t.Equals("GenericPC", StringComparison.OrdinalIgnoreCase)) return true;
            if (t.Equals("Nepoznat model", StringComparison.OrdinalIgnoreCase)) return true;
            if (t.Equals("Nepoznat serijski", StringComparison.OrdinalIgnoreCase)) return true;
            if (t.StartsWith("Nepoznat", StringComparison.OrdinalIgnoreCase)) return true;
            if (t.StartsWith("Učitavanje", StringComparison.OrdinalIgnoreCase)) return true;
            if (t.StartsWith("Ucitavanje", StringComparison.OrdinalIgnoreCase)) return true;
            if (t.StartsWith("Detekcija", StringComparison.OrdinalIgnoreCase)) return true;
            if (t.StartsWith("Očitavanje", StringComparison.OrdinalIgnoreCase)) return true;
            if (t.StartsWith("Ocitavanje", StringComparison.OrdinalIgnoreCase)) return true;
            if (t.StartsWith("Provera", StringComparison.OrdinalIgnoreCase)) return true;
            if (t.StartsWith("Loading", StringComparison.OrdinalIgnoreCase)) return true;
            if (t.StartsWith("Detecting", StringComparison.OrdinalIgnoreCase)) return true;
            if (t.StartsWith("Reading", StringComparison.OrdinalIgnoreCase)) return true;
            if (t.StartsWith("Checking", StringComparison.OrdinalIgnoreCase)) return true;
            if (t.StartsWith("To be filled", StringComparison.OrdinalIgnoreCase)) return true;
            if (t.Equals("Default string", StringComparison.OrdinalIgnoreCase)) return true;
            if (t.Equals("System Product Name", StringComparison.OrdinalIgnoreCase)) return true;
            if (t.Equals("System Serial Number", StringComparison.OrdinalIgnoreCase)) return true;
            if (t.IndexOf("加载中", StringComparison.Ordinal) >= 0) return true;
            if (t.IndexOf("正在加载", StringComparison.Ordinal) >= 0) return true;
            if (t.IndexOf("读取中", StringComparison.Ordinal) >= 0) return true;
            if (t.IndexOf("检测中", StringComparison.Ordinal) >= 0) return true;
            if (t.IndexOf("正在检测", StringComparison.Ordinal) >= 0) return true;
            if (t.IndexOf("未知", StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        private void GetCurrentDeviceIdentity(out string model, out string serial, out string mfg, out string sku)
        {
            string vmModel = ViewModel != null ? ViewModel.ComputerModel : null;
            string apModel = (ViewModel != null && ViewModel.Autopilot != null) ? ViewModel.Autopilot.HwModel : null;
            string hwModel = (ViewModel != null && ViewModel.HardwareInfo != null) ? ViewModel.HardwareInfo.MotherboardModel : null;
            if (!IsPlaceholderIdentity(vmModel))
                model = vmModel.Trim();
            else if (!IsPlaceholderIdentity(apModel))
                model = apModel.Trim();
            else if (!IsPlaceholderIdentity(hwModel))
                model = hwModel.Trim();
            else
                model = "";

            string vmSerial = ViewModel != null ? ViewModel.SerialNumber : null;
            string apSerial = (ViewModel != null && ViewModel.Autopilot != null) ? ViewModel.Autopilot.HwSerialNumber : null;
            string hwSerial = (ViewModel != null && ViewModel.HardwareInfo != null) ? ViewModel.HardwareInfo.SerialNumber : null;
            if (!IsPlaceholderIdentity(vmSerial))
                serial = vmSerial.Trim();
            else if (!IsPlaceholderIdentity(apSerial))
                serial = apSerial.Trim();
            else if (!IsPlaceholderIdentity(hwSerial))
                serial = hwSerial.Trim();
            else
                serial = "";

            mfg = "";
            sku = "";
            try
            {
                using (RegistryKey biosReg = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS"))
                {
                    if (biosReg != null)
                    {
                        object sm = biosReg.GetValue("SystemManufacturer");
                        if (sm != null) mfg = sm.ToString().Trim();
                        object ss = biosReg.GetValue("SystemSKU");
                        if (ss != null) sku = ss.ToString().Trim();
                    }
                }
            }
            catch { }
        }

        private DriverGateState _cachedLocalDriverGate = null;
        private int _driverGateEvalInFlight = 0;
        private DateTime _lastDriverGateEvalUtc = DateTime.MinValue;
        private DateTime _lastNotifyProbeRefreshUtc = DateTime.MinValue;

        private static DriverGateState CloneDriverGateState(DriverGateState src)
        {
            DriverGateState dst = new DriverGateState();
            if (src == null) return dst;
            dst.IsUnlocked = src.IsUnlocked;
            dst.CanEmergencyUnlock = src.CanEmergencyUnlock;
            dst.ReasonTitle = src.ReasonTitle ?? string.Empty;
            dst.ReasonDetail = src.ReasonDetail ?? string.Empty;
            dst.IsDriverInstallActive = src.IsDriverInstallActive;
            dst.HasCode14PendingReboot = src.HasCode14PendingReboot;
            dst.HasBackgroundWuDrivers = src.HasBackgroundWuDrivers;
            dst.ProblemDevices = src.ProblemDevices != null
                ? new List<string>(src.ProblemDevices)
                : new List<string>();
            return dst;
        }

        private void TriggerAsyncDriverGateRefresh(bool force)
        {
            if (!force && _cachedLocalDriverGate != null &&
                (DateTime.UtcNow - _lastDriverGateEvalUtc).TotalMilliseconds < 1500.0)
            {
                return;
            }
            if (Interlocked.CompareExchange(ref _driverGateEvalInFlight, 1, 0) != 0)
            {
                return;
            }

            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    DriverGateState evaluated = DriverReadinessGate.EvaluateLocal();
                    if (evaluated == null) evaluated = new DriverGateState();

                    bool changed = (_cachedLocalDriverGate == null) ||
                        (_cachedLocalDriverGate.IsUnlocked != evaluated.IsUnlocked) ||
                        (_cachedLocalDriverGate.HasCode14PendingReboot != evaluated.HasCode14PendingReboot) ||
                        ((_cachedLocalDriverGate.ProblemDevices != null ? _cachedLocalDriverGate.ProblemDevices.Count : 0) !=
                         (evaluated.ProblemDevices != null ? evaluated.ProblemDevices.Count : 0));

                    _cachedLocalDriverGate = evaluated;
                    _lastDriverGateEvalUtc = DateTime.UtcNow;

                    if (changed && _embeddedHwTestShell != null &&
                        Dispatcher != null && !Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
                    {
                        Dispatcher.BeginInvoke((Action)delegate
                        {
                            if (_embeddedHwTestShell != null)
                            {
                                _embeddedHwTestShell.RefreshDriverGateUi();
                            }
                        });
                    }
                }
                catch { }
                finally
                {
                    Interlocked.Exchange(ref _driverGateEvalInFlight, 0);
                }
            });
        }

        public DriverGateState EvaluateInteractiveTestsDriverGate()
        {
            TriggerAsyncDriverGateRefresh(false);
            DriverGateState state = CloneDriverGateState(_cachedLocalDriverGate);

            int currentPhase = (ViewModel != null) ? ViewModel.CurrentPhase : 1;
            bool isServiceFinished = (ViewModel != null) && (ViewModel.IsServiceFinished || ViewModel.IsCompleted);
            bool phase2Failed = (_failedPhase == 2);
            bool postRebootFinalizePending = _postRebootFinalizePending;
            bool phase2CompletedOrSkipped = isServiceFinished || (currentPhase >= 5 && !postRebootFinalizePending);
            if (!phase2CompletedOrSkipped && !phase2Failed && !postRebootFinalizePending)
            {
                try
                {
                    string statePath = @"C:\setup\deploy-state.json";
                    if (File.Exists(statePath))
                    {
                        string dsJson = File.ReadAllText(statePath, Encoding.UTF8);
                        JsonNode dsRoot = JsonHelper.Parse(dsJson);
                        if (dsRoot != null && dsRoot.IsObject &&
                            dsRoot["phase"].AsInt(1) >= 5 &&
                            !dsRoot["post_reboot_finalize"].AsBool(false))
                        {
                            phase2CompletedOrSkipped = true;
                        }
                    }
                }
                catch { }
            }
            bool phase2NotDone = phase2Failed || !phase2CompletedOrSkipped;

            bool journalInterimRebootPending = false;
            if (currentPhase <= 3 && !isServiceFinished)
            {
                try
                {
                    string journalPath = @"C:\setup\step-journal.json";
                    if (File.Exists(journalPath))
                    {
                        string jTxt = File.ReadAllText(journalPath, Encoding.UTF8);
                        if (!string.IsNullOrWhiteSpace(jTxt))
                        {
                            JsonNode jRoot = JsonHelper.Parse(jTxt);
                            if (jRoot != null && jRoot.IsObject && jRoot["interim_reboot_required"].AsBool(false))
                            {
                                journalInterimRebootPending = true;
                            }
                        }
                    }
                }
                catch { }
            }

            bool driversTabBusy = false;
            if (ViewModel != null && ViewModel.Drivers != null)
            {
                if (ViewModel.Drivers.IsBatchResolving || ViewModel.Drivers.IsScanning)
                {
                    driversTabBusy = true;
                }
                else if (ViewModel.Drivers.MissingDevices != null)
                {
                    for (int i = 0; i < ViewModel.Drivers.MissingDevices.Count; i++)
                    {
                        DriverItemViewModel d = ViewModel.Drivers.MissingDevices[i];
                        if (d != null && d.IsInstalling)
                        {
                            driversTabBusy = true;
                            break;
                        }
                    }
                }
            }

            int bgPendingDrivers;
            bool bgInstallingDriver;
            GetBackgroundWuDriverState(out bgPendingDrivers, out bgInstallingDriver);
            bool hasBgWuDrivers = (bgPendingDrivers > 0) || bgInstallingDriver;

            state.IsDriverInstallActive = phase2NotDone || driversTabBusy || hasBgWuDrivers;
            state.HasBackgroundWuDrivers = hasBgWuDrivers;
            if (journalInterimRebootPending)
            {
                state.HasCode14PendingReboot = true;
            }

            if (phase2Failed)
            {
                state.IsUnlocked = false;
                state.CanEmergencyUnlock = false;
                state.ReasonTitle = LocalizationService.Get("HwTGatePhase2FailedTitle", "Faza 2 (Instalacija drajvera) nije uspešno završena");
                state.ReasonDetail = LocalizationService.Get("HwTGatePhase2FailedDetail", "Interaktivni testovi periferija su zaključani dok se Faza 2 (Drajveri) ne završi ili dok se drajveri ne instaliraju u tabu Drajveri.");
                return state;
            }

            if (phase2NotDone)
            {
                state.IsUnlocked = false;
                state.CanEmergencyUnlock = false;
                state.ReasonTitle = LocalizationService.Get("HwTGateDriversPipelineTitle", "Instalacija drajvera i završni restart još nisu završeni");
                state.ReasonDetail = LocalizationService.Get("HwTGateDriversPipelineDetail", "Interaktivni testovi periferija su zaključani dok se drajveri ne instaliraju i sistem ne restartuje pred Fazu 5.");
                return state;
            }

            if (driversTabBusy)
            {
                state.IsUnlocked = false;
                state.CanEmergencyUnlock = false;
                state.ReasonTitle = LocalizationService.Get("HwTGateDriversBusyTitle", "Instalacija ili skeniranje drajvera je u toku");
                state.ReasonDetail = LocalizationService.Get("HwTGateDriversBusyDetail", "Sačekajte da se operacija u tabu Drajveri završi pre pokretanja interaktivnih testova.");
                return state;
            }

            if (hasBgWuDrivers)
            {
                state.IsUnlocked = false;
                state.CanEmergencyUnlock = false;
                state.ReasonTitle = LocalizationService.Get("HwTGateBgWuDriversTitle", "Pozadinski Windows Update preuzima ili instalira drajvere");
                state.ReasonDetail = string.Format(
                    LocalizationService.Get("HwTGateBgWuDriversDetailFmt", "Pozadinski WU obrađuje drajvere (preostalo drajvera: {0}). Interaktivni testovi će se otključati po završetku instalacije drajvera."),
                    Math.Max(1, bgPendingDrivers));
                return state;
            }

            if (state.HasCode14PendingReboot)
            {
                state.IsUnlocked = false;
                state.CanEmergencyUnlock = false;
                if (string.IsNullOrEmpty(state.ReasonTitle))
                {
                    state.ReasonTitle = LocalizationService.Get("HwTGateRebootReqTitle", "Potreban je međurestart sistema radi aktivacije drajvera (Code 14)");
                    state.ReasonDetail = LocalizationService.Get("HwTGateRebootReqDetail", "Neki instalirani drajveri zahtevaju restart računara pre nego što periferije postanu spremne za interaktivno testiranje.");
                }
                return state;
            }

            if (state.ProblemDevices != null && state.ProblemDevices.Count > 0)
            {
                state.IsUnlocked = false;
                state.CanEmergencyUnlock = true;
                return state;
            }

            state.IsUnlocked = true;
            state.CanEmergencyUnlock = false;
            state.ReasonTitle = string.Empty;
            state.ReasonDetail = string.Empty;
            return state;
        }

        private void OpenDriversTabFromHwTest()
        {
            Action openAction = delegate
            {
                NavigateToAlertTab("drivers");
                DetectDrivers();
            };

            if (Dispatcher != null && !Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
            {
                if (Dispatcher.CheckAccess()) openAction();
                else Dispatcher.BeginInvoke(openAction);
            }
        }

        public void NotifyHwTestDriverStateChanged(bool refreshHardwareProbe)
        {
            if (_embeddedHwTestShell == null) return;
            TriggerAsyncDriverGateRefresh(true);
            Action act = delegate
            {
                if (_embeddedHwTestShell == null) return;
                try
                {
                    if (refreshHardwareProbe)
                    {
                        DateTime nowUtc = DateTime.UtcNow;
                        if ((nowUtc - _lastNotifyProbeRefreshUtc).TotalMilliseconds >= 1500.0)
                        {
                            DriverGateState gate = EvaluateInteractiveTestsDriverGate();
                            if (gate != null && gate.IsUnlocked)
                            {
                                _lastNotifyProbeRefreshUtc = nowUtc;
                                _embeddedHwTestShell.RefreshHardwareProbeAfterDrivers();
                            }
                        }
                    }
                    _embeddedHwTestShell.RefreshDriverGateUi();
                }
                catch { }
            };

            if (Dispatcher != null && !Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
            {
                if (Dispatcher.CheckAccess()) act();
                else Dispatcher.BeginInvoke(act);
            }
        }

        private void EnsureEmbeddedHwTestTab()
        {
            try
            {
                if (MainTabControl == null) return;

                if (_hwTestTabItem == null)
                {
                    foreach (object item in MainTabControl.Items)
                    {
                        TabItem existingTab = item as TabItem;
                        if (existingTab != null && string.Equals(Convert.ToString(existingTab.Tag), HwTestTabTag, StringComparison.Ordinal))
                        {
                            _hwTestTabItem = existingTab;
                            break;
                        }
                    }
                }

                if (_embeddedHwTestShell == null)
                {
                    string model, serial, mfg, sku;
                    GetCurrentDeviceIdentity(out model, out serial, out mfg, out sku);

                    ModelProfileClient client = new ModelProfileClient();
                    HwTestLogger.Initialize(client.HubBaseUrl);
                    _embeddedHwTestShell = new HwTestShell(@"C:\setup\hwtest-result.json", model, serial, mfg, sku, client);
                    _embeddedHwTestShell.SetUefiBiosRebootHandler(HandlePhase5UefiBiosRebootRequest);
                    _embeddedHwTestShell.SetInteractiveTestsGateProvider(EvaluateInteractiveTestsDriverGate);
                    _embeddedHwTestShell.SetOpenDriversTabHandler(OpenDriversTabFromHwTest);
                    _embeddedHwTestShell.PendingInteractiveTestsRequired += OnPendingInteractiveTestsRequired;
                }

                if (_hwTestTabItem == null)
                {
                    UIElement embeddedView = _embeddedHwTestShell.CreateEmbeddedView(this, OnEmbeddedHwTestReportSaved);

                    _hwTestTabItem = new TabItem
                    {
                        Tag = HwTestTabTag,
                        Content = embeddedView
                    };
                    if (ViewModel != null)
                    {
                        _hwTestTabItem.SetBinding(HeaderedContentControl.HeaderProperty,
                            new System.Windows.Data.Binding("TabHwTestHeader") { Source = ViewModel });
                    }
                    else
                    {
                        _hwTestTabItem.Header = "Hardverski testovi";
                    }

                    // Dodajemo na kraj MainTabControl-a da indeksi postojecih tabova (0..7) ostanu netaknuti.
                    MainTabControl.Items.Add(_hwTestTabItem);
                    _embeddedHwTestShell.RefreshDriverGateUi();
                }
            }
            catch (Exception ex)
            {
                if (ViewModel != null)
                {
                    ViewModel.AddLog("WARN", "Hardver", "Nije uspela inicijalizacija ugrađenog HW Test taba: " + ex.Message);
                }
            }
        }

        private void RefreshEmbeddedHwTestIdentity()
        {
            if (_embeddedHwTestShell == null) return;
            try
            {
                string model, serial, mfg, sku;
                GetCurrentDeviceIdentity(out model, out serial, out mfg, out sku);
                _embeddedHwTestShell.UpdateIdentity(model, serial);
            }
            catch { }
        }

        private void ShutdownEmbeddedHwTest()
        {
            try
            {
                if (_embeddedHwTestShell != null)
                {
                    try { _embeddedHwTestShell.PendingInteractiveTestsRequired -= OnPendingInteractiveTestsRequired; } catch { }
                    _embeddedHwTestShell.ShutdownEmbedded();
                }
            }
            catch { }

            lock (_phase5HwSyncLock)
            {
                if (_phase5HwCompletedEvent != null)
                {
                    try { _phase5HwCompletedEvent.Set(); } catch { }
                }
            }
        }

        private bool ForceSkipEmbeddedPhase5()
        {
            return ForceSkipEmbeddedPhase5(null);
        }

        private bool ForceSkipEmbeddedPhase5(string customReason)
        {
            if (_phase5UefiRebootInProgress) return false;
            if (_embeddedHwTestShell == null) return false;

            try
            {
                _phase5EmergencySkipAuthorized = true;
                _phase5LastCancelled = true;
                if (!string.IsNullOrWhiteSpace(customReason))
                {
                    _embeddedHwTestShell.ForceCompletePhase5SkipPending(customReason.Trim());
                }
                else
                {
                    _embeddedHwTestShell.ForceCompletePhase5SkipPending();
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        private bool TryReadPersistedInteractiveTestsFromReportFiles(out List<string> pendingNames, out int completedInteractiveCount)
        {
            pendingNames = new List<string>();
            completedInteractiveCount = 0;

            string currentModel, currentSerial, currentMfg, currentSku;
            GetCurrentDeviceIdentity(out currentModel, out currentSerial, out currentMfg, out currentSku);

            string[] candidatePaths = new string[]
            {
                @"C:\setup\hwtest-result.json",
                @"C:\Windows\Logs\WIS\hwtest-result.json"
            };

            for (int pIdx = 0; pIdx < candidatePaths.Length; pIdx++)
            {
                string path = candidatePaths[pIdx];
                try
                {
                    if (!File.Exists(path)) continue;
                    string json = File.ReadAllText(path, Encoding.UTF8);
                    if (string.IsNullOrWhiteSpace(json)) continue;

                    JsonNode root = JsonHelper.Parse(json);
                    if (root == null || !root.IsObject) continue;

                    string fileSerial = root.ContainsKey("serial") ? root["serial"].AsString("") : "";
                    if (string.IsNullOrEmpty(fileSerial) && root.ContainsKey("serial_number"))
                    {
                        fileSerial = root["serial_number"].AsString("");
                    }

                    if (!IsPlaceholderIdentity(currentSerial) && !IsPlaceholderIdentity(fileSerial) &&
                        !string.Equals(currentSerial.Trim(), fileSerial.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    JsonNode testsNode = root["tests"];
                    if (testsNode == null || !testsNode.IsArray) continue;

                    List<string> filePending = new List<string>();
                    int fileCompleted = 0;
                    int interactiveTotal = 0;

                    for (int i = 0; i < testsNode.Count; i++)
                    {
                        JsonNode t = testsNode[i];
                        if (t == null || !t.IsObject) continue;
                        bool isAuto = t["automated"].AsBool(true);
                        if (isAuto) continue;

                        string status = t["status"].AsString("").Trim().ToUpperInvariant();
                        if (status == "NA" || status == "NOTAPPLICABLE" || status == "NOT_APPLICABLE")
                        {
                            continue;
                        }

                        interactiveTotal++;
                        if (status == "PASS" || status == "FAIL")
                        {
                            fileCompleted++;
                        }
                        else if (status == "SKIP")
                        {
                            string skipReason = t.ContainsKey("manual_skip_reason") ? t["manual_skip_reason"].AsString("") : "";
                            if (string.IsNullOrWhiteSpace(skipReason) && t.ContainsKey("reason"))
                            {
                                string r = t["reason"].AsString("");
                                if (!string.IsNullOrWhiteSpace(r) &&
                                    r.IndexOf("Nije pokrenut", StringComparison.OrdinalIgnoreCase) < 0 &&
                                    r.IndexOf("未运行", StringComparison.OrdinalIgnoreCase) < 0 &&
                                    r.IndexOf("Čeka", StringComparison.OrdinalIgnoreCase) < 0)
                                {
                                    skipReason = r;
                                }
                            }

                            if (!string.IsNullOrWhiteSpace(skipReason))
                            {
                                fileCompleted++;
                            }
                            else
                            {
                                string title = t["title"].AsString("");
                                if (string.IsNullOrEmpty(title)) title = t["id"].AsString("Interaktivni test");
                                filePending.Add(title);
                            }
                        }
                        else
                        {
                            string title = t["title"].AsString("");
                            if (string.IsNullOrEmpty(title)) title = t["id"].AsString("Interaktivni test");
                            filePending.Add(title);
                        }
                    }

                    if (interactiveTotal > 0 || testsNode.Count > 0)
                    {
                        pendingNames = filePending;
                        completedInteractiveCount = fileCompleted;
                        return true;
                    }
                }
                catch { }
            }

            return false;
        }

        private static List<string> ReadPendingInteractiveTestsFromReportFile()
        {
            var list = new List<string>();
            try
            {
                string path = @"C:\setup\hwtest-result.json";
                if (!File.Exists(path)) return list;

                string json = File.ReadAllText(path, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(json)) return list;

                JsonNode root = JsonHelper.Parse(json);
                if (root == null || !root.IsObject) return list;

                JsonNode testsNode = root["tests"];
                if (testsNode == null || !testsNode.IsArray) return list;

                for (int i = 0; i < testsNode.Count; i++)
                {
                    JsonNode t = testsNode[i];
                    if (t == null || !t.IsObject) continue;
                    bool isAuto = t["automated"].AsBool(true);
                    if (isAuto) continue;

                    string status = t["status"].AsString("").Trim().ToUpperInvariant();
                    if (status == "PENDING" || status == "RUNNING" || string.IsNullOrEmpty(status))
                    {
                        string title = t["title"].AsString("");
                        if (string.IsNullOrEmpty(title)) title = t["id"].AsString("Interaktivni test");
                        list.Add(title);
                    }
                }
            }
            catch { }
            return list;
        }

        private List<string> GetPendingInteractiveHwTestNames()
        {
            try
            {
                if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WIS_STRESS_TEST_INPUT_JSON")))
                {
                    return new List<string>();
                }
            }
            catch { }

            List<string> persistedPending;
            int persistedCompleted;
            bool hasPersistedFile = TryReadPersistedInteractiveTestsFromReportFiles(out persistedPending, out persistedCompleted);

            if (_embeddedHwTestShell != null)
            {
                try
                {
                    List<string> shellPending = null;
                    if (Dispatcher != null && !Dispatcher.CheckAccess() && !Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
                    {
                        Dispatcher.Invoke((Action)(() =>
                        {
                            if (_embeddedHwTestShell != null)
                            {
                                shellPending = _embeddedHwTestShell.GetPendingInteractiveTestNames();
                            }
                        }));
                    }
                    else
                    {
                        shellPending = _embeddedHwTestShell.GetPendingInteractiveTestNames();
                    }

                    if (shellPending != null)
                    {
                        if (shellPending.Count == 0)
                        {
                            return shellPending;
                        }
                        if (hasPersistedFile && persistedCompleted > 0 && persistedPending.Count == 0)
                        {
                            return new List<string>();
                        }
                        if (hasPersistedFile && persistedCompleted > 0 && persistedPending.Count < shellPending.Count)
                        {
                            return persistedPending;
                        }
                        return shellPending;
                    }
                }
                catch { }
            }

            if (hasPersistedFile)
            {
                return persistedPending;
            }

            if (File.Exists(@"C:\setup\hwtest-result.json"))
            {
                return ReadPendingInteractiveTestsFromReportFile();
            }

            if (ViewModel != null && ViewModel.IsLaptop && !ViewModel.IsHwTestFinished)
            {
                return new List<string> { "Obavezni interaktivni testovi periferija (Faza 5)" };
            }

            return new List<string>();
        }

        private bool HasUnfinishedInteractiveHwTests()
        {
            try
            {
                if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WIS_STRESS_TEST_INPUT_JSON")))
                {
                    return false;
                }
            }
            catch { }

            if (_phase5EmergencySkipAuthorized)
            {
                return false;
            }

            List<string> persistedPending;
            int persistedCompleted;
            bool hasPersistedFile = TryReadPersistedInteractiveTestsFromReportFiles(out persistedPending, out persistedCompleted);

            if (_embeddedHwTestShell != null)
            {
                try
                {
                    bool hasUnfinished = false;
                    if (Dispatcher != null && !Dispatcher.CheckAccess() && !Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
                    {
                        Dispatcher.Invoke((Action)(() =>
                        {
                            if (_embeddedHwTestShell != null)
                            {
                                hasUnfinished = _embeddedHwTestShell.HasUnfinishedInteractiveTests();
                            }
                        }));
                    }
                    else
                    {
                        hasUnfinished = _embeddedHwTestShell.HasUnfinishedInteractiveTests();
                    }

                    if (!hasUnfinished)
                    {
                        return false;
                    }
                    if (hasPersistedFile && persistedCompleted > 0 && persistedPending.Count == 0)
                    {
                        return false;
                    }
                    return true;
                }
                catch { }
            }

            if (hasPersistedFile)
            {
                return persistedPending.Count > 0;
            }

            if (File.Exists(@"C:\setup\hwtest-result.json"))
            {
                return ReadPendingInteractiveTestsFromReportFile().Count > 0;
            }

            return (ViewModel != null && ViewModel.IsLaptop && !ViewModel.IsHwTestFinished);
        }

        private void PlayPendingTestsAttentionSound()
        {
            DateTime now = DateTime.UtcNow;
            if ((now - _lastPendingAlertSoundUtc).TotalSeconds < 2.0) return;
            _lastPendingAlertSoundUtc = now;

            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    Console.Beep(880, 140);
                    Thread.Sleep(70);
                    Console.Beep(1046, 180);
                    Thread.Sleep(70);
                    Console.Beep(880, 140);
                }
                catch
                {
                    try { System.Media.SystemSounds.Exclamation.Play(); } catch { }
                }
            });
        }

        private void OnPendingInteractiveTestsRequired(List<string> pendingNames)
        {
            if (_phase5UefiRebootInProgress || _phase5EmergencySkipAuthorized) return;
            if (Dispatcher == null || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;

            Dispatcher.BeginInvoke((Action)(() =>
            {
                if (_phase5UefiRebootInProgress || _phase5EmergencySkipAuthorized) return;

                List<string> currentPending = (pendingNames != null && pendingNames.Count > 0)
                    ? pendingNames
                    : GetPendingInteractiveHwTestNames();

                if (currentPending == null || currentPending.Count == 0)
                {
                    HidePendingInteractiveTestsWarning();
                    return;
                }

                if (ViewModel != null)
                {
                    ViewModel.IsCompleted = false;
                    ViewModel.SubPhase = string.Format(LocalizationService.Get("HwTPendingInteractiveSubPhase", "Čekaju obavezni interaktivni testovi ({0})..."), currentPending.Count);
                    ViewModel.LiveStatus = string.Format(LocalizationService.Get("HwTAutoDonePendingInteractive", "⚠️ Automatski proces je završen — preostalo {0} obaveznih interaktivnih testova u Fazi 5."), currentPending.Count);
                    ViewModel.AddLog("WARN", "Hardver", string.Format(
                        "Automatski proces je završen; obavezni interaktivni testovi čekaju servisera ({0}): {1}",
                        currentPending.Count,
                        string.Join(", ", currentPending.ToArray())));
                }

                ShowPendingInteractiveTestsWarning(currentPending, true, true);
            }));
        }

        private void StartPendingInteractiveTestsFromUi()
        {
            if (_isSequentialPeripheralRunActive) return;

            EnsureEmbeddedHwTestTab();
            if (_embeddedHwTestShell == null)
            {
                MessageBox.Show(
                    LocalizationService.Get("HwTModuleUnavailable", "Ugrađeni modul za hardversko testiranje nije dostupan."),
                    LocalizationService.Get("HwTTitle", "WIS Hardverski testovi"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            DriverGateState gate = EvaluateInteractiveTestsDriverGate();
            _embeddedHwTestShell.RefreshDriverGateUi();
            if (gate != null && !gate.IsUnlocked && !gate.CanEmergencyUnlock)
            {
                if (MainTabControl != null && _hwTestTabItem != null)
                {
                    MainTabControl.SelectedItem = _hwTestTabItem;
                }
                if (ViewModel != null)
                {
                    ViewModel.AddLog("WARN", "Hardver", string.Format(
                        "Pokretanje interaktivnih testova odbijeno — instalacija drajvera nije završena: {0} ({1})",
                        gate.ReasonTitle, gate.ReasonDetail));
                }
                MessageBox.Show(
                    this,
                    string.Format("{0}\n\n{1}", gate.ReasonTitle, gate.ReasonDetail),
                    LocalizationService.Get("HwTTitle", "WIS Hardverski testovi"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (gate != null && gate.IsUnlocked)
            {
                _embeddedHwTestShell.RefreshHardwareProbeAfterDrivers();
            }

            HidePendingInteractiveTestsOverlayOnly();
            if (MainTabControl != null && _hwTestTabItem != null)
            {
                MainTabControl.SelectedItem = _hwTestTabItem;
            }

            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, (Action)(() =>
            {
                if (_isSequentialPeripheralRunActive || _embeddedHwTestShell == null) return;
                _isSequentialPeripheralRunActive = true;
                DateTime beforeRunUtc = DateTime.UtcNow;
                try
                {
                    _embeddedHwTestShell.RunSequentialPeripheralTests();
                }
                catch (Exception ex)
                {
                    if (ViewModel != null)
                    {
                        ViewModel.AddLog("WARN", "Hardver", "Greška pri pokretanju interaktivnih testova: " + ex.Message);
                    }
                }
                finally
                {
                    _isSequentialPeripheralRunActive = false;
                }

                if (_phase5UefiRebootInProgress || _phase5EmergencySkipAuthorized) return;

                List<string> remaining = GetPendingInteractiveHwTestNames();
                if (remaining.Count > 0)
                {
                    if (ViewModel != null)
                    {
                        ViewModel.SubPhase = string.Format(LocalizationService.Get("HwTRemainingInteractiveSubPhase", "Preostalo interaktivnih testova: {0}"), remaining.Count);
                        ViewModel.LiveStatus = string.Format(LocalizationService.Get("HwTTestingStoppedRemaining", "⚠️ Testiranje prekinuto — preostalo još {0} obaveznih interaktivnih testova."), remaining.Count);
                        ViewModel.AddLog("WARN", "Hardver", string.Format(
                            "Interaktivno testiranje zaustavljeno pre kraja; preostalo {0} testova: {1}",
                            remaining.Count,
                            string.Join(", ", remaining.ToArray())));
                    }
                    ShowPendingInteractiveTestsWarning(remaining, true, true);
                }
                else
                {
                    HidePendingInteractiveTestsWarning();
                    // Ako RunSequentialPeripheralTests() nije automatski sačuvao izveštaj (zavisno od stanja workflowa),
                    // OnEmbeddedHwTestReportSaved će okinuti čim se izveštaj snimi; ako nije u poslednjih par sekundi, podseti servisera.
                    if (_lastHwTestReportSavedUtc < beforeRunUtc && ViewModel != null)
                    {
                        ViewModel.AddLog("INFO", "Hardver", "Svi interaktivni testovi su završeni — čuvam hardverski izveštaj...");
                    }
                }
            }));
        }

        private string ShowEmergencySkipReasonDialog()
        {
            var dlg = new Window
            {
                Title = LocalizationService.Get("HwTEmergencySkipDialogTitle", "Obavezan razlog vanrednog preskakanja (Faza 5)"),
                Width = 540,
                Height = 290,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Background = MainViewModel.CreateFrozenBrush(30, 30, 32),
                Foreground = Brushes.White
            };

            var root = new Grid { Margin = new Thickness(18) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var titleTb = new TextBlock
            {
                Text = LocalizationService.Get("HwTEmergencySkipHeader", "⚠️ VANREDNO PRESKAKANJE OBAVEZNIH TESTOVA"),
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Foreground = MainViewModel.CreateFrozenBrush(245, 158, 11),
                Margin = new Thickness(0, 0, 0, 8)
            };
            Grid.SetRow(titleTb, 0);
            root.Children.Add(titleTb);

            var descTb = new TextBlock
            {
                Text = LocalizationService.Get("HwTEmergencySkipDesc", "Unesite jasan servisni razlog zašto se preostali interaktivni testovi preskaču (npr. neispravan ekran, kvar tastature, čeka zamenu dela). Uređaj će na ServiceHub-u biti označen kao NEEDS_SERVICE:"),
                FontSize = 12,
                Foreground = MainViewModel.CreateFrozenBrush(220, 220, 224),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            };
            Grid.SetRow(descTb, 1);
            root.Children.Add(descTb);

            var reasonBox = new TextBox
            {
                Background = MainViewModel.CreateFrozenBrush(20, 20, 22),
                Foreground = Brushes.White,
                CaretBrush = Brushes.White,
                BorderBrush = MainViewModel.CreateFrozenBrush(217, 119, 6),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8, 6, 8, 6),
                FontSize = 12.5,
                TextWrapping = TextWrapping.Wrap,
                AcceptsReturn = false,
                MaxLength = 300,
                VerticalContentAlignment = VerticalAlignment.Top
            };
            Grid.SetRow(reasonBox, 2);
            root.Children.Add(reasonBox);

            var errTb = new TextBlock
            {
                Text = LocalizationService.Get("HwTEmergencySkipValidation", "Obavezan unos (minimalno 5 karaktera)."),
                FontSize = 11,
                Foreground = MainViewModel.CreateFrozenBrush(248, 113, 113),
                Margin = new Thickness(0, 6, 0, 10)
            };
            Grid.SetRow(errTb, 3);
            root.Children.Add(errTb);

            var btnPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            var btnCancel = new Button
            {
                Content = LocalizationService.Get("HwTCancel", "Odustani"),
                IsCancel = true,
                Background = MainViewModel.CreateFrozenBrush(45, 45, 48),
                Foreground = Brushes.White,
                BorderBrush = MainViewModel.CreateFrozenBrush(78, 78, 86),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(14, 7, 14, 7),
                Margin = new Thickness(0, 0, 8, 0),
                Cursor = System.Windows.Input.Cursors.Hand
            };

            var btnConfirm = new Button
            {
                Content = LocalizationService.Get("HwTEmergencySkipConfirmBtn", "Potvrdi vanredno preskakanje"),
                IsDefault = true,
                IsEnabled = false,
                Background = MainViewModel.CreateFrozenBrush(185, 28, 28),
                Foreground = Brushes.White,
                BorderBrush = MainViewModel.CreateFrozenBrush(239, 68, 68),
                BorderThickness = new Thickness(1),
                FontWeight = FontWeights.Bold,
                Padding = new Thickness(14, 7, 14, 7),
                Cursor = System.Windows.Input.Cursors.Hand
            };

            string confirmedReason = null;
            reasonBox.TextChanged += delegate
            {
                string trimmed = (reasonBox.Text ?? "").Trim();
                bool valid = trimmed.Length >= 5;
                btnConfirm.IsEnabled = valid;
                errTb.Visibility = valid ? Visibility.Collapsed : Visibility.Visible;
            };
            btnConfirm.Click += delegate
            {
                string trimmed = (reasonBox.Text ?? "").Trim();
                if (trimmed.Length < 5)
                {
                    errTb.Visibility = Visibility.Visible;
                    return;
                }
                confirmedReason = trimmed;
                dlg.DialogResult = true;
                dlg.Close();
            };

            btnPanel.Children.Add(btnCancel);
            btnPanel.Children.Add(btnConfirm);
            Grid.SetRow(btnPanel, 4);
            root.Children.Add(btnPanel);

            dlg.Content = root;
            dlg.Loaded += delegate { reasonBox.Focus(); };

            bool? res = dlg.ShowDialog();
            return (res == true && !string.IsNullOrWhiteSpace(confirmedReason)) ? confirmedReason : null;
        }

        private void RequestEmergencySkipPhase5WithConfirmation()
        {
            if (_phase5UefiRebootInProgress) return;

            List<string> pending = GetPendingInteractiveHwTestNames();
            string pendingSummary = pending.Count > 0
                ? string.Join(", ", pending.ToArray())
                : "Interaktivni testovi periferija";

            MessageBoxResult confirm = MessageBox.Show(
                this,
                string.Format(
                    LocalizationService.Get("HwTEmergencySkipConfirmBody",
                        "UPOZORENJE: Preostali obavezni interaktivni testovi ({0}):\n{1}\n\n" +
                        "Vanredno preskakanje je dozvoljeno ISKLJUČIVO ako uređaj ima fizički kvar ili ne može proći test:\n" +
                        "• Svi neurađeni testovi biće označeni kao SKIP.\n" +
                        "• Uređaj NEĆE dobiti status READY na ServiceHub-u (biće označen kao NEEDS_SERVICE).\n" +
                        "• Razlog preskakanja je obavezan i upisuje se u izveštaj.\n\n" +
                        "Da li ste sigurni da želite da nastavite vanredno preskakanje?"),
                    pending.Count,
                    pendingSummary),
                LocalizationService.Get("HwTEmergencySkipConfirmTitle", "WIS — Vanredno preskakanje Faze 5"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);

            if (confirm != MessageBoxResult.Yes) return;

            string reason = ShowEmergencySkipReasonDialog();
            if (string.IsNullOrWhiteSpace(reason)) return;

            HidePendingInteractiveTestsWarning();
            if (ViewModel != null)
            {
                ViewModel.AddLog("WARN", "Korisnik", "Vanredno servisno preskakanje Faze 5 — Razlog: " + reason);
            }

            if (!ForceSkipEmbeddedPhase5(reason))
            {
                MessageBox.Show(
                    this,
                    LocalizationService.Get("HwTEmergencySkipNotReady", "Vanredno preskakanje nije moglo biti izvršeno jer test modul nije spreman."),
                    LocalizationService.Get("HwTErrorTitle", "WIS Greška"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private static int ReadSummaryInt(Dictionary<string, object> summary, string key)
        {
            if (summary == null || !summary.ContainsKey(key) || summary[key] == null) return 0;
            int val;
            if (int.TryParse(Convert.ToString(summary[key], System.Globalization.CultureInfo.InvariantCulture), out val))
                return val;
            return 0;
        }

        private static void CountInteractiveStatusesInRunResult(HwTestRunResult runResult, out int pendingInteractive, out int skippedInteractive)
        {
            pendingInteractive = 0;
            skippedInteractive = 0;
            if (runResult == null || runResult.Tests == null) return;

            for (int i = 0; i < runResult.Tests.Count; i++)
            {
                Dictionary<string, object> item = runResult.Tests[i];
                if (item == null) continue;

                bool isAuto = true;
                object autoObj;
                if (item.TryGetValue("automated", out autoObj) && autoObj != null)
                {
                    if (autoObj is bool) isAuto = (bool)autoObj;
                    else bool.TryParse(Convert.ToString(autoObj), out isAuto);
                }
                if (isAuto) continue;

                string status = item.ContainsKey("status") && item["status"] != null
                    ? Convert.ToString(item["status"]).Trim().ToUpperInvariant()
                    : "";
                if (status == "PENDING" || status == "RUNNING" || string.IsNullOrEmpty(status))
                {
                    pendingInteractive++;
                }
                else if (status == "SKIP")
                {
                    skippedInteractive++;
                }
            }
        }

        private void SyncBacklitKeyboardVerdictFromRunResult(HwTestRunResult runResult)
        {
            if (runResult == null || runResult.Tests == null) return;
            try
            {
                for (int i = 0; i < runResult.Tests.Count; i++)
                {
                    Dictionary<string, object> item = runResult.Tests[i];
                    if (item == null) continue;
                    string id = item.ContainsKey("id") && item["id"] != null ? Convert.ToString(item["id"]) : "";
                    if (!string.Equals(id, "keyboard", StringComparison.OrdinalIgnoreCase)) continue;

                    string kbdStatus = item.ContainsKey("status") && item["status"] != null
                        ? Convert.ToString(item["status"]).Trim().ToUpperInvariant()
                        : "";
                    string kbdReason = item.ContainsKey("reason") && item["reason"] != null
                        ? Convert.ToString(item["reason"])
                        : "";

                    string blPassed = "";
                    string blReason = "";
                    object detailsObj;
                    if (item.TryGetValue("details", out detailsObj) && detailsObj is Dictionary<string, object>)
                    {
                        var details = (Dictionary<string, object>)detailsObj;
                        object bpObj;
                        if (details.TryGetValue("backlight_passed", out bpObj) && bpObj != null)
                        {
                            blPassed = Convert.ToString(bpObj).Trim().ToLowerInvariant();
                        }
                        object brObj;
                        if (details.TryGetValue("backlight_reason", out brObj) && brObj != null)
                        {
                            blReason = Convert.ToString(brObj).Trim();
                        }
                    }

                    if (blPassed == "true")
                    {
                        ApplyBacklitKeyboardVerdict(true, !string.IsNullOrEmpty(blReason) ? blReason : LocalizationService.Get("HwTBacklitConfirmed", "Potvrđeno u Fazi 5 (Test tastature)"));
                    }
                    else if (blPassed == "no_hardware")
                    {
                        ApplyBacklitKeyboardVerdict(false, !string.IsNullOrEmpty(blReason) ? blReason : LocalizationService.Get("HwTBacklitNone", "Model nema pozadinsko osvetljenje (potvrđeno u Fazi 5)"));
                    }
                    else if (blPassed == "false")
                    {
                        ApplyBacklitKeyboardVerdict(true, !string.IsNullOrEmpty(blReason) ? blReason : LocalizationService.Get("HwTBacklitFaulty", "Svetleća tastatura prisutna (neispravna u Fazi 5)"));
                    }
                    else if ((blPassed == "n/a" || string.IsNullOrEmpty(blPassed)) && kbdStatus == "PASS")
                    {
                        string evTxt = !string.IsNullOrEmpty(blReason) ? blReason : kbdReason;
                        if (!string.IsNullOrEmpty(evTxt) &&
                            (evTxt.IndexOf("nema", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             evTxt.IndexOf("non-backlit", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             evTxt.IndexOf("without", StringComparison.OrdinalIgnoreCase) >= 0))
                        {
                            ApplyBacklitKeyboardVerdict(false, evTxt);
                        }
                    }
                    break;
                }
            }
            catch { }
        }

        private void OnEmbeddedHwTestReportSaved(HwTestRunResult runResult)
        {
            if (runResult == null || runResult.Summary == null) return;
            _lastHwTestReportSavedUtc = DateTime.UtcNow;

            string overall = runResult.Summary.ContainsKey("overall") && runResult.Summary["overall"] != null
                ? Convert.ToString(runResult.Summary["overall"])
                : "UNKNOWN";
            int pass = ReadSummaryInt(runResult.Summary, "pass");
            int fail = ReadSummaryInt(runResult.Summary, "fail");
            int skip = ReadSummaryInt(runResult.Summary, "skip");
            int na = ReadSummaryInt(runResult.Summary, "na");

            int totalApplicable = pass + fail + skip;
            if (totalApplicable <= 0) totalApplicable = pass + fail + skip + na;

            int pendingInteractive;
            int skippedInteractive;
            CountInteractiveStatusesInRunResult(runResult, out pendingInteractive, out skippedInteractive);

            Dispatcher.BeginInvoke((Action)(() =>
            {
                SyncBacklitKeyboardVerdictFromRunResult(runResult);

                bool allInteractiveDone = (pendingInteractive == 0) && !HasUnfinishedInteractiveHwTests();
                bool hasIncompleteSkip = (skippedInteractive > 0);
                string displayOverall = (hasIncompleteSkip && string.Equals(overall, "PASS_WITH_SKIP", StringComparison.OrdinalIgnoreCase))
                    ? "NEPOTPUNO (SKIP)"
                    : overall;

                if (ViewModel != null)
                {
                    ViewModel.IsHwTestFinished = allInteractiveDone || _phase5EmergencySkipAuthorized;
                    ViewModel.HwTestOverallStatus = displayOverall;
                    ViewModel.HwTestSummaryText = fail > 0
                        ? string.Format("{0}/{1} PASS, {2} FAIL, {3} SKIP", pass, totalApplicable, fail, skip)
                        : string.Format("{0}/{1} PASS, {2} SKIP", pass, totalApplicable, skip);

                    if (fail > 0)
                    {
                        ViewModel.HwTestBadgeBrush = MainViewModel.CreateFrozenBrush(220, 53, 69);
                    }
                    else if (hasIncompleteSkip)
                    {
                        ViewModel.HwTestBadgeBrush = MainViewModel.CreateFrozenBrush(230, 126, 34);
                    }
                    else if (overall == "PASS" || overall == "PASS_WITH_SKIP")
                    {
                        ViewModel.HwTestBadgeBrush = MainViewModel.CreateFrozenBrush(40, 167, 69);
                    }

                    ViewModel.AddLog(
                        (fail > 0 || hasIncompleteSkip) ? "WARN" : "SUCCESS",
                        "Hardver",
                        string.Format("Hardverski test završen: {0} ({1}/{2} PASS, {3} SKIP, {4} FAIL)", displayOverall, pass, totalApplicable, skip, fail)
                    );
                    ViewModel.RebuildOverviewAlerts();
                }

                SendIpcMessage(new Dictionary<string, object>
                {
                    { "action", "hwtest_complete" },
                    { "hwtest_overall", overall },
                    { "hwtest_summary", ViewModel != null ? ViewModel.HwTestSummaryText : "" },
                    { "pass", pass },
                    { "fail", fail },
                    { "skip", skip },
                    { "na", na }
                });

                EnsureHwTestButtons();

                if (allInteractiveDone || _phase5EmergencySkipAuthorized)
                {
                    HidePendingInteractiveTestsWarning();

                    bool phase6AlreadyCompleted = _phase6ScriptCompletedSuccessfully ||
                                                  (ViewModel != null && (ViewModel.IsServiceFinished || ViewModel.CurrentPhase >= 6));

                    if (!phase6AlreadyCompleted)
                    {
                        bool waitLoopActive = false;
                        lock (_phase5HwSyncLock)
                        {
                            waitLoopActive = (_phase5HwCompletedEvent != null);
                        }

                        if (!waitLoopActive && _isMasterMode &&
                            Interlocked.CompareExchange(ref _phase6DispatchGuard, 1, 0) == 0)
                        {
                            ThreadPool.QueueUserWorkItem(delegate
                            {
                                try
                                {
                                    SaveDeployStatePhase(6);
                                    ExecutePhase(6);
                                }
                                catch { }
                            });
                        }
                    }
                    else if (pendingInteractive == 0 && skippedInteractive == 0 && _telemetryService != null)
                    {
                        if (ViewModel != null)
                        {
                            ViewModel.AddLog("INFO", "Telemetry", "Svi interaktivni testovi su naknadno završeni — šaljem ažurirani izveštaj na ServiceHub...");
                            ViewModel.DeploymentStatus = "READY";
                            ViewModel.RebuildOverviewAlerts();
                        }
                        _telemetryService.ReuploadUpdatedReportAfterHwTestAsync((ok, info) =>
                        {
                            if (Dispatcher != null && !Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
                            {
                                Dispatcher.BeginInvoke((Action)(() =>
                                {
                                    if (ViewModel != null)
                                    {
                                        ViewModel.AddLog(
                                            ok ? "SUCCESS" : "WARN",
                                            "Telemetry",
                                            ok
                                                ? "Ažurirani hardverski izveštaj je uspešno ponovo poslat na ServiceHub: " + info
                                                : "Re-upload ažuriranog izveštaja na ServiceHub nije uspeo: " + info);
                                    }
                                }));
                            }
                        });
                    }
                }
                else
                {
                    ShowPendingInteractiveTestsWarning(GetPendingInteractiveHwTestNames(), false, false);
                }
            }));
        }

        private void UpdatePhase5StepFromHwTest(string stepId, string title, string state, string reason)
        {
            if (string.IsNullOrEmpty(stepId)) return;

            Dispatcher.BeginInvoke((Action)delegate
            {
                if (ViewModel == null || ViewModel.Phases == null) return;

                PhaseItem targetPhase = null;
                for (int i = 0; i < ViewModel.Phases.Count; i++)
                {
                    if (ViewModel.Phases[i].Number == 5)
                    {
                        targetPhase = ViewModel.Phases[i];
                        break;
                    }
                }
                if (targetPhase == null) return;

                StepItem stepItem = null;
                for (int i = 0; i < targetPhase.Steps.Count; i++)
                {
                    if (string.Equals(targetPhase.Steps[i].Id, stepId, StringComparison.OrdinalIgnoreCase))
                    {
                        stepItem = targetPhase.Steps[i];
                        break;
                    }
                }
                if (stepItem == null)
                {
                    stepItem = new StepItem
                    {
                        Id = stepId,
                        Title = !string.IsNullOrEmpty(title) ? title : stepId
                    };
                    targetPhase.Steps.Add(stepItem);
                }
                else if (!string.IsNullOrEmpty(title))
                {
                    stepItem.Title = title;
                }

                stepItem.Status = string.IsNullOrEmpty(state) ? "WAIT" : state;
                stepItem.Reason = reason ?? "";
                ViewModel.RefreshProgressBars();
            });
        }

        private void OnPhase5WorkflowFinished(HwTestRunResult runResult, int cpuDurationSec, float maxTemp, float maxThrot, bool thermalCritical, bool cancelled)
        {
            _phase5LastCpuDurationSec = cpuDurationSec;
            _phase5LastMaxTemp = maxTemp;
            _phase5LastMaxThrot = maxThrot;
            _phase5LastThermalCritical = thermalCritical;
            _phase5LastCancelled = cancelled;

            lock (_phase5HwSyncLock)
            {
                if (_phase5HwCompletedEvent != null)
                {
                    try { _phase5HwCompletedEvent.Set(); } catch { }
                }
            }
        }

        private void HandlePhase5UefiBiosRebootRequest(string testId)
        {
            // KRITIČNA ZAŠTITA (Race-Condition Guard): mora biti true PRE ShutdownEmbeddedHwTest() i buđenja waitEvent-a
            _phase5UefiRebootInProgress = true;

            int resumePhase = (ViewModel != null && ViewModel.CurrentPhase >= 2 && ViewModel.CurrentPhase <= 5)
                ? ViewModel.CurrentPhase
                : 5;
            SaveDeployStatePhase(resumePhase);
            EnsureRunOnceEntry();

            string safeTestId = string.IsNullOrEmpty(testId) ? "peripherals" : testId;
            try
            {
                if (ViewModel != null)
                {
                    ViewModel.AddLog("WARN", "Hardver", string.Format(
                        "Pokrenut restart u UEFI BIOS za test '{0}' (sačuvana Faza {1} i hwtest-checkpoint.json).",
                        safeTestId, resumePhase));
                    ViewModel.SubPhase = string.Format(LocalizationService.Get("HwTBiosRestartSubPhase", "Restart u UEFI BIOS ({0})..."), safeTestId);
                    ViewModel.LiveStatus = LocalizationService.Get("HwTBiosRestartLiveStatus", "Restartujem u UEFI BIOS — WIS nastavlja automatski po povratku...");
                }
            }
            catch { }

            StopScreenCaptureWorker();
            ShutdownEmbeddedHwTest();

            bool fwOk = false;
            int fwExitCode = -1;
            string fwErr = "";
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = "shutdown.exe",
                    Arguments = "/r /fw /t 0",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (Process proc = Process.Start(psi))
                {
                    if (proc != null)
                    {
                        if (proc.WaitForExit(3000))
                        {
                            fwExitCode = proc.ExitCode;
                            fwOk = (fwExitCode == 0);
                        }
                        else
                        {
                            fwOk = true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                fwErr = ex.Message;
            }

            if (!fwOk)
            {
                try
                {
                    if (ViewModel != null)
                    {
                        ViewModel.AddLog("WARN", "Hardver", string.Format(
                            "shutdown.exe /r /fw /t 0 nije uspeo (ExitCode={0}{1}) — pokrećem fallback /r /t 3 /f.",
                            fwExitCode, string.IsNullOrEmpty(fwErr) ? "" : (", " + fwErr)));
                    }
                }
                catch { }

                MessageBox.Show(
                    LocalizationService.Get("HwTManualBiosBody",
                        "Direktan ulazak u UEFI firmware (/r /fw) nije uspeo na ovom računaru.\n\n" +
                        "Računar će se sada restartovati — pritisnite BIOS taster odmah pri paljenju:\n" +
                        "HP: F10 | Lenovo: F1 | Dell: F2"),
                    LocalizationService.Get("HwTManualBiosTitle", "Ručni ulazak u BIOS"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);

                try
                {
                    ProcessStartInfo fallbackPsi = new ProcessStartInfo
                    {
                        FileName = "shutdown.exe",
                        Arguments = "/r /t 3 /f",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    Process.Start(fallbackPsi);
                }
                catch { }
            }

            Dispatcher.BeginInvoke((Action)(() => Close()));
        }

        private void ExecutePhase5IntegratedHwTest()
        {
            _phase5EmergencySkipAuthorized = false;
            Interlocked.Exchange(ref _phase6DispatchGuard, 0);

            ManualResetEvent waitEvent = new ManualResetEvent(false);
            lock (_phase5HwSyncLock)
            {
                _phase5UefiRebootInProgress = false;
                _phase5HwCompletedEvent = waitEvent;
                _phase5LastCpuDurationSec = 0;
                _phase5LastMaxTemp = 0f;
                _phase5LastMaxThrot = 0f;
                _phase5LastThermalCritical = false;
                _phase5LastCancelled = false;
            }

            try
            {
                string cancelMarker = @"C:\setup\.cancel_stress";
                string skipMarker = @"C:\setup\.skip";
                if (File.Exists(cancelMarker)) File.Delete(cancelMarker);
                if (File.Exists(skipMarker)) File.Delete(skipMarker);
            }
            catch { }

            bool tabReady = false;
            Dispatcher.Invoke((Action)(() =>
            {
                EnsureEmbeddedHwTestTab();
                if (ViewModel != null && ViewModel.CurrentPhase < 5)
                {
                    ViewModel.CurrentPhase = 5;
                }
                if (_embeddedHwTestShell != null && _hwTestTabItem != null)
                {
                    RefreshEmbeddedHwTestIdentity();
                    DriverGateState gate = EvaluateInteractiveTestsDriverGate();
                    if (gate != null && gate.IsUnlocked)
                    {
                        _embeddedHwTestShell.RefreshHardwareProbeAfterDrivers();
                    }
                    _embeddedHwTestShell.RefreshDriverGateUi();
                    if (MainTabControl != null)
                    {
                        MainTabControl.SelectedItem = _hwTestTabItem;
                    }
                    ViewModel.IsCompleted = false;
                    ViewModel.ShutdownTimerActive = false;
                    _embeddedHwTestShell.StartPhase5FullWorkflow(UpdatePhase5StepFromHwTest, OnPhase5WorkflowFinished);
                    tabReady = true;
                }
            }));

            if (!tabReady)
            {
                RunAutomatedPhase5StressFallback();

                bool isLaptopFallback = (ViewModel != null && ViewModel.IsLaptop);
                if (isLaptopFallback || HasUnfinishedInteractiveHwTests())
                {
                    lock (_phase5HwSyncLock)
                    {
                        _phase5HwCompletedEvent = null;
                    }
                    try { waitEvent.Dispose(); } catch { }

                    Dispatcher.BeginInvoke((Action)(() =>
                    {
                        if (ViewModel != null)
                        {
                            ViewModel.AddLog("ERROR", "Hardver", "Ugrađeni tab Hardverski testovi nije dostupan na laptopu — prelazak u Fazu 6 je blokiran dok se testovi ne završe.");
                            ViewModel.LiveStatus = LocalizationService.Get("HwTNotStartedWarning", "⚠️ Hardverski testovi nisu pokrenuti! Kliknite 'Ponovi Fazu 5' ili pokrenite testove pre završetka.");
                            SetFailedPhase(5);
                        }
                    }));
                    return;
                }

                if (Interlocked.CompareExchange(ref _phase6DispatchGuard, 1, 0) == 0)
                {
                    SaveDeployStatePhase(6);
                    ExecutePhase(6);
                }
                return;
            }

            int timeoutMs = GetPhaseTimeoutMinutes(5) * 60 * 1000;
            int elapsedAutoMs = 0;
            bool signaled = false;
            bool autoTimedOut = false;

            while (!signaled)
            {
                if (_phase5UefiRebootInProgress) break;
                signaled = waitEvent.WaitOne(500);
                if (signaled || _phase5UefiRebootInProgress) break;

                bool waitingManual = false;
                try { waitingManual = (_embeddedHwTestShell != null && _embeddedHwTestShell.IsWaitingForManualTests); } catch { }
                if (!waitingManual)
                {
                    elapsedAutoMs += 500;
                    if (elapsedAutoMs >= timeoutMs)
                    {
                        autoTimedOut = true;
                        break;
                    }
                }

                try
                {
                    string cancelMarker = @"C:\setup\.cancel_stress";
                    string skipMarker = @"C:\setup\.skip";
                    if (File.Exists(cancelMarker) || File.Exists(skipMarker))
                    {
                        if (File.Exists(cancelMarker)) File.Delete(cancelMarker);
                        if (File.Exists(skipMarker)) File.Delete(skipMarker);

                        bool isLaptopOrManual = waitingManual || HasUnfinishedInteractiveHwTests() || (ViewModel != null && ViewModel.IsLaptop);
                        if (!isLaptopOrManual)
                        {
                            ForceSkipEmbeddedPhase5();
                        }
                        else
                        {
                            Dispatcher.BeginInvoke((Action)(() =>
                            {
                                if (ViewModel != null)
                                {
                                    ViewModel.AddLog("WARN", "TaskRunner", "Tihi .skip/.cancel marker je ignorisan u Fazi 5 jer uređaj ima obavezne interaktivne testove (koristite isključivo 'Vanredno preskoči...' uz unos razloga).");
                                }
                            }));
                        }
                    }
                }
                catch { }
            }

            if (_phase5UefiRebootInProgress)
            {
                lock (_phase5HwSyncLock)
                {
                    _phase5HwCompletedEvent = null;
                }
                try { waitEvent.Dispose(); } catch { }
                return;
            }

            if (!signaled && autoTimedOut)
            {
                bool hasPendingOrLaptop = HasUnfinishedInteractiveHwTests() || (ViewModel != null && ViewModel.IsLaptop);
                if (hasPendingOrLaptop)
                {
                    lock (_phase5HwSyncLock)
                    {
                        _phase5HwCompletedEvent = null;
                    }
                    try { waitEvent.Dispose(); } catch { }

                    try
                    {
                        if (_embeddedHwTestShell != null)
                        {
                            _embeddedHwTestShell.CancelPhase5Workflow();
                        }
                    }
                    catch { }

                    Dispatcher.BeginInvoke((Action)(() =>
                    {
                        if (ViewModel != null)
                        {
                            ViewModel.AddLog("ERROR", "TaskRunner", "Automatski deo Faze 5 (stres/dijagnostika) prekoračio je vremenski limit od 45 minuta — obavezni interaktivni testovi nisu preskočeni i Faza 6 je zaustavljena.");
                            ViewModel.LiveStatus = LocalizationService.Get("HwTPhase5TimeoutWarning", "⚠️ Faza 5 zaustavljena (timeout automatskog testa). Ponovite Fazu 5 ili dovršite interaktivne testove.");
                            SetFailedPhase(5);
                        }
                        ShowPendingInteractiveTestsWarning(GetPendingInteractiveHwTestNames(), true, true);
                    }));
                    return;
                }

                Dispatcher.BeginInvoke((Action)(() =>
                {
                    if (ViewModel != null)
                    {
                        ViewModel.AddLog("WARN", "TaskRunner", "Faza 5 (Desktop bez interaktivnih testova) je dostigla vremenski limit — snimam dosadašnji izveštaj i prelazim na Fazu 6.");
                    }
                }));
                ForceSkipEmbeddedPhase5();
                waitEvent.WaitOne(5000);
            }

            lock (_phase5HwSyncLock)
            {
                _phase5HwCompletedEvent = null;
            }
            try { waitEvent.Dispose(); } catch { }

            if (_phase5UefiRebootInProgress)
            {
                return;
            }

            WriteUiStressTestResultFile(
                _phase5LastCpuDurationSec,
                _phase5LastMaxTemp,
                _phase5LastMaxThrot,
                _phase5LastThermalCritical,
                _phase5LastCancelled);
            EnsureStressTestResultFile();

            bool isSensorUnavailable = !HasCriticalThermalEvidence() && !HasIncompleteStressResult() &&
                (string.Equals(_lastStressStatus, "UNKNOWN", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(_lastStressReason, "SENSOR_UNAVAILABLE", StringComparison.OrdinalIgnoreCase) ||
                 _lastStressMaxTemp <= 0.5f);

            if (isSensorUnavailable)
            {
                _criticalStressFailure = false;
                _lastStressEmergencyAbort = false;
                _lastStressStatus = "UNKNOWN";
            }
            else if (HasCriticalThermalEvidence())
            {
                RecordThermalOverheatNote();
            }

            if (HasUnfinishedInteractiveHwTests() && !_phase5EmergencySkipAuthorized)
            {
                Dispatcher.BeginInvoke((Action)(() =>
                {
                    if (ViewModel != null)
                    {
                        ViewModel.AddLog("WARN", "Hardver", "Prelazak u Fazu 6 je zadržan dok se svi obavezni interaktivni testovi ne završe.");
                    }
                    ShowPendingInteractiveTestsWarning(GetPendingInteractiveHwTestNames(), true, true);
                }));
                return;
            }

            if (Interlocked.CompareExchange(ref _phase6DispatchGuard, 1, 0) == 0)
            {
                SaveDeployStatePhase(6);
                ExecutePhase(6);
            }
        }
    }
}
