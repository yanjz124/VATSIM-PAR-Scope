using System.Globalization;
using System.IO;
using System.Text;
using PARScope.Core.Traffic;

namespace PARScope.App.Services;

/// <summary>
/// Diagnostics: records every received position report to CSV and a per-aircraft rate summary every
/// 10 s, so the actual VATSIM update rate (Velocity fast vs 5 s slow) can be checked after a session.
/// Files live in %AppData%\VATSIM-PAR-Scope\diagnostics; each CSV is capped at 10 MB and only the most
/// recent files are kept.
/// </summary>
public sealed class TrafficRecorder : IDisposable
{
    private const long MaxBytes = 10 * 1024 * 1024;
    private const int KeepFiles = 10;

    private readonly Lock _lock = new();
    private readonly Dictionary<string, AircraftStats> _stats = new(StringComparer.OrdinalIgnoreCase);
    private StreamWriter? _csv;
    private double _lastSummary;

    public static string Folder { get; } = Path.Combine(AppSettings.DataFolder, "diagnostics");

    public bool Enabled { get; set; } = true;

    public sealed class AircraftStats
    {
        public string Source = "";
        public double LastTime = double.NaN;
        public double LastFast = double.NaN, LastSlow = double.NaN;
        public double FastInterval = double.NaN, SlowInterval = double.NaN;
        public int FastCount, SlowCount;
    }

    public void Record(PositionReport r, double receivedAt)
    {
        lock (_lock)
        {
            if (!_stats.TryGetValue(r.Callsign, out var s)) _stats[r.Callsign] = s = new AircraftStats();
            double interval = double.IsNaN(s.LastTime) ? double.NaN : r.Time - s.LastTime;
            s.LastTime = r.Time;
            s.Source = r.Source.ToString();
            if (r.Source == ReportSource.Fast)
            {
                if (!double.IsNaN(s.LastFast)) s.FastInterval = Smooth(s.FastInterval, r.Time - s.LastFast);
                s.LastFast = r.Time;
                s.FastCount++;
            }
            else if (r.Source is ReportSource.Slow or ReportSource.Feed)
            {
                if (!double.IsNaN(s.LastSlow)) s.SlowInterval = Smooth(s.SlowInterval, r.Time - s.LastSlow);
                s.LastSlow = r.Time;
                s.SlowCount++;
            }

            if (!Enabled) return;
            var w = Writer();
            if (w == null) return;
            var v = r.VelocityEnu;
            w.WriteLine(string.Join(',',
                receivedAt.ToString("F3", CultureInfo.InvariantCulture),
                r.Time.ToString("F3", CultureInfo.InvariantCulture),
                DateTime.UtcNow.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
                r.Source, r.Callsign,
                r.Position.LatDeg.ToString("F6", CultureInfo.InvariantCulture),
                r.Position.LonDeg.ToString("F6", CultureInfo.InvariantCulture),
                r.Position.AltFt.ToString("F0", CultureInfo.InvariantCulture),
                v?.X.ToString("F2", CultureInfo.InvariantCulture) ?? "",
                v?.Y.ToString("F2", CultureInfo.InvariantCulture) ?? "",
                v?.Z.ToString("F2", CultureInfo.InvariantCulture) ?? "",
                double.IsNaN(interval) ? "" : interval.ToString("F3", CultureInfo.InvariantCulture),
                r.PressureAltFt?.ToString("F0", CultureInfo.InvariantCulture) ?? "",
                r.GroundSpeedKt?.ToString("F0", CultureInfo.InvariantCulture) ?? "",
                r.Beacon?.ToString("0000", CultureInfo.InvariantCulture) ?? "",
                // STARS data (100 ft Mode C) is marked, so a stepped altitude in the log can be told from the pilot's.
                r.CoarseAltitude ? "stars" : ""));
        }
    }

    /// <summary>Snapshot for the live diagnostics table, and the periodic rates.log summary.</summary>
    public IReadOnlyList<string> Summary(double now)
    {
        lock (_lock)
        {
            foreach (var k in _stats.Where(kv => now - kv.Value.LastTime > 60).Select(kv => kv.Key).ToList()) _stats.Remove(k);
            var lines = _stats.OrderBy(kv => kv.Key).Select(kv =>
            {
                var s = kv.Value;
                string fast = !double.IsNaN(s.LastFast) && now - s.LastFast < 3 && s.FastInterval > 0 ? $"{1 / s.FastInterval,4:0.0} Hz" : "   -   ";
                string slow = double.IsNaN(s.SlowInterval) ? "  -  " : $"{s.SlowInterval,4:0.0} s";
                return $"{kv.Key,-10} {s.Source,-9} fast {fast}  slow {slow}  age {now - s.LastTime,4:0.0} s";
            }).ToList();

            if (Enabled && now - _lastSummary >= 10 && lines.Count > 0)
            {
                _lastSummary = now;
                try
                {
                    Directory.CreateDirectory(Folder);
                    File.AppendAllText(Path.Combine(Folder, "rates.log"),
                        $"--- {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z{Environment.NewLine}{string.Join(Environment.NewLine, lines)}{Environment.NewLine}");
                }
                catch (IOException) { }
            }
            return lines;
        }
    }

    private StreamWriter? Writer()
    {
        try
        {
            if (_csv != null && _csv.BaseStream.Length < MaxBytes) return _csv;
            _csv?.Dispose();
            Directory.CreateDirectory(Folder);
            foreach (var old in new DirectoryInfo(Folder).GetFiles("traffic-*.csv").OrderByDescending(f => f.Name).Skip(KeepFiles - 1))
                old.Delete();
            var path = Path.Combine(Folder, $"traffic-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv");
            _csv = new StreamWriter(path, false, new UTF8Encoding(false)) { AutoFlush = true };
            _csv.WriteLine("recv_s,report_s,utc,source,callsign,lat,lon,alt_ft,v_east_mps,v_north_mps,v_up_mps,interval_s,mode_c_ft,gs_kt,beacon,coarse_alt");
            return _csv;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static double Smooth(double prev, double sample) => double.IsNaN(prev) ? sample : prev + (sample - prev) * 0.3;

    public void Dispose()
    {
        lock (_lock) _csv?.Dispose();
    }
}
