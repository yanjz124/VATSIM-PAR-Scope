using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using RossCarlson.Vatsim.Vpilot.Plugins;
using RossCarlson.Vatsim.Vpilot.Plugins.Events;

namespace RadarStreamerPlugin
{
    /// <summary>
    /// Streams traffic seen by vPilot to the PAR scope as NDJSON over UDP (protocol v2).
    ///
    /// Message types:
    ///   hello  – plugin started; reports whether the Velocity fast hook is active
    ///   net    – network connected / disconnected / session ended
    ///   add    – aircraft added to the sim (callsign, type, position)
    ///   pos    – position report; src = "slow" (public API, ~5 s) or "fast" (Velocity, up to 5 Hz, with velocities)
    ///   del    – aircraft removed
    ///   stats  – packet rate counters, once a minute
    /// </summary>
    public class Plugin : IPlugin
    {
        public string Name => "Radar Streamer";

        private IBroker _vPilot;
        private LineSender _sender;
        private FastPositionHook _hook;
        private Timer _statsTimer;
        private bool _rateStats = true;

        private long _fastCount, _slowCount;
        private readonly ConcurrentDictionary<string, byte> _fastCallsigns = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

        public void Initialize(IBroker broker)
        {
            _vPilot = broker;

            string host = "127.0.0.1";
            int port = 49090;
            bool fastHook = true;
            try
            {
                var ini = new IniFile(Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? ".", "RadarStreamerPlugin.ini"));
                host = ReadOr(ini.Read("Host", "Stream"), host);
                if (int.TryParse(ini.Read("Port", "Stream"), out var p) && p > 0 && p < 65536) port = p;
                fastHook = !string.Equals(ini.Read("FastHook", "Velocity"), "false", StringComparison.OrdinalIgnoreCase);
                _rateStats = !string.Equals(ini.Read("RateStats", "Diagnostics"), "false", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                Log("Could not read RadarStreamerPlugin.ini (" + ex.Message + "); using defaults.");
            }

            _sender = new LineSender(host, port, Log);

            broker.NetworkConnected += OnNetworkConnected;
            broker.NetworkDisconnected += (s, e) => Send(new JsonLine("net").Str("state", "disconnected"));
            broker.SessionEnded += OnSessionEnded;
            broker.AircraftAdded += OnAircraftAdded;
            broker.AircraftUpdated += OnAircraftUpdated;
            broker.AircraftDeleted += (s, e) => Send(new JsonLine("del").Str("cs", e.Callsign));

            if (fastHook)
            {
                try { InstallFastHook(broker); }
                catch (Exception ex) { Log("Fast hook unavailable (" + ex.Message + "); using slow updates only."); }
            }

            _statsTimer = new Timer(_ => ReportStats(), null, 60000, 60000);

            Log("Streaming to " + host + ":" + port + (_hook != null ? " with Velocity fast positions." : " (slow updates only)."));
            SendHello();
        }

        // Kept in its own non-inlined method so a missing bbv assembly surfaces as a catchable
        // exception here instead of failing JIT of Initialize.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void InstallFastHook(IBroker broker)
        {
            _hook = FastPositionHook.TryInstall(broker, OnFastPosition, Log);
        }

        private void SendHello()
        {
            Send(new JsonLine("hello")
                .Str("plugin", typeof(Plugin).Assembly.GetName().Version.ToString())
                .Bool("fastHook", _hook != null && _hook.IsActive));
        }

        private void OnNetworkConnected(object sender, NetworkConnectedEventArgs e)
        {
            SendHello();
            Send(new JsonLine("net")
                .Str("state", "connected")
                .Str("cs", e.Callsign)
                .Str("ac", e.TypeCode)
                .Bool("observer", e.ObserverMode));
        }

        private void OnSessionEnded(object sender, EventArgs e)
        {
            Send(new JsonLine("net").Str("state", "ended"));
            _statsTimer?.Dispose();
            _hook?.Uninstall();
            _sender?.Dispose();
        }

        private void OnAircraftAdded(object sender, AircraftAddedEventArgs e)
        {
            Send(new JsonLine("add")
                .Str("cs", e.Callsign)
                .Str("ac", e.TypeCode)
                .Num("lat", e.Latitude)
                .Num("lon", e.Longitude)
                .Num("alt", e.Altitude)
                .Num("palt", e.PressureAltitude)
                .Num("hdg", e.Heading)
                .Num("gs", e.Speed)
                .Num("pitch", e.Pitch)
                .Num("bank", e.Bank));
        }

        private void OnAircraftUpdated(object sender, AircraftUpdatedEventArgs e)
        {
            Interlocked.Increment(ref _slowCount);
            Send(new JsonLine("pos")
                .Str("src", "slow")
                .Str("cs", e.Callsign)
                .Num("lat", e.Latitude)
                .Num("lon", e.Longitude)
                .Num("alt", e.Altitude)
                .Num("palt", e.PressureAltitude)
                .Num("hdg", e.Heading)
                .Num("gs", e.Speed)
                .Num("pitch", e.Pitch)
                .Num("bank", e.Bank));
        }

        // Runs on vPilot's network thread: keep it to formatting and a queue push.
        private void OnFastPosition(FastPosition p)
        {
            Interlocked.Increment(ref _fastCount);
            _fastCallsigns.TryAdd(p.Callsign, 0);
            var line = new JsonLine("pos")
                .Str("src", "fast")
                .Str("cs", p.Callsign)
                .Num("lat", p.Lat)
                .Num("lon", p.Lon)
                .Num("alt", p.AltFt)
                .Num("ve", p.VelEast)
                .Num("vu", p.VelUp)
                .Num("vn", p.VelNorth)
                .Num("rx", p.RotX)
                .Num("ry", p.RotY)
                .Num("rz", p.RotZ);
            if (p.AglFt.HasValue) line.Num("agl", p.AglFt.Value);
            Send(line);
        }

        private void ReportStats()
        {
            long fast = Interlocked.Exchange(ref _fastCount, 0);
            long slow = Interlocked.Exchange(ref _slowCount, 0);
            int fastTargets = _fastCallsigns.Count;
            _fastCallsigns.Clear();
            bool active = _hook != null && _hook.IsActive;

            Send(new JsonLine("stats")
                .Bool("fastHook", active)
                .Num("fastPerMin", fast)
                .Num("slowPerMin", slow)
                .Num("fastTargets", fastTargets)
                .Num("dropped", _sender.Dropped));

            if (_rateStats && (fast > 0 || slow > 0))
                Log($"Last minute: {fast} fast ({fast / 60.0:F1}/s, {fastTargets} aircraft), {slow} slow; fast hook {(active ? "active" : "inactive")}.");
        }

        private void Send(JsonLine line) => _sender?.Enqueue(line.ToString());

        private void Log(string message)
        {
            try { _vPilot?.PostDebugMessage("[RadarStreamer] " + message); } catch { }
        }

        private static string ReadOr(string value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }
}
