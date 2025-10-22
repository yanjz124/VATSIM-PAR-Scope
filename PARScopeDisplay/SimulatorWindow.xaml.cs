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
using System.Linq;
using System.Windows.Threading;

namespace PARScopeDisplay
{
    public partial class SimulatorWindow : Window
    {
    private readonly List<FakeAircraft> _aircraft = new List<FakeAircraft>();
        private UdpClient _udp;
        private IPEndPoint _endpoint;
        private CancellationTokenSource _cts;
    private DispatcherTimer _uiTimer;
    private MainWindow.RunwaySettings _runway;
    // Update interval in milliseconds. Default to 500ms (0.5s) for a snappier simulator refresh.
    // NOTE: motion is computed using the elapsed time between ticks (dt). That means shortening
    // the update interval increases update frequency but does NOT change movement per second —
    // the aircraft will not fly faster, only update more often. If desired this could be exposed
    // in the UI later.
    private int _updateIntervalMs = 500;

        public SimulatorWindow()
        {
            InitializeComponent();
            _endpoint = new IPEndPoint(IPAddress.Parse("127.0.0.1"), 49090);
            _udp = new UdpClient();
            RefreshList();
            this.Loaded += SimulatorWindow_Loaded;
        }

        private void SimulatorWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // ensure any existing aircraft are unpaused and auto-move starts
            UnpauseAll();
        }

