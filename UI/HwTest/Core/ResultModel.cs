using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace WIS.HwTest.Core
{
    public enum TestStatus
    {
        Pending,
        Running,
        Pass,
        Fail,
        Skip,
        NotApplicable
    }

    public class TestItemResult
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }
        public TestStatus Status { get; set; }
        public bool HasWarning { get; set; }
        public bool IsAutomated { get; set; }
        public string MetricSummary { get; set; }
        public int Attempts { get; set; }
        public int DurationSeconds { get; set; }
        public string Reason { get; set; }
        public Dictionary<string, object> Details { get; set; }

        public TestItemResult(string id, string title, string description)
        {
            Id = id;
            Title = title;
            Description = description;
            Status = TestStatus.Pending;
            HasWarning = false;
            IsAutomated = false;
            MetricSummary = string.Empty;
            Attempts = 0;
            DurationSeconds = 0;
            Reason = string.Empty;
            Details = new Dictionary<string, object>();
        }
    }

    public class HwTestRunResult
    {
        public string Schema { get; set; }
        public string Started { get; set; }
        public string Finished { get; set; }
        public string Model { get; set; }
        public string Serial { get; set; }
        public string AppVersion { get; set; }
        public Dictionary<string, object> Summary { get; set; }
        public List<Dictionary<string, object>> Tests { get; set; }

        public HwTestRunResult()
        {
            Schema = "wis.hwtest.v1";
            AppVersion = "3.0.0";
            Summary = new Dictionary<string, object>();
            Tests = new List<Dictionary<string, object>>();
        }
    }

    public class KeyLayoutDefinition
    {
        public string Id { get; set; }
        public string Label { get; set; }
        public int VKey { get; set; }
        public List<int> AlternateVkeys { get; set; }
        public double Row { get; set; }
        public double Col { get; set; }
        public double W { get; set; }
        public double H { get; set; }
        public bool IsNumpad { get; set; }
        public bool IsOptional { get; set; }
        public int ExtendedMode { get; set; }

        public KeyLayoutDefinition()
        {
            AlternateVkeys = new List<int>();
            W = 1.0;
            H = 1.0;
            IsOptional = false;
            ExtendedMode = 0;
        }
    }

    public static class SimpleJson
    {
        public static string EscapeString(string str)
        {
            if (str == null) return "null";
            StringBuilder sb = new StringBuilder();
            sb.Append('"');
            foreach (char c in str)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ')
                            sb.AppendFormat("\\u{0:x4}", (int)c);
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        public static string Serialize(object obj)
        {
            if (obj == null) return "null";
            if (obj is string) return EscapeString((string)obj);
            if (obj is bool) return ((bool)obj) ? "true" : "false";
            if (obj is int || obj is long || obj is uint || obj is ulong || obj is short || obj is byte)
                return obj.ToString();
            if (obj is double || obj is float)
                return ((IFormattable)obj).ToString("G", System.Globalization.CultureInfo.InvariantCulture);

            if (obj is IDictionary<string, object>)
            {
                IDictionary<string, object> dict = (IDictionary<string, object>)obj;
                StringBuilder sb = new StringBuilder();
                sb.Append("{");
                bool first = true;
                foreach (KeyValuePair<string, object> kvp in dict)
                {
                    if (!first) sb.Append(",");
                    first = false;
                    sb.Append(EscapeString(kvp.Key));
                    sb.Append(":");
                    sb.Append(Serialize(kvp.Value));
                }
                sb.Append("}");
                return sb.ToString();
            }

            if (obj is IEnumerable<object>)
            {
                IEnumerable<object> list = (IEnumerable<object>)obj;
                StringBuilder sb = new StringBuilder();
                sb.Append("[");
                bool first = true;
                foreach (object item in list)
                {
                    if (!first) sb.Append(",");
                    first = false;
                    sb.Append(Serialize(item));
                }
                sb.Append("]");
                return sb.ToString();
            }

            return EscapeString(obj.ToString());
        }
    }
}
