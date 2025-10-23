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
    private readonly System.Collections.ObjectModel.ObservableCollection<FakeAircraft> _aircraft = new System.Collections.ObjectModel.ObservableCollection<FakeAircraft>();
        private UdpClient _udp;
        private IPEndPoint _endpoint;
        private CancellationTokenSource _cts;
    private DispatcherTimer _uiTimer;
    private MainWindow.RunwaySettings _runway;
    private FakeAircraft _lastSelectedAircraft;
    // Track callsigns spawned by this simulator instance so we can ensure they are deleted on close
    private readonly HashSet<string> _spawnedCallsigns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
            // Note: UI semantics: Spawn bearing is TRUE (users enter true bearing).
            // Spawn heading is MAGNETIC (users enter magnetic heading which we convert
            // to true when creating/updating aircraft). Persist values exactly as
            // presented in the UI so saved configs match user expectations.
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
            // Avoid resetting the ItemsSource repeatedly (which breaks selection).
            // The ObservableCollection will notify the UI when items or properties change.
            try
            {
                if (AircraftList.ItemsSource == null)
                {
                    AircraftList.ItemsSource = _aircraft;
                }
            }
            catch { }
        }

        private void OnAddClick(object sender, RoutedEventArgs e)
        {
            // Read spawn parameters from UI
            // UI semantics: Spawn bearing is TRUE, Spawn heading is MAGNETIC.
            // Read bearing as true directly; convert heading from magnetic->true below.
            double bearingTrue = 360, range = 5, alt = 3000, speed = 140, headingMag = 180;
            try { double.TryParse(SpawnBearingBox.Text.Trim(), out bearingTrue); } catch { }
            try { double.TryParse(SpawnRangeBox.Text.Trim(), out range); } catch { }
            try { double.TryParse(SpawnAltBox.Text.Trim(), out alt); } catch { }
            try { double.TryParse(SpawnSpeedBox.Text.Trim(), out speed); } catch { }
            try { double.TryParse(SpawnHeadingBox.Text.Trim(), out headingMag); } catch { }

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
                // User-entered bearing is TRUE already, so use it directly
                DestinationFrom(sensorLat, sensorLon, bearingTrue, range, out lat, out lon);
            }

            var ac = new FakeAircraft
            {
                Callsign = (TryGetSpawnCallsign(out var sc) && !string.IsNullOrWhiteSpace(sc)) ? sc : "FAKE" + (_aircraft.Count + 1),
                Lat = lat,
                Lon = lon,
                AltFt = alt,
                // Convert magnetic heading to true heading for internal representation
                HeadingDeg = NormalizeAngle(((_runway != null) ? (headingMag - (_runway?.MagVariationDeg ?? 0.0)) : headingMag)),
                SpeedKts = speed,
                VsFpm = 0,
                TypeCode = "B738"
            };
            // ensure newly spawned aircraft are active and will move automatically
            ac.IsPaused = false;
            _aircraft.Add(ac);
            try { _spawnedCallsigns.Add(ac.Callsign); } catch { }
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
                // Use a fixed dt equal to the configured update interval. This guarantees
                // that movement per tick is consistent (e.g. for 500ms updates, per-tick
                // movement is speed_kts/7200 NM), and prevents large movement spikes if
                // the background task experiences scheduling delays.
                var fixedDt = _updateIntervalMs / 1000.0;
                while (!token.IsCancellationRequested)
                {
                    var dt = fixedDt;
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
                        Dispatcher.Invoke(() => {
                            try { StatusText.Text = "Auto-move error: " + ex.Message; } catch { }
                        });
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
                    // Only refresh the UI on the dispatcher timer. Movement and updates
                    // are handled by the background auto-loop (Task.Run). Calling
                    // OnStepClick here would apply movement twice (once in background
                    // loop and once on UI thread) which causes targets to move too fast.
                    _uiTimer.Tick += (s, e) =>
                    {
                        try { RefreshList(); } catch { }
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
            var newDebugAircraft = new List<FakeAircraft>();
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
                        newDebugAircraft.Add(ac);
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
                        newDebugAircraft.Add(acv);
                        count++;
                    }
                }
            }

            // Add all new debug aircraft to the shared collection under a lock, then send add messages
            try
            {
                if (newDebugAircraft.Count > 0)
                {
                    lock (_aircraft)
                    {
                        foreach (var nac in newDebugAircraft)
                        {
                            _aircraft.Add(nac);
                            try { _spawnedCallsigns.Add(nac.Callsign); } catch { }
                        }
                    }

                    // Send add messages outside the lock to avoid blocking the simulation loop on network I/O
                    foreach (var nac in newDebugAircraft)
                    {
                        try { SendNdjson(BuildAddJson(nac)); } catch { }
                    }
                }
            }
            catch { }

            RefreshList();
            StatusText.Text = $"Spawned {count} debug aircraft (wedge + vertical projections)";
            UnpauseAll();

        }

        private void OnDeleteAllClick(object sender, RoutedEventArgs e)
        {
            // Stop the auto-update loop first so it doesn't send updates that re-create targets on the scope.
            StopAuto();
            // Snapshot and clear under lock to avoid races with other threads.
            FakeAircraft[] snapshot;
            lock (_aircraft)
            {
                snapshot = _aircraft.ToArray();
                _aircraft.Clear();
            }

            // Collect callsigns and send batched delete messages to avoid oversized UDP payloads
            try
            {
                var calls = snapshot.Select(a => a.Callsign).Where(s => !string.IsNullOrEmpty(s)).ToArray();
                SendBatchedDeletes(calls);
            }
            catch
            {
                // Fallback to per-aircraft deletes if batching fails
                foreach (var ac in snapshot) { try { SendNdjson(BuildDeleteJson(ac)); } catch { } }
            }

            // Clear spawned callsigns tracking as we've deleted all
            try { _spawnedCallsigns.Clear(); } catch { }

            RefreshList();
            StatusText.Text = "Deleted all aircraft";
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
                try { _spawnedCallsigns.Remove(ac.Callsign); } catch { }
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

        // Normalize angle into [0,360)
        private static double NormalizeAngle(double deg)
        {
            double r = deg % 360.0;
            if (double.IsNaN(r) || double.IsInfinity(r)) return 0.0;
            if (r < 0) r += 360.0;
            // handle rounding tiny negatives
            if (r >= 360.0) r -= 360.0;
            return r;
        }

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
        private void OnHeadingInc(object sender, RoutedEventArgs e)
        {
            ChangeSelectedAc(ac =>
            {
                double magVar = _runway?.MagVariationDeg ?? 0.0;
                // convert stored true heading to magnetic for UI math
                double headingMag = NormalizeAngle(ac.HeadingDeg + magVar);
                headingMag = NormalizeAngle(headingMag + 5.0);
                // convert back to true for storage
                ac.HeadingDeg = NormalizeAngle(headingMag - magVar);
            });
        }

        private void OnHeadingDec(object sender, RoutedEventArgs e)
        {
            ChangeSelectedAc(ac =>
            {
                double magVar = _runway?.MagVariationDeg ?? 0.0;
                double headingMag = NormalizeAngle(ac.HeadingDeg + magVar);
                headingMag = NormalizeAngle(headingMag - 5.0);
                ac.HeadingDeg = NormalizeAngle(headingMag - magVar);
            });
        }
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
                    _lastSelectedAircraft = selected;
                    SendNdjson(BuildUpdateJson(selected));
                    StatusText.Text = "Auto-updated " + selected.Callsign;
                }
            }
        }

        private void PopulateFields(FakeAircraft ac)
        {
            CallsignBox.Text = ac.Callsign;
            AltBox.Text = ac.AltFt.ToString("F0");
            // Show heading to the user in magnetic (UI is magnetic)
            double magVar = _runway?.MagVariationDeg ?? 0.0;
            double headingMag = NormalizeAngle(ac.HeadingDeg + magVar);
            HeadingBox.Text = headingMag.ToString("F1");
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
                // Interpret entered heading as magnetic; convert to true for storage
                double magVar = _runway?.MagVariationDeg ?? 0.0;
                double trueHeading = NormalizeAngle(tmp - magVar);
                if (!DoubleEquals(ac.HeadingDeg, trueHeading)) { ac.HeadingDeg = trueHeading; changed = true; }
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
                            _lastSelectedAircraft = ac;
                            SendNdjson(BuildUpdateJson(ac));
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
                        // Magnetic input -> convert to true
                        double magVar = _runway?.MagVariationDeg ?? 0.0;
                        double trueHeading = NormalizeAngle(tmp - magVar);
                        if (!DoubleEquals(ac.HeadingDeg, trueHeading)) { ac.HeadingDeg = trueHeading; changed = true; }
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
                }
                else
                {
                    StopAuto();
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

        // Send a batched delete message split into payloads that keep UDP packet sizes reasonable.
        private void SendBatchedDeletes(string[] callsigns)
        {
            if (callsigns == null || callsigns.Length == 0) return;

            // conservative payload cap to avoid fragmentation (bytes)
            const int payloadCap = 1000;
            var sb = new StringBuilder();
            var batch = new List<string>();

            Action flush = () =>
            {
                if (batch.Count == 0) return;
                try
                {
                    var sbb = new StringBuilder();
                    sbb.Append('{'); sbb.Append("\"type\":\"delete_simulator\""); sbb.Append(','); sbb.Append("\"t\":").Append(UnixMs()); sbb.Append(','); sbb.Append("\"callsigns\":[");
                    for (int i = 0; i < batch.Count; i++) { if (i > 0) sbb.Append(','); sbb.Append('"').Append(Escape(batch[i])).Append('"'); }
                    sbb.Append(']'); sbb.Append('}'); sbb.Append('\n');
                    var payload = sbb.ToString();
                    var bytes = Encoding.UTF8.GetBytes(payload);
                    // send a few times
                    for (int attempt = 0; attempt < 4; attempt++) { try { _udp.Send(bytes, bytes.Length, _endpoint); } catch { } try { Thread.Sleep(20); } catch { } }
                }
                catch { }
                batch.Clear();
            };

            foreach (var cs in callsigns)
            {
                if (string.IsNullOrEmpty(cs)) continue;
                // estimate size if we add this callsign
                int estSize = sb.Length + cs.Length + 8; // rough
                if (estSize > payloadCap && batch.Count > 0)
                {
                    flush(); sb.Clear();
                }
                batch.Add(cs);
                sb.Append(cs);
            }
            flush();
        }

        private static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        // Safe helper to read the spawn callsign textbox if present in the XAML
        private bool TryGetSpawnCallsign(out string callsign)
        {
            callsign = null;
            try
            {
                if (this.Dispatcher == null) return false;
                string txt = null;
                this.Dispatcher.Invoke(() => { try { txt = (SpawnCallsignBox != null) ? SpawnCallsignBox.Text.Trim() : null; } catch { txt = null; } });
                callsign = txt;
                return true;
            }
            catch { callsign = null; return false; }
        }

        private static long UnixMs()
        {
            var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            return (long)(DateTime.UtcNow - epoch).TotalMilliseconds;
        }

        private void AircraftList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (AircraftList.SelectedItem is FakeAircraft ac && ac != _lastSelectedAircraft)
            {
                _lastSelectedAircraft = ac;
                PopulateFields(ac);
            }
        }

        // Ensure clicking anywhere in the item selects the row (including clicks on buttons)
        private void ListBoxItem_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            try
            {
                var lbi = sender as System.Windows.Controls.ListBoxItem;
                if (lbi != null && !lbi.IsSelected)
                {
                    lbi.IsSelected = true;
                    lbi.Focus();
                    e.Handled = true;
                }
            }
            catch { }
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
                // make a snapshot of callsigns to delete
                var snapshot = _aircraft.ToArray();
                var calls = snapshot.Select(a => a.Callsign).Where(s => !string.IsNullOrEmpty(s)).ToArray();

                // If we have many callsigns, send a single aggregated delete message that MainWindow will specially handle.
                if (calls.Length > 0)
                {
                    try
                    {
                        // build a compact JSON with type "delete_simulator" and the callsigns array
                        var sb = new StringBuilder();
                        sb.Append('{'); sb.Append("\"type\":\"delete_simulator\""); sb.Append(','); sb.Append("\"t\":").Append(UnixMs()); sb.Append(','); sb.Append("\"callsigns\":[");
                        for (int i = 0; i < calls.Length; i++) { if (i > 0) sb.Append(','); sb.Append('"').Append(Escape(calls[i])).Append('"'); }
                        sb.Append(']'); sb.Append('}'); sb.Append('\n');
                        var payload = sb.ToString();

                        // send multiple times to increase chance of delivery
                        for (int attempt = 0; attempt < 6; attempt++)
                        {
                            try { SendNdjson(payload); } catch { }
                            try { System.Threading.Thread.Sleep(40); } catch { }
                        }
                    }
                    catch { }
                }

                // As a fallback, also send individual delete messages a few times
                foreach (var ac in snapshot)
                {
                    for (int attempt = 0; attempt < 3; attempt++)
                    {
                        try { SendNdjson(BuildDeleteJson(ac)); } catch { }
                        try { System.Threading.Thread.Sleep(20); } catch { }
                    }
                }

                // Clear local list and spawned tracking
                lock (_aircraft)
                {
                    _aircraft.Clear();
                }
                try { _spawnedCallsigns.Clear(); } catch { }

                RefreshList();
            }
            catch { }

            base.OnClosing(e);
        }

        private class FakeAircraft : System.ComponentModel.INotifyPropertyChanged
        {
            private string _callsign;
            private double _lat;
            private double _lon;
            private double _altFt;
            private double _headingDeg;
            private double _speedKts;
            private double _vsFpm;
            private string _typeCode;
            private bool _isPaused;

            public string Callsign
            {
                get => _callsign;
                set { _callsign = value; OnPropertyChanged(nameof(Callsign)); }
            }

            public double Lat
            {
                get => _lat;
                set { _lat = value; OnPropertyChanged(nameof(Lat)); }
            }

            public double Lon
            {
                get => _lon;
                set { _lon = value; OnPropertyChanged(nameof(Lon)); }
            }

            public double AltFt
            {
                get => _altFt;
                set { _altFt = value; OnPropertyChanged(nameof(AltFt)); }
            }

            public double HeadingDeg
            {
                get => _headingDeg;
                set { _headingDeg = value; OnPropertyChanged(nameof(HeadingDeg)); }
            }

            public double SpeedKts
            {
                get => _speedKts;
                set { _speedKts = value; OnPropertyChanged(nameof(SpeedKts)); }
            }

            public double VsFpm
            {
                get => _vsFpm;
                set { _vsFpm = value; OnPropertyChanged(nameof(VsFpm)); }
            }

            public string TypeCode
            {
                get => _typeCode;
                set { _typeCode = value; OnPropertyChanged(nameof(TypeCode)); }
            }

            public bool IsPaused
            {
                get => _isPaused;
                set { _isPaused = value; OnPropertyChanged(nameof(IsPaused)); }
            }

            public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;

            protected void OnPropertyChanged(string propertyName)
            {
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));
            }

            public override string ToString() => Callsign;
        }
    }
}