        // Unpause every aircraft and ensure the auto-move background loop is running.
        private void UnpauseAll()
        {
            lock (_aircraft)
            {
                foreach (var ac in _aircraft)
                {
                    ac.IsPaused = false;
                }
            }
            RefreshList();
            StartAuto();
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

        // Return a serializable snapshot of simulator configuration for persistence
        public SimulatorConfig GetCurrentConfig()
        {
            double bearing = 360, range = 5, alt = 3000, speed = 140, heading = 180;
            int interval = _updateIntervalMs;
            try { double.TryParse(SpawnBearingBox.Text.Trim(), out bearing); } catch { }
            try { double.TryParse(SpawnRangeBox.Text.Trim(), out range); } catch { }
            try { double.TryParse(SpawnAltBox.Text.Trim(), out alt); } catch { }
            try { double.TryParse(SpawnSpeedBox.Text.Trim(), out speed); } catch { }
            try { double.TryParse(SpawnHeadingBox.Text.Trim(), out heading); } catch { }
            return new SimulatorConfig { SpawnBearing = bearing, SpawnRange = range, SpawnAlt = alt, SpawnSpeed = speed, SpawnHeading = heading, UpdateIntervalMs = interval };
        }

        // Apply saved simulator configuration into UI controls (best-effort)
        public void ApplySavedConfig(SimulatorConfig cfg)
        {
            if (cfg == null) return;
            try { SpawnBearingBox.Text = cfg.SpawnBearing.ToString(); } catch { }
            try { SpawnRangeBox.Text = cfg.SpawnRange.ToString(); } catch { }
            try { SpawnAltBox.Text = cfg.SpawnAlt.ToString(); } catch { }
            try { SpawnSpeedBox.Text = cfg.SpawnSpeed.ToString(); } catch { }
            try { SpawnHeadingBox.Text = cfg.SpawnHeading.ToString(); } catch { }
            try { _updateIntervalMs = cfg.UpdateIntervalMs > 0 ? cfg.UpdateIntervalMs : _updateIntervalMs; } catch { }
        }

        private void RefreshList()
        {
            AircraftList.ItemsSource = null;
            AircraftList.ItemsSource = _aircraft;
        }

        private void OnAddClick(object sender, RoutedEventArgs e)
        {
            // Read spawn parameters from UI
            double bearing = 360, range = 5, alt = 3000, speed = 140, heading = 180;
            try { double.TryParse(SpawnBearingBox.Text.Trim(), out bearing); } catch { }
            try { double.TryParse(SpawnRangeBox.Text.Trim(), out range); } catch { }
            try { double.TryParse(SpawnAltBox.Text.Trim(), out alt); } catch { }
            try { double.TryParse(SpawnSpeedBox.Text.Trim(), out speed); } catch { }
            try { double.TryParse(SpawnHeadingBox.Text.Trim(), out heading); } catch { }

            double lat = 51.4700, lon = -0.4543; // fallback coords
            // If a runway is selected, spawn relative to the runway's sensor position
            if (_runway != null)
            {
                double approachRad = DegToRad(_runway.HeadingTrueDeg) + Math.PI;
                // compute sensor lat/lon by moving from threshold toward runway end (negative along-approach)
                double sensorLat = _runway.ThresholdLat, sensorLon = _runway.ThresholdLon;
                if (_runway.SensorOffsetNm != 0)
                {
                    double sensorBearing = RadToDeg(approachRad);
                    double tmpLat, tmpLon;
                    DestinationFrom(_runway.ThresholdLat, _runway.ThresholdLon, sensorBearing, -_runway.SensorOffsetNm, out tmpLat, out tmpLon);
                    sensorLat = tmpLat; sensorLon = tmpLon;
                }
                // Destination from sensor along requested bearing and range
                DestinationFrom(sensorLat, sensorLon, bearing, range, out lat, out lon);
            }

            var ac = new FakeAircraft
            {
                Callsign = "FAKE" + (_aircraft.Count + 1),
                Lat = lat,
                Lon = lon,
                AltFt = alt,
                HeadingDeg = heading,
                SpeedKts = speed,
                VsFpm = 0,
                TypeCode = "B738"
            };
            // ensure newly spawned aircraft are active and will move automatically
            ac.IsPaused = false;
            _aircraft.Add(ac);
            RefreshList();
            AircraftList.SelectedItem = ac;
            PopulateFields(ac);
            // Send an 'add' message immediately so the scope receives the new target (manual send buttons were removed)
            try
            {
                SendNdjson(BuildAddJson(ac));
                StatusText.Text = "Spawned and sent add for " + ac.Callsign;
                // make sure all targets are active
                UnpauseAll();
            }
            catch { StatusText.Text = "Spawned but failed to send add"; }
        }

        private void OnRemoveClick(object sender, RoutedEventArgs e)
        {
            if (AircraftList.SelectedItem is FakeAircraft ac)
            {
                // send delete to ensure the scope removes this target, then remove locally
                try { SendNdjson(BuildDeleteJson(ac)); } catch { }
                _aircraft.Remove(ac);
                RefreshList();
                StatusText.Text = "Removed and sent delete for " + ac.Callsign;
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
            StartAuto();
        }

        // Start the background auto-move loop if not already running
        private void StartAuto()
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
                    try
                    {
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

                        // refresh UI on the dispatcher so the property pane reflects new values
                        Dispatcher.Invoke(() => RefreshList());
                    }
                    catch (Exception ex)
                    {
                        Dispatcher.Invoke(() => StatusText.Text = "Auto-move error: " + ex.Message);
                        // swallow and continue; task will keep running
                    }

                    await Task.Delay(_updateIntervalMs, token);
                }
            }, token);
            Dispatcher.Invoke(() => StatusText.Text = "Auto-moving");

            // Also set up a UI-thread timer that steps movement on the dispatcher to ensure
            // visual updates occur even if the background task is delayed or blocked.
            try
            {
                if (_uiTimer == null)
                {
                    _uiTimer = new DispatcherTimer();
                    _uiTimer.Interval = TimeSpan.FromMilliseconds(_updateIntervalMs);
                    _uiTimer.Tick += (s, e) =>
                    {
                        try { OnStepClick(this, null); } catch { }
                    };
                }
                _uiTimer.Start();
            }
            catch { }
        }

        private void OnSpawnDebugGridClick(object sender, RoutedEventArgs e)
        {
            if (_runway == null)
            {
                StatusText.Text = "No runway selected for debug grid";
                return;
            }

            double fieldElevFt = _runway.FieldElevFt;
            double approachRad = DegToRad(_runway.HeadingTrueDeg) + Math.PI;
            double crossRad = approachRad + Math.PI / 2;

            // Create a polar wedge-shaped debug grid centered on the approach bearing.
            // This fills the wedge out to 1.0 NM plus a small buffer to exercise the "outside" filter.
            // Parameters: radial step, angular step, radial buffer and altitude slices.
            double maxRadiusNm = 1.0;          // primary wedge radius
            double radialBufferNm = 0.05;      // small buffer beyond 1.0 NM to test outside filtering
            double radialStepNm = 0.05;        // radial spacing
            double angleStepDeg = 5.0;         // angular spacing in degrees
            double bufferDeg = 2.5;            // angular buffer outside max azimuth to test edge cases

            // compute wedge center and half-angle based on runway settings (MaxAzimuthDeg is total cone)
            double configuredTotalAz = (_runway.MaxAzimuthDeg > 0) ? _runway.MaxAzimuthDeg : 10.0;
            double wedgeHalfDeg = configuredTotalAz / 2.0;
            wedgeHalfDeg += bufferDeg; // add a small angular buffer

            int count = 0;
            // compute sensor lat/lon based on runway threshold and sensor offset (along runway heading)
            double sensorLat = _runway.ThresholdLat;
            double sensorLon = _runway.ThresholdLon;
            if (_runway.SensorOffsetNm != 0)
            {
                // Sensor offset sign: move from threshold toward the runway end (opposite the approach bearing).
                // Historically the offset was applied the other way; invert here so a positive SensorOffsetNm
                // places the sensor toward the runway end (i.e. negative along-approach distance).
                double sensorBearing = RadToDeg(approachRad); // along approach
                double tmpLat, tmpLon;
                DestinationFrom(_runway.ThresholdLat, _runway.ThresholdLon, sensorBearing, -_runway.SensorOffsetNm, out tmpLat, out tmpLon);
                sensorLat = tmpLat;
                sensorLon = tmpLon;
            }

            // alt slices from field elevation up to +800 ft every 100 ft
            // iterate radially from just behind the sensor (negative small buffer) out to 1.0 DME so we cover
            // the range "-0.05nm to 1.0nm from runway end to approach end" as requested.
            for (double r = -radialBufferNm; r <= maxRadiusNm + 1e-9; r += radialStepNm)
            {
                // iterate angles from -wedgeHalfDeg..+wedgeHalfDeg around the approachRad
                for (double aDeg = -wedgeHalfDeg; aDeg <= wedgeHalfDeg + 1e-9; aDeg += angleStepDeg)
                {
                    double bearingDeg = RadToDeg(approachRad) + aDeg;
                    double latP, lonP;
                    // origin is now the sensor position (not the runway threshold)
                    DestinationFrom(sensorLat, sensorLon, bearingDeg, r, out latP, out lonP);

                    // For lateral wedge points keep them at field elevation (vertical variation handled below)
                    {
                        var ac = new FakeAircraft
                        {
                            Callsign = $"D{count:D4}",
                            Lat = latP,
                            Lon = lonP,
                            AltFt = fieldElevFt,
                            HeadingDeg = (_runway.HeadingTrueDeg + 180) % 360,
                            SpeedKts = 0, // debug grid should be stationary
                            VsFpm = 0,
                            TypeCode = "B738",
                            IsPaused = false // unpaused so they will be included in auto-loop (speed 0 keeps them stationary)
                        };
                        _aircraft.Add(ac);
                        SendNdjson(BuildAddJson(ac));
                        count++;
                    }
                }
            }

            RefreshList();
            StatusText.Text = $"Spawned {count} debug aircraft (wedge up to {maxRadiusNm} NM + buffer)";
            // ensure debug-grid targets are unpaused (but speed 0 keeps them stationary)
            UnpauseAll();

            // --- Vertical projection set along final from the sensor ---
            // Generate points along the approach centerline (aDeg = 0) out to 1.0 NM
            // for projection angles from 0..10 degrees so we can test vertical filter edges.
            double projMaxDeg = 10.0;
            double projDegStep = 1.0;
            double projRadialStep = radialStepNm; // keep same radial step
            double bearingFinalDeg = RadToDeg(approachRad); // approach bearing

            for (double ang = 0.0; ang <= projMaxDeg + 1e-9; ang += projDegStep)
            {
                for (double r2 = projRadialStep; r2 <= maxRadiusNm + 1e-9; r2 += projRadialStep)
                {
                    double latV, lonV;
                    DestinationFrom(sensorLat, sensorLon, bearingFinalDeg, r2, out latV, out lonV);
                    // distance in feet
                    double distFt = r2 * 6076.12;
                    double altProjected = fieldElevFt + Math.Tan(DegToRad(ang)) * distFt;
                    {
                        var acv = new FakeAircraft
                        {
                            Callsign = $"V{count:D4}",
                            Lat = latV,
                            Lon = lonV,
                            AltFt = altProjected,
                            HeadingDeg = (_runway.HeadingTrueDeg + 180) % 360,
                            SpeedKts = 0, // vertical projection stays stationary
                            VsFpm = 0,
                            TypeCode = "B738",
                            IsPaused = false
                        };
                    _aircraft.Add(acv);
                    SendNdjson(BuildAddJson(acv));
                    count++;
                    }
                }
            }

            RefreshList();
            StatusText.Text = $"Spawned {count} debug aircraft (wedge + vertical projections)";
            UnpauseAll();

        }

        private void OnDeleteAllClick(object sender, RoutedEventArgs e)
        {
            var snapshot = _aircraft.ToArray();
            foreach (var ac in snapshot)
            {
                SendNdjson(BuildDeleteJson(ac));
            }
            _aircraft.Clear();
            RefreshList();
            StatusText.Text = "Deleted all aircraft";
            // no active aircraft remain; stop auto loop
            StopAuto();
        }
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
            StopAuto();
        }

        private void StopAuto()
        {
            if (_cts != null)
            {
                _cts.Cancel();
                _cts = null;
                Dispatcher.Invoke(() => StatusText.Text = "Stopped");
            }
            try
            {
                if (_uiTimer != null)
                {
                    _uiTimer.Stop();
                }
            }
            catch { }
        }

        // Advance one movement tick for all unpaused aircraft and send updates.
        private void OnStepClick(object sender, RoutedEventArgs e)
        {
            double dt = _updateIntervalMs / 1000.0; // seconds
            lock (_aircraft)
            {
                foreach (var ac in _aircraft)
                {
                    if (ac.IsPaused) continue;
                    double nmPerSec = ac.SpeedKts / 3600.0;
                    double meters = nmPerSec * 1852.0 * dt;
                    double dLat = (meters * Math.Cos(ac.HeadingDeg * Math.PI / 180.0)) / 111319.9;
                    double dLon = (meters * Math.Sin(ac.HeadingDeg * Math.PI / 180.0)) / (111319.9 * Math.Cos(ac.Lat * Math.PI / 180.0));
                    ac.Lat += dLat;
                    ac.Lon += dLon;
                    ac.AltFt += (ac.VsFpm / 60.0) * dt;
                    SendNdjson(BuildUpdateJson(ac));
                }
            }
            RefreshList();
            StatusText.Text = "Stepped movement by " + dt + "s";
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

                // If any aircraft are unpaused, ensure the auto-move loop is running so they move.
                // If all aircraft are paused, stop the auto-move loop to save CPU/network.
                bool anyUnpaused = _aircraft.Any(a => !a.IsPaused);
                if (anyUnpaused)
                {
                    // do a single step immediately so the UI shows movement right away
                    try { OnStepClick(this, null); } catch { }
                    StartAuto();
                    // keep the toggle UI consistent if present
                    try { if (AutoMoveToggle != null) AutoMoveToggle.IsChecked = true; } catch { }
                }
                else
                {
                    StopAuto();
                    try { if (AutoMoveToggle != null) AutoMoveToggle.IsChecked = false; } catch { }
                }
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
            // If there are no generated aircraft, close without prompting
            if (_aircraft == null || _aircraft.Count == 0)
            {
                base.OnClosing(e);
                return;
            }

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
