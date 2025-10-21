using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.ComponentModel;

namespace PARScopeDisplay
{
    public partial class SimulatorWindow : Window
    {
    private readonly List<FakeAircraft> _aircraft = new List<FakeAircraft>();
        private UdpClient _udp;
        private IPEndPoint _endpoint;
        private CancellationTokenSource _cts;
    private MainWindow.RunwaySettings _runway;
        // Update interval in milliseconds. Default to 5000ms to match typical vPilot update cadence.
        // If vPilot moves to faster updates, lower this value or expose it in the UI.
        private int _updateIntervalMs = 5000;

        public SimulatorWindow()
        {
            InitializeComponent();
            _endpoint = new IPEndPoint(IPAddress.Parse("127.0.0.1"), 49090);
            _udp = new UdpClient();
            RefreshList();
        }

        public SimulatorWindow(MainWindow.RunwaySettings runway) : this()
        {
            _runway = runway;
            if (_runway == null)
            {
                StatusText.Text = "Warning: no runway selected - spawn will use fixed lat/lon";
            }
            else
            {
                StatusText.Text = "Simulator bound to runway " + _runway.Icao + " " + _runway.Runway;
            }
        }

        private void RefreshList()
        {
            AircraftList.ItemsSource = null;
            AircraftList.ItemsSource = _aircraft;
        }

        private void OnAddClick(object sender, RoutedEventArgs e)
        {
            var ac = new FakeAircraft
            {
                Callsign = "FAKE" + (_aircraft.Count + 1),
                Lat = 51.4700, // sample near London Heathrow by default
                Lon = -0.4543,
                AltFt = 3000,
                HeadingDeg = 180,
                SpeedKts = 140,
                VsFpm = 0,
                TypeCode = "B738"
            };
            _aircraft.Add(ac);
            RefreshList();
            AircraftList.SelectedItem = ac;
            PopulateFields(ac);
        }

        private void OnRemoveClick(object sender, RoutedEventArgs e)
        {
            if (AircraftList.SelectedItem is FakeAircraft ac)
            {
                _aircraft.Remove(ac);
                RefreshList();
            }
        }

        private void OnUpdateClick(object sender, RoutedEventArgs e)
        {
            if (AircraftList.SelectedItem is FakeAircraft ac)
            {
                ApplyFields(ac);
                RefreshList();
            }
        }

        private void OnSendAddClick(object sender, RoutedEventArgs e)
        {
            if (AircraftList.SelectedItem is FakeAircraft ac)
            {
                SendNdjson(BuildAddJson(ac));
                StatusText.Text = "Sent add for " + ac.Callsign;
            }
        }

        private void OnSendUpdateClick(object sender, RoutedEventArgs e)
        {
            if (AircraftList.SelectedItem is FakeAircraft ac)
            {
                ApplyFields(ac);
                SendNdjson(BuildUpdateJson(ac));
                StatusText.Text = "Sent update for " + ac.Callsign;
            }
        }

        private void OnSendDeleteClick(object sender, RoutedEventArgs e)
        {
            if (AircraftList.SelectedItem is FakeAircraft ac)
            {
                SendNdjson(BuildDeleteJson(ac));
                StatusText.Text = "Sent delete for " + ac.Callsign;
            }
        }

        private void OnStartAutoClick(object sender, RoutedEventArgs e)
        {
            if (_cts != null) return;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            Task.Run(async () =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                long lastMs = sw.ElapsedMilliseconds;
                while (!token.IsCancellationRequested)
                {
                    var nowMs = sw.ElapsedMilliseconds;
                    var dt = (nowMs - lastMs) / 1000.0; // seconds since last update
                    if (dt <= 0) dt = _updateIntervalMs / 1000.0;
                    lastMs = nowMs;

                    lock (_aircraft)
                    {
                        foreach (var ac in _aircraft)
                        {
                            // skip paused aircraft
                            if (ac.IsPaused) continue;

                            // Motion: move along heading by speed * dt
                            double nmPerSec = ac.SpeedKts / 3600.0; // NM per second at current speed
                            double meters = nmPerSec * 1852.0 * dt; // meters moved during dt seconds
                            // Approximate lat/lon change (small-dist)
                            double dLat = (meters * Math.Cos(ac.HeadingDeg * Math.PI / 180.0)) / 111319.9;
                            double dLon = (meters * Math.Sin(ac.HeadingDeg * Math.PI / 180.0)) / (111319.9 * Math.Cos(ac.Lat * Math.PI / 180.0));
                            ac.Lat += dLat;
                            ac.Lon += dLon;
                            // vertical speed: VsFpm is ft per minute -> ft per second = VsFpm/60
                            ac.AltFt += (ac.VsFpm / 60.0) * dt;
                            // send update
                            SendNdjson(BuildUpdateJson(ac));
                        }
                    }

                    await Task.Delay(_updateIntervalMs, token);
                }
            }, token);
            StatusText.Text = "Auto-moving";
        }

