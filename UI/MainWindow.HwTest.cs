using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
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

        private static bool IsPlaceholderIdentity(string val)
        {
            if (string.IsNullOrEmpty(val)) return true;
            string t = val.Trim();
            if (t.Length == 0) return true;
            if (t.Equals("N/A", StringComparison.OrdinalIgnoreCase)) return true;
            if (t.Equals("Standardni PC", StringComparison.OrdinalIgnoreCase)) return true;
            if (t.Equals("Nepoznat model", StringComparison.OrdinalIgnoreCase)) return true;
            if (t.StartsWith("Nepoznat", StringComparison.OrdinalIgnoreCase)) return true;
            if (t.StartsWith("Učitavanje", StringComparison.OrdinalIgnoreCase)) return true;
            if (t.StartsWith("Ucitavanje", StringComparison.OrdinalIgnoreCase)) return true;
            if (t.StartsWith("Loading", StringComparison.OrdinalIgnoreCase)) return true;
            if (t.StartsWith("To be filled", StringComparison.OrdinalIgnoreCase)) return true;
            if (t.Equals("Default string", StringComparison.OrdinalIgnoreCase)) return true;
            if (t.Equals("System Product Name", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private void GetCurrentDeviceIdentity(out string model, out string serial, out string mfg, out string sku)
        {
            string vmModel = ViewModel != null ? ViewModel.ComputerModel : null;
            string hwModel = (ViewModel != null && ViewModel.HardwareInfo != null) ? ViewModel.HardwareInfo.MotherboardModel : null;
            if (!IsPlaceholderIdentity(vmModel))
                model = vmModel.Trim();
            else if (!IsPlaceholderIdentity(hwModel))
                model = hwModel.Trim();
            else
                model = "";

            string vmSerial = ViewModel != null ? ViewModel.SerialNumber : null;
            string hwSerial = (ViewModel != null && ViewModel.HardwareInfo != null) ? ViewModel.HardwareInfo.SerialNumber : null;
            if (!IsPlaceholderIdentity(vmSerial))
                serial = vmSerial.Trim();
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
                        _hwTestTabItem.Header = "🧪 Hardverski testovi";
                    }

                    // Dodajemo na kraj MainTabControl-a da indeksi postojecih tabova (0..7) ostanu netaknuti.
                    MainTabControl.Items.Add(_hwTestTabItem);
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
            ManualResetEvent ev;
            lock (_phase5HwSyncLock)
            {
                ev = _phase5HwCompletedEvent;
            }
            if (ev == null || _embeddedHwTestShell == null) return false;

            try
            {
                _phase5LastCancelled = true;
                _embeddedHwTestShell.ForceCompletePhase5SkipPending();
                return true;
            }
            catch
            {
                return false;
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

        private void OnEmbeddedHwTestReportSaved(HwTestRunResult runResult)
        {
            if (runResult == null || runResult.Summary == null) return;

            string overall = runResult.Summary.ContainsKey("overall") && runResult.Summary["overall"] != null
                ? Convert.ToString(runResult.Summary["overall"])
                : "UNKNOWN";
            int pass = ReadSummaryInt(runResult.Summary, "pass");
            int fail = ReadSummaryInt(runResult.Summary, "fail");
            int skip = ReadSummaryInt(runResult.Summary, "skip");
            int na = ReadSummaryInt(runResult.Summary, "na");

            int totalApplicable = pass + fail + skip;
            if (totalApplicable <= 0) totalApplicable = pass + fail + skip + na;

            Dispatcher.BeginInvoke((Action)(() =>
            {
                if (ViewModel != null)
                {
                    ViewModel.IsHwTestFinished = true;
                    ViewModel.HwTestOverallStatus = overall;
                    ViewModel.HwTestSummaryText = fail > 0
                        ? string.Format("{0}/{1} PASS, {2} FAIL, {3} SKIP", pass, totalApplicable, fail, skip)
                        : string.Format("{0}/{1} PASS, {2} SKIP", pass, totalApplicable, skip);

                    if (overall == "PASS" || overall == "PASS_WITH_SKIP")
                    {
                        ViewModel.HwTestBadgeBrush = MainViewModel.CreateFrozenBrush(40, 167, 69);
                    }
                    else if (overall == "FAIL")
                    {
                        ViewModel.HwTestBadgeBrush = MainViewModel.CreateFrozenBrush(220, 53, 69);
                    }

                    ViewModel.AddLog(
                        overall == "FAIL" ? "WARN" : "SUCCESS",
                        "Hardver",
                        string.Format("Hardverski test završen: {0} ({1}/{2} PASS, {3} SKIP, {4} FAIL)", overall, pass, totalApplicable, skip, fail)
                    );
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
                if (targetPhase.IsActive)
                {
                    targetPhase.IsExpanded = true;
                }
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

        private void ExecutePhase5IntegratedHwTest()
        {
            ManualResetEvent waitEvent = new ManualResetEvent(false);
            lock (_phase5HwSyncLock)
            {
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
                if (_embeddedHwTestShell != null && _hwTestTabItem != null)
                {
                    RefreshEmbeddedHwTestIdentity();
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
                ExecutePhase(6);
                return;
            }

            int timeoutMs = GetPhaseTimeoutMinutes(5) * 60 * 1000;
            int elapsedMs = 0;
            bool signaled = false;

            while (!signaled && elapsedMs < timeoutMs)
            {
                signaled = waitEvent.WaitOne(500);
                if (signaled) break;
                elapsedMs += 500;

                try
                {
                    string cancelMarker = @"C:\setup\.cancel_stress";
                    string skipMarker = @"C:\setup\.skip";
                    if (File.Exists(cancelMarker) || File.Exists(skipMarker))
                    {
                        if (File.Exists(cancelMarker)) File.Delete(cancelMarker);
                        if (File.Exists(skipMarker)) File.Delete(skipMarker);
                        ForceSkipEmbeddedPhase5();
                    }
                }
                catch { }
            }

            if (!signaled)
            {
                Dispatcher.BeginInvoke((Action)(() =>
                {
                    if (ViewModel != null)
                    {
                        ViewModel.AddLog("WARN", "TaskRunner", "Faza 5 (Hardverski testovi) je dostigla vremenski limit — snimam dosadašnji izveštaj i prelazim na Fazu 6.");
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
                HandleCriticalThermalAbort(2);
                return;
            }

            ExecutePhase(6);
        }
    }
}
