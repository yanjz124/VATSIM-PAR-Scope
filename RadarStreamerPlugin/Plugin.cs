using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using RossCarlson.Vatsim.Vpilot.Plugins;
using RossCarlson.Vatsim.Vpilot.Plugins.Events;

namespace RadarStreamerPlugin
{
    public class Plugin : IPlugin
    {
        public string Name { get { return "Radar Streamer"; } }

        private IBroker _vPilot;
        private UdpClient _udp;
        private IPEndPoint _endpoint;
        private readonly object _sendLock = new object();

        // Remember the type code by callsign so we can include it on updates
        private readonly Dictionary<string, string> _typeByCallsign = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // TODO: Make configurable via ini if desired
        private string _host = "127.0.0.1";
        private int _port = 49090;

        public void Initialize(IBroker broker)
        {
            _vPilot = broker;

            _endpoint = new IPEndPoint(IPAddress.Parse(_host), _port);
            _udp = new UdpClient();

            _vPilot.AircraftAdded += OnAircraftAdded;
            _vPilot.AircraftUpdated += OnAircraftUpdated;
            _vPilot.AircraftDeleted += OnAircraftDeleted;
            _vPilot.NetworkConnected += OnNetworkConnected;
            _vPilot.NetworkDisconnected += OnNetworkDisconnected;
            _vPilot.SessionEnded += OnSessionEnded;

            _vPilot.PostDebugMessage("[RadarStreamer] Initialized. Streaming NDJSON over UDP to " + _host + ":" + _port);

            // Optional: emit an initialization status line
            var initJson = "{\"type\":\"plugin_initialized\",\"t\":" + UnixMs() + ",\"host\":\"" + Escape(_host) + "\",\"port\":" + _port + "}\n";
            Send(initJson);
        }

        private void OnNetworkConnected(object sender, NetworkConnectedEventArgs e)
        {
            string observer = e.ObserverMode ? "true" : "false";
            _vPilot.PostDebugMessage("[RadarStreamer] Network connected as " + e.Callsign + " (" + e.TypeCode + ") observer=" + observer);

            var now = UnixMs();
            var json = new StringBuilder(192)
                .Append("{\"type\":\"network_connected\"")
                .Append(",\"t\":").Append(now)
                .Append(",\"callsign\":\"").Append(Escape(e.Callsign)).Append("\"")
                .Append(",\"typeCode\":\"").Append(Escape(e.TypeCode)).Append("\"")
                .Append(",\"observer\":").Append(observer)
                .Append("}\n").ToString();
            Send(json);
        }

        private void OnNetworkDisconnected(object sender, EventArgs e)
        {
            _vPilot.PostDebugMessage("[RadarStreamer] Network disconnected");
            var json = "{\"type\":\"network_disconnected\",\"t\":" + UnixMs() + "}\n";
            Send(json);
        }

        private void OnSessionEnded(object sender, EventArgs e)
        {
            _vPilot.PostDebugMessage("[RadarStreamer] Session ended (vPilot closing)");
            var json = "{\"type\":\"session_ended\",\"t\":" + UnixMs() + "}\n";
            Send(json);
        }

        private void OnAircraftAdded(object sender, AircraftAddedEventArgs e)
        {
            if (!string.IsNullOrEmpty(e.Callsign) && !string.IsNullOrEmpty(e.TypeCode))
            {
                _typeByCallsign[e.Callsign] = e.TypeCode;
            }

            var now = UnixMs();
            var json = new StringBuilder(256)
                .Append("{\"type\":\"add\"")
                .Append(",\"t\":").Append(now)
                .Append(",\"callsign\":\"").Append(Escape(e.Callsign)).Append("\"")
                .Append(",\"typeCode\":\"").Append(Escape(e.TypeCode)).Append("\"")
                .Append(",\"lat\":").Append(e.Latitude.ToString(CultureInfo.InvariantCulture))
                .Append(",\"lon\":").Append(e.Longitude.ToString(CultureInfo.InvariantCulture))
                .Append(",\"alt_ft\":").Append(e.Altitude.ToString(CultureInfo.InvariantCulture))
                .Append(",\"pressAlt_ft\":").Append(e.PressureAltitude.ToString(CultureInfo.InvariantCulture))
                .Append(",\"pitch_deg\":").Append(e.Pitch.ToString(CultureInfo.InvariantCulture))
                .Append(",\"bank_deg\":").Append(e.Bank.ToString(CultureInfo.InvariantCulture))
                .Append(",\"heading_deg\":").Append(e.Heading.ToString(CultureInfo.InvariantCulture))
                .Append(",\"speed_kts\":").Append(e.Speed.ToString(CultureInfo.InvariantCulture))
                .Append("}\n")
                .ToString();

            Send(json);
        }

        private void OnAircraftUpdated(object sender, AircraftUpdatedEventArgs e)
        {
            string callsignKey = e.Callsign ?? string.Empty;
            string typeCode;
            _typeByCallsign.TryGetValue(callsignKey, out typeCode);
            var now = UnixMs();

            var json = new StringBuilder(256)
                .Append("{\"type\":\"update\"")
                .Append(",\"t\":").Append(now)
                .Append(",\"callsign\":\"").Append(Escape(e.Callsign)).Append("\"");

            if (!string.IsNullOrEmpty(typeCode))
                json.Append(",\"typeCode\":\"").Append(Escape(typeCode)).Append("\"");

            json.Append(",\"lat\":").Append(e.Latitude.ToString(CultureInfo.InvariantCulture))
                .Append(",\"lon\":").Append(e.Longitude.ToString(CultureInfo.InvariantCulture))
                .Append(",\"alt_ft\":").Append(e.Altitude.ToString(CultureInfo.InvariantCulture))
                .Append(",\"pressAlt_ft\":").Append(e.PressureAltitude.ToString(CultureInfo.InvariantCulture))
                .Append(",\"pitch_deg\":").Append(e.Pitch.ToString(CultureInfo.InvariantCulture))
                .Append(",\"bank_deg\":").Append(e.Bank.ToString(CultureInfo.InvariantCulture))
                .Append(",\"heading_deg\":").Append(e.Heading.ToString(CultureInfo.InvariantCulture))
                .Append(",\"speed_kts\":").Append(e.Speed.ToString(CultureInfo.InvariantCulture))
                .Append("}\n");

            Send(json.ToString());
        }

        private void OnAircraftDeleted(object sender, AircraftDeletedEventArgs e)
        {
            if (!string.IsNullOrEmpty(e.Callsign))
                _typeByCallsign.Remove(e.Callsign);

            var now = UnixMs();
            var json =
                "{\"type\":\"delete\",\"t\":" + now +
                ",\"callsign\":\"" + Escape(e.Callsign) + "\"}\n";

            Send(json);
        }

        private void Send(string line)
        {
            try
            {
                var bytes = Encoding.UTF8.GetBytes(line);
                lock (_sendLock)
                {
                    _udp.Send(bytes, bytes.Length, _endpoint);
                }
            }
            catch (Exception ex)
            {
                if (_vPilot != null)
                {
                    _vPilot.PostDebugMessage("[RadarStreamer] UDP send error: " + ex.Message);
                }
            }
        }

        private static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private static long UnixMs()
        {
            var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            return (long)(DateTime.UtcNow - epoch).TotalMilliseconds;
        }
    }
}