        private void OnSpawnClick(object sender, RoutedEventArgs e)
        {
            double bearing = 0, rangeNm = 0, alt = 0, speed = 0, heading = 0;
            double.TryParse(SpawnBearingBox.Text.Trim(), out bearing);
            double.TryParse(SpawnRangeBox.Text.Trim(), out rangeNm);
            double.TryParse(SpawnAltBox.Text.Trim(), out alt);
            double.TryParse(SpawnSpeedBox.Text.Trim(), out speed);
            double.TryParse(SpawnHeadingBox.Text.Trim(), out heading);

            double lat = 51.0, lon = 0.0; // fallback
            if (_runway != null)
            {
                // compute destination from runway threshold lat/lon
                DestinationFrom(_runway.ThresholdLat, _runway.ThresholdLon, bearing, rangeNm, out lat, out lon);
            }

            var ac = new FakeAircraft { Callsign = "FAKE" + (_aircraft.Count + 1), Lat = lat, Lon = lon, AltFt = alt, SpeedKts = speed, HeadingDeg = heading, VsFpm = 0, TypeCode = "B738", IsPaused = false };
            _aircraft.Add(ac);
            RefreshList();
            AircraftList.SelectedItem = ac;
            PopulateFields(ac);
            // automatically send add and start motion (if not already running)
            SendNdjson(BuildAddJson(ac));
            StatusText.Text = "Spawned " + ac.Callsign + " (sent add)";
            if (_cts == null)
            {
                // start auto-moving automatically
                Dispatcher.Invoke(() => OnStartAutoClick(this, null));
            }
        }

