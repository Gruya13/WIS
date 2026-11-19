using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using WIS.HwTest.Core;
using WIS.HwTest.Interop;

namespace WIS.HwTest.Tests
{
    public class KeyboardTestWindow : Window
    {
        private readonly ModelProfileClient _profileClient;
        private readonly HardwareProbeResult _hwInfo;
        private readonly string _modelName;
        private readonly string _serialNumber;
        private string _activeLayoutName;
        private string _layoutSource = "local_fallback";
        private string _oemPartDesc = "";
        private bool _layoutManuallyChanged = false;

        private readonly List<KeyLayoutDefinition> _allKeys;
        private readonly HashSet<string> _pressedKeys;
        private readonly HashSet<string> _removedKeys;
        private readonly HashSet<string> _addedKeys;
        private readonly Dictionary<string, Border> _keyVisuals;

        private IntPtr _hookId = IntPtr.Zero;
        private NativeMethods.LowLevelKeyboardProc _hookProc;

        private DispatcherTimer _restTimer;
        private int _restSecondsLeft = 5;
        private bool _isRestPhase = true;
        private string _stuckKeyDetected = null;

        private Grid _keysCanvas;
        private TextBlock _statusBanner;
        private TextBlock _subTitleText;
        private TextBlock _timerText;
        private TextBlock _optionalCountText;
        private Button _btnRemoveKey;
        private Button _btnCycleBrandLayout;
        private Button _btnToggleIso;
        private Button _btnToggleNumpad;
        private Button _btnToggleNav;
        private Button _btnFinishOptionalNow;
        private string _selectedKeyForRemoval = null;
        private int _totalSubCols = 320;
        private DispatcherTimer _optionalGraceTimer;
        private HwndSource _hwndSource;
        private bool _isAltDown = false;
        private Border _restOverlay;
        private TextBlock _restOverlayTimerText;
        private bool _inBacklightPhase = false;
        private Border _backlightOverlay;
        private TextBlock _backlightMethodText;
        private TextBlock _backlightPulseStatusText;
        private Border _backlightBulbBadge;
        private TextBlock _backlightBulbIcon;
        private Button _btnBacklightNoHw;
        private DispatcherTimer _backlightPulseTimer;
        private int _backlightPulseStep = 0;
        private string _backlightMode = "ec_hotkey";
        private bool _backlightHardwarePulseSupported = false;
        private int _lenovoOriginalLevel = -1;
        private int _ibmPmSubMode = 0; // 1=MultiASL (0x222680/84), 2=ASL (0x222660/64), 3=ThinkLight (0x2225EC/F0)
        private int _initialBacklightLevel = -1;
        private string _dellBacklightAttrName = null;
        private string _dellOriginalValue = null;
        private string[] _dellLevelValues = null;
        private int _lastObservedHwLevel = -1;
        private bool _hwLevelChangedByUser = false;
        private volatile bool _backlightWorkerBusy = false;
        private bool? _backlightTestPassed = null;
        private string _backlightReason = "";
        private bool _isClosingOrDone = false;

        public TestItemResult Result { get; private set; }

        public KeyboardTestWindow(string modelName, string recommendedLayout, ModelProfileClient profileClient)
            : this(modelName, recommendedLayout, profileClient, null, "")
        {
        }

        public KeyboardTestWindow(
            string modelName,
            string recommendedLayout,
            ModelProfileClient profileClient,
            HardwareProbeResult hwInfo,
            string serialNumber)
        {
            _modelName = modelName ?? "Generic";
            _serialNumber = serialNumber ?? (hwInfo != null ? hwInfo.SerialNumber : "");
            _hwInfo = hwInfo;
            _activeLayoutName = NormalizeLayoutKey(recommendedLayout ?? "laptop-tkl-ansi");
            _profileClient = profileClient;

            Result = new TestItemResult("keyboard", "Tastatura", "Ispitivanje odziva svih fizičkih tastera");

            _allKeys = new List<KeyLayoutDefinition>();
            _pressedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _removedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _addedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _keyVisuals = new Dictionary<string, Border>(StringComparer.OrdinalIgnoreCase);

            Title = "WIS — Test Tastature";
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Maximized;
            Background = new SolidColorBrush(Color.FromRgb(24, 24, 28));
            Topmost = true;

            // Učitavanje profila sa ServisHub-a (OEM lookup + heuristika + ručne izmene) i šablona
            LoadLayoutAndProfile();

            // Izgradnja UI-ja
            BuildUi();

            // Pokretanje faze mirovanja (5 sekundi)
            StartRestPhase();

            // Postavljanje LL Keyboard kuke
            InstallHook();

            SourceInitialized += (s, e) => {
                try
                {
                    NativeMethods.EnableDarkTitleBar(this);
                    WindowInteropHelper wih = new WindowInteropHelper(this);
                    IntPtr hwnd = wih.Handle;
                    if (hwnd != IntPtr.Zero)
                    {
                        int style = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_STYLE);
                        NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_STYLE, style & ~NativeMethods.WS_SYSMENU);

                        _hwndSource = HwndSource.FromHwnd(hwnd);
                        if (_hwndSource != null)
                        {
                            _hwndSource.AddHook(WndProcHook);
                        }
                    }
                }
                catch { }
            };

