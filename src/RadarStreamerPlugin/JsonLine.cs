using System;
using System.Globalization;
using System.Text;

namespace RadarStreamerPlugin
{
    /// <summary>
    /// Minimal allocation-light builder for one NDJSON object. .NET Framework has no
    /// System.Text.Json, and pulling a JSON package into vPilot's process risks version clashes.
    /// </summary>
    internal sealed class JsonLine
    {
        private readonly StringBuilder _sb = new StringBuilder(256);
        private bool _first = true;

        public JsonLine(string type)
        {
            _sb.Append('{');
            Num("v", Protocol.Version);
            Str("type", type);
            Num("t", Protocol.UnixMs());
        }

        public JsonLine Str(string key, string value)
        {
            Key(key);
            _sb.Append('"');
            if (value != null)
            {
                foreach (char c in value)
                {
                    if (c == '"' || c == '\\') _sb.Append('\\').Append(c);
                    else if (c < 0x20) _sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else _sb.Append(c);
                }
            }
            _sb.Append('"');
            return this;
        }

        public JsonLine Num(string key, double value)
        {
            Key(key);
            if (double.IsNaN(value) || double.IsInfinity(value)) _sb.Append("null");
            else _sb.Append(value.ToString("R", CultureInfo.InvariantCulture));
            return this;
        }

        public JsonLine Num(string key, long value)
        {
            Key(key);
            _sb.Append(value.ToString(CultureInfo.InvariantCulture));
            return this;
        }

        public JsonLine Bool(string key, bool value)
        {
            Key(key);
            _sb.Append(value ? "true" : "false");
            return this;
        }

        public override string ToString() => _sb.ToString() + "}\n";

        private void Key(string key)
        {
            if (!_first) _sb.Append(',');
            _first = false;
            _sb.Append('"').Append(key).Append("\":");
        }
    }

    internal static class Protocol
    {
        public const int Version = 2;
        private static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        public static long UnixMs() => (long)(DateTime.UtcNow - Epoch).TotalMilliseconds;
    }
}