        // Per-item pause/unpause/delete handlers wired from the list item template
        private void OnPauseClick(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is FakeAircraft ac)
            {
                ac.IsPaused = true;
                RefreshList();
                StatusText.Text = "Paused " + ac.Callsign;
            }
        }

        private void OnUnpauseClick(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is FakeAircraft ac)
            {
                ac.IsPaused = false;
                RefreshList();
                StatusText.Text = "Unpaused " + ac.Callsign;
            }
        }

        private void OnDeleteItemClick(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is FakeAircraft ac)
            {
                // send delete then remove
                SendNdjson(BuildDeleteJson(ac));
                _aircraft.Remove(ac);
                RefreshList();
                StatusText.Text = "Deleted " + ac.Callsign;
            }
        }

        // Haversine-based destination: given start lat/lon in degrees, bearing degrees and distance in NM
        private void DestinationFrom(double latDeg, double lonDeg, double bearingDeg, double distNm, out double outLat, out double outLon)
        {
            double R = 6378137.0; // meters
            double distM = distNm * 1852.0;
            double lat1 = DegToRad(latDeg);
            double lon1 = DegToRad(lonDeg);
            double brng = DegToRad(bearingDeg);

            double lat2 = Math.Asin(Math.Sin(lat1) * Math.Cos(distM / R) + Math.Cos(lat1) * Math.Sin(distM / R) * Math.Cos(brng));
            double lon2 = lon1 + Math.Atan2(Math.Sin(brng) * Math.Sin(distM / R) * Math.Cos(lat1), Math.Cos(distM / R) - Math.Sin(lat1) * Math.Sin(lat2));

            outLat = RadToDeg(lat2);
            outLon = RadToDeg(lon2);
        }

        private static double DegToRad(double deg) { return deg * Math.PI / 180.0; }
        private static double RadToDeg(double rad) { return rad * 180.0 / Math.PI; }

        private void OnStopAutoClick(object sender, RoutedEventArgs e)
        {
            if (_cts != null)
            {
                _cts.Cancel();
                _cts = null;
                StatusText.Text = "Stopped";
            }
        }

        // Increment/decrement helpers: update selected aircraft and refresh/send update
        private void OnAltInc(object sender, RoutedEventArgs e) => ChangeSelectedAc(ac => ac.AltFt += 100);
        private void OnAltDec(object sender, RoutedEventArgs e) => ChangeSelectedAc(ac => ac.AltFt -= 100);
        private void OnHeadingInc(object sender, RoutedEventArgs e) => ChangeSelectedAc(ac => ac.HeadingDeg = (ac.HeadingDeg + 5) % 360);
        private void OnHeadingDec(object sender, RoutedEventArgs e) => ChangeSelectedAc(ac => ac.HeadingDeg = (ac.HeadingDeg - 5 + 360) % 360);
        private void OnSpeedInc(object sender, RoutedEventArgs e) => ChangeSelectedAc(ac => ac.SpeedKts += 5);
        private void OnSpeedDec(object sender, RoutedEventArgs e) => ChangeSelectedAc(ac => ac.SpeedKts = Math.Max(0, ac.SpeedKts - 5));
        private void OnVsInc(object sender, RoutedEventArgs e) => ChangeSelectedAc(ac => ac.VsFpm += 100);
        private void OnVsDec(object sender, RoutedEventArgs e) => ChangeSelectedAc(ac => ac.VsFpm -= 100);

        private void ChangeSelectedAc(Action<FakeAircraft> mutator)
        {
            if (AircraftList.SelectedItem is FakeAircraft ac)
            {
                mutator(ac);
                PopulateFields(ac);
                RefreshList();
                // keep the modified aircraft selected/highlighted
                AircraftList.SelectedItem = ac;
                SendNdjson(BuildUpdateJson(ac));
                StatusText.Text = "Sent update for " + ac.Callsign;
            }
        }

        // Apply changes immediately when a property textbox loses focus
        private void OnPropertyLostFocus(object sender, RoutedEventArgs e)
        {
            var selected = AircraftList.SelectedItem as FakeAircraft;
            if (selected == null) return;

            // Only apply the specific field that lost focus so other properties aren't overwritten by stale UI values
            if (sender is System.Windows.Controls.TextBox tb)
            {
                var changed = ApplySingleField(selected, tb);
                if (changed)
                {
                    RefreshList();
                    SendNdjson(BuildUpdateJson(selected));
                    StatusText.Text = "Auto-updated " + selected.Callsign;
                    AircraftList.SelectedItem = selected;
                }
            }
        }

        private void PopulateFields(FakeAircraft ac)
        {
            CallsignBox.Text = ac.Callsign;
            AltBox.Text = ac.AltFt.ToString("F0");
            HeadingBox.Text = ac.HeadingDeg.ToString("F1");
            SpeedBox.Text = ac.SpeedKts.ToString("F1");
            VsBox.Text = ac.VsFpm.ToString("F0");
            TypeCodeBox.Text = ac.TypeCode;
        }

        private bool ApplyFields(FakeAircraft ac)
        {
            bool changed = false;

            var newCall = CallsignBox.Text.Trim();
            var oldCall = ac.Callsign;
            if (!string.Equals(oldCall, newCall, StringComparison.Ordinal))
            {
                // send delete for old callsign so the scope removes the old entity
                if (!string.IsNullOrEmpty(oldCall)) SendNdjson("{\"type\":\"delete\",\"t\":" + UnixMs() + ",\"callsign\":\"" + Escape(oldCall) + "\"}\n");
                ac.Callsign = newCall;
                changed = true;
            }

            double tmp;
            if (double.TryParse(AltBox.Text.Trim(), out tmp))
            {
                if (!DoubleEquals(ac.AltFt, tmp)) { ac.AltFt = tmp; changed = true; }
            }
            if (double.TryParse(HeadingBox.Text.Trim(), out tmp))
            {
                if (!DoubleEquals(ac.HeadingDeg, tmp)) { ac.HeadingDeg = tmp; changed = true; }
            }
            if (double.TryParse(SpeedBox.Text.Trim(), out tmp))
            {
                if (!DoubleEquals(ac.SpeedKts, tmp)) { ac.SpeedKts = tmp; changed = true; }
            }
            if (double.TryParse(VsBox.Text.Trim(), out tmp))
            {
                if (!DoubleEquals(ac.VsFpm, tmp)) { ac.VsFpm = tmp; changed = true; }
            }

            var newType = TypeCodeBox.Text.Trim();
            if (!string.Equals(ac.TypeCode, newType, StringComparison.Ordinal))
            {
                ac.TypeCode = newType;
                changed = true;
            }

            return changed;
        }

        private static bool DoubleEquals(double a, double b, double eps = 1e-6)
        {
            return Math.Abs(a - b) <= eps;
        }

        // Commit on Enter key in property boxes
        private void OnPropertyEnterKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                if (AircraftList.SelectedItem is FakeAircraft ac)
                {
                    // Commit only the field where Enter was pressed to avoid overwriting other properties
                    if (sender is System.Windows.Controls.TextBox tb)
                    {
                        var changed = ApplySingleField(ac, tb);
                        if (changed)
                        {
                            RefreshList();
                            SendNdjson(BuildUpdateJson(ac));
                            AircraftList.SelectedItem = ac;
                            StatusText.Text = "Auto-updated " + ac.Callsign;
                        }
                    }
                    Keyboard.ClearFocus();
                }
                e.Handled = true;
            }
        }

        // Apply a single property from the corresponding textbox to the aircraft. Returns true if a change occurred.
        private bool ApplySingleField(FakeAircraft ac, System.Windows.Controls.TextBox tb)
        {
            if (ac == null || tb == null) return false;
            bool changed = false;
            double tmp;
            switch (tb.Name)
            {
                case "CallsignBox":
                    var newCall = CallsignBox.Text.Trim();
                    var oldCall = ac.Callsign;
                    if (!string.Equals(oldCall, newCall, StringComparison.Ordinal))
                    {
                        if (!string.IsNullOrEmpty(oldCall)) SendNdjson("{\"type\":\"delete\",\"t\":" + UnixMs() + ",\"callsign\":\"" + Escape(oldCall) + "\"}\n");
                        ac.Callsign = newCall;
                        changed = true;
                    }
                    break;
                case "AltBox":
                    if (double.TryParse(AltBox.Text.Trim(), out tmp))
                    {
                        if (!DoubleEquals(ac.AltFt, tmp)) { ac.AltFt = tmp; changed = true; }
                    }
                    break;
                case "HeadingBox":
                    if (double.TryParse(HeadingBox.Text.Trim(), out tmp))
                    {
                        if (!DoubleEquals(ac.HeadingDeg, tmp)) { ac.HeadingDeg = tmp; changed = true; }
                    }
                    break;
                case "SpeedBox":
                    if (double.TryParse(SpeedBox.Text.Trim(), out tmp))
                    {
                        if (!DoubleEquals(ac.SpeedKts, tmp)) { ac.SpeedKts = tmp; changed = true; }
                    }
                    break;
                case "VsBox":
                    if (double.TryParse(VsBox.Text.Trim(), out tmp))
                    {
                        if (!DoubleEquals(ac.VsFpm, tmp)) { ac.VsFpm = tmp; changed = true; }
                    }
                    break;
                case "TypeCodeBox":
                    var newType = TypeCodeBox.Text.Trim();
                    if (!string.Equals(ac.TypeCode, newType, StringComparison.Ordinal))
                    {
                        ac.TypeCode = newType;
                        changed = true;
                    }
                    break;
            }
            return changed;
        }

        // Toggle pause/unpause for the aircraft bound to the clicked item
        private void OnTogglePauseClick(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is FakeAircraft ac)
            {
                ac.IsPaused = !ac.IsPaused;
                RefreshList();
                AircraftList.SelectedItem = ac;
                StatusText.Text = ac.IsPaused ? "Paused " + ac.Callsign : "Unpaused " + ac.Callsign;
            }
        }

        private string BuildAddJson(FakeAircraft ac)
        {
            var now = UnixMs();
            var sb = new StringBuilder();
            sb.Append('{');
            sb.Append("\"type\":\"add\"");
            sb.Append(",\"t\":").Append(now);
            sb.Append(",\"callsign\":\"").Append(Escape(ac.Callsign)).Append('\"');
            sb.Append(",\"typeCode\":\"").Append(Escape(ac.TypeCode)).Append('\"');
            sb.Append(",\"lat\":").Append(ac.Lat.ToString(System.Globalization.CultureInfo.InvariantCulture));
            sb.Append(",\"lon\":").Append(ac.Lon.ToString(System.Globalization.CultureInfo.InvariantCulture));
            sb.Append(",\"alt_ft\":").Append(ac.AltFt.ToString(System.Globalization.CultureInfo.InvariantCulture));
            sb.Append(",\"vs_fpm\":").Append(ac.VsFpm.ToString(System.Globalization.CultureInfo.InvariantCulture));
            sb.Append(",\"heading_deg\":").Append(ac.HeadingDeg.ToString(System.Globalization.CultureInfo.InvariantCulture));
            sb.Append(",\"speed_kts\":").Append(ac.SpeedKts.ToString(System.Globalization.CultureInfo.InvariantCulture));
            sb.Append('}');
            sb.Append('\n');
            return sb.ToString();
        }

        private string BuildUpdateJson(FakeAircraft ac)
        {
            var now = UnixMs();
            var sb = new StringBuilder();
            sb.Append('{');
            sb.Append("\"type\":\"update\"");
            sb.Append(",\"t\":").Append(now);
            sb.Append(",\"callsign\":\"").Append(Escape(ac.Callsign)).Append('\"');
            if (!string.IsNullOrEmpty(ac.TypeCode)) sb.Append(",\"typeCode\":\"").Append(Escape(ac.TypeCode)).Append('\"');
            sb.Append(",\"lat\":").Append(ac.Lat.ToString(System.Globalization.CultureInfo.InvariantCulture));
            sb.Append(",\"lon\":").Append(ac.Lon.ToString(System.Globalization.CultureInfo.InvariantCulture));
            sb.Append(",\"alt_ft\":").Append(ac.AltFt.ToString(System.Globalization.CultureInfo.InvariantCulture));
            sb.Append(",\"vs_fpm\":").Append(ac.VsFpm.ToString(System.Globalization.CultureInfo.InvariantCulture));
            sb.Append(",\"heading_deg\":").Append(ac.HeadingDeg.ToString(System.Globalization.CultureInfo.InvariantCulture));
            sb.Append(",\"speed_kts\":").Append(ac.SpeedKts.ToString(System.Globalization.CultureInfo.InvariantCulture));
            sb.Append('}');
            sb.Append('\n');
            return sb.ToString();
        }

        private string BuildDeleteJson(FakeAircraft ac)
        {
            var now = UnixMs();
            return "{\"type\":\"delete\",\"t\":" + now + ",\"callsign\":\"" + Escape(ac.Callsign) + "\"}\n";
        }

        private void SendNdjson(string line)
        {
            try
            {
                var bytes = Encoding.UTF8.GetBytes(line);
                _udp.Send(bytes, bytes.Length, _endpoint);
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => StatusText.Text = "UDP send error: " + ex.Message);
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

        private void AircraftList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (AircraftList.SelectedItem is FakeAircraft ac)
            {
                PopulateFields(ac);
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            OnStopAutoClick(this, null);
            try { _udp?.Close(); } catch { }
            base.OnClosed(e);
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            // Warn the user that closing the simulator will delete all generated aircraft
            var res = MessageBox.Show(this,
                "Closing the simulator will send delete messages for all generated aircraft and remove them from the scope. Continue?",
                "Confirm simulator close",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (res != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }

            // Stop any auto movement and send delete messages for all generated targets
            OnStopAutoClick(this, null);
            try
            {
                // make a snapshot to avoid modification during enumeration
                var snapshot = _aircraft.ToArray();
                foreach (var ac in snapshot)
                {
                    try { SendNdjson(BuildDeleteJson(ac)); } catch { }
                }
                _aircraft.Clear();
                RefreshList();
            }
            catch { }

            base.OnClosing(e);
        }

        private class FakeAircraft
        {
            public string Callsign { get; set; }
            public double Lat { get; set; }
            public double Lon { get; set; }
            public double AltFt { get; set; }
            public double HeadingDeg { get; set; }
            public double SpeedKts { get; set; }
            public double VsFpm { get; set; }
            public string TypeCode { get; set; }
            public bool IsPaused { get; set; }
            public override string ToString() => Callsign;
        }
    }
}