            Closed += (s, e) => {
                if (_hwndSource != null)
                {
                    try { _hwndSource.RemoveHook(WndProcHook); } catch { }
                    _hwndSource = null;
                }
                UninstallHook();
            };
        }

        private IntPtr WndProcHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == NativeMethods.WM_SYSCOMMAND)
            {
                int cmd = wParam.ToInt32() & 0xFFF0;
                if (cmd == NativeMethods.SC_KEYMENU)
                {
                    handled = true;
                    return IntPtr.Zero;
                }
            }
            else if (msg == NativeMethods.WM_APPCOMMAND)
            {
                handled = true;
                return (IntPtr)1;
            }
            return IntPtr.Zero;
        }

        private static string NormalizeLayoutKey(string layout)
        {
            if (string.IsNullOrEmpty(layout)) return "laptop-tkl-ansi";
            string k = layout.Trim().ToLowerInvariant();
            if (k == "laptop-tkl") return "laptop-tkl-ansi";
            if (k == "laptop-tkl-nav") return "laptop-tkl-nav-ansi";
            if (k == "laptop-numpad") return "laptop-numpad-ansi";
            if (k == "thinkpad-tkl") return "thinkpad-tkl-ansi";
            if (k == "thinkpad-numpad") return "thinkpad-numpad-ansi";
            if (k == "desktop-105") return "desktop-105-iso";
            if (k == "desktop-104") return "desktop-104-ansi";
            return k;
        }

        private void LoadLayoutAndProfile()
        {
            ModelProfileData resolved = null;
            bool hubProvidedLayout = false;
            try
            {
                if (_profileClient != null)
                {
                    string mfg = _hwInfo != null ? _hwInfo.Manufacturer : "";
                    string sku = _hwInfo != null
                        ? (!string.IsNullOrEmpty(_hwInfo.SystemSkuNumber) ? _hwInfo.SystemSkuNumber : _hwInfo.ProductMtm)
                        : "";
                    double diag = _hwInfo != null ? _hwInfo.ScreenInches : 0.0;
                    bool isDesktop = _hwInfo != null && !_hwInfo.IsLaptop;
                    bool? osOem102 = _hwInfo != null ? _hwInfo.OsHasOem102 : null;

                    resolved = _profileClient.ResolveKeyboardLayout(
                        mfg, _modelName, _serialNumber, sku, diag, isDesktop, osOem102);

                    if (resolved == null || string.IsNullOrEmpty(resolved.BaseLayout))
                    {
                        resolved = _profileClient.GetProfile(_modelName);
                    }

                    if (resolved != null)
                    {
                        if (!string.IsNullOrEmpty(resolved.BaseLayout))
                        {
                            _activeLayoutName = NormalizeLayoutKey(resolved.BaseLayout);
                            hubProvidedLayout = true;
                        }
                        if (!string.IsNullOrEmpty(resolved.Source))
                            _layoutSource = resolved.Source;
                        if (!string.IsNullOrEmpty(resolved.OemPartDescription))
                            _oemPartDesc = resolved.OemPartDescription;
                        foreach (string rk in resolved.RemovedKeys)
                            _removedKeys.Add(rk);
                        foreach (string ak in resolved.AddedKeys)
                            _addedKeys.Add(ak);
                    }
                }
            }
            catch { }

            if (!hubProvidedLayout && _hwInfo != null)
            {
                if (!string.IsNullOrEmpty(_hwInfo.FactoryKeyboardDesc))
                    _oemPartDesc = _hwInfo.FactoryKeyboardDesc;
                _layoutSource = _hwInfo.FactoryBomLoaded ? "client_oem_bom" : "client_model_heuristic";
            }

            // ThinkPad nadogradnja sa zastarelih laptop-tkl/numpad profila
            string modelUpper = string.Format(
                "{0} {1} {2}",
                _hwInfo != null ? (_hwInfo.Manufacturer ?? "") : "",
                _modelName ?? "",
                _hwInfo != null ? (_hwInfo.SystemSkuNumber ?? "") : "").ToUpperInvariant();
            bool isThinkPadHw = (_hwInfo != null && _hwInfo.IsThinkPad) || modelUpper.Contains("THINKPAD");

            if (isThinkPadHw && _activeLayoutName.StartsWith("laptop-", StringComparison.OrdinalIgnoreCase))
            {
                bool isIso = _activeLayoutName.EndsWith("-iso", StringComparison.OrdinalIgnoreCase);
                bool isNum = _activeLayoutName.IndexOf("numpad", StringComparison.OrdinalIgnoreCase) >= 0;
                _activeLayoutName = isNum
                    ? (isIso ? "thinkpad-numpad-iso" : "thinkpad-numpad-ansi")
                    : (isIso ? "thinkpad-tkl-iso" : "thinkpad-tkl-ansi");
                if (!isNum)
                {
                    _addedKeys.Remove("Home");
                    _addedKeys.Remove("PageUp");
                    _addedKeys.Remove("PageDown");
                    _addedKeys.Remove("End");
                }
                _addedKeys.Remove("PrintScreen");
            }
            else if (_activeLayoutName == "laptop-tkl-ansi" || _activeLayoutName == "laptop-tkl-iso")
            {
                // Ako je baza laptop-tkl-*, a profil ili klijentska detekcija kaže da model ima
                // vertikalnu kolonu Home/PageUp/PageDown/End (npr. EliteBook 840 G8), koristi laptop-tkl-nav-*.
                bool hasNavFromAdded = _addedKeys.Contains("Home") && _addedKeys.Contains("End") &&
                                       (_addedKeys.Contains("PageUp") || _addedKeys.Contains("PageDown"));
                bool hasNavFromHw = !hubProvidedLayout && _hwInfo != null && _hwInfo.HasNavColumn;

                if (hasNavFromAdded || hasNavFromHw)
                {
                    bool isIso = _activeLayoutName.EndsWith("-iso", StringComparison.OrdinalIgnoreCase);
                    _activeLayoutName = isIso ? "laptop-tkl-nav-iso" : "laptop-tkl-nav-ansi";
                    _addedKeys.Remove("Home");
                    _addedKeys.Remove("PageUp");
                    _addedKeys.Remove("PageDown");
                    _addedKeys.Remove("End");
                }
            }

            LoadKeysFromLayoutJson(_activeLayoutName);
            ApplyAddedKeysToModel();
            HwTestLogger.Info("KEYBOARD_INIT", string.Format(
                "Layout={0} | Source={1} | TotalLoadedKeys={2} | Mandatory={3} | Added={4} | Removed={5} | OemDesc='{6}'",
                _activeLayoutName, _layoutSource, _allKeys.Count, GetMandatoryKeyCount(),
                string.Join(",", new List<string>(_addedKeys).ToArray()),
                string.Join(",", new List<string>(_removedKeys).ToArray()),
                _oemPartDesc));
        }

        private string ReadLayoutsJsonContent()
        {
            try
            {
                string[] paths = new string[] {
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "keyboard_layouts.json"),
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "Assets", "keyboard_layouts.json")
                };
                foreach (string p in paths)
                {
                    if (File.Exists(p))
                        return File.ReadAllText(p, Encoding.UTF8);
                }

                Assembly asm = Assembly.GetExecutingAssembly();
                using (Stream stream = asm.GetManifestResourceStream("Assets.keyboard_layouts.json"))
                {
                    if (stream != null)
                    {
                        using (StreamReader rdr = new StreamReader(stream, Encoding.UTF8))
                        {
                            return rdr.ReadToEnd();
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        private void LoadKeysFromLayoutJson(string layoutKey)
        {
            _allKeys.Clear();
            string content = ReadLayoutsJsonContent();
            if (string.IsNullOrEmpty(content)) return;

            string normKey = NormalizeLayoutKey(layoutKey);
            int idxLayout = FindJsonPropertyIndex(content, normKey);
            if (idxLayout < 0)
            {
                idxLayout = FindJsonPropertyIndex(content, "laptop-tkl-ansi");
            }
            if (idxLayout < 0) return;

            int idxKeys = FindJsonPropertyIndex(content, "keys", idxLayout);
            if (idxKeys < 0) return;

            int startArr = content.IndexOf('[', idxKeys);
            if (startArr < 0) return;
            int endArr = FindMatchingDelimiter(content, startArr, '[', ']');
            if (endArr <= startArr) return;

            string keysJson = content.Substring(startArr, endArr - startArr + 1);
            int pos = 0;
            while (pos < keysJson.Length)
            {
                int objStart = FindNextCharOutsideStrings(keysJson, '{', pos);
                if (objStart < 0) break;
                int objEnd = FindMatchingDelimiter(keysJson, objStart, '{', '}');
                if (objEnd <= objStart) break;

                string objStr = keysJson.Substring(objStart + 1, objEnd - objStart - 1);
                KeyLayoutDefinition kd = ParseKeyDef(objStr);
                if (kd != null && !string.IsNullOrEmpty(kd.Id))
                {
                    _allKeys.Add(kd);
                }
                pos = objEnd + 1;
            }
        }

        private static int FindJsonPropertyIndex(string json, string propName, int startIndex = 0)
        {
            string pattern = "\"" + propName + "\"";
            int pos = startIndex;
            while (pos < json.Length)
            {
                int idx = json.IndexOf(pattern, pos, StringComparison.Ordinal);
                if (idx < 0) return -1;
                int after = idx + pattern.Length;
                while (after < json.Length && char.IsWhiteSpace(json[after])) after++;
                if (after < json.Length && json[after] == ':')
                    return idx;
                pos = after;
            }
            return -1;
        }

        private static int FindNextCharOutsideStrings(string s, char target, int startIndex)
        {
            bool inStr = false;
            for (int i = startIndex; i < s.Length; i++)
            {
                char ch = s[i];
                if (ch == '"' && !IsEscaped(s, i))
                {
                    inStr = !inStr;
                    continue;
                }
                if (!inStr && ch == target)
                    return i;
            }
            return -1;
        }

        private static int FindMatchingDelimiter(string s, int openIdx, char openCh, char closeCh)
        {
            int depth = 0;
            bool inStr = false;
            for (int i = openIdx; i < s.Length; i++)
            {
                char ch = s[i];
                if (ch == '"' && !IsEscaped(s, i))
                {
                    inStr = !inStr;
                    continue;
                }
                if (inStr) continue;
                if (ch == openCh) depth++;
                else if (ch == closeCh)
                {
                    depth--;
                    if (depth == 0) return i;
                }
            }
            return -1;
        }

        private static bool IsEscaped(string s, int index)
        {
            int backslashes = 0;
            for (int i = index - 1; i >= 0 && s[i] == '\\'; i--)
                backslashes++;
            return (backslashes % 2) == 1;
        }

        private static List<string> SplitTopLevelPairs(string objInner)
        {
            List<string> pairs = new List<string>();
            bool inStr = false;
            int bracketDepth = 0;
            int braceDepth = 0;
            int start = 0;

            for (int i = 0; i < objInner.Length; i++)
            {
                char ch = objInner[i];
                if (ch == '"' && !IsEscaped(objInner, i))
                {
                    inStr = !inStr;
                    continue;
                }
                if (inStr) continue;

                if (ch == '[') bracketDepth++;
                else if (ch == ']') bracketDepth--;
                else if (ch == '{') braceDepth++;
                else if (ch == '}') braceDepth--;
                else if (ch == ',' && bracketDepth == 0 && braceDepth == 0)
                {
                    pairs.Add(objInner.Substring(start, i - start));
                    start = i + 1;
                }
            }
            if (start < objInner.Length)
                pairs.Add(objInner.Substring(start));

            return pairs;
        }

        private static string UnescapeJsonString(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            StringBuilder sb = new StringBuilder(raw.Length);
            for (int i = 0; i < raw.Length; i++)
            {
                char ch = raw[i];
                if (ch == '\\' && i + 1 < raw.Length)
                {
                    char next = raw[i + 1];
                    if (next == '\\' || next == '"' || next == '/')
                    {
                        sb.Append(next);
                        i++;
                        continue;
                    }
                    if (next == 'n') { sb.Append('\n'); i++; continue; }
                    if (next == 'r') { sb.Append('\r'); i++; continue; }
                    if (next == 't') { sb.Append('\t'); i++; continue; }
                }
                sb.Append(ch);
            }
            return sb.ToString();
        }

        private KeyLayoutDefinition ParseKeyDef(string objStr)
        {
            KeyLayoutDefinition kd = new KeyLayoutDefinition();
            List<string> pairs = SplitTopLevelPairs(objStr);
            foreach (string pair in pairs)
            {
                int colon = FindNextCharOutsideStrings(pair, ':', 0);
                if (colon <= 0) continue;

                string k = UnescapeJsonString(pair.Substring(0, colon).Trim().Trim('"'));
                string rawVal = pair.Substring(colon + 1).Trim();

                if (k == "alternateVkeys")
                {
                    int arrS = rawVal.IndexOf('[');
                    int arrE = rawVal.LastIndexOf(']');
                    if (arrS >= 0 && arrE > arrS)
                    {
                        string inner = rawVal.Substring(arrS + 1, arrE - arrS - 1);
                        string[] nums = inner.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                        foreach (string ns in nums)
                        {
                            int av;
                            if (int.TryParse(ns.Trim(), out av))
                                kd.AlternateVkeys.Add(av);
                        }
                    }
                    continue;
                }

                string v = UnescapeJsonString(rawVal.Trim('"'));
                if (k == "id") kd.Id = v;
                else if (k == "label") kd.Label = v;
                else if (k == "vkey") { int vk; if (int.TryParse(v, out vk)) kd.VKey = vk; }
                else if (k == "row") { double r; if (double.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out r)) kd.Row = r; }
                else if (k == "col") { double c; if (double.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out c)) kd.Col = c; }
                else if (k == "w") { double w; if (double.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out w)) kd.W = w; }
                else if (k == "h") { double h; if (double.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out h)) kd.H = h; }
                else if (k == "isNumpad") kd.IsNumpad = v.Equals("true", StringComparison.OrdinalIgnoreCase);
                else if (k == "isOptional") kd.IsOptional = v.Equals("true", StringComparison.OrdinalIgnoreCase);
                else if (k == "extendedMode") { int em; if (int.TryParse(v, out em)) kd.ExtendedMode = em; }
            }
            return kd;
        }

        private KeyLayoutDefinition FindKeyById(string keyId)
        {
            foreach (KeyLayoutDefinition kd in _allKeys)
            {
                if (kd.Id.Equals(keyId, StringComparison.OrdinalIgnoreCase))
                    return kd;
            }
            return null;
        }

        private KeyLayoutDefinition BuildDynamicKeyDefinition(string keyId, uint vkOverride = 0)
        {
            string id = keyId ?? string.Format("Key_0x{0:X2}", vkOverride);
            string label = id;
            int vk = (int)vkOverride;
            double w = 1.25;

            if (id.Equals("Oem102", StringComparison.OrdinalIgnoreCase) || vk == 0xE2)
            {
                id = "Oem102"; label = "< > |"; vk = 0xE2; w = 1.0;
            }
            else if (id.Equals("Copilot", StringComparison.OrdinalIgnoreCase))
            {
                id = "Copilot"; label = "Copilot"; vk = 0x86; w = 1.5; // VK_F23
            }
            else if (id.Equals("Home", StringComparison.OrdinalIgnoreCase) || vk == 0x24)
            {
                id = "Home"; label = "Home"; vk = 0x24; w = 1.25;
            }
            else if (id.Equals("PageUp", StringComparison.OrdinalIgnoreCase) || vk == 0x21)
            {
                id = "PageUp"; label = "PgUp"; vk = 0x21; w = 1.25;
            }
            else if (id.Equals("PageDown", StringComparison.OrdinalIgnoreCase) || vk == 0x22)
            {
                id = "PageDown"; label = "PgDn"; vk = 0x22; w = 1.25;
            }
            else if (id.Equals("End", StringComparison.OrdinalIgnoreCase) || vk == 0x23)
            {
                id = "End"; label = "End"; vk = 0x23; w = 1.25;
            }
            else if (id.Equals("Insert", StringComparison.OrdinalIgnoreCase) || vk == 0x2D)
            {
                id = "Insert"; label = "Ins"; vk = 0x2D; w = 1.25;
            }
            else if (id.Equals("Pause", StringComparison.OrdinalIgnoreCase) || vk == 0x13)
            {
                id = "Pause"; label = "Pause"; vk = 0x13; w = 1.25;
            }
            else if (id.Equals("Scroll", StringComparison.OrdinalIgnoreCase) || vk == 0x91)
            {
                id = "Scroll"; label = "ScrLk"; vk = 0x91; w = 1.25;
            }
            else if (id.Equals("Apps", StringComparison.OrdinalIgnoreCase) || vk == 0x5D)
            {
                id = "Apps"; label = "Menu"; vk = 0x5D; w = 1.25;
            }
            else if (id.Equals("NumLock", StringComparison.OrdinalIgnoreCase) || vk == 0x90)
            {
                id = "NumLock"; label = "NumLk"; vk = 0x90; w = 1.25;
            }
            else if (id.Equals("Calc", StringComparison.OrdinalIgnoreCase) || vk == 0xB7)
            {
                id = "Calc"; label = "Calc"; vk = 0xB7; w = 1.25;
            }
            else if (vk == 0xAD) { id = "VolumeMute"; label = "Mute"; w = 1.10; }
            else if (vk == 0xAE) { id = "VolumeDown"; label = "Vol-"; w = 1.10; }
            else if (vk == 0xAF) { id = "VolumeUp"; label = "Vol+"; w = 1.10; }
            else if (vk == 0xB0) { id = "MediaNext"; label = "Next"; w = 1.10; }
            else if (vk == 0xB1) { id = "MediaPrev"; label = "Prev"; w = 1.10; }
            else if (vk == 0xB3) { id = "MediaPlay"; label = "Play"; w = 1.10; }
            else if (vk >= 0x60 && vk <= 0x69) { id = "NumPad" + (vk - 0x60); label = (vk - 0x60).ToString(); w = 1.0; }
            else if (vk == 0x6A) { id = "Multiply"; label = "*"; w = 1.0; }
            else if (vk == 0x6B) { id = "Add"; label = "+"; w = 1.0; }
            else if (vk == 0x6D) { id = "Subtract"; label = "-"; w = 1.0; }
            else if (vk == 0x6E) { id = "Decimal"; label = "."; w = 1.0; }
            else if (vk == 0x6F) { id = "Divide"; label = "/"; w = 1.0; }
            else if (vk >= 0x7C && vk <= 0x87) { int fn = vk - 0x6F; id = "F" + fn; label = "F" + fn; w = 1.0; }
            else if (vk > 0)
            {
                label = string.Format("VK 0x{0:X2}", vk);
            }

            KeyLayoutDefinition kd = new KeyLayoutDefinition
            {
                Id = id,
                Label = label,
                VKey = vk,
                Row = 0.0,
                Col = 0.0,
                W = w,
                H = 1.0,
                IsNumpad = (vk >= 0x60 && vk <= 0x6F) || vk == 0x90,
                IsOptional = false,
                ExtendedMode = 0
            };
            return kd;
        }

        private bool HasActiveNumpadKeys()
        {
            foreach (KeyLayoutDefinition k in _allKeys)
            {
                if (!_removedKeys.Contains(k.Id) && k.IsNumpad) return true;
            }
            return false;
        }

        private static bool IsRow0CellFree(List<KeyLayoutDefinition> row0Keys, double colStart, double colEnd)
        {
            foreach (KeyLayoutDefinition k in row0Keys)
            {
                double ks = k.Col;
                double ke = k.Col + k.W;
                if (colStart < ke - 0.01 && colEnd > ks + 0.01) return false;
            }
            return true;
        }

        private static bool TryGetStandardNumpadCoords(string id, out double row, out double col, out double w, out double h, out int extMode)
        {
            row = 1.0; col = 16.0; w = 1.0; h = 1.0; extMode = 0;
            if (id == "NumLock") { row = 1.0; col = 16.0; return true; }
            if (id == "Divide") { row = 1.0; col = 17.0; return true; }
            if (id == "Multiply") { row = 1.0; col = 18.0; return true; }
            if (id == "Subtract") { row = 1.0; col = 19.0; return true; }
            if (id == "NumPad7") { row = 2.0; col = 16.0; return true; }
            if (id == "NumPad8") { row = 2.0; col = 17.0; return true; }
            if (id == "NumPad9") { row = 2.0; col = 18.0; return true; }
            if (id == "Add") { row = 2.0; col = 19.0; h = 2.0; return true; }
            if (id == "NumPad4") { row = 3.0; col = 16.0; return true; }
            if (id == "NumPad5") { row = 3.0; col = 17.0; return true; }
            if (id == "NumPad6") { row = 3.0; col = 18.0; return true; }
            if (id == "NumPad1") { row = 4.0; col = 16.0; return true; }
            if (id == "NumPad2") { row = 4.0; col = 17.0; return true; }
            if (id == "NumPad3") { row = 4.0; col = 18.0; return true; }
            if (id == "NumPadEnter") { row = 4.0; col = 19.0; h = 2.0; extMode = 2; return true; }
            if (id == "NumPad0") { row = 5.0; col = 16.0; w = 2.0; return true; }
            if (id == "Decimal") { row = 5.0; col = 18.0; return true; }
            return false;
        }

        private void ReflowTopRowInsertKey(KeyLayoutDefinition newKey)
        {
            List<KeyLayoutDefinition> row0 = new List<KeyLayoutDefinition>();
            foreach (KeyLayoutDefinition k in _allKeys)
            {
                if (_removedKeys.Contains(k.Id)) continue;
                if (k.Row < 0.8 && k.Col < 15.55) row0.Add(k);
            }

            row0.Sort(delegate(KeyLayoutDefinition a, KeyLayoutDefinition b)
            {
                return a.Col.CompareTo(b.Col);
            });

            int insertIdx = row0.Count;
            for (int i = 0; i < row0.Count; i++)
            {
                if (row0[i].Id.Equals("DeleteRow0", StringComparison.OrdinalIgnoreCase) ||
                    row0[i].Id.Equals("Delete", StringComparison.OrdinalIgnoreCase))
                {
                    insertIdx = i;
                    break;
                }
            }
            row0.Insert(insertIdx, newKey);

            int n = row0.Count;
            if (n <= 0) return;

            // Zadržavamo ukupnu širinu 15.50 jedinica uz male vizuelne razmake između F-grupa
            double totalWidth = 15.50;
            double gapTotal = 0.30;
            double avail = totalWidth - gapTotal;
            double unitW = Math.Floor((avail / n) * 20.0) / 20.0;
            if (unitW < 0.50) unitW = 0.50;

            double cur = 0.0;
            for (int i = 0; i < n; i++)
            {
                KeyLayoutDefinition curKey = row0[i];
                double kw = unitW;
                if (i == n - 1)
                {
                    kw = Math.Max(unitW, Math.Round((totalWidth - cur) * 20.0) / 20.0);
                }
                curKey.Row = 0.0;
                curKey.Col = Math.Round(cur * 20.0) / 20.0;
                curKey.W = kw;
                curKey.H = 1.0;
                cur += kw;

                if (curKey.Id.Equals("F4", StringComparison.OrdinalIgnoreCase) ||
                    curKey.Id.Equals("F8", StringComparison.OrdinalIgnoreCase) ||
                    curKey.Id.Equals("F12", StringComparison.OrdinalIgnoreCase))
                {
                    cur += 0.10;
                }
            }
        }

        private bool ReflowBottomRowInsertKey(KeyLayoutDefinition newKey)
        {
            KeyLayoutDefinition altR = FindKeyById("AltRight");
            KeyLayoutDefinition ctrlR = FindKeyById("ControlRight");
            if (altR == null || ctrlR == null || _removedKeys.Contains(altR.Id) || _removedKeys.Contains(ctrlR.Id))
            {
                return false;
            }

            double startCol = altR.Col;
            double endCol = ctrlR.Col + ctrlR.W;
            double span = endCol - startCol;
            if (span < 1.8) return false;

            // Smeštamo AltGr, Apps/Copilot i CtrlR u isti raspon bez preklapanja sa Space ili PrintScreen
            List<KeyLayoutDefinition> mid = new List<KeyLayoutDefinition>();
            foreach (KeyLayoutDefinition k in _allKeys)
            {
                if (_removedKeys.Contains(k.Id)) continue;
                if (k == altR || k == ctrlR) continue;
                if (k.Row >= 4.8 && k.Row <= 5.6 && k.Col >= startCol - 0.01 && (k.Col + k.W) <= endCol + 0.01)
                {
                    mid.Add(k);
                }
            }
            mid.Sort(delegate(KeyLayoutDefinition a, KeyLayoutDefinition b) { return a.Col.CompareTo(b.Col); });

            List<KeyLayoutDefinition> seq = new List<KeyLayoutDefinition>();
            seq.Add(altR);
            for (int i = 0; i < mid.Count; i++) seq.Add(mid[i]);
            seq.Add(newKey);
            seq.Add(ctrlR);

            double eachW = Math.Floor((span / seq.Count) * 20.0) / 20.0;
            double cur = startCol;
            for (int i = 0; i < seq.Count; i++)
            {
                double kw = (i == seq.Count - 1) ? Math.Round((endCol - cur) * 20.0) / 20.0 : eachW;
                seq[i].Row = 5.0;
                seq[i].Col = Math.Round(cur * 20.0) / 20.0;
                seq[i].W = kw;
                seq[i].H = 1.0;
                cur += kw;
            }
            return true;
        }

        private KeyLayoutDefinition PlaceOrActivateKeyInPhysicalPosition(string keyId, uint vkOverride, out bool needGridRebuild)
        {
            needGridRebuild = false;
            KeyLayoutDefinition candidate = BuildDynamicKeyDefinition(keyId, vkOverride);
            if (candidate.VKey <= 0) return null;

            KeyLayoutDefinition existing = FindKeyById(candidate.Id);
            if (existing != null)
            {
                if (_removedKeys.Contains(existing.Id))
                {
                    _removedKeys.Remove(existing.Id);
                    needGridRebuild = true;
                }
                return existing;
            }

            if (candidate.Id.Equals("Oem102", StringComparison.OrdinalIgnoreCase))
            {
                KeyLayoutDefinition shLeft = FindKeyById("ShiftLeft");
                if (shLeft != null && shLeft.W > 1.5)
                {
                    shLeft.W = 1.25;
                }
                candidate.Row = 4.0;
                candidate.Col = 1.25;
                candidate.W = 1.0;
                candidate.H = 1.0;
                _allKeys.Add(candidate);
                needGridRebuild = true;
                return candidate;
            }

            if (candidate.Id.Equals("Apps", StringComparison.OrdinalIgnoreCase) ||
                candidate.Id.Equals("Copilot", StringComparison.OrdinalIgnoreCase))
            {
                if (ReflowBottomRowInsertKey(candidate))
                {
                    _allKeys.Add(candidate);
                    needGridRebuild = true;
                    return candidate;
                }
            }

            if (candidate.IsNumpad || candidate.Id.Equals("NumPadEnter", StringComparison.OrdinalIgnoreCase))
            {
                double nr, nc, nw, nh;
                int em;
                if (TryGetStandardNumpadCoords(candidate.Id, out nr, out nc, out nw, out nh, out em))
                {
                    candidate.Row = nr;
                    candidate.Col = nc;
                    candidate.W = nw;
                    candidate.H = nh;
                    candidate.ExtendedMode = em;
                    candidate.IsNumpad = true;
                    _allKeys.Add(candidate);
                    needGridRebuild = true;
                    return candidate;
                }
            }

            // Za Home/End/PgUp/PgDn na šablonu sa Numpadom proveri slobodne slotove iznad Numpada (c=16..19, r=0)
            if (HasActiveNumpadKeys() &&
                (candidate.Id == "Home" || candidate.Id == "End" || candidate.Id == "PageUp" || candidate.Id == "PageDown" || candidate.Id == "Insert"))
            {
                List<KeyLayoutDefinition> r0 = new List<KeyLayoutDefinition>();
                foreach (KeyLayoutDefinition k in _allKeys)
                {
                    if (!_removedKeys.Contains(k.Id) && k.Row < 0.8) r0.Add(k);
                }
                double[] preferredCols = new double[] { 16.0, 17.0, 18.0, 19.0 };
                for (int i = 0; i < preferredCols.Length; i++)
                {
                    if (IsRow0CellFree(r0, preferredCols[i], preferredCols[i] + 1.0))
                    {
                        candidate.Row = 0.0;
                        candidate.Col = preferredCols[i];
                        candidate.W = 1.0;
                        candidate.H = 1.0;
                        _allKeys.Add(candidate);
                        needGridRebuild = true;
                        return candidate;
                    }
                }
            }

            // Svi ostali standardni tasteri iz funkcionalnog/nav reda idu direktno u gornji red (Row 0) bez 6. reda
            if (candidate.Id == "Insert" || candidate.Id == "PrintScreen" || candidate.Id == "Pause" ||
                candidate.Id == "Scroll" || candidate.Id == "Home" || candidate.Id == "End" ||
                candidate.Id == "PageUp" || candidate.Id == "PageDown")
            {
                ReflowTopRowInsertKey(candidate);
                _allKeys.Add(candidate);
                needGridRebuild = true;
                return candidate;
            }

            // Media / vendor / dodatni tasteri: produži desno na Row 0 (nikada prazan 6. red na dnu)
            double maxRow0End = 15.5;
            foreach (KeyLayoutDefinition k in _allKeys)
            {
                if (_removedKeys.Contains(k.Id)) continue;
                if (k.Row < 0.8 && (k.Col + k.W) > maxRow0End)
                {
                    maxRow0End = k.Col + k.W;
                }
            }
            candidate.Row = 0.0;
            candidate.Col = Math.Round((maxRow0End + 0.15) * 20.0) / 20.0;
            candidate.H = 1.0;
            _allKeys.Add(candidate);
            needGridRebuild = true;
            return candidate;
        }

        private void ApplyAddedKeysToModel()
        {
            foreach (string ak in _addedKeys)
            {
                if (_removedKeys.Contains(ak)) continue;
                if (FindKeyById(ak) != null) continue;

                bool ignored;
                PlaceOrActivateKeyInPhysicalPosition(ak, 0, out ignored);
            }
        }

        private void BuildUi()
        {
            Grid mainGrid = new Grid();
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(86) });  // Header
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Keyboard visual
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(80) });  // Footer actions

            // 1. Header
            Border header = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(32, 34, 40)),
                Padding = new Thickness(25, 10, 25, 10)
            };
            Grid hg = new Grid();
            hg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            hg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            hg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            StackPanel spTitle = new StackPanel();
            _statusBanner = new TextBlock
            {
                Text = "⏳ FAZA 1: MIRUJTE! Ne pritiskajte nijedan taster (provera zaglavljenih kontakata)...",
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(255, 193, 7))
            };
            spTitle.Children.Add(_statusBanner);

            _subTitleText = new TextBlock
            {
                FontSize = 13,
                Foreground = new SolidColorBrush(Color.FromRgb(170, 170, 180)),
                Margin = new Thickness(0, 4, 0, 0)
            };
            UpdateSubtitleText();
            spTitle.Children.Add(_subTitleText);
            hg.Children.Add(spTitle);

            // Brzi prekidači (ANSI/ISO i Numpad) u gornjoj traci
            StackPanel spToggles = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(15, 0, 20, 0)
            };

            _btnCycleBrandLayout = new Button
            {
                Background = new SolidColorBrush(Color.FromRgb(55, 65, 85)),
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                FontSize = 12,
                Padding = new Thickness(12, 7, 12, 7),
                Margin = new Thickness(0, 0, 8, 0),
                Focusable = false,
                ToolTip = "Prebaci raspored proizvođača (ThinkPad / HP Nav / Standard)"
            };
            _btnCycleBrandLayout.Click += (s, e) => CycleBrandLayout();
            spToggles.Children.Add(_btnCycleBrandLayout);

            _btnToggleIso = new Button
            {
                Background = new SolidColorBrush(Color.FromRgb(55, 65, 85)),
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                FontSize = 12,
                Padding = new Thickness(12, 7, 12, 7),
                Margin = new Thickness(0, 0, 8, 0),
                Focusable = false,
                ToolTip = "Prebaci između ANSI (široki Shift) i ISO (< > | pored levog Shifta)"
            };
            _btnToggleIso.Click += (s, e) => ToggleAnsiIso();
            spToggles.Children.Add(_btnToggleIso);

            _btnToggleNumpad = new Button
            {
                Background = new SolidColorBrush(Color.FromRgb(55, 65, 85)),
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                FontSize = 12,
                Padding = new Thickness(12, 7, 12, 7),
                Margin = new Thickness(0, 0, 8, 0),
                Focusable = false,
                ToolTip = "Dodaj ili ukloni numerički blok (Numpad)"
            };
            _btnToggleNumpad.Click += (s, e) => ToggleNumpad();
            spToggles.Children.Add(_btnToggleNumpad);

            _btnToggleNav = new Button
            {
                Background = new SolidColorBrush(Color.FromRgb(55, 65, 85)),
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                FontSize = 12,
                Padding = new Thickness(12, 7, 12, 7),
                Focusable = false,
                ToolTip = "Dodaj ili ukloni desnu kolonu Home / PgUp / PgDn / End (npr. EliteBook 840 G5-G8)"
            };
            _btnToggleNav.Click += (s, e) => ToggleNav();
            spToggles.Children.Add(_btnToggleNav);

            UpdateToggleButtonsText();
            Grid.SetColumn(spToggles, 1);
            hg.Children.Add(spToggles);

            StackPanel spTimerCol = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            _timerText = new TextBlock
            {
                Text = "05s",
                FontSize = 30,
                FontWeight = FontWeights.ExtraBold,
                Foreground = new SolidColorBrush(Color.FromRgb(255, 193, 7)),
                HorizontalAlignment = HorizontalAlignment.Right
            };
            spTimerCol.Children.Add(_timerText);
            _optionalCountText = new TextBlock
            {
                Text = "",
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(120, 175, 225)),
                HorizontalAlignment = HorizontalAlignment.Right,
                Visibility = Visibility.Collapsed
            };
            spTimerCol.Children.Add(_optionalCountText);
            Grid.SetColumn(spTimerCol, 2);
            hg.Children.Add(spTimerCol);
            header.Child = hg;
            Grid.SetRow(header, 0);
            mainGrid.Children.Add(header);

            // 2. Vizuelni prikaz tastera skaliran preko celog dostupnog prostora prozora (bez praznog mesta desno)
            Viewbox vb = new Viewbox
            {
                Stretch = Stretch.Fill,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Margin = new Thickness(4, 6, 4, 6)
            };
            _keysCanvas = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };

            RenderKeyboardGrid();

            vb.Child = _keysCanvas;
            Grid.SetRow(vb, 1);
            mainGrid.Children.Add(vb);

            // Overlay sa velikim upozorenjem i tajmerom preko cele tastature tokom Faze 1 (mirovanje 5s)
            _restOverlay = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(232, 34, 16, 18)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(220, 53, 69)),
                BorderThickness = new Thickness(3),
                CornerRadius = new CornerRadius(12),
                Margin = new Thickness(12),
                Visibility = Visibility.Visible
            };
            StackPanel spRest = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            spRest.Children.Add(new TextBlock
            {
                Text = "✋ NE DIRAJTE TASTATURU!",
                FontSize = 44,
                FontWeight = FontWeights.ExtraBold,
                Foreground = new SolidColorBrush(Color.FromRgb(255, 193, 7)),
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 12)
            });
            spRest.Children.Add(new TextBlock
            {
                Text = "Provera zaglavljenih ili kratko spojenih kontakata u toku — sklonite ruke sa tastera",
                FontSize = 20,
                Foreground = Brushes.White,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 24)
            });
            _restOverlayTimerText = new TextBlock
            {
                Text = "5",
                FontSize = 112,
                FontWeight = FontWeights.ExtraBold,
                Foreground = new SolidColorBrush(Color.FromRgb(255, 193, 7)),
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            spRest.Children.Add(_restOverlayTimerText);
            _restOverlay.Child = spRest;
            Grid.SetRow(_restOverlay, 1);
            mainGrid.Children.Add(_restOverlay);

            // Overlay za Fazu 3: Provera pozadinskog osvetljenja tastature
            _backlightOverlay = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(242, 20, 24, 34)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(255, 193, 7)),
                BorderThickness = new Thickness(3),
                CornerRadius = new CornerRadius(12),
                Margin = new Thickness(14),
                Padding = new Thickness(32),
                Visibility = Visibility.Collapsed
            };
            StackPanel spBl = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                MaxWidth = 860
            };
            spBl.Children.Add(new TextBlock
            {
                Text = "💡 TEST POZADINSKOG OSVETLJENJA TASTATURE",
                FontSize = 30,
                FontWeight = FontWeights.ExtraBold,
                Foreground = new SolidColorBrush(Color.FromRgb(255, 193, 7)),
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 18)
            });

            _backlightBulbBadge = new Border
            {
                Width = 116,
                Height = 116,
                CornerRadius = new CornerRadius(58),
                Background = new SolidColorBrush(Color.FromRgb(42, 46, 58)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(120, 125, 140)),
                BorderThickness = new Thickness(3),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 14)
            };
            _backlightBulbIcon = new TextBlock
            {
                Text = "💡",
                FontSize = 54,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            _backlightBulbBadge.Child = _backlightBulbIcon;
            spBl.Children.Add(_backlightBulbBadge);

            _backlightPulseStatusText = new TextBlock
            {
                Text = "PULS: INICIJALIZACIJA...",
                FontSize = 20,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(78, 201, 176)),
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 12)
            };
            spBl.Children.Add(_backlightPulseStatusText);

            _backlightMethodText = new TextBlock
            {
                Text = "Provera kontrole pozadinskog osvetljenja...",
                FontSize = 15,
                Foreground = new SolidColorBrush(Color.FromRgb(220, 224, 232)),
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 26)
            };
            spBl.Children.Add(_backlightMethodText);

            WrapPanel wpBlBtns = new WrapPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            Button btnBlPass = new Button
            {
                Content = "✓ Osvetljenje radi (PASS)",
                Background = new SolidColorBrush(Color.FromRgb(40, 167, 69)),
                Foreground = Brushes.White,
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Padding = new Thickness(24, 14, 24, 14),
                Margin = new Thickness(8),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            btnBlPass.Click += (s, e) =>
            {
                _backlightTestPassed = true;
                _backlightReason = "Serviser potvrdio rad pozadinskog osvetljenja";
                FinishPass();
            };
            wpBlBtns.Children.Add(btnBlPass);

            Button btnBlFail = new Button
            {
                Content = "✗ Osvetljenje ne radi (FAIL)",
                Background = new SolidColorBrush(Color.FromRgb(210, 50, 62)),
                Foreground = Brushes.White,
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Padding = new Thickness(24, 14, 24, 14),
                Margin = new Thickness(8),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            btnBlFail.Click += (s, e) =>
            {
                _backlightTestPassed = false;
                FinishFail("Pozadinsko osvetljenje tastature ne radi (prijavio serviser)");
            };
            wpBlBtns.Children.Add(btnBlFail);

            _btnBacklightNoHw = new Button
            {
                Content = "⚪ Model nema osvetljenje (Nastavi)",
                Background = new SolidColorBrush(Color.FromRgb(82, 88, 102)),
                Foreground = Brushes.White,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Padding = new Thickness(18, 14, 18, 14),
                Margin = new Thickness(8),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            _btnBacklightNoHw.Click += (s, e) =>
            {
                _backlightTestPassed = null;
                _backlightReason = "Laptop nema ugrađeno pozadinsko osvetljenje (potvrdio serviser)";
                FinishPass();
            };
            wpBlBtns.Children.Add(_btnBacklightNoHw);

            spBl.Children.Add(wpBlBtns);
            _backlightOverlay.Child = spBl;
            Grid.SetRow(_backlightOverlay, 1);
            mainGrid.Children.Add(_backlightOverlay);

            // 3. Footer
            Border footer = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(32, 34, 40)),
                Padding = new Thickness(25, 15, 25, 15)
            };
            Grid fg = new Grid();
            fg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            fg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            TextBlock autoLearnHint = new TextBlock
            {
                Text = "💡 Auto-Learn aktivan: Pritisak bilo kog dodatnog tastera automatski ga dodaje na ekran i čuva profil.",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(140, 180, 170)),
                VerticalAlignment = VerticalAlignment.Center
            };
            fg.Children.Add(autoLearnHint);

            StackPanel spFoot = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            _btnFinishOptionalNow = new Button
            {
                Content = "✓ Završi (opcioni nisu prisutni)",
                Background = new SolidColorBrush(Color.FromRgb(34, 139, 75)),
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                Padding = new Thickness(14, 8, 14, 8),
                Margin = new Thickness(0, 0, 12, 0),
                Visibility = Visibility.Collapsed,
                Focusable = false
            };
            _btnFinishOptionalNow.Click += (s, e) => {
                StopOptionalGraceTimer();
                CompletePhysicalKeysPhase();
            };
            spFoot.Children.Add(_btnFinishOptionalNow);

            _btnRemoveKey = new Button
            {
                Content = "❌ Taster ne postoji na ovom modelu",
                Background = new SolidColorBrush(Color.FromRgb(180, 40, 40)),
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                Padding = new Thickness(15, 8, 15, 8),
                Margin = new Thickness(0, 0, 20, 0),
                IsEnabled = false
            };
            _btnRemoveKey.Click += (s, e) => RemoveSelectedKey();
            spFoot.Children.Add(_btnRemoveKey);

            Button btnKeyboardFail = new Button
            {
                Content = "✗ Prijavi neispravne tastere (FAIL)",
                Background = new SolidColorBrush(Color.FromRgb(210, 50, 62)),
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                Padding = new Thickness(15, 8, 15, 8),
                Margin = new Thickness(0, 0, 14, 0),
                Focusable = false
            };
            btnKeyboardFail.Click += (s, e) => ReportBrokenKeysFail();
            spFoot.Children.Add(btnKeyboardFail);

            Button btnSkip = new Button
            {
                Content = "Preskoči test tastature",
                Background = new SolidColorBrush(Color.FromRgb(80, 85, 95)),
                Foreground = Brushes.White,
                Padding = new Thickness(15, 8, 15, 8)
            };
            btnSkip.Click += (s, e) => FinishSkip();
            spFoot.Children.Add(btnSkip);

            Grid.SetColumn(spFoot, 1);
            fg.Children.Add(spFoot);
            footer.Child = fg;
            Grid.SetRow(footer, 2);
            mainGrid.Children.Add(footer);

            Content = mainGrid;
        }

        private void UpdateSubtitleText()
        {
            if (_subTitleText == null) return;
            string partHint = string.IsNullOrEmpty(_oemPartDesc) ? "" : (" | OEM: " + _oemPartDesc);
            _subTitleText.Foreground = new SolidColorBrush(Color.FromRgb(170, 170, 180));
            _subTitleText.Text = string.Format(
                "Model: {0} | Šablon: {1} ({2}){3} | F1-F12 i Alt+Space blokirani za OS tokom testa (za Fn red koristite Fn+Esc)",
                _modelName, _activeLayoutName, _layoutSource, partHint);
        }

        private void UpdateToggleButtonsText()
        {
            bool isThinkPad = _activeLayoutName.StartsWith("thinkpad-", StringComparison.OrdinalIgnoreCase);
            bool hasNav = _activeLayoutName.IndexOf("-nav-", StringComparison.OrdinalIgnoreCase) >= 0;
            bool hasNum = _activeLayoutName.IndexOf("numpad", StringComparison.OrdinalIgnoreCase) >= 0 ||
                          _activeLayoutName.IndexOf("desktop", StringComparison.OrdinalIgnoreCase) >= 0;

            if (_btnCycleBrandLayout != null)
            {
                string brandLbl = isThinkPad ? "ThinkPad" : (hasNav ? "HP Nav" : "Standard");
                _btnCycleBrandLayout.Content = "⌨ Šablon: " + brandLbl;
            }
            if (_btnToggleIso != null)
            {
                bool isIso = _activeLayoutName.EndsWith("-iso", StringComparison.OrdinalIgnoreCase);
                _btnToggleIso.Content = isIso ? "🌐 ISO (< > | ✓)" : "🌐 ANSI -> Dodaj ISO (< > |)";
            }
            if (_btnToggleNumpad != null)
            {
                _btnToggleNumpad.Content = hasNum ? "🔢 Numpad: UKLJUČEN" : "🔢 + Dodaj Numpad";
            }
            if (_btnToggleNav != null)
            {
                _btnToggleNav.Visibility = (hasNum || isThinkPad) ? Visibility.Collapsed : Visibility.Visible;
                _btnToggleNav.Content = hasNav ? "📑 Nav kolona: UKLJUČENA" : "📑 + Nav kolona";
            }
        }

        private void RenderKeyboardGrid()
        {
            _keysCanvas.Children.Clear();
            _keysCanvas.RowDefinitions.Clear();
            _keysCanvas.ColumnDefinitions.Clear();
            _keyVisuals.Clear();

            // Dinamički računamo stvaran broj redova (0.5 jedinice) i sub-kolona (po 0.05 jedinice)
            // kako bi se svaka ThinkPad/HP/Dell širina renderovala tačno bez zaokruživanja ili preklapanja.
            double maxRowUnits = 6.0;
            int maxColSub = 310;
            foreach (KeyLayoutDefinition kd in _allKeys)
            {
                if (_removedKeys.Contains(kd.Id)) continue;
                double endRow = kd.Row + kd.H;
                if (endRow > maxRowUnits) maxRowUnits = endRow;

                int endColSub = (int)Math.Ceiling((kd.Col + kd.W) * 20.0);
                if (endColSub > maxColSub) maxColSub = endColSub;
            }

            int totalHalfRows = Math.Max(12, (int)Math.Ceiling(maxRowUnits * 2.0));
            _totalSubCols = Math.Max(100, Math.Min(560, maxColSub));

            for (int r = 0; r < totalHalfRows; r++)
                _keysCanvas.RowDefinitions.Add(new RowDefinition { Height = new GridLength(29) });
            for (int c = 0; c < _totalSubCols; c++)
                _keysCanvas.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) });

            foreach (KeyLayoutDefinition kd in _allKeys)
            {
                if (_removedKeys.Contains(kd.Id)) continue;
                AddKeyVisualToCanvas(kd, _pressedKeys.Contains(kd.Id), _addedKeys.Contains(kd.Id));
            }
        }

        private void AddKeyVisualToCanvas(KeyLayoutDefinition kd, bool isPressed, bool isAutoLearned)
        {
            Color bg = isPressed
                ? Color.FromRgb(40, 167, 69)
                : (kd.IsOptional
                    ? Color.FromRgb(34, 42, 56)
                    : (isAutoLearned ? Color.FromRgb(38, 70, 83) : Color.FromRgb(45, 48, 56)));
            Color br = isPressed
                ? Color.FromRgb(78, 201, 176)
                : (kd.IsOptional
                    ? Color.FromRgb(88, 130, 175)
                    : (isAutoLearned ? Color.FromRgb(78, 201, 176) : Color.FromRgb(65, 70, 82)));

            Border keyBox = new Border
            {
                Background = new SolidColorBrush(bg),
                BorderBrush = new SolidColorBrush(br),
                BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(5),
                Margin = new Thickness(1.5),
                Opacity = (!isPressed && kd.IsOptional) ? 0.86 : 1.0,
                Tag = kd
            };

            string displayText = (!isPressed && kd.IsOptional) ? (kd.Label + "\n(opc)") : kd.Label;
            TextBlock tb = new TextBlock
            {
                Text = displayText,
                Foreground = Brushes.White,
                FontSize = (!isPressed && kd.IsOptional) ? 9.5 : (kd.Label.Length > 4 ? 10 : 12.5),
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center
            };
            keyBox.Child = tb;
            keyBox.ToolTip = kd.IsOptional
                ? "Opcioni taster: pritisnite ako postoji na ovom modelu (ne blokira test ako fizički ne postoji) | Desni klik = ukloni"
                : "Levi klik = izaberi | Desni klik = ukloni taster ako fizički ne postoji";

            keyBox.MouseLeftButtonDown += (s, e) => {
                Border b = s as Border;
                if (b != null && b.Tag is KeyLayoutDefinition)
                {
                    KeyLayoutDefinition target = (KeyLayoutDefinition)b.Tag;
                    SelectKeyForRemoval(target.Id);
                }
            };

            keyBox.MouseRightButtonDown += (s, e) => {
                Border b = s as Border;
                if (b != null && b.Tag is KeyLayoutDefinition)
                {
                    KeyLayoutDefinition target = (KeyLayoutDefinition)b.Tag;
                    SelectKeyForRemoval(target.Id);
                    RemoveSelectedKey();
                }
            };

            int colStart = Math.Max(0, (int)Math.Round(kd.Col * 20.0));
            int colSpan = Math.Max(6, (int)Math.Round(kd.W * 20.0));
            if (colStart + colSpan > _totalSubCols) colStart = Math.Max(0, _totalSubCols - colSpan);

            int rowStart = Math.Max(0, (int)Math.Round(kd.Row * 2.0));
            int rowSpan = Math.Max(1, (int)Math.Round(kd.H * 2.0));
            if (kd.H < 0.8 && Math.Abs(kd.Row - Math.Floor(kd.Row)) < 0.01 &&
                kd.Id.Equals("Down", StringComparison.OrdinalIgnoreCase))
            {
                rowStart = (int)Math.Round(kd.Row * 2.0) + 1;
                rowSpan = 1;
            }

            Grid.SetRow(keyBox, rowStart);
            Grid.SetColumn(keyBox, colStart);
            Grid.SetColumnSpan(keyBox, colSpan);
            Grid.SetRowSpan(keyBox, rowSpan);

            _keysCanvas.Children.Add(keyBox);
            _keyVisuals[kd.Id] = keyBox;
        }

        private void ToggleAnsiIso()
        {
            bool currentlyIso = _activeLayoutName.EndsWith("-iso", StringComparison.OrdinalIgnoreCase);
            bool isDesktop = _activeLayoutName.IndexOf("desktop", StringComparison.OrdinalIgnoreCase) >= 0;
            bool isThinkPad = _activeLayoutName.StartsWith("thinkpad-", StringComparison.OrdinalIgnoreCase);
            bool isNumpad = _activeLayoutName.IndexOf("numpad", StringComparison.OrdinalIgnoreCase) >= 0;
            bool isNav = _activeLayoutName.IndexOf("-nav-", StringComparison.OrdinalIgnoreCase) >= 0;

            string nextLayout;
            if (isDesktop)
                nextLayout = currentlyIso ? "desktop-104-ansi" : "desktop-105-iso";
            else if (isThinkPad && isNumpad)
                nextLayout = currentlyIso ? "thinkpad-numpad-ansi" : "thinkpad-numpad-iso";
            else if (isThinkPad)
                nextLayout = currentlyIso ? "thinkpad-tkl-ansi" : "thinkpad-tkl-iso";
            else if (isNumpad)
                nextLayout = currentlyIso ? "laptop-numpad-ansi" : "laptop-numpad-iso";
            else if (isNav)
                nextLayout = currentlyIso ? "laptop-tkl-nav-ansi" : "laptop-tkl-nav-iso";
            else
                nextLayout = currentlyIso ? "laptop-tkl-ansi" : "laptop-tkl-iso";

            if (currentlyIso)
            {
                _addedKeys.Remove("Oem102");
            }
            else
            {
                _removedKeys.Remove("Oem102");
            }

            SwitchLayoutRuntime(nextLayout);
        }

        private void ToggleNumpad()
        {
            bool currentlyIso = _activeLayoutName.EndsWith("-iso", StringComparison.OrdinalIgnoreCase);
            bool isThinkPad = _activeLayoutName.StartsWith("thinkpad-", StringComparison.OrdinalIgnoreCase);
            bool isNumpad = _activeLayoutName.IndexOf("numpad", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            _activeLayoutName.IndexOf("desktop", StringComparison.OrdinalIgnoreCase) >= 0;

            string nextLayout;
            if (isNumpad)
            {
                if (isThinkPad)
                {
                    nextLayout = currentlyIso ? "thinkpad-tkl-iso" : "thinkpad-tkl-ansi";
                }
                else
                {
                    bool useNav = _hwInfo != null && _hwInfo.HasNavColumn;
                    nextLayout = useNav
                        ? (currentlyIso ? "laptop-tkl-nav-iso" : "laptop-tkl-nav-ansi")
                        : (currentlyIso ? "laptop-tkl-iso" : "laptop-tkl-ansi");
                }
            }
            else
            {
                nextLayout = isThinkPad
                    ? (currentlyIso ? "thinkpad-numpad-iso" : "thinkpad-numpad-ansi")
                    : (currentlyIso ? "laptop-numpad-iso" : "laptop-numpad-ansi");
            }

            SwitchLayoutRuntime(nextLayout);
        }

        private void ToggleNav()
        {
            bool currentlyIso = _activeLayoutName.EndsWith("-iso", StringComparison.OrdinalIgnoreCase);
            bool currentlyNav = _activeLayoutName.IndexOf("-nav-", StringComparison.OrdinalIgnoreCase) >= 0;

            string nextLayout;
            if (currentlyNav)
            {
                nextLayout = currentlyIso ? "laptop-tkl-iso" : "laptop-tkl-ansi";
            }
            else
            {
                string[] navKeys = new string[] { "Home", "PageUp", "PageDown", "End" };
                for (int i = 0; i < navKeys.Length; i++)
                {
                    _addedKeys.Remove(navKeys[i]);
                    _removedKeys.Remove(navKeys[i]);
                }
                nextLayout = currentlyIso ? "laptop-tkl-nav-iso" : "laptop-tkl-nav-ansi";
            }

            SwitchLayoutRuntime(nextLayout);
        }

        private void CycleBrandLayout()
        {
            bool currentlyIso = _activeLayoutName.EndsWith("-iso", StringComparison.OrdinalIgnoreCase);
            bool isThinkPad = _activeLayoutName.StartsWith("thinkpad-", StringComparison.OrdinalIgnoreCase);
            bool isNav = _activeLayoutName.IndexOf("-nav-", StringComparison.OrdinalIgnoreCase) >= 0;
            bool isNumpad = _activeLayoutName.IndexOf("numpad", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            _activeLayoutName.IndexOf("desktop", StringComparison.OrdinalIgnoreCase) >= 0;

            string nextLayout;
            if (isNumpad)
            {
                nextLayout = isThinkPad
                    ? (currentlyIso ? "laptop-numpad-iso" : "laptop-numpad-ansi")
                    : (currentlyIso ? "thinkpad-numpad-iso" : "thinkpad-numpad-ansi");
            }
            else if (isThinkPad)
            {
                nextLayout = currentlyIso ? "laptop-tkl-nav-iso" : "laptop-tkl-nav-ansi";
            }
            else if (isNav)
            {
                nextLayout = currentlyIso ? "laptop-tkl-iso" : "laptop-tkl-ansi";
            }
            else
            {
                nextLayout = currentlyIso ? "thinkpad-tkl-iso" : "thinkpad-tkl-ansi";
            }

            if (nextLayout.StartsWith("thinkpad-", StringComparison.OrdinalIgnoreCase) ||
                nextLayout.IndexOf("-nav-", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                _addedKeys.Remove("Home");
                _addedKeys.Remove("PageUp");
                _addedKeys.Remove("PageDown");
                _addedKeys.Remove("End");
                _addedKeys.Remove("PrintScreen");
            }

            SwitchLayoutRuntime(nextLayout);
        }

        private void SwitchLayoutRuntime(string nextLayout)
        {
            StopOptionalGraceTimer();
            _activeLayoutName = NormalizeLayoutKey(nextLayout);
            _layoutManuallyChanged = true;
            _layoutSource = "manual_toggle";
            HwTestLogger.Info("KEYBOARD_TOGGLE", "Prebačen šablon na: " + _activeLayoutName);

            LoadKeysFromLayoutJson(_activeLayoutName);
            ApplyAddedKeysToModel();
            RenderKeyboardGrid();
            UpdateSubtitleText();
            UpdateToggleButtonsText();

            if (!_isRestPhase)
            {
                UpdateKeyCounterAndCheckCompletion();
            }

            if (_profileClient != null)
            {
                bool isIso = _activeLayoutName.EndsWith("-iso", StringComparison.OrdinalIgnoreCase);
                bool hasNumpad = _activeLayoutName.IndexOf("numpad", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 _activeLayoutName.IndexOf("desktop", StringComparison.OrdinalIgnoreCase) >= 0;
                _profileClient.PostKeyboardOverride(
                    _modelName,
                    new List<string>(_removedKeys),
                    new List<string>(_addedKeys),
                    _activeLayoutName,
                    "serviser",
                    isIso,
                    hasNumpad,
                    "manual_toggle");
            }
        }

        private void StartRestPhase()
        {
            _isRestPhase = true;
            _restSecondsLeft = 5;
            if (_restOverlay != null) _restOverlay.Visibility = Visibility.Visible;
            if (_restOverlayTimerText != null) _restOverlayTimerText.Text = "5";

            _restTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _restTimer.Tick += (s, e) => {
                _restSecondsLeft--;
                if (_restSecondsLeft > 0)
                {
                    _timerText.Text = string.Format("{0:D2}s", _restSecondsLeft);
                    if (_restOverlayTimerText != null)
                        _restOverlayTimerText.Text = _restSecondsLeft.ToString();
                }
                else
                {
                    _restTimer.Stop();
                    _isRestPhase = false;
                    if (_restOverlay != null) _restOverlay.Visibility = Visibility.Collapsed;
                    HwTestLogger.Info("KEYBOARD_PHASE2", "Faza mirovanja (5s) prošla čisto. Obavezni tasteri: " + GetMandatoryKeyCount() + ", Ukupno aktivni: " + GetRequiredKeyCount());
                    _statusBanner.Text = "✅ FAZA 2: TESTIRANJE! Pritisnite redom svaki taster na tastaturi.";
                    _statusBanner.Foreground = new SolidColorBrush(Color.FromRgb(78, 201, 176));
                    _timerText.Foreground = new SolidColorBrush(Color.FromRgb(78, 201, 176));
                    UpdateKeyCounterAndCheckCompletion();
                }
            };
            _restTimer.Start();
        }

        private int GetRequiredKeyCount()
        {
            int count = 0;
            foreach (KeyLayoutDefinition kd in _allKeys)
            {
                if (!_removedKeys.Contains(kd.Id)) count++;
            }
            return count;
        }

        private int GetMandatoryKeyCount()
        {
            int count = 0;
            foreach (KeyLayoutDefinition kd in _allKeys)
            {
                if (!_removedKeys.Contains(kd.Id) && !kd.IsOptional) count++;
            }
            return count;
        }

        private int CountPressedActiveKeys()
        {
            int count = 0;
            foreach (KeyLayoutDefinition kd in _allKeys)
            {
                if (!_removedKeys.Contains(kd.Id) && _pressedKeys.Contains(kd.Id))
                    count++;
            }
            return count;
        }

        private int CountPressedMandatoryKeys()
        {
            int count = 0;
            foreach (KeyLayoutDefinition kd in _allKeys)
            {
                if (!_removedKeys.Contains(kd.Id) && !kd.IsOptional && _pressedKeys.Contains(kd.Id))
                    count++;
            }
            return count;
        }

        private int CountTotalOptionalKeys()
        {
            int count = 0;
            foreach (KeyLayoutDefinition kd in _allKeys)
            {
                if (!_removedKeys.Contains(kd.Id) && kd.IsOptional) count++;
            }
            return count;
        }

        private int CountPressedOptionalKeys()
        {
            int count = 0;
            foreach (KeyLayoutDefinition kd in _allKeys)
            {
                if (!_removedKeys.Contains(kd.Id) && kd.IsOptional && _pressedKeys.Contains(kd.Id))
                    count++;
            }
            return count;
        }

        private void StopOptionalGraceTimer()
        {
            if (_optionalGraceTimer != null)
            {
                _optionalGraceTimer.Stop();
            }
            if (_btnFinishOptionalNow != null)
            {
                _btnFinishOptionalNow.Visibility = Visibility.Collapsed;
            }
        }

        private void StartOrResetOptionalGraceTimer(int pressedAll, int effectiveTotal)
        {
            if (_optionalGraceTimer == null)
            {
                _optionalGraceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
                _optionalGraceTimer.Tick += delegate(object s, EventArgs e)
                {
                    StopOptionalGraceTimer();
                    CompletePhysicalKeysPhase();
                };
            }
            else
            {
                _optionalGraceTimer.Stop();
            }

            List<string> optLabels = new List<string>();
            foreach (KeyLayoutDefinition kd in _allKeys)
            {
                if (_removedKeys.Contains(kd.Id) || !kd.IsOptional || _pressedKeys.Contains(kd.Id)) continue;
                optLabels.Add((kd.Label ?? kd.Id).Replace("\n", " "));
            }

            if (_btnFinishOptionalNow != null)
            {
                _btnFinishOptionalNow.Visibility = Visibility.Visible;
            }
            if (_statusBanner != null)
            {
                _statusBanner.Foreground = new SolidColorBrush(Color.FromRgb(78, 201, 176));
                _statusBanner.Text = string.Format(
                    "✅ Svi obavezni tasteri OK ({0}/{1})! Ako postoje pritisnite još: [{2}] — završava za 1.2s...",
                    pressedAll,
                    effectiveTotal,
                    string.Join(", ", optLabels.ToArray()));
            }
            _optionalGraceTimer.Start();
        }

        private void UpdateKeyCounterAndCheckCompletion()
        {
            if (_isRestPhase || _inBacklightPhase || _isClosingOrDone) return;

            int mandatoryTotal = GetMandatoryKeyCount();
            int pressedMandatory = CountPressedMandatoryKeys();
            int pressedOptional = CountPressedOptionalKeys();
            int totalOptional = CountTotalOptionalKeys();
            int unpressedOptional = Math.Max(0, totalOptional - pressedOptional);
            int pressedAll = pressedMandatory + pressedOptional;
            int effectiveTotal = mandatoryTotal + pressedOptional;

            if (_timerText != null)
            {
                _timerText.Text = string.Format("{0}/{1}", pressedAll, effectiveTotal);
            }
            if (_optionalCountText != null)
            {
                if (unpressedOptional > 0)
                {
                    _optionalCountText.Text = string.Format("+{0} opc. slot", unpressedOptional);
                    _optionalCountText.Visibility = Visibility.Visible;
                }
                else
                {
                    _optionalCountText.Visibility = Visibility.Collapsed;
                }
            }

            if (pressedMandatory >= mandatoryTotal && mandatoryTotal > 0)
            {
                if (unpressedOptional == 0)
                {
                    StopOptionalGraceTimer();
                    CompletePhysicalKeysPhase();
                    return;
                }
                StartOrResetOptionalGraceTimer(pressedAll, effectiveTotal);
            }
            else
            {
                StopOptionalGraceTimer();
                UpdateRemainingKeysHint(mandatoryTotal - pressedMandatory);
            }
        }

        private void InstallHook()
        {
            _hookProc = HookCallback;
            using (var curProcess = System.Diagnostics.Process.GetCurrentProcess())
            using (var curModule = curProcess.MainModule)
            {
                _hookId = NativeMethods.SetWindowsHookEx(
                    NativeMethods.WH_KEYBOARD_LL,
                    _hookProc,
                    NativeMethods.GetModuleHandle(curModule.ModuleName),
                    0
                );
            }
        }

        private void UninstallHook()
        {
            _isAltDown = false;
            if (_hookId != IntPtr.Zero)
            {
                NativeMethods.UnhookWindowsHookEx(_hookId);
                _hookId = IntPtr.Zero;
            }
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int msg = wParam.ToInt32();
                bool isKeyDown = (msg == NativeMethods.WM_KEYDOWN || msg == NativeMethods.WM_SYSKEYDOWN);
                bool isKeyUp = (msg == NativeMethods.WM_KEYUP || msg == NativeMethods.WM_SYSKEYUP);

                if (isKeyDown || isKeyUp)
                {
                    NativeMethods.KBDLLHOOKSTRUCT kb = (NativeMethods.KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(NativeMethods.KBDLLHOOKSTRUCT));
                    uint vk = kb.vkCode;
                    bool isAltKey = (vk == 0x12 || vk == 0xA4 || vk == 0xA5);
                    bool isAltActive = _isAltDown || ((kb.flags & 0x20) != 0);
                    bool isFunctionKey = (vk >= 0x70 && vk <= 0x7B); // F1..F12
                    bool isMediaOrFnKey = (vk >= 0xA6 && vk <= 0xB7); // Browser/Volume/Media/LaunchApp

                    if (isKeyUp)
                    {
                        if (isAltKey)
                        {
                            _isAltDown = false;
                            return (IntPtr)1;
                        }
                        if (vk == 0x5B || vk == 0x5C || isFunctionKey || isMediaOrFnKey || vk == 0xFF)
                        {
                            return (IntPtr)1;
                        }
                    }
                    else if (isKeyDown)
                    {
                        if (isAltKey)
                        {
                            _isAltDown = true;
                        }

                        if (isMediaOrFnKey)
                        {
                            uint capturedVk = vk;
                            Dispatcher.BeginInvoke(new Action(() => {
                                OnFnMediaKeyIntercepted(capturedVk);
                            }));
                            // Gutamo multimedijalne / akcione tastere da ne menjaju OS volume/browser tokom testa
                            return (IntPtr)1;
                        }

                        uint keyToLog = vk;
                        bool isExt = (kb.flags & 0x01) != 0;
                        uint sc = kb.scanCode;
                        Dispatcher.BeginInvoke(new Action(() => {
                            OnKeyPressed(keyToLog, isExt, sc);
                        }));

                        // Gutamo Alt (sprečava Alt -> Space meni), Alt+Space, Win, Tab, PrtSc i F1-F12 za Windows
                        if (isAltKey ||
                            (vk == 0x20 && isAltActive) ||
                            vk == 0x5B || vk == 0x5C ||
                            vk == 0x09 || vk == 0x2C ||
                            isFunctionKey || vk == 0xFF)
                        {
                            return (IntPtr)1;
                        }
                    }
                }
            }
            return NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        private void OnFnMediaKeyIntercepted(uint vk)
        {
            if (_inBacklightPhase) return;
            if (_isRestPhase)
            {
                OnKeyPressed(vk);
                return;
            }

            // Pokušaj automatsko mapiranje multimedijalnog koda na odgovarajući F1-F12 taster preko alternateVkeys
            bool matched = OnKeyPressed(vk);
            if (matched)
            {
                HwTestLogger.Info("KEYBOARD_FN_MAP", string.Format(
                    "Media/Fn taster VK 0x{0:X2} mapiran na F-red (OS akcija blokirana)",
                    vk));
                if (_subTitleText != null)
                {
                    _subTitleText.Foreground = new SolidColorBrush(Color.FromRgb(78, 201, 176));
                    _subTitleText.Text = string.Format(
                        "✓ Media taster (0x{0:X2}) automatski prepoznat na F-redu (OS akcija blokirana).",
                        vk);
                }
                return;
            }

            HwTestLogger.Warn("KEYBOARD_FN_MODE", string.Format(
                "Detektovan Media/Fn taster VK 0x{0:X2} dok je test aktivan — OS akcija blokirana (podsetnik: Fn+Esc)",
                vk));

            if (_subTitleText != null)
            {
                _subTitleText.Foreground = new SolidColorBrush(Color.FromRgb(255, 193, 7));
                _subTitleText.Text = string.Format(
                    "⚠️ Detektovan Media/Fn taster (0x{0:X2}) umesto F1-F12 — OS akcija blokirana. Pritisnite Fn+Esc da F-red šalje F1-F12!",
                    vk);
            }
        }

        private uint TranslateNumPadOffKey(uint vk, bool isExtended, uint scanCode)
        {
            if (vk == 0x0C) return 0x65; // VK_CLEAR uvek predstavlja NumPad 5
            if (!isExtended && HasActiveNumpadKeys())
            {
                if (scanCode == 0x47) return 0x67; // NumPad7
                if (scanCode == 0x48) return 0x68; // NumPad8
                if (scanCode == 0x49) return 0x69; // NumPad9
                if (scanCode == 0x4B) return 0x64; // NumPad4
                if (scanCode == 0x4C) return 0x65; // NumPad5
                if (scanCode == 0x4D) return 0x66; // NumPad6
                if (scanCode == 0x4F) return 0x61; // NumPad1
                if (scanCode == 0x50) return 0x62; // NumPad2
                if (scanCode == 0x51) return 0x63; // NumPad3
                if (scanCode == 0x52) return 0x60; // NumPad0
                if (scanCode == 0x53) return 0x6E; // Decimal
            }
            return vk;
        }

        private bool OnKeyPressed(uint vk)
        {
            return OnKeyPressed(vk, false, 0);
        }

        private bool OnKeyPressed(uint vk, bool isExtended, uint scanCode)
        {
            if (_inBacklightPhase) return false;
            if (_isRestPhase)
            {
                if (_isClosingOrDone) return false;
                _isClosingOrDone = true;
                _stuckKeyDetected = string.Format("VKey 0x{0:X2}", vk);
                HwTestLogger.Warn("KEYBOARD_STUCK", "Zaglavljen taster u Fazi 1: " + _stuckKeyDetected);
                _restTimer.Stop();
                UninstallHook();

                MessageBox.Show(
                    string.Format("Greška u fazi mirovanja! Detektovan pritisak/zaglavljen taster: {0}.", _stuckKeyDetected),
                    "FAIL — Zaglavljen taster",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error
                );

                Result.Status = TestStatus.Fail;
                Result.Reason = string.Format("Zaglavljen taster u fazi mirovanja: {0}", _stuckKeyDetected);
                Result.Details["stuck_key"] = _stuckKeyDetected;
                SafeCloseDialog(false);
                return false;
            }

            uint effectiveVk = TranslateNumPadOffKey(vk, isExtended, scanCode);

            // Faza 2: Proveri aktivne tastere u _allKeys (uz poštovanje extendedMode za Enter vs NumPadEnter)
            bool matchedAnyActive = false;
            List<string> matchedIds = new List<string>();
            foreach (KeyLayoutDefinition kd in _allKeys)
            {
                if (_removedKeys.Contains(kd.Id)) continue;
                if (kd.ExtendedMode == 1 && isExtended) continue;
                if (kd.ExtendedMode == 2 && !isExtended) continue;

                bool match = (kd.VKey == effectiveVk);
                if (!match && kd.AlternateVkeys != null)
                {
                    foreach (int alt in kd.AlternateVkeys)
                    {
                        if (alt == effectiveVk) { match = true; break; }
                    }
                }

                if (match)
                {
                    matchedAnyActive = true;
                    bool isFirstPress = _pressedKeys.Add(kd.Id);
                    if (isFirstPress) matchedIds.Add(kd.Id);
                    Border b;
                    if (_keyVisuals.TryGetValue(kd.Id, out b))
                    {
                        b.Background = new SolidColorBrush(Color.FromRgb(40, 167, 69));
                        b.BorderBrush = new SolidColorBrush(Color.FromRgb(78, 201, 176));
                        b.Opacity = 1.0;
                        TextBlock innerTb = b.Child as TextBlock;
                        if (innerTb != null && innerTb.Text != kd.Label)
                        {
                            innerTb.Text = kd.Label;
                            innerTb.FontSize = kd.Label.Length > 4 ? 10 : 12.5;
                        }
                    }
                }
            }

            if (matchedIds.Count > 0)
            {
                HwTestLogger.Info("KEYBOARD_KEY", string.Format("VK 0x{0:X2} (ext={1}, sc=0x{2:X2}) -> {3} ({4}/{5})",
                    effectiveVk, isExtended ? 1 : 0, scanCode,
                    string.Join(",", matchedIds.ToArray()), CountPressedActiveKeys(), GetMandatoryKeyCount()));
            }

            // Auto-Learn in-place: pritisnut taster koji nije u aktivnom skupu postavljamo na pravu fizičku poziciju
            if (!matchedAnyActive)
            {
                bool ignoreAutoLearn = (effectiveVk == 0x00 || effectiveVk == 0x07 || effectiveVk == 0x0A || effectiveVk == 0x0B ||
                                        (effectiveVk >= 0x15 && effectiveVk <= 0x1F) || (effectiveVk >= 0xA6 && effectiveVk <= 0xB7) ||
                                        effectiveVk == 0xE7 || effectiveVk >= 0xFC);
                if (ignoreAutoLearn)
                {
                    HwTestLogger.Info("KEYBOARD_FN_IGNORE", string.Format("Ignorisan vendor/Fn kod VK 0x{0:X2} u Auto-Learn", effectiveVk));
                }
                else
                {
                    bool needRebuild;
                    string dynamicIdHint = (effectiveVk == 13 && isExtended) ? "NumPadEnter" : null;
                    KeyLayoutDefinition learned = PlaceOrActivateKeyInPhysicalPosition(dynamicIdHint, effectiveVk, out needRebuild);
                    if (learned != null)
                    {
                        _addedKeys.Add(learned.Id);
                        _pressedKeys.Add(learned.Id);
                        if (needRebuild)
                        {
                            RenderKeyboardGrid();
                        }
                        HwTestLogger.Info("KEYBOARD_AUTOLEARN", string.Format(
                            "In-place Auto-Learn: {0} (Label='{1}', VK=0x{2:X2}, Row={3:F1}, Col={4:F2})",
                            learned.Id, learned.Label, effectiveVk, learned.Row, learned.Col));
                    }
                }
            }

            UpdateKeyCounterAndCheckCompletion();
            return matchedAnyActive;
        }

        private void UpdateRemainingKeysHint(int remainingCount)
        {
            if (_isRestPhase || _inBacklightPhase || _statusBanner == null) return;
            if (remainingCount <= 0 || remainingCount > 3) return;

            List<string> missingLabels = new List<string>();
            foreach (KeyLayoutDefinition kd in _allKeys)
            {
                if (_removedKeys.Contains(kd.Id) || kd.IsOptional || _pressedKeys.Contains(kd.Id)) continue;
                string cleanLbl = (kd.Label ?? kd.Id).Replace("\n", " ");
                missingLabels.Add(string.Format("{0} ({1})", cleanLbl, kd.Id));
            }
            if (missingLabels.Count > 0)
            {
                _statusBanner.Foreground = new SolidColorBrush(Color.FromRgb(255, 193, 7));
                _statusBanner.Text = string.Format(
                    "⏳ Preostalo još {0}: [{1}] — Ako neki taster NE POSTOJI, DESNI KLIK na njega (ili '🌐 ISO/ANSI') za uklanjanje!",
                    missingLabels.Count,
                    string.Join(", ", missingLabels.ToArray()));
            }
        }

        private void SelectKeyForRemoval(string keyId)
        {
            if (!string.IsNullOrEmpty(_selectedKeyForRemoval) && !_pressedKeys.Contains(_selectedKeyForRemoval))
            {
                Border prevB;
                if (_keyVisuals.TryGetValue(_selectedKeyForRemoval, out prevB))
                {
                    prevB.BorderBrush = new SolidColorBrush(Color.FromRgb(75, 80, 95));
                    prevB.BorderThickness = new Thickness(1);
                }
            }
            _selectedKeyForRemoval = keyId;
            Border selB;
            if (!string.IsNullOrEmpty(keyId) && _keyVisuals.TryGetValue(keyId, out selB) && !_pressedKeys.Contains(keyId))
            {
                selB.BorderBrush = Brushes.Orange;
                selB.BorderThickness = new Thickness(3);
            }
            _btnRemoveKey.IsEnabled = true;
            _btnRemoveKey.Background = new SolidColorBrush(Color.FromRgb(220, 95, 25));
            _btnRemoveKey.Content = string.Format("❌ Ukloni taster '{0}' za model", keyId);
        }

        private void RemoveSelectedKey()
        {
            if (string.IsNullOrEmpty(_selectedKeyForRemoval)) return;

            var mbr = MessageBox.Show(
                string.Format("Da li ste sigurni da taster '{0}' fizički NE POSTOJI na modelu '{1}'?\nOvaj izuzetak će biti sačuvan na ServisHub-u za sve buduće servise ovog modela.", _selectedKeyForRemoval, _modelName),
                "Potvrda uklanjanja tastera iz šablona",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question
            );

            if (mbr == MessageBoxResult.Yes)
            {
                _removedKeys.Add(_selectedKeyForRemoval);
                _addedKeys.Remove(_selectedKeyForRemoval);
                Border b;
                if (_keyVisuals.TryGetValue(_selectedKeyForRemoval, out b))
                {
                    b.Visibility = Visibility.Collapsed;
                }

                if (_profileClient != null)
                {
                    bool isIso = _activeLayoutName.EndsWith("-iso", StringComparison.OrdinalIgnoreCase);
                    bool hasNumpad = _activeLayoutName.IndexOf("numpad", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                     _activeLayoutName.IndexOf("desktop", StringComparison.OrdinalIgnoreCase) >= 0;
                    _profileClient.PostKeyboardOverride(
                        _modelName,
                        new List<string>(_removedKeys),
                        new List<string>(_addedKeys),
                        _activeLayoutName,
                        "serviser",
                        isIso,
                        hasNumpad,
                        "manual_remove");
                }

                _selectedKeyForRemoval = null;
                _btnRemoveKey.IsEnabled = false;
                _btnRemoveKey.Content = "❌ Taster ne postoji na ovom modelu";

                UpdateKeyCounterAndCheckCompletion();
            }
        }

        private bool ShouldTestBacklight()
        {
            if (_hwInfo == null) return true;
            if (_hwInfo.HasKeyboardBacklight.HasValue) return _hwInfo.HasKeyboardBacklight.Value;
            return _hwInfo.IsLaptop;
        }

        private void CompletePhysicalKeysPhase()
        {
            if (_inBacklightPhase) return;
            StopOptionalGraceTimer();
            if (ShouldTestBacklight())
            {
                StartBacklightPhase();
            }
            else
            {
                _backlightTestPassed = null;
                _backlightReason = (_hwInfo != null && !string.IsNullOrEmpty(_hwInfo.KeyboardBacklightEvidence))
                    ? "Preskočeno: " + _hwInfo.KeyboardBacklightEvidence
                    : "Model nema pozadinsko osvetljenje (preskočeno)";
                FinishPass();
            }
        }

        private void StartBacklightPhase()
        {
            _inBacklightPhase = true;
            UninstallHook(); // Oslobodi Fn+Key prečice ako model koristi EC kontrolu osvetljenja

            _statusBanner.Text = "💡 Faza 3/3: Provera pozadinskog osvetljenja tastature — izaberite PASS ili FAIL";
            _statusBanner.Foreground = new SolidColorBrush(Color.FromRgb(255, 193, 7));
            _timerText.Text = "BL";

            if (_btnBacklightNoHw != null)
            {
                bool isConfirmedPresent = (_hwInfo != null && _hwInfo.HasKeyboardBacklight == true);
                _btnBacklightNoHw.Visibility = isConfirmedPresent ? Visibility.Collapsed : Visibility.Visible;
            }

            UpdateBacklightInstructionText("Proveravam IBMPmDrv / EnergyDrv / WMI / Dynamic Lighting kontroler...");
            _backlightMode = "detecting";
            _backlightHardwarePulseSupported = false;
            _hwLevelChangedByUser = false;
            _lastObservedHwLevel = -1;
            if (_backlightOverlay != null)
            {
                _backlightOverlay.Visibility = Visibility.Visible;
            }

            // Asinhrono detektuj hardversku kontrolu pozadinskog osvetljenja:
            // 1. ThinkPad IBMPmDrv (MultiASL/ASL/ThinkLight), 2. IdeaPad EnergyDrv,
            // 3. Lenovo WMI, 4. Dell WMI BIOS, 5. Win11 Dynamic Lighting LampArray, 6. EC Hotkey
            System.Threading.ThreadPool.QueueUserWorkItem(new System.Threading.WaitCallback(delegate(object state)
            {
                string detectedMode = "ec_hotkey";
                string detailMsg = "";

                if (TryInitLenovoIbmPmBacklight())
                {
                    detectedMode = "lenovo_ibmpm";
                    detailMsg = _ibmPmSubMode == 1 ? "ThinkPad IBMPmDrv MultiASL (0x222684)" :
                                (_ibmPmSubMode == 2 ? "ThinkPad IBMPmDrv ASL (0x222664)" : "ThinkPad ThinkLight (0x2225F0)");
                }
                else if (TryInitLenovoEnergyDrvBacklight())
                {
                    detectedMode = "lenovo_energydrv";
                    detailMsg = "Lenovo EnergyDrv (0x83102144)";
                }
                else if (TryInitLenovoBacklightWmi())
                {
                    detectedMode = "lenovo_wmi";
                    detailMsg = "Lenovo WMI";
                }
                else if (TryInitDellWmiBacklight())
                {
                    detectedMode = "dell_wmi";
                    detailMsg = "Dell WMI BIOS (" + (_dellBacklightAttrName ?? "KeyboardIllumination") + ")";
                }
                else if (TryPulseLampArray(1.0))
                {
                    detectedMode = "lamp_array";
                    detailMsg = "Windows Dynamic Lighting / LampArray";
                }

                Dispatcher.BeginInvoke(new Action(delegate()
                {
                    if (!_inBacklightPhase) return;
                    _backlightMode = detectedMode;
                    _backlightHardwarePulseSupported = (
                        detectedMode == "lenovo_ibmpm" ||
                        detectedMode == "lenovo_energydrv" ||
                        detectedMode == "lenovo_wmi" ||
                        detectedMode == "dell_wmi" ||
                        detectedMode == "lamp_array");

                    if (_backlightPulseTimer != null)
                    {
                        _backlightPulseTimer.Interval = TimeSpan.FromMilliseconds(detectedMode == "dell_wmi" ? 900 : 650);
                    }

                    if (detectedMode == "lenovo_ibmpm")
                    {
                        UpdateBacklightInstructionText(string.Format("Automatsko pulsiranje aktivno ({0}: 0% -> 50% -> 100%). Gledajte tastaturu!", detailMsg));
                    }
                    else if (detectedMode == "lenovo_energydrv")
                    {
                        UpdateBacklightInstructionText("Automatsko pulsiranje aktivno (Lenovo EnergyDrv: 0% -> 50% -> 100%). Gledajte tastaturu!");
                    }
                    else if (detectedMode == "lenovo_wmi")
                    {
                        UpdateBacklightInstructionText("Automatsko pulsiranje aktivno (Lenovo WMI: nivo 0 -> 1 -> 2). Gledajte tastaturu!");
                    }
                    else if (detectedMode == "dell_wmi")
                    {
                        UpdateBacklightInstructionText(string.Format("Automatsko pulsiranje aktivno ({0}). Gledajte tastaturu!", detailMsg));
                    }
                    else if (detectedMode == "lamp_array")
                    {
                        UpdateBacklightInstructionText("Automatsko pulsiranje aktivno (Windows Dynamic Lighting / LampArray). Gledajte tastaturu!");
                    }
                    else
                    {
                        UpdateBacklightInstructionText(BuildManualBacklightHint());
                    }
                    HwTestLogger.Info("KEYBOARD_BACKLIGHT", "Backlight faza pokrenuta. Mod=" + detectedMode + (string.IsNullOrEmpty(detailMsg) ? "" : " (" + detailMsg + ")"));
                    PulseKeyboardBacklightStep();
                }));
            }));

            _backlightPulseStep = 0;
            if (_backlightPulseTimer == null)
            {
                _backlightPulseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(650) };
                _backlightPulseTimer.Tick += delegate(object s, EventArgs e)
                {
                    PulseKeyboardBacklightStep();
                };
            }
            else
            {
                _backlightPulseTimer.Interval = TimeSpan.FromMilliseconds(650);
            }
            PulseKeyboardBacklightStep();
            _backlightPulseTimer.Start();
        }

        private string BuildManualBacklightHint()
        {
            string mfg = (_hwInfo != null ? (_hwInfo.Manufacturer ?? "") : "").ToUpperInvariant();
            string hotkey = "Fn + taster za osvetljenje (ikonica tastature)";
            if (mfg.Contains("HP") || mfg.Contains("HEWLETT")) hotkey = "HP prečica: Fn + F9 / F11 (ili samo F9 / F11)";
            else if (mfg.Contains("LENOVO")) hotkey = "Lenovo prečica: Fn + Space";
            else if (mfg.Contains("DELL")) hotkey = "Dell prečica: Fn + F5 / F10 (ili F6)";

            string biosWarn = "";
            if (_hwInfo != null && _hwInfo.KeyboardBacklightBiosDisabled)
            {
                biosWarn = "\n⚠ BIOS upozorenje: '" + (_hwInfo.KeyboardBacklightEvidence ?? "Backlight Disabled") + "'";
            }
            else if (_hwInfo != null && !string.IsNullOrEmpty(_hwInfo.KeyboardBacklightEvidence))
            {
                biosWarn = "\nDetektovano: " + _hwInfo.KeyboardBacklightEvidence;
            }

            return string.Format(
                "Kontroler na ovom modelu upravlja LED diodama hardverski (EC).\nPritisnite {0} ako svetlo nije uključeno i proverite rad dok indikator pulsira.{1}",
                hotkey, biosWarn);
        }

        private void UpdateBacklightInstructionText(string text)
        {
            if (_backlightMethodText != null)
            {
                _backlightMethodText.Text = text;
            }
        }

        private void PulseKeyboardBacklightStep()
        {
            if (!_inBacklightPhase) return;

            int[] cycleLevels = new int[] { 0, 1, 2, 1 };
            int level = cycleLevels[_backlightPulseStep % cycleLevels.Length];
            _backlightPulseStep++;

            if (_backlightMode == "detecting")
            {
                if (_backlightBulbBadge != null && _backlightPulseStatusText != null)
                {
                    _backlightBulbBadge.Background = new SolidColorBrush(Color.FromRgb(42, 46, 58));
                    _backlightBulbBadge.BorderBrush = new SolidColorBrush(Color.FromRgb(120, 125, 140));
                    _backlightPulseStatusText.Text = "INICIJALIZACIJA KONTROLERA...";
                    _backlightPulseStatusText.Foreground = new SolidColorBrush(Color.FromRgb(78, 201, 176));
                }
                return;
            }

            if (!_backlightHardwarePulseSupported)
            {
                // Model koristi direktnu EC prečicu (Fn+Space / Fn+F5) umesto automatskog upisa
                if (_backlightBulbBadge != null && _backlightPulseStatusText != null)
                {
                    if (_hwLevelChangedByUser && _lastObservedHwLevel >= 0)
                    {
                        string lvlLabel = _lastObservedHwLevel == 0 ? "0% (UGAŠENO)" : (_lastObservedHwLevel == 1 ? "50% (SREDNJE)" : "100% (PUNO)");
                        _backlightBulbBadge.Background = new SolidColorBrush(Color.FromRgb(30, 95, 58));
                        _backlightBulbBadge.BorderBrush = new SolidColorBrush(Color.FromRgb(78, 201, 176));
                        _backlightPulseStatusText.Text = "DETEKTOVAN HARDVERSKI NIVO: " + lvlLabel + " (Fn prečica potvrđena!)";
                        _backlightPulseStatusText.Foreground = new SolidColorBrush(Color.FromRgb(120, 235, 170));
                    }
                    else
                    {
                        bool tickAlt = (_backlightPulseStep % 2) == 0;
                        _backlightBulbBadge.Background = tickAlt ? new SolidColorBrush(Color.FromRgb(65, 55, 24)) : new SolidColorBrush(Color.FromRgb(42, 46, 58));
                        _backlightBulbBadge.BorderBrush = tickAlt ? new SolidColorBrush(Color.FromRgb(255, 193, 7)) : new SolidColorBrush(Color.FromRgb(160, 140, 60));
                        _backlightPulseStatusText.Text = "RUČNA PROVERA: Pritisnite prečicu na tastaturi (npr. Fn + Space / Fn + F5)";
                        _backlightPulseStatusText.Foreground = new SolidColorBrush(Color.FromRgb(255, 210, 90));
                    }
                }

                // Ako IBMPmDrv podržava readback, prati promenu koju serviser napravi preko Fn+Space
                if (_ibmPmSubMode > 0 && !_backlightWorkerBusy)
                {
                    _backlightWorkerBusy = true;
                    System.Threading.ThreadPool.QueueUserWorkItem(new System.Threading.WaitCallback(delegate(object state)
                    {
                        try
                        {
                            int hwLvl = GetLenovoIbmPmBacklightLevel();
                            if (hwLvl >= 0 && hwLvl != _lastObservedHwLevel)
                            {
                                bool firstRead = (_lastObservedHwLevel < 0);
                                _lastObservedHwLevel = hwLvl;
                                if (!firstRead)
                                {
                                    _hwLevelChangedByUser = true;
                                    HwTestLogger.Info("KEYBOARD_BACKLIGHT", "EC readback detektovao ručnu promenu nivoa osvetljenja: " + hwLvl);
                                }
                            }
                        }
                        finally
                        {
                            _backlightWorkerBusy = false;
                        }
                    }));
                }
                return;
            }

            // 1. Vizuelni puls na ekranu sinhronizovan sa stvarnim hardverskim nivoom
            if (_backlightBulbBadge != null && _backlightPulseStatusText != null)
            {
                if (level == 0)
                {
                    _backlightBulbBadge.Background = new SolidColorBrush(Color.FromRgb(34, 38, 48));
                    _backlightBulbBadge.BorderBrush = new SolidColorBrush(Color.FromRgb(100, 106, 120));
                    _backlightPulseStatusText.Text = "PULS: 0% (UGAŠENO)";
                    _backlightPulseStatusText.Foreground = new SolidColorBrush(Color.FromRgb(160, 168, 180));
                }
                else if (level == 1)
                {
                    _backlightBulbBadge.Background = new SolidColorBrush(Color.FromRgb(95, 78, 22));
                    _backlightBulbBadge.BorderBrush = new SolidColorBrush(Color.FromRgb(235, 180, 35));
                    _backlightPulseStatusText.Text = "PULS: 50% (SREDNJE)";
                    _backlightPulseStatusText.Foreground = new SolidColorBrush(Color.FromRgb(245, 200, 66));
                }
                else
                {
                    _backlightBulbBadge.Background = new SolidColorBrush(Color.FromRgb(165, 130, 18));
                    _backlightBulbBadge.BorderBrush = new SolidColorBrush(Color.FromRgb(255, 230, 90));
                    _backlightPulseStatusText.Text = "PULS: 100% (PUNO OSVETLJENJE)";
                    _backlightPulseStatusText.Foreground = new SolidColorBrush(Color.FromRgb(255, 232, 102));
                }
            }

            // 2. Hardversko pulsiranje u pozadinskoj niti (ne blokira UI)
            if (!_backlightWorkerBusy)
            {
                _backlightWorkerBusy = true;
                int targetLevel = level;
                string mode = _backlightMode;
                System.Threading.ThreadPool.QueueUserWorkItem(new System.Threading.WaitCallback(delegate(object state)
                {
                    try
                    {
                        if (mode == "lenovo_ibmpm")
                        {
                            SetLenovoIbmPmBacklightLevel(targetLevel);
                        }
                        else if (mode == "lenovo_energydrv")
                        {
                            SetLenovoEnergyDrvBacklightLevel(targetLevel);
                        }
                        else if (mode == "lenovo_wmi")
                        {
                            SetLenovoBacklightLevel(targetLevel);
                        }
                        else if (mode == "dell_wmi")
                        {
                            SetDellWmiBacklightLevel(targetLevel);
                        }
                        else if (mode == "lamp_array")
                        {
                            double b = targetLevel == 0 ? 0.0 : (targetLevel == 1 ? 0.5 : 1.0);
                            TryPulseLampArray(b);
                        }
                    }
                    finally
                    {
                        _backlightWorkerBusy = false;
                    }
                }));
            }
        }

        private bool TryInitLenovoIbmPmBacklight()
        {
            IntPtr hDev = HardwareProbe.OpenKernelDeviceHandle(@"\\.\IBMPmDrv");
            if (hDev == IntPtr.Zero) return false;

            _ibmPmSubMode = 0;
            try
            {
                uint inVal = 0;
                uint outVal = 0;
                uint bytesRet = 0;

                // 1. MultiASL (MLCG / MLCS): IOCTL 0x222680 / 0x222684
                if (NativeMethods.DeviceIoControl(hDev, 0x222680u, ref inVal, 4u, ref outVal, 4u, out bytesRet, IntPtr.Zero) &&
                    bytesRet == 4u &&
                    (outVal & 0x80000000u) == 0u &&
                    (outVal & 0x50000u) == 0x50000u)
                {
                    _ibmPmSubMode = 1;
                    _initialBacklightLevel = (int)(outVal & 0x0Fu);
                }
                else
                {
                    // 2. ASL: IOCTL 0x222660 / 0x222664
                    inVal = 0;
                    outVal = 0;
                    bytesRet = 0;
                    if (NativeMethods.DeviceIoControl(hDev, 0x222660u, ref inVal, 4u, ref outVal, 4u, out bytesRet, IntPtr.Zero) &&
                        bytesRet == 4u &&
                        (outVal & 0x80000000u) == 0u &&
                        ((outVal >> 16) & 1u) != 0u)
                    {
                        _ibmPmSubMode = 2;
                        _initialBacklightLevel = (int)(outVal & 0x0Fu);
                    }
                    else
                    {
                        // 3. ThinkLight: IOCTL 0x2225EC / 0x2225F0
                        inVal = 0;
                        outVal = 0;
                        bytesRet = 0;
                        if (NativeMethods.DeviceIoControl(hDev, 0x2225ECu, ref inVal, 4u, ref outVal, 4u, out bytesRet, IntPtr.Zero) &&
                            bytesRet == 4u &&
                            (outVal & 0x80000000u) == 0u &&
                            ((outVal >> 16) & 1u) != 0u)
                        {
                            _ibmPmSubMode = 3;
                            _initialBacklightLevel = (int)(outVal & 0x0Fu);
                        }
                    }
                }
            }
            catch { }
            finally
            {
                NativeMethods.CloseHandle(hDev);
            }

            if (_ibmPmSubMode == 0) return false;

            _lastObservedHwLevel = _initialBacklightLevel;
            int probeLevel = (_initialBacklightLevel == 0) ? 1 : 0;
            bool writeOk = SetLenovoIbmPmBacklightLevel(probeLevel);
            HwTestLogger.Info(
                "KEYBOARD_BACKLIGHT",
                string.Format("IBMPmDrv detekcija: subMode={0}, initialLevel={1}, probeWrite({2})={3}",
                    _ibmPmSubMode, _initialBacklightLevel, probeLevel, writeOk));
            return writeOk;
        }

        private int GetLenovoIbmPmBacklightLevel()
        {
            if (_ibmPmSubMode <= 0) return -1;
            IntPtr hDev = HardwareProbe.OpenKernelDeviceHandle(@"\\.\IBMPmDrv");
            if (hDev == IntPtr.Zero) return -1;

            try
            {
                uint ioctl = _ibmPmSubMode == 1 ? 0x222680u : (_ibmPmSubMode == 2 ? 0x222660u : 0x2225ECu);
                uint inVal = 0;
                uint outVal = 0;
                uint bytesRet = 0;
                if (NativeMethods.DeviceIoControl(hDev, ioctl, ref inVal, 4u, ref outVal, 4u, out bytesRet, IntPtr.Zero) &&
                    bytesRet == 4u &&
                    (outVal & 0x80000000u) == 0u)
                {
                    return (int)(outVal & 0x0Fu);
                }
            }
            catch { }
            finally
            {
                NativeMethods.CloseHandle(hDev);
            }
            return -1;
        }

        private bool SetLenovoIbmPmBacklightLevel(int targetLevel)
        {
            if (_ibmPmSubMode <= 0) return false;
            IntPtr hDev = HardwareProbe.OpenKernelDeviceHandle(@"\\.\IBMPmDrv");
            if (hDev == IntPtr.Zero) return false;

            try
            {
                int clamped = Math.Max(0, Math.Min(2, targetLevel));
                if (_ibmPmSubMode == 1)
                {
                    // MultiASL (MLCG -> MLCS): zadrži mod/flag bitove iz 0x222680
                    uint qIn = 0;
                    uint qOut = 0;
                    uint qRet = 0;
                    if (!NativeMethods.DeviceIoControl(hDev, 0x222680u, ref qIn, 4u, ref qOut, 4u, out qRet, IntPtr.Zero) ||
                        qRet != 4u || (qOut & 0x80000000u) != 0u)
                    {
                        return false;
                    }

                    uint inVal = (qOut & 0xF0u) | ((qOut >> 13) & 0x100u) | ((uint)clamped & 0x0Fu);
                    uint outRes = 0;
                    uint bytesRet = 0;
                    if (NativeMethods.DeviceIoControl(hDev, 0x222684u, ref inVal, 4u, ref outRes, 4u, out bytesRet, IntPtr.Zero))
                    {
                        return (outRes & 0x80000000u) == 0u;
                    }
                    return false;
                }
                else if (_ibmPmSubMode == 2)
                {
                    // ASL: IOCTL 0x222664
                    uint inVal = (uint)clamped;
                    uint outRes = 0;
                    uint bytesRet = 0;
                    if (NativeMethods.DeviceIoControl(hDev, 0x222664u, ref inVal, 4u, ref outRes, 4u, out bytesRet, IntPtr.Zero))
                    {
                        return (outRes & 0x80000000u) == 0u;
                    }
                    return false;
                }
                else if (_ibmPmSubMode == 3)
                {
                    // ThinkLight: IOCTL 0x2225F0 (0 = Off, 1 = On)
                    uint inVal = (clamped > 0) ? 1u : 0u;
                    uint outRes = 0;
                    uint bytesRet = 0;
                    if (NativeMethods.DeviceIoControl(hDev, 0x2225F0u, ref inVal, 4u, ref outRes, 4u, out bytesRet, IntPtr.Zero))
                    {
                        return (outRes & 0x80000000u) == 0u;
                    }
                    return false;
                }
            }
            catch { }
            finally
            {
                NativeMethods.CloseHandle(hDev);
            }
            return false;
        }

        private bool TryInitLenovoEnergyDrvBacklight()
        {
            string mfg = (_hwInfo != null ? (_hwInfo.Manufacturer ?? "") : "").ToUpperInvariant();
            if (!mfg.Contains("LENOVO")) return false;

            string probeDetail;
            if (!HardwareProbe.TryProbeLenovoEnergyDrvBacklight(out probeDetail))
            {
                return false;
            }

            _initialBacklightLevel = 0;
            bool writeOk = SetLenovoEnergyDrvBacklightLevel(1);
            HwTestLogger.Info(
                "KEYBOARD_BACKLIGHT",
                string.Format("EnergyDrv detekcija: {0}, probeWrite(1)={1}", probeDetail, writeOk));
            return writeOk;
        }

        private static bool SetLenovoEnergyDrvBacklightLevel(int level)
        {
            IntPtr hDev = HardwareProbe.OpenKernelDeviceHandle(@"\\.\EnergyDrv");
            if (hDev == IntPtr.Zero) return false;

            try
            {
                uint inVal = level <= 0 ? 0x00033u : (level == 1 ? 0x10033u : 0x20033u);
                uint outRes = 0;
                uint bytesRet = 0;
                if (NativeMethods.DeviceIoControl(hDev, 0x83102144u, ref inVal, 4u, ref outRes, 4u, out bytesRet, IntPtr.Zero))
                {
                    return (outRes & 0x80000000u) == 0u;
                }
            }
            catch { }
            finally
            {
                NativeMethods.CloseHandle(hDev);
            }
            return false;
        }

        private bool TryInitLenovoBacklightWmi()
        {
            string mfg = (_hwInfo != null ? (_hwInfo.Manufacturer ?? "") : "").ToUpperInvariant();
            if (!mfg.Contains("LENOVO")) return false;

            bool classFound = false;

            try
            {
                using (var searcher = new System.Management.ManagementObjectSearcher(@"root\WMI", "SELECT * FROM Lenovo_KeyboardBacklight"))
                {
                    foreach (System.Management.ManagementObject obj in searcher.Get())
                    {
                        classFound = true;
                        try
                        {
                            System.Management.ManagementBaseObject outParams = obj.InvokeMethod("GetKeyboardBacklightStatus", null, null);
                            if (outParams != null && outParams["Status"] != null)
                            {
                                _lenovoOriginalLevel = Convert.ToInt32(outParams["Status"]);
                            }
                        }
                        catch { }
                        break;
                    }
                }
            }
            catch { }

            if (!classFound)
            {
                try
                {
                    using (var searcher = new System.Management.ManagementObjectSearcher(@"root\WMI", "SELECT * FROM Lenovo_SetKeyboardBacklightStatus"))
                    {
                        foreach (System.Management.ManagementObject obj in searcher.Get())
                        {
                            classFound = true;
                            if (obj.Properties["Mode"] != null && obj.Properties["Mode"].Value != null)
                            {
                                _lenovoOriginalLevel = Convert.ToInt32(obj.Properties["Mode"].Value);
                            }
                            break;
                        }
                    }
                }
                catch { }
            }

            if (!classFound)
            {
                try
                {
                    using (var searcher = new System.Management.ManagementObjectSearcher(@"root\WMI", "SELECT * FROM LENOVO_LIGHTING_METHOD"))
                    {
                        foreach (System.Management.ManagementObject obj in searcher.Get())
                        {
                            classFound = true;
                            break;
                        }
                    }
                }
                catch { }
            }

            if (!classFound) return false;

            _initialBacklightLevel = _lenovoOriginalLevel;
            int probeLevel = (_lenovoOriginalLevel == 0) ? 1 : 0;
            bool writeOk = SetLenovoBacklightLevel(probeLevel);
            HwTestLogger.Info(
                "KEYBOARD_BACKLIGHT",
                string.Format("Lenovo WMI probeWrite({0})={1} (orig={2})", probeLevel, writeOk, _lenovoOriginalLevel));
            return writeOk;
        }

        private static bool SetLenovoBacklightLevel(int level)
        {
            try
            {
                using (var searcher = new System.Management.ManagementObjectSearcher(@"root\WMI", "SELECT * FROM Lenovo_KeyboardBacklight"))
                {
                    foreach (System.Management.ManagementObject obj in searcher.Get())
                    {
                        System.Management.ManagementBaseObject inParams = obj.GetMethodParameters("SetKeyboardBacklightStatus");
                        if (inParams != null)
                        {
                            inParams["Status"] = (uint)level;
                            obj.InvokeMethod("SetKeyboardBacklightStatus", inParams, null);
                            return true;
                        }
                    }
                }
            }
            catch { }

            try
            {
                using (var searcher = new System.Management.ManagementObjectSearcher(@"root\WMI", "SELECT * FROM Lenovo_SetKeyboardBacklightStatus"))
                {
                    foreach (System.Management.ManagementObject obj in searcher.Get())
                    {
                        if (obj.Properties["Mode"] != null)
                        {
                            obj["Mode"] = (uint)level;
                            obj.Put();
                            return true;
                        }
                    }
                }
            }
            catch { }

            try
            {
                using (var searcher = new System.Management.ManagementObjectSearcher(@"root\WMI", "SELECT * FROM LENOVO_LIGHTING_METHOD"))
                {
                    foreach (System.Management.ManagementObject obj in searcher.Get())
                    {
                        System.Management.ManagementBaseObject inParams = obj.GetMethodParameters("Set_Lighting_Current_Status");
                        if (inParams != null)
                        {
                            inParams["Lighting_ID"] = (uint)0;
                            inParams["Current_State_Type"] = (uint)(level > 0 ? 1 : 0);
                            inParams["Current_Brightness_Level"] = (uint)(level <= 0 ? 0 : (level == 1 ? 1 : 2));
                            obj.InvokeMethod("Set_Lighting_Current_Status", inParams, null);
                            return true;
                        }
                    }
                }
            }
            catch { }

            return false;
        }

        private bool TryInitDellWmiBacklight()
        {
            string mfg = (_hwInfo != null ? (_hwInfo.Manufacturer ?? "") : "").ToUpperInvariant();
            if (!mfg.Contains("DELL")) return false;

            try
            {
                string[] dellScopes = new string[]
                {
                    @"root\dcim\sysman\biosattributes|EnumerationAttribute",
                    @"root\dcim\sysman|DCIM_BIOSEnumeration"
                };

                foreach (string pair in dellScopes)
                {
                    string[] sp = pair.Split('|');
                    try
                    {
                        using (var searcher = new System.Management.ManagementObjectSearcher(sp[0], "SELECT * FROM " + sp[1]))
                        {
                            foreach (System.Management.ManagementObject obj in searcher.Get())
                            {
                                string attrName = obj["AttributeName"] != null ? obj["AttributeName"].ToString() : "";
                                if (string.IsNullOrEmpty(attrName)) continue;

                                if (attrName.IndexOf("KeyboardIllumination", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    attrName.IndexOf("KeyboardBacklight", StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    string curVal = ExtractFirstWmiString(obj["CurrentValue"]);
                                    string[] possible = obj["PossibleValues"] as string[];
                                    if (possible == null || possible.Length < 2) continue;

                                    string offVal = possible[0];
                                    string brightVal = possible[possible.Length - 1];
                                    string dimVal = possible[possible.Length / 2];

                                    foreach (string pv in possible)
                                    {
                                        string u = (pv ?? "").ToUpperInvariant();
                                        if (u.Contains("DISABL") || u.Contains("OFF") || u == "0") offVal = pv;
                                        else if (u.Contains("DIM") || u.Contains("25") || u.Contains("50") || u.Contains("LOW")) dimVal = pv;
                                        else if (u.Contains("BRIGHT") || u.Contains("100") || u.Contains("HIGH") || u == "ON") brightVal = pv;
                                    }

                                    _dellBacklightAttrName = attrName;
                                    _dellOriginalValue = curVal;
                                    _dellLevelValues = new string[] { offVal, dimVal, brightVal };

                                    bool writeOk = SetDellWmiBacklightLevel(1);
                                    HwTestLogger.Info(
                                        "KEYBOARD_BACKLIGHT",
                                        string.Format("Dell WMI detekcija: attr={0}, orig={1}, probeWrite={2}",
                                            attrName, curVal ?? "", writeOk));
                                    if (writeOk) return true;
                                }
                            }
                        }
                    }
                    catch { }
                }
            }
            catch { }

            return false;
        }

        private static string ExtractFirstWmiString(object val)
        {
            if (val == null) return "";
            string[] arr = val as string[];
            if (arr != null && arr.Length > 0) return arr[0] ?? "";
            return val.ToString();
        }

        private bool SetDellWmiBacklightLevel(int level)
        {
            if (string.IsNullOrEmpty(_dellBacklightAttrName) || _dellLevelValues == null || _dellLevelValues.Length < 3)
            {
                return false;
            }
            int idx = Math.Max(0, Math.Min(2, level));
            return SetDellWmiAttributeRaw(_dellBacklightAttrName, _dellLevelValues[idx]);
        }

        private static bool SetDellWmiAttributeRaw(string attrName, string attrVal)
        {
            if (string.IsNullOrEmpty(attrName) || string.IsNullOrEmpty(attrVal)) return false;

            try
            {
                using (var searcher = new System.Management.ManagementObjectSearcher(@"root\dcim\sysman\biosattributes", "SELECT * FROM BIOSAttributeInterface"))
                {
                    foreach (System.Management.ManagementObject obj in searcher.Get())
                    {
                        System.Management.ManagementBaseObject inParams = obj.GetMethodParameters("SetAttribute");
                        if (inParams == null) continue;

                        foreach (System.Management.PropertyData p in inParams.Properties)
                        {
                            if (p.Name.Equals("Target", StringComparison.OrdinalIgnoreCase)) p.Value = 0;
                            else if (p.Name.Equals("AuthorizationToken", StringComparison.OrdinalIgnoreCase)) p.Value = "";
                            else if (p.Name.Equals("PasswordEncoding", StringComparison.OrdinalIgnoreCase)) p.Value = (uint)0;
                            else if (p.Name.Equals("AttributeName", StringComparison.OrdinalIgnoreCase))
                            {
                                p.Value = p.IsArray ? (object)(new string[] { attrName }) : (object)attrName;
                            }
                            else if (p.Name.Equals("AttributeValue", StringComparison.OrdinalIgnoreCase))
                            {
                                p.Value = p.IsArray ? (object)(new string[] { attrVal }) : (object)attrVal;
                            }
                        }

                        System.Management.ManagementBaseObject outParams = obj.InvokeMethod("SetAttribute", inParams, null);
                        if (outParams != null && outParams["Status"] != null)
                        {
                            return Convert.ToInt32(outParams["Status"]) == 0;
                        }
                        if (outParams != null && outParams["ReturnValue"] != null)
                        {
                            return Convert.ToInt32(outParams["ReturnValue"]) == 0;
                        }
                    }
                }
            }
            catch { }

            return false;
        }

        private static bool TryPulseLampArray(double brightness)
        {
            try
            {
                Type lampArrayType = Type.GetType("Windows.Devices.Lights.LampArray, Windows, ContentType=WindowsRuntime", false);
                Type devInfoType = Type.GetType("Windows.Devices.Enumeration.DeviceInformation, Windows, ContentType=WindowsRuntime", false);
                if (lampArrayType == null || devInfoType == null) return false;

                System.Reflection.MethodInfo getSelector = lampArrayType.GetMethod("GetDeviceSelector", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static, null, Type.EmptyTypes, null);
                if (getSelector == null) return false;
                string selector = getSelector.Invoke(null, null) as string;
                if (string.IsNullOrEmpty(selector)) return false;

                System.Reflection.MethodInfo findAll = devInfoType.GetMethod("FindAllAsync", new Type[] { typeof(string) });
                if (findAll == null) return false;
                object asyncOp = findAll.Invoke(null, new object[] { selector });
                if (asyncOp == null) return false;

                object collection = AwaitWinRtOperation(asyncOp);
                var items = collection as System.Collections.IEnumerable;
                if (items == null) return false;

                System.Reflection.MethodInfo fromId = lampArrayType.GetMethod("FromIdAsync", new Type[] { typeof(string) });
                if (fromId == null) return false;

                bool anySuccess = false;
                foreach (object devInfo in items)
                {
                    System.Reflection.PropertyInfo idProp = devInfo.GetType().GetProperty("Id");
                    string id = idProp != null ? idProp.GetValue(devInfo, null) as string : null;
                    if (string.IsNullOrEmpty(id)) continue;

                    object laOp = fromId.Invoke(null, new object[] { id });
                    object lampArray = AwaitWinRtOperation(laOp);
                    if (lampArray == null) continue;

                    System.Reflection.PropertyInfo kindProp = lampArray.GetType().GetProperty("LampArrayKind");
                    if (kindProp != null)
                    {
                        object kindVal = kindProp.GetValue(lampArray, null);
                        // LampArrayKind.Keyboard == 1
                        if (kindVal != null && Convert.ToInt32(kindVal) != 1)
                        {
                            IDisposable disp = lampArray as IDisposable;
                            if (disp != null) disp.Dispose();
                            continue;
                        }
                    }

                    System.Reflection.PropertyInfo brightProp = lampArray.GetType().GetProperty("BrightnessLevel");
                    if (brightProp != null && brightProp.CanWrite)
                    {
                        brightProp.SetValue(lampArray, brightness, null);
                        anySuccess = true;
                    }
                }
                return anySuccess;
            }
            catch
            {
                return false;
            }
        }

        private static object AwaitWinRtOperation(object asyncOp)
        {
            if (asyncOp == null) return null;
            Type opType = asyncOp.GetType();
            System.Reflection.PropertyInfo statusProp = opType.GetProperty("Status");
            System.Reflection.MethodInfo getResults = opType.GetMethod("GetResults");
            if (statusProp == null || getResults == null) return null;

            int waited = 0;
            while (waited < 1200)
            {
                int st = Convert.ToInt32(statusProp.GetValue(asyncOp, null));
                if (st != 0) break; // 0 == Started
                System.Threading.Thread.Sleep(25);
                waited += 25;
            }

            int finalSt = Convert.ToInt32(statusProp.GetValue(asyncOp, null));
            if (finalSt == 1) // 1 == Completed
            {
                return getResults.Invoke(asyncOp, null);
            }
            return null;
        }

        private void StopBacklightPhase(bool restoreOriginal)
        {
            _inBacklightPhase = false;
            if (_backlightPulseTimer != null)
            {
                _backlightPulseTimer.Stop();
            }

            if (restoreOriginal && _backlightHardwarePulseSupported)
            {
                string mode = _backlightMode;
                int orig = _initialBacklightLevel >= 0 ? _initialBacklightLevel : (_lenovoOriginalLevel >= 0 ? _lenovoOriginalLevel : 1);
                string dellAttr = _dellBacklightAttrName;
                string dellOrig = _dellOriginalValue;
                System.Threading.ThreadPool.QueueUserWorkItem(new System.Threading.WaitCallback(delegate(object state)
                {
                    if (mode == "lenovo_ibmpm")
                    {
                        SetLenovoIbmPmBacklightLevel(orig);
                    }
                    else if (mode == "lenovo_energydrv")
                    {
                        SetLenovoEnergyDrvBacklightLevel(orig);
                    }
                    else if (mode == "lenovo_wmi")
                    {
                        SetLenovoBacklightLevel(orig);
                    }
                    else if (mode == "dell_wmi" && !string.IsNullOrEmpty(dellAttr) && !string.IsNullOrEmpty(dellOrig))
                    {
                        SetDellWmiAttributeRaw(dellAttr, dellOrig);
                    }
                    else if (mode == "lamp_array")
                    {
                        TryPulseLampArray(1.0);
                    }
                }));
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            StopOptionalGraceTimer();
            StopBacklightPhase(true);
            UninstallHook();
            base.OnClosed(e);
        }

        private void SafeCloseDialog(bool result)
        {
            try { DialogResult = result; } catch { }
            try { Close(); } catch { }
        }

        private void FinishPass()
        {
            if (_isClosingOrDone) return;
            _isClosingOrDone = true;
            StopOptionalGraceTimer();
            StopBacklightPhase(true);
            UninstallHook();

            if (_profileClient != null && (_addedKeys.Count > 0 || _layoutManuallyChanged))
            {
                bool isIso = _activeLayoutName.EndsWith("-iso", StringComparison.OrdinalIgnoreCase);
                bool hasNumpad = _activeLayoutName.IndexOf("numpad", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 _activeLayoutName.IndexOf("desktop", StringComparison.OrdinalIgnoreCase) >= 0;
                _profileClient.PostKeyboardOverride(
                    _modelName,
                    new List<string>(_removedKeys),
                    new List<string>(_addedKeys),
                    _activeLayoutName,
                    "serviser_autolearn",
                    isIso,
                    hasNumpad,
                    _layoutSource);
            }

            Result.Status = TestStatus.Pass;
            Result.Attempts = 1;
            Result.Details["layout"] = _activeLayoutName;
            Result.Details["layout_source"] = _layoutSource;
            Result.Details["total_keys"] = GetMandatoryKeyCount() + CountPressedOptionalKeys();
            Result.Details["mandatory_keys_total"] = GetMandatoryKeyCount();
            Result.Details["optional_keys_pressed"] = CountPressedOptionalKeys();
            Result.Details["pressed_keys"] = CountPressedActiveKeys();
            Result.Details["removed_keys"] = new List<string>(_removedKeys);
            Result.Details["added_keys"] = new List<string>(_addedKeys);
            Result.Details["backlight_tested"] = (_backlightTestPassed.HasValue || !string.IsNullOrEmpty(_backlightReason));
            Result.Details["backlight_passed"] = _backlightTestPassed.HasValue ? (_backlightTestPassed.Value ? "true" : "false") : "n/a";
            Result.Details["backlight_method"] = _backlightMode;
            if (!string.IsNullOrEmpty(_backlightReason))
            {
                Result.Details["backlight_reason"] = _backlightReason;
            }

            SafeCloseDialog(true);
        }

        private void ReportBrokenKeysFail()
        {
            List<string> missingLabels = new List<string>();
            foreach (KeyLayoutDefinition kd in _allKeys)
            {
                if (_removedKeys.Contains(kd.Id) || kd.IsOptional) continue;
                if (!_pressedKeys.Contains(kd.Id))
                {
                    string lbl = !string.IsNullOrEmpty(kd.Label) ? kd.Label.Replace("\n", " ") : kd.Id;
                    missingLabels.Add(lbl);
                }
            }

            Result.Details["failed_keys"] = new List<string>(missingLabels);
            string reason = missingLabels.Count > 0
                ? string.Format("Neispravni tasteri na tastaturi ({0}): {1}",
                    missingLabels.Count,
                    string.Join(", ", missingLabels.GetRange(0, Math.Min(12, missingLabels.Count)).ToArray()) + (missingLabels.Count > 12 ? "..." : ""))
                : "Kvar tastera tastature (prijavio serviser)";
            FinishFail(reason);
        }

        private void FinishFail(string reason)
        {
            if (_isClosingOrDone) return;
            _isClosingOrDone = true;
            StopOptionalGraceTimer();
            StopBacklightPhase(true);
            UninstallHook();
            Result.Status = TestStatus.Fail;
            Result.Attempts = 1;
            Result.Reason = reason;
            Result.Details["layout"] = _activeLayoutName;
            Result.Details["layout_source"] = _layoutSource;
            Result.Details["total_keys"] = GetMandatoryKeyCount() + CountPressedOptionalKeys();
            Result.Details["mandatory_keys_total"] = GetMandatoryKeyCount();
            Result.Details["optional_keys_pressed"] = CountPressedOptionalKeys();
            Result.Details["pressed_keys"] = CountPressedActiveKeys();
            Result.Details["backlight_tested"] = (_backlightTestPassed.HasValue || !string.IsNullOrEmpty(_backlightReason));
            Result.Details["backlight_passed"] = _backlightTestPassed.HasValue ? (_backlightTestPassed.Value ? "true" : "false") : "n/a";
            Result.Details["backlight_method"] = _backlightMode;
            SafeCloseDialog(false);
        }

        private void FinishSkip()
        {
            if (_isClosingOrDone) return;
            _isClosingOrDone = true;
            StopOptionalGraceTimer();
            StopBacklightPhase(true);
            UninstallHook();
            Result.Status = TestStatus.Skip;
            Result.Reason = "Korisnik preskočio test tastature";
            SafeCloseDialog(false);
        }
    }
}
