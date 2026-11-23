using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace WIS.HwTest.Core
{
    /// <summary>
    /// Deljeni dvojezični sloj (Srpski / 简体中文) za WIS.HwTest u standalone i embedded režimu.
    /// Strogo kompatibilan sa C# 5.0 / .NET Framework 4.8 (/langversion:5).
    /// </summary>
    public static class HwTestLocalization
    {
        private static readonly object _syncLock = new object();
        private static string _currentLanguage = LoadInitialLanguage();

        public static event Action LanguageChanged;

        public static string CurrentLanguage
        {
            get
            {
                lock (_syncLock)
                {
                    return _currentLanguage;
                }
            }
        }

        public static bool IsChinese
        {
            get
            {
                lock (_syncLock)
                {
                    return string.Equals(_currentLanguage, "zh", StringComparison.OrdinalIgnoreCase);
                }
            }
        }

        public static string NormalizeLanguage(string langCode)
        {
            if (string.IsNullOrEmpty(langCode)) return "sr";
            string norm = langCode.Trim().ToLowerInvariant();
            if (norm == "zh" || norm == "zh-cn" || norm == "zh_cn" || norm == "chinese" || norm == "cn")
            {
                return "zh";
            }
            return "sr";
        }

        public static void SetLanguage(string langCode)
        {
            string normalized = NormalizeLanguage(langCode);
            bool changed = false;
            lock (_syncLock)
            {
                if (!string.Equals(_currentLanguage, normalized, StringComparison.OrdinalIgnoreCase))
                {
                    _currentLanguage = normalized;
                    changed = true;
                }
            }
            if (changed)
            {
                Action handler = LanguageChanged;
                if (handler != null)
                {
                    try { handler(); } catch { }
                }
            }
        }

        public static void ReloadFromConfig()
        {
            SetLanguage(LoadInitialLanguage());
        }

        // Registar prevoda (sr <-> zh) za naknadno (live) ponovno prevodjenje vec prikazanog teksta
        private static readonly Dictionary<string, string> _srToZh = new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, string> _zhToSr = new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly List<string[]> _templates = new List<string[]>();
        private static readonly HashSet<string> _templateKeys = new HashSet<string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, Regex> _templateRegex = new Dictionary<string, Regex>(StringComparer.Ordinal);
        private const int MaxRegistrySize = 6000;

        private static void Register(string sr, string zh)
        {
            if (string.IsNullOrEmpty(sr) || string.IsNullOrEmpty(zh)) return;
            lock (_syncLock)
            {
                if (_srToZh.Count >= MaxRegistrySize) return;
                if (!_srToZh.ContainsKey(sr)) _srToZh[sr] = zh;
                if (!_zhToSr.ContainsKey(zh)) _zhToSr[zh] = sr;
            }
        }

        private static void RegisterTemplate(string srFmt, string zhFmt)
        {
            if (string.IsNullOrEmpty(srFmt) || string.IsNullOrEmpty(zhFmt)) return;
            lock (_syncLock)
            {
                if (_templates.Count >= MaxRegistrySize) return;
                if (_templateKeys.Add(srFmt))
                {
                    _templates.Add(new string[] { srFmt, zhFmt });
                }
            }
        }

        public static string Pick(string sr, string zh)
        {
            Register(sr, zh);
            if (IsChinese && !string.IsNullOrEmpty(zh))
            {
                return zh;
            }
            return sr != null ? sr : string.Empty;
        }

        public static string PickFormat(string srFormat, string zhFormat, params object[] args)
        {
            RegisterTemplate(srFormat, zhFormat);
            string fmt = Pick(srFormat, zhFormat);
            if (args == null || args.Length == 0)
            {
                return fmt;
            }
            try
            {
                return string.Format(fmt, args);
            }
            catch
            {
                return fmt;
            }
        }

        /// <summary>
        /// Ponovo prevodi vec prikazan tekst (npr. Title/Description/MetricSummary/Reason) na trenutni jezik.
        /// Radi za tacno registrovane Pick() parove i za PickFormat() sablone (argumenti se prenose kao tekst).
        /// Nepoznat tekst se vraca nepromenjen.
        /// </summary>
        public static string Retranslate(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            bool toZh = IsChinese;
            string hit;
            lock (_syncLock)
            {
                if (toZh)
                {
                    if (_srToZh.TryGetValue(text, out hit)) return hit;
                    if (_zhToSr.ContainsKey(text)) return text;
                }
                else
                {
                    if (_zhToSr.TryGetValue(text, out hit)) return hit;
                    if (_srToZh.ContainsKey(text)) return text;
                }
            }

            string[][] snapshot;
            lock (_syncLock)
            {
                snapshot = _templates.ToArray();
            }
            for (int i = 0; i < snapshot.Length; i++)
            {
                string fromFmt;
                string toFmt;
                for (int dir = 0; dir < 2; dir++)
                {
                    fromFmt = dir == 0 ? snapshot[i][0] : snapshot[i][1];
                    toFmt = toZh ? snapshot[i][1] : snapshot[i][0];
                    if (fromFmt.IndexOf('{') < 0) continue;
                    Regex rx = GetTemplateRegex(fromFmt);
                    Match m = rx.Match(text);
                    if (!m.Success) continue;
                    int maxIdx = -1;
                    foreach (Match pm in Regex.Matches(fromFmt, @"\{(\d+)"))
                    {
                        int n = int.Parse(pm.Groups[1].Value);
                        if (n > maxIdx) maxIdx = n;
                    }
                    object[] args = new object[maxIdx + 1];
                    for (int a = 0; a <= maxIdx; a++)
                    {
                        Group g = m.Groups["a" + a];
                        args[a] = g.Success ? g.Value : string.Empty;
                    }
                    try { return string.Format(toFmt, args); } catch { return text; }
                }
            }
            return text;
        }

        private static Regex GetTemplateRegex(string fmt)
        {
            lock (_syncLock)
            {
                Regex rx;
                if (_templateRegex.TryGetValue(fmt, out rx)) return rx;
                string escaped = Regex.Escape(fmt);
                string pattern = Regex.Replace(escaped, @"\\\{(\d+)(?::[^}]*)?\}", delegate(Match m)
                {
                    return "(?<a" + m.Groups[1].Value + ">.*?)";
                });
                rx = new Regex("^" + pattern + "$", RegexOptions.Singleline);
                _templateRegex[fmt] = rx;
                return rx;
            }
        }

        private static string LoadInitialLanguage()
        {
            try
            {
                string cfgPath = @"C:\setup\wis-config.json";
                if (File.Exists(cfgPath))
                {
                    string json = File.ReadAllText(cfgPath, Encoding.UTF8);
                    Match m = Regex.Match(json, @"""language""\s*:\s*""([^""]+)""", RegexOptions.IgnoreCase);
                    if (m.Success && !string.IsNullOrEmpty(m.Groups[1].Value))
                    {
                        return NormalizeLanguage(m.Groups[1].Value);
                    }
                }
            }
            catch { }

            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WIS"))
                {
                    if (key != null)
                    {
                        object obj = key.GetValue("Language");
                        if (obj != null)
                        {
                            return NormalizeLanguage(obj.ToString());
                        }
                    }
                }
            }
            catch { }

            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\WIS"))
                {
                    if (key != null)
                    {
                        object obj = key.GetValue("Language");
                        if (obj != null)
                        {
                            return NormalizeLanguage(obj.ToString());
                        }
                    }
                }
            }
            catch { }

            return "sr";
        }
    }
}
