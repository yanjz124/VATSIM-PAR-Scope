using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Linq;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Windows.Controls;
using System.Diagnostics;
using System.Timers;

namespace PARScopeDisplay
{
    // METAR data source
    public enum MetarSource
    {
        NOAA,    // aviationweather.gov
        VATSIM   // metar.vatsim.net
    }

    // Radar sweep snapshot: captures all targets at a specific moment in time
    public class RadarSweep
    {
        public DateTime SweepTime;
        public List<HistoryDot> Dots = new List<HistoryDot>();
    }

    // Individual history dot: geographic position independent of callsign
    public class HistoryDot
    {
        public double Lat;
        public double Lon;
        public double AltFt;
        public string Callsign; // kept for debugging/labeling
        // Flags indicating which scopes saw this dot at capture time
        public bool SeenVertical;
        public bool SeenAzimuth;
        public bool SeenPlan;
    }

    public partial class MainWindow : Window
    {
        private UdpClient _udpClient;
        private bool _listening;
        private readonly ConcurrentDictionary<string, Dictionary<string, object>> _aircraft = new ConcurrentDictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
        private DateTime _lastEvent = DateTime.MinValue;
        private DispatcherTimer _uiTimer;
        private readonly JavaScriptSerializer _json = new JavaScriptSerializer() { MaxJsonLength = int.MaxValue };
        private RunwaySettings _runway = null;
        private NASRDataLoader _nasrLoader = null;
            // Watcher to auto-reload NASR cache when nasr_cache.json changes on disk
            private FileSystemWatcher _nasrWatcher = null;
            // Debounce timer to coalesce rapid file-change events
            private System.Timers.Timer _nasrReloadTimer = null;
        
        // Radar sweep history system: independent of per-callsign tracking
        // Each sweep is a snapshot of all targets at that moment (lat/lon/alt)
        private readonly List<RadarSweep> _radarSweeps = new List<RadarSweep>();
        private const double HistoryLifetimeSec = 30.0; // seconds before history dots expire
        private bool _hideGroundTraffic = false;
        private int _historyDotsCount = 5; // Number of history sweeps to display (user configurable)
        private bool _showVerticalDevLines = true;
        private bool _showAzimuthDevLines = true;
        private bool _showApproachLights = true;
    // Datablock display toggles for each scope (vertical/azimuth/plan)
    private bool _showVerticalDatablocks = true;
    private bool _showAzimuthDatablocks = true;
    private bool _showPlanDatablocks = true;
        // Debug wedge filtering toggles: when false, scopes will not filter by wedge (show everything)
        private bool _enableVerticalWedgeFilter = true;
        private bool _enableAzimuthWedgeFilter = true;
        private int _planAltTopHundreds = 999; // e.g. 100 -> 10000 ft top threshold
        private readonly SolidColorBrush _centerlineBrush = new SolidColorBrush(Color.FromArgb(160, 60, 120, 60)); // semi-transparent darker green
        // Debug/UI flags referenced by XAML event handlers
        // Radar scan interval (seconds) - exposed in UI via ScanIntervalBox (0.5 to 10.0 sec)
        private double _radarScanIntervalSec = 1.0;
        private DispatcherTimer _radarTimer;

        // METAR functionality
        private MetarSource _metarSource = MetarSource.NOAA;
        private bool _metarShowFull = true; // true = full METAR, false = abbreviated
        private string _currentMetar = string.Empty;
        private System.Timers.Timer _metarRefreshTimer;
        private const double MetarRefreshIntervalMs = 5 * 60 * 1000; // 5 minutes

    // Snapshot used for display: populated at each radar sweep so the on-screen
    // "current" targets move only when a sweep occurs (simulating radar scan).
    // This decouples visual update rate from incoming NDJSON update frequency.
    private readonly object _snapshotLock = new object();
    private Dictionary<string, Dictionary<string, object>> _displaySnapshot = new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastSweepTime = DateTime.MinValue;

    // Single-file application state filename
    private const string AppStateFileName = "app_state.json";

        public MainWindow()
        {
            InitializeComponent();
            
            // Initialize UI sliders
            if (HistoryDotsSlider != null)
            {
                HistoryDotsSlider.Value = _historyDotsCount;
                if (HistoryDotsLabel != null) HistoryDotsLabel.Text = _historyDotsCount.ToString();
            }
            if (ScanIntervalSlider != null)
            {
                ScanIntervalSlider.Value = _radarScanIntervalSec;
                if (ScanIntervalLabel != null) ScanIntervalLabel.Text = _radarScanIntervalSec.ToString("F1") + "s";
            }
            
            // Load application state (single file) if present. This will restore
            // runway, UI settings and optionally the NASR cache. We must restore
            // NASR cache file BEFORE constructing NASRDataLoader so it can pick it up.
            try { LoadAppState(); } catch { }

            // Initialize NASR loader and try to load cached data (will read nasr_cache.json)
            _nasrLoader = new NASRDataLoader();
            try
            {
                // Ensure loader picks up any existing on-disk cache produced by LoadAppState
                _nasrLoader.EnsureCacheLoaded();
            }
            catch { }
            // Backfill any loaded runway settings with NASR data if available
            try { BackfillRunwayFromNasr(); } catch { }

            // Set up NASR cache watcher to auto-reload when nasr_cache.json is updated externally
            try
            {
                string appDataPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VATSIM-PAR-Scope");
                string cachePath = System.IO.Path.Combine(appDataPath, "nasr_cache.json");
                var dir = System.IO.Path.GetDirectoryName(cachePath);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    _nasrWatcher = new FileSystemWatcher(dir, "nasr_cache.json");
                    _nasrWatcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName;
                    _nasrWatcher.Changed += NasrWatcher_Changed;
                    _nasrWatcher.Created += NasrWatcher_Changed;
                    _nasrWatcher.Renamed += NasrWatcher_Renamed;
                    _nasrWatcher.EnableRaisingEvents = true;

                    // debounce timer - 500ms default
                    _nasrReloadTimer = new System.Timers.Timer(500) { AutoReset = false };
                    _nasrReloadTimer.Elapsed += (s, e) => Dispatcher.BeginInvoke(new Action(() => {
                        try { if (_nasrLoader != null) _nasrLoader.EnsureCacheLoaded(); } catch { }
                        try { UpdateNasrStatus(); } catch { }
                    }));
                }
            }
            catch { }
            // Update NASR status text in the UI
            try { UpdateNasrStatus(); } catch { }
            // Also refresh once the window is fully loaded to ensure UI is ready
            try { this.Loaded += (s, e) => { try { UpdateNasrStatus(); } catch { } this.Activate(); }; } catch { }

            // Ensure UI config boxes reflect restored runway and settings
            UpdateConfigBoxes();
            // Initialize view toggles from UI checkboxes (Display menu)
            OnViewToggleChanged(null, null);

            // Keyboard hooks: allow PageUp/PageDown to adjust Range
            this.PreviewKeyDown += MainWindow_PreviewKeyDown;
            
            StartUdpListener();

            _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _uiTimer.Tick += (s, e) => UpdateUi();
            _uiTimer.Start();

            // Start radar sampling timer (creates geographic snapshots at configured scan rate)
            _radarTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(_radarScanIntervalSec) };
            _radarTimer.Tick += (s, e) => SampleRadar();
            _radarTimer.Start();
            
            // Initialize METAR auto-refresh timer (will be started when runway is selected)
            _metarRefreshTimer = new System.Timers.Timer(MetarRefreshIntervalMs);
            _metarRefreshTimer.Elapsed += (s, e) => Dispatcher.Invoke(() => LoadMetar());
            _metarRefreshTimer.AutoReset = true;
            
            // Ensure we capture an initial sweep/snapshot when the UI is ready and a runway is selected
            this.Loaded += (s, e) => { 
                try { 
                    if (_runway != null) 
                    {
                        SampleRadar();
                        // Load METAR on startup if runway is already selected
                        LoadMetar();
                        StartMetarRefreshTimer();
                    }
                } catch { } 
            };

            this.Closed += (s, e) =>
            {
                try { SaveAppState(); } catch { }
            };
        }

        // If a runway was restored from saved settings but missing geolocation
        // information, try to backfill it from the NASR loader so the plan view
        // can render correctly. This uses the stored FaaLid or Icao to find data.
        private void BackfillRunwayFromNasr()
        {
            try
            {
                if (_runway == null || _nasrLoader == null) return;
                // Prefer the stored FaaLid for NASR lookup since keys are FAA LIDs, otherwise fallback to ICAO
                string key = !string.IsNullOrEmpty(_runway.FaaLid) ? _runway.FaaLid : _runway.Icao;
                if (string.IsNullOrEmpty(key)) return;
                var r = _nasrLoader.GetRunway(key, _runway.Runway);
                if (r == null) return;
                // Only overwrite if values look empty/zero
                if (_runway.ThresholdLat == 0) _runway.ThresholdLat = r.Latitude;
                if (_runway.ThresholdLon == 0) _runway.ThresholdLon = r.Longitude;
                if (_runway.HeadingTrueDeg == 0) _runway.HeadingTrueDeg = r.TrueHeading;
                if (_runway.ThrCrossingHgtFt == 0) _runway.ThrCrossingHgtFt = r.ThrCrossingHgtFt;
                if (_runway.FieldElevFt == 0) _runway.FieldElevFt = r.FieldElevationFt;
                // If NASR provided an ICAO ID (e.g., KXXX) use it for future lookups and display
                try
                {
                    if (!string.IsNullOrEmpty(r.IcaoId) && !r.IcaoId.Equals(_runway.Icao, StringComparison.OrdinalIgnoreCase))
                    {
                        _runway.Icao = r.IcaoId.ToUpperInvariant();
                    }
                    // Ensure FaaLid is set for display (strip leading 'K' for US ICAO codes)
                    if (string.IsNullOrEmpty(_runway.FaaLid) && !string.IsNullOrEmpty(r.AirportId))
                    {
                        var aid = r.AirportId.ToUpperInvariant();
                        if (aid.Length == 4 && aid.StartsWith("K")) _runway.FaaLid = aid.Substring(1);
                        else _runway.FaaLid = aid;
                    }
                }
                catch { }

                // Persist any corrections so subsequent startups will have concrete geometry/ids
                try { SaveRunwaySettings(_runway); } catch { }
            }
            catch { }
        }

        // Allow external callers (simulator or UI) to trigger a radar sweep immediately.
        // This will capture history dots and update the display snapshot used for rendering.
        public void TriggerSweepNow()
        {
            try { SampleRadar(); } catch { }
        }

        private void StartUdpListener()
        {
            _listening = true;
            Task.Run(() =>
            {
                try
                {
                    _udpClient = new UdpClient(49090);
                    var ep = new IPEndPoint(IPAddress.Any, 49090);
                    while (_listening)
                    {
                        var data = _udpClient.Receive(ref ep);
                        var text = Encoding.UTF8.GetString(data);
                        var lines = text.Split('\n');
                        foreach (var raw in lines)
                        {
                            var line = raw.Trim();
                            if (line.Length == 0) continue;
                            try { ProcessNdjson(line); } catch { /* ignore bad line */ }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() =>
                    {
                        StatusText.Text = "Error: " + ex.Message;
                        StatusText.Foreground = Brushes.OrangeRed;
                    });
                }
            });
        }

        private void ProcessNdjson(string line)
        {
            var obj = _json.Deserialize<Dictionary<string, object>>(line);
            if (obj == null || !obj.ContainsKey("type")) return;
            var type = (obj["type"] ?? "").ToString();

            _lastEvent = DateTime.UtcNow;
            Dispatcher.Invoke(() =>
            {
                StatusText.Text = "Connected";
                StatusText.Foreground = Brushes.Green;
                LastEventText.Text = _lastEvent.ToString("HH:mm:ss") + "Z";
            });

            if (type == "add" || type == "update")
            {
                string callsign = obj.ContainsKey("callsign") && obj["callsign"] != null ? obj["callsign"].ToString() : null;
                if (!string.IsNullOrEmpty(callsign))
                {
                    _aircraft[callsign] = obj;
                }
            }
            else if (type == "delete")
            {
                string callsign = obj.ContainsKey("callsign") && obj["callsign"] != null ? obj["callsign"].ToString() : null;
                if (!string.IsNullOrEmpty(callsign))
                {
                    // Remove aircraft from live data source
                    // Radar sweeps will naturally stop capturing this target
                    Dictionary<string, object> removed;
                    _aircraft.TryRemove(callsign, out removed);
                }
            }
            else if (type == "network_disconnected" || type == "session_ended")
            {
                _aircraft.Clear();
                lock (_radarSweeps)
                {
                    _radarSweeps.Clear();
                }
                Dispatcher.Invoke(() =>
                {
                    StatusText.Text = "Disconnected";
                    StatusText.Foreground = Brushes.Red;
                });
            }
        }

        private void UpdateUi()
        {
            // Clear and redraw canvases
            VerticalScopeCanvas.Children.Clear();
            AzimuthScopeCanvas.Children.Clear();
            PlanViewCanvas.Children.Clear();

            // Update runway display
            if (_runway != null)
            {
                // Display FAA LID for user, but ICAO_ID is used for METAR only
                string displayCode = !string.IsNullOrEmpty(_runway.FaaLid) ? _runway.FaaLid : _runway.Icao;
                RunwayText.Text = displayCode + " " + _runway.Runway;
            }
            else
            {
                RunwayText.Text = "(not set)";
            }

            // Empty scope background per PAR layout
            DrawVerticalEmpty(VerticalScopeCanvas);
            DrawAzimuthEmpty(AzimuthScopeCanvas);
            DrawPlanEmpty(PlanViewCanvas);

            var now = DateTime.UtcNow;

            // Debug text showing aircraft count and sweep count
            var sb = new StringBuilder();
            sb.AppendLine($"=== Traffic Data ({now:HH:mm:ss}) ===");
            sb.AppendLine($"Total Aircraft: {_aircraft.Count}");
            lock (_radarSweeps)
            {
                sb.AppendLine($"Radar Sweeps: {_radarSweeps.Count}");
                int totalDots = 0;
                foreach (var sweep in _radarSweeps) totalDots += sweep.Dots.Count;
                sb.AppendLine($"Total History Dots: {totalDots}");
            }
            sb.AppendLine();
            
            // Table header with fixed-width columns
            sb.AppendLine("Callsign  Latitude    Longitude    Altitude   Speed");
            sb.AppendLine("--------  ----------  -----------  ---------  -----");
            
            // Use the last radar sweep snapshot for display so the on-screen targets move at
            // the configured radar scan interval. Fall back to live data if snapshot is empty.
            Dictionary<string, Dictionary<string, object>> displaySnapLocal = null;
            lock (_snapshotLock) { displaySnapLocal = _displaySnapshot != null && _displaySnapshot.Count > 0 ? new Dictionary<string, Dictionary<string, object>>(_displaySnapshot, StringComparer.OrdinalIgnoreCase) : null; }

            if (displaySnapLocal != null)
            {
                sb.AppendLine("Using radar sweep snapshot for display");
                sb.AppendLine($"Last sweep: {_lastSweepTime:HH:mm:ss}Z");
                foreach (var kvp in displaySnapLocal)
                {
                    var ac = kvp.Value;
                    var callsign = ac.ContainsKey("callsign") && ac["callsign"] != null ? ac["callsign"].ToString() : "";
                    double lat = GetDouble(ac, "lat", 0);
                    double lon = GetDouble(ac, "lon", 0);
                    double alt = GetDouble(ac, "alt_ft", 0);
                    double gs = GetGroundSpeedKts(ac);
                    string row = string.Format("{0,-9} {1,10:F4}  {2,11:F4}  {3,8:F0}ft  {4,4:F0}kt",
                        callsign, lat, lon, alt, gs);
                    sb.AppendLine(row);
                    DrawAircraft(ac);
                }
            }
            else
            {
                foreach (var kvp in _aircraft)
                {
                    var ac = kvp.Value;
                    var callsign = ac.ContainsKey("callsign") && ac["callsign"] != null ? ac["callsign"].ToString() : "";
                    double lat = GetDouble(ac, "lat", 0);
                    double lon = GetDouble(ac, "lon", 0);
                    double alt = GetDouble(ac, "alt_ft", 0);
                    double gs = GetGroundSpeedKts(ac);
                    string row = string.Format("{0,-9} {1,10:F4}  {2,11:F4}  {3,8:F0}ft  {4,4:F0}kt",
                        callsign, lat, lon, alt, gs);
                    sb.AppendLine(row);
                    DrawAircraft(ac);
                }
            }

            // Draw all history dots from radar sweeps (independent of current targets)
            DrawHistoryDots();
        }

        private void OnHideGroundChanged(object sender, RoutedEventArgs e)
        {
            // Checkbox is "Show Ground Aircraft" - so checked means show (don't hide)
            // Internally we store a "hide" boolean, so invert the checkbox value.
            _hideGroundTraffic = HideGroundCheckBox.IsChecked != true;
        }

        private void OnShowVerticalDevChanged(object sender, RoutedEventArgs e)
        {
            // wired to legacy handler - read from Display menu
            if (Display_ShowVerticalDev != null)
                _showVerticalDevLines = Display_ShowVerticalDev.IsChecked == true;
        }

        private void OnShowAzimuthDevChanged(object sender, RoutedEventArgs e)
        {
            // wired to legacy handler - read from Display menu
            if (Display_ShowAzimuthDev != null)
                _showAzimuthDevLines = Display_ShowAzimuthDev.IsChecked == true;
        }

        private void OnViewToggleChanged(object sender, RoutedEventArgs e)
        {
            try
            {
                _showApproachLights = (Display_ShowApproachLights != null && Display_ShowApproachLights.IsChecked == true);
                _showVerticalDevLines = (Display_ShowVerticalDev != null && Display_ShowVerticalDev.IsChecked == true);
                _showAzimuthDevLines = (Display_ShowAzimuthDev != null && Display_ShowAzimuthDev.IsChecked == true);
                _enableAzimuthWedgeFilter = (Display_EnableAzimuthWedgeFilter != null && Display_EnableAzimuthWedgeFilter.IsChecked == true);
                _enableVerticalWedgeFilter = (Display_EnableVerticalWedgeFilter != null && Display_EnableVerticalWedgeFilter.IsChecked == true);
                // New datablock toggles (if menu items exist)
                _showVerticalDatablocks = (Display_ShowVerticalData != null && Display_ShowVerticalData.IsChecked == true);
                _showAzimuthDatablocks = (Display_ShowAzimuthData != null && Display_ShowAzimuthData.IsChecked == true);
                _showPlanDatablocks = (Display_ShowPlanData != null && Display_ShowPlanData.IsChecked == true);
                // Refresh UI to apply new toggles
                UpdateUi();
            }
            catch { }
        }

        private void OnMetarSourceChanged(object sender, RoutedEventArgs e)
        {
            try
            {
                var menuItem = sender as MenuItem;
                if (menuItem == null) return;

                if (menuItem == Display_MetarSource_NOAA)
                {
                    _metarSource = MetarSource.NOAA;
                    Display_MetarSource_NOAA.IsChecked = true;
                    Display_MetarSource_VATSIM.IsChecked = false;
                }
                else if (menuItem == Display_MetarSource_VATSIM)
                {
                    _metarSource = MetarSource.VATSIM;
                    Display_MetarSource_NOAA.IsChecked = false;
                    Display_MetarSource_VATSIM.IsChecked = true;
                }

                // Reload METAR with new source
                LoadMetar();
            }
            catch { }
        }

        private void OnMetarClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            try
            {
                // Toggle between full and abbreviated METAR
                _metarShowFull = !_metarShowFull;
                UpdateMetarDisplay();
            }
            catch { }
        }

        // XAML event handlers (stubs) -------------------------------------------------
        private void OnScanIntervalChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (ScanIntervalSlider == null) return;
            _radarScanIntervalSec = ScanIntervalSlider.Value;
            if (ScanIntervalLabel != null)
            {
                ScanIntervalLabel.Text = _radarScanIntervalSec.ToString("F1") + "s";
            }
            // Update timer interval
            if (_radarTimer != null)
            {
                _radarTimer.Interval = TimeSpan.FromSeconds(_radarScanIntervalSec);
            }
        }

        /// <summary>
        /// Radar sampling logic: captures a geographic snapshot of all current targets at this moment.
        /// This runs on a timer at the configured scan rate (0.5-10 seconds).
        /// Each sweep creates history dots that are independent of future target state.
        /// </summary>
        private void SampleRadar()
        {
            if (_runway == null) return;
            
            var sweep = new RadarSweep { SweepTime = DateTime.UtcNow };
            
            // Snapshot all current aircraft positions (geographic coordinates)
            foreach (var kvp in _aircraft)
            {
                var ac = kvp.Value;
                string callsign = ac.ContainsKey("callsign") && ac["callsign"] != null ? ac["callsign"].ToString() : "";
                
                double lat = GetDouble(ac, "lat", 0);
                double lon = GetDouble(ac, "lon", 0);
                double alt = GetDouble(ac, "alt_ft", 0);
                
                // Determine which scopes "saw" this target at sample time
                // Use actual wedge geometry to mark visibility per scope
                bool seenVert = false, seenAzi = false, seenPlan = false;
                
                try
                {
                    // Compute wedge membership (same logic as DrawAircraft filtering)
                    double sensorOffsetNm = _runway.SensorOffsetNm > 0 ? _runway.SensorOffsetNm : 0.5;
                    double rangeNm = _runway.RangeNm > 0 ? _runway.RangeNm : 10.0;
                    double halfAzDeg = GetHalfAzimuthDeg(_runway);
                    
                    // Get sensor position
                    double sensorLat, sensorLon;
                    GetSensorLatLon(_runway, sensorOffsetNm, out sensorLat, out sensorLon);
                    
                    // ENU relative to sensor
                    double east_s = 0, north_s = 0;
                    GeoToEnu(sensorLat, sensorLon, lat, lon, out east_s, out north_s);
                    
                    double hdgRad = DegToRad(_runway.HeadingTrueDeg);
                    double approachRad = hdgRad + Math.PI;
                    double cosA = Math.Cos(approachRad), sinA = Math.Sin(approachRad);
                    double alongFromSensorNm = (north_s * cosA + east_s * sinA) / 1852.0;
                    double crossFromSensorNm = (-north_s * sinA + east_s * cosA) / 1852.0;
                    
                    // Azimuth and elevation
                    double azimuthDeg = 0.0, elevationDeg = 0.0;
                    if (Math.Abs(alongFromSensorNm) > 0.0001)
                    {
                        azimuthDeg = Math.Atan2(crossFromSensorNm, alongFromSensorNm) * 180.0 / Math.PI;
                        double distFt = Math.Abs(alongFromSensorNm) * 6076.12;
                        double altRef = _runway.FieldElevFt;
                        elevationDeg = Math.Atan2(alt - altRef, distFt) * 180.0 / Math.PI;
                    }
                    
                    // Wedge membership tests
                    double includeNegBuffer = 0.3, includePosBuffer = 0.5;
                    bool inAzimuthScope = Math.Abs(azimuthDeg) <= halfAzDeg && alongFromSensorNm >= -includeNegBuffer && alongFromSensorNm <= rangeNm + includePosBuffer;
                    bool inVerticalScope = inAzimuthScope && elevationDeg <= 6.0;
                    
                    // Apply filter toggles
                    seenAzi = _enableAzimuthWedgeFilter ? inAzimuthScope : true;
                    seenVert = _enableVerticalWedgeFilter ? inVerticalScope : true;
                    
                    // Plan view: check ground filter and altitude ceiling
                    bool isGround = false;
                    double east_t = 0, north_t = 0;
                    GeoToEnu(_runway.ThresholdLat, _runway.ThresholdLon, lat, lon, out east_t, out north_t);
                    double distFromThrM = Math.Sqrt(east_t * east_t + north_t * north_t);
                    double distanceFromThresholdFt = distFromThrM * 3.28084;
                    double halfDegGlideAlt = _runway.FieldElevFt + Math.Tan(DegToRad(0.5)) * distanceFromThresholdFt;
                    double groundThreshold = Math.Max(_runway.FieldElevFt + 20, halfDegGlideAlt);
                    isGround = (alt < groundThreshold);
                    
                    seenPlan = (!_hideGroundTraffic || !isGround) && !(_planAltTopHundreds > 0 && alt > _planAltTopHundreds * 100);
                }
                catch
                {
                    // On error, default to visible
                    seenVert = true;
                    seenAzi = true;
                    seenPlan = true;
                }
                
                // Add dot to this sweep (store geographic position)
                sweep.Dots.Add(new HistoryDot
                {
                    Lat = lat,
                    Lon = lon,
                    AltFt = alt,
                    Callsign = callsign,
                    SeenVertical = seenVert,
                    SeenAzimuth = seenAzi,
                    SeenPlan = seenPlan
                });
            }
            
            // Add sweep to history
            lock (_radarSweeps)
            {
                _radarSweeps.Add(sweep);
                
                // Prune old sweeps (older than HistoryLifetimeSec)
                var now = DateTime.UtcNow;
                _radarSweeps.RemoveAll(s => (now - s.SweepTime).TotalSeconds > HistoryLifetimeSec);
            }

            // Also capture a display snapshot (callsign -> attribute map) representing the
            // exact state of targets at sweep time. This snapshot will be used by UpdateUi
            // to render the "current" targets so their motion is tied to the radar scan.
            try
            {
                var snap = new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
                foreach (var d in sweep.Dots)
                {
                    if (string.IsNullOrEmpty(d.Callsign)) continue;
                    // Try to copy the live aircraft dictionary if present, otherwise create a minimal map
                    Dictionary<string, object> live;
                    if (_aircraft.TryGetValue(d.Callsign, out live))
                    {
                        // shallow copy to freeze values at sweep time
                        var copy = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                        foreach (var kv in live) copy[kv.Key] = kv.Value;
                        snap[d.Callsign] = copy;
                    }
                    else
                    {
                        var minimal = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                        minimal["callsign"] = d.Callsign;
                        minimal["lat"] = d.Lat;
                        minimal["lon"] = d.Lon;
                        minimal["alt_ft"] = d.AltFt;
                        snap[d.Callsign] = minimal;
                    }
                }
                lock (_snapshotLock)
                {
                    _displaySnapshot = snap;
                    _lastSweepTime = sweep.SweepTime;
                }
            }
            catch { }
        }

        /// <summary>
        /// Draw all history dots from radar sweeps, projecting from geographic coordinates at draw time.
        /// Dots are faded based on sweep age (older = more transparent).
        /// Dots are filtered by their stored "Seen" flags (captured at sample time).
        /// </summary>
        private void DrawHistoryDots()
        {
            if (_runway == null) return;
            
            RadarSweep[] sweeps;
            lock (_radarSweeps)
            {
                sweeps = _radarSweeps.ToArray();
            }
            
            if (sweeps.Length == 0) return;
            
            // Limit to most recent N sweeps based on _historyDotsCount
            int startIdx = Math.Max(0, sweeps.Length - _historyDotsCount);
            var displaySweeps = sweeps.Skip(startIdx).ToArray();
            
            var now = DateTime.UtcNow;
            
            // Canvas sizes
            double vWidth = VerticalScopeCanvas.ActualWidth > 0 ? VerticalScopeCanvas.ActualWidth : 400;
            double vHeight = VerticalScopeCanvas.ActualHeight > 0 ? VerticalScopeCanvas.ActualHeight : 300;
            double aWidth = AzimuthScopeCanvas.ActualWidth > 0 ? AzimuthScopeCanvas.ActualWidth : 400;
            double aHeight = AzimuthScopeCanvas.ActualHeight > 0 ? AzimuthScopeCanvas.ActualHeight : 300;
            double pWidth = PlanViewCanvas.ActualWidth > 0 ? PlanViewCanvas.ActualWidth : 400;
            double pHeight = PlanViewCanvas.ActualHeight > 0 ? PlanViewCanvas.ActualHeight : 520;
            
            // Draw each sweep's dots (oldest to newest for proper layering)
            for (int sweepIdx = 0; sweepIdx < displaySweeps.Length; sweepIdx++)
            {
                var sweep = displaySweeps[sweepIdx];
                double sweepAgeSeconds = (now - sweep.SweepTime).TotalSeconds;
                
                // Calculate fade: older sweeps are more transparent
                // Map sweep index to alpha: oldest (index 0) = 0.15, newest (last) = 0.45
                float baseAlpha = 0.15f + ((float)sweepIdx / Math.Max(1, displaySweeps.Length - 1)) * 0.30f;
                
                foreach (var dot in sweep.Dots)
                {
                    // Vertical scope
                    if (dot.SeenVertical)
                    {
                        double vx, vy;
                        if (TryProjectToVertical(dot.Lat, dot.Lon, dot.AltFt, vWidth, vHeight, out vx, out vy))
                        {
                            var vDot = new Ellipse { Width = 5, Height = 5, Fill = new SolidColorBrush(Color.FromScRgb(baseAlpha, 1f, 1f, 1f)) };
                            vDot.Margin = new Thickness(vx - 2.5, vy - 2.5, 0, 0);
                            VerticalScopeCanvas.Children.Add(vDot);
                            Canvas.SetZIndex(vDot, 999);
                        }
                    }
                    
                    // Azimuth scope
                    if (dot.SeenAzimuth)
                    {
                        double ax, ay;
                        if (TryProjectToAzimuth(dot.Lat, dot.Lon, dot.AltFt, aWidth, aHeight, out ax, out ay))
                        {
                            var aDot = new Ellipse { Width = 5, Height = 5, Fill = new SolidColorBrush(Color.FromScRgb(baseAlpha, 1f, 1f, 1f)) };
                            aDot.Margin = new Thickness(ax - 2.5, ay - 2.5, 0, 0);
                            AzimuthScopeCanvas.Children.Add(aDot);
                            Canvas.SetZIndex(aDot, 999);
                        }
                    }
                    
                    // Plan scope
                    if (dot.SeenPlan)
                    {
                        double px, py;
                        if (TryProjectToPlan(dot.Lat, dot.Lon, dot.AltFt, pWidth, pHeight, out px, out py))
                        {
                            var pDot = new Ellipse { Width = 4, Height = 4, Fill = new SolidColorBrush(Color.FromScRgb(baseAlpha, 1f, 1f, 1f)) };
                            pDot.Margin = new Thickness(px - 2, py - 2, 0, 0);
                            PlanViewCanvas.Children.Add(pDot);
                            Canvas.SetZIndex(pDot, 999);
                        }
                    }
                }
            }
        }

        private void OnHistoryDotsChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            _historyDotsCount = (int)HistoryDotsSlider.Value;
            if (HistoryDotsLabel != null)
            {
                HistoryDotsLabel.Text = _historyDotsCount.ToString();
            }
        }

        private void MainWindow_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            try
            {
                if (e.Key == System.Windows.Input.Key.PageUp)
                {
                    // Increase range by 1 (or to next integer if currently fractional)
                    if (double.TryParse(RangeBox.Text, out double rng))
                    {
                        // If the value is fractional, round up to next integer; otherwise add 1
                        double newR = Math.Ceiling(rng) > rng ? Math.Ceiling(rng) : (rng + 1.0);
                        RangeBox.Text = newR.ToString("F1");
                        OnConfigChanged(RangeBox, null);
                        e.Handled = true;
                    }
                }
                else if (e.Key == System.Windows.Input.Key.PageDown)
                {
                    if (double.TryParse(RangeBox.Text, out double rng))
                    {
                        double newR = Math.Floor(rng) < rng ? Math.Floor(rng) : (rng - 1.0);
                        if (newR < 0) newR = 0;
                        RangeBox.Text = newR.ToString("F1");
                        OnConfigChanged(RangeBox, null);
                        e.Handled = true;
                    }
                }
            }
            catch { }
        }

        private void DrawVerticalEmpty(System.Windows.Controls.Canvas canvas)
        {
            RunwaySettings rs = GetActiveRunwayDefaults();
            double w = canvas.ActualWidth > 0 ? canvas.ActualWidth : 800;
            double h = canvas.ActualHeight > 0 ? canvas.ActualHeight : 260;

            // Border
                var border = new Rectangle { Width = w, Height = h, Stroke = Brushes.Gray, StrokeThickness = 1 };
                canvas.Children.Add(border);

            // Title - positioned more to the left to avoid overlapping with glide slope info
            var title = new TextBlock();
            title.Text = "VERTICAL";
            title.Foreground = Brushes.White;
            title.FontWeight = FontWeights.Bold;
            title.Margin = new Thickness(35, 2, 0, 0);
            canvas.Children.Add(title);

            // Info (GS and DH)
            var info = new TextBlock();
            info.Text = string.Format("Glide Slope {0:0.0}° | DH {1:0}ft", rs.GlideSlopeDeg, rs.DecisionHeightFt);
            info.Foreground = Brushes.LightGray;
            info.Margin = new Thickness(150, 2, 0, 0);
            canvas.Children.Add(info);

            // Range grid and labels with sensor offset
            double rangeNm = rs.RangeNm > 0 ? rs.RangeNm : 10.0;
            double sensorOffsetNm = rs.SensorOffsetNm > 0 ? rs.SensorOffsetNm : 0.5;
            double totalRangeNm = rangeNm + sensorOffsetNm;
            double pxPerNm = w / totalRangeNm;
            double thresholdX = sensorOffsetNm * pxPerNm;
            int i;

            // Compute touchdown pixel (tdPixel) relative to threshold so we can make TD the origin for distance labels
            double gsRad = DegToRad(rs.GlideSlopeDeg);
            double tchLocal = rs.ThrCrossingHgtFt > 0 ? rs.ThrCrossingHgtFt : 0; // in feet
            double tdOffsetNm = 0; // distance from threshold toward runway (positive value means runway side)
            if (gsRad > 0.000001 && tchLocal > 0)
            {
                double distFt = tchLocal / Math.Tan(gsRad);
                tdOffsetNm = distFt / 6076.12; // convert to NM
            }
            double tdPixel = thresholdX - (tdOffsetNm * pxPerNm);

            // Determine which integer tick corresponds to THR so we can label it
            int thrIndex = (int)Math.Round((thresholdX - tdPixel) / pxPerNm);

            // (debug labels removed)

            for (i = 0; i <= (int)Math.Floor(rangeNm); i++)
            {
                double x = tdPixel + i * pxPerNm; // origin now at TD
                var vline = new Line();
                vline.X1 = x; vline.Y1 = 0; vline.X2 = x; vline.Y2 = h;
                vline.Stroke = new SolidColorBrush(Color.FromRgb(30, 100, 30));
                vline.StrokeThickness = 0.5;
                // All lines dashed except at TD origin
                if (i != 0)
                {
                    var dash = new DoubleCollection(); dash.Add(3); dash.Add(4); vline.StrokeDashArray = dash;
                }
                canvas.Children.Add(vline);

                var lbl = new TextBlock();
                lbl.Foreground = Brushes.White; lbl.FontSize = 12;
                if (i == 0) lbl.Text = "TD"; else if (i == thrIndex) lbl.Text = "THR"; else lbl.Text = (i + "NM");
                lbl.Margin = new Thickness(Math.Max(0, x + 3), h - 18, 0, 0);
                canvas.Children.Add(lbl);
            }

            // Vertical scale labels on LEFT edge showing altitude MSL (field elevation at bottom)
            double bottomMargin = 30;
            double workH = h - bottomMargin;
            double fieldElevFt = rs.FieldElevFt;
            double altAt6DegAtFullRange = fieldElevFt + Math.Tan(DegToRad(6.0)) * (totalRangeNm * 6076.12);
            double altRangeFt = altAt6DegAtFullRange - fieldElevFt;
            double pxPerFt = workH / altRangeFt;
            
            // Add "ft MSL" label at top left
            var ftLabel = new TextBlock();
            ftLabel.Text = "ft MSL";
            ftLabel.Foreground = Brushes.LightGray;
            ftLabel.FontSize = 11;
            ftLabel.Margin = new Thickness(2, 2, 0, 0);
            canvas.Children.Add(ftLabel);
            
            // Altitude scale in feet MSL (every 500 ft above field elevation)
            int altStep = 500;
            int minAltFt = ((int)(fieldElevFt / altStep)) * altStep; // Round down to nearest 500
            int maxAltFt = (int)Math.Ceiling(altAt6DegAtFullRange / altStep) * altStep;
            for (i = minAltFt; i <= maxAltFt; i += altStep)
            {
                if (i < fieldElevFt) continue; // Don't show below field elevation
                if (i > altAt6DegAtFullRange) break;
                
                // Y position on canvas (field elevation at bottom)
                double y = workH - ((i - fieldElevFt) * pxPerFt);
                if (y < 0 || y > workH) continue;
                
                var tx = new TextBlock();
                tx.Foreground = Brushes.LightGray;
                tx.FontSize = 10; 
                tx.Text = i.ToString();
                // Position labels on left side like azimuth scope
                tx.Margin = new Thickness(2, y - 6, 0, 0);
                canvas.Children.Add(tx);
            }

            // Vertical wedge envelope: from threshold to 10 NM and up to 6° ceiling
            DrawVerticalWedge(canvas, w, h, rs, rangeNm);

            // Approach lighting: render a thin, lighter-blue line starting at threshold going toward the runway
            try
            {
                // honor view toggle
                if (!_showApproachLights) { /* skip drawing approach lights */ }
                else
                {
                // Query NASR for approach lighting code for this runway (if loader present)
                string apchCode = null;
                if (_nasrLoader != null && rs != null && !string.IsNullOrEmpty(rs.FaaLid ?? rs.Icao))
                {
                    var lookupKey = !string.IsNullOrEmpty(rs.FaaLid) ? rs.FaaLid : rs.Icao;
                    var rwd = _nasrLoader.GetRunway(lookupKey, rs.Runway);
                    if (rwd != null) apchCode = rwd.ApchLgtSystemCode;
                }
                // Prefer explicit runway override if user set a custom length
                double apchLenFt = 0.0;
                if (rs != null && rs.ApproachLightLengthFt > 0) apchLenFt = rs.ApproachLightLengthFt;
                else apchLenFt = GetApproachLightLengthFt(apchCode);
                if (apchLenFt > 0)
                {
                    // convert feet to pixels along centerline (from threshold toward runway side = negative X direction)
                    double pxPerNmLocal = pxPerNm; // already computed earlier
                    double apchLenNm = apchLenFt / 6076.12;
                    double startX = thresholdX;
                    double endX = Math.Min(w, thresholdX + (apchLenNm * pxPerNmLocal));
                    // Lift the approach-light line only a pixel or two above the runway so it remains visible
                    double approachThickness = 2.0;
                    // Keep the approach light just above the runway bar (approx 1-2 px). Use approach half-thickness + 1px gap.
                    double liftPx = (approachThickness / 2.0) + 1.0;
                    double y = Math.Max(0, workH - liftPx);
                    var apchLine = new Line { X1 = startX, X2 = endX, Y1 = y, Y2 = y, Stroke = new SolidColorBrush(Color.FromArgb(200, 135, 206, 250)), StrokeThickness = approachThickness };
                    apchLine.Tag = "DBG";
                    canvas.Children.Add(apchLine);
                    Canvas.SetZIndex(apchLine, 900);
                }
                }
            }
            catch { }

            // Glide slope reference line
            DrawGlideSlope(canvas, w, h, rangeNm);

            // Vertical deviation guideline set (origin at glideslope at threshold + TCH)
            if (_showVerticalDevLines)
            {
                // Compute altitude at threshold where GS passes: field elev + TCH
                double tchDev = rs.ThrCrossingHgtFt > 0 ? rs.ThrCrossingHgtFt : 0.0;
                double alt0 = fieldElevFt + tchDev; // altitude at threshold on GS
                // TD is at distance from threshold where GS reaches field elevation
                double tdOffsetNmLocal = 0;
                double gsRadLocal = DegToRad(rs.GlideSlopeDeg);
                if (gsRadLocal > 0.000001 && tchDev > 0)
                {
                    double distFtLocal = tchDev / Math.Tan(gsRadLocal);
                    tdOffsetNmLocal = distFtLocal / 6076.12;
                }

                // angles in degrees to draw (0, ±0.5°, ±1°, ±2°) originating at TD
                var vAngles = new List<double> { 0.0, 0.5, 1.0, 2.0 };
                foreach (var ang in vAngles)
                {
                    for (int sign = -1; sign <= 1; sign += 2)
                    {
                        if (ang == 0.0 && sign == -1) continue;
                        double a = ang * sign;
                        var line = new Line();
                        if (ang == 0.0) { line.Stroke = _centerlineBrush; line.StrokeThickness = 2; }
                        else { line.Stroke = new SolidColorBrush(Color.FromRgb(160, 140, 40)); line.StrokeThickness = 1; var dash = new DoubleCollection(); dash.Add(4); dash.Add(6); line.StrokeDashArray = dash; }

                        // Draw deviation from TD origin: TD is where glideslope reaches field elevation
                        double startX = tdPixel; // TD origin in pixels
                        double startY = workH;   // field elevation (ground) at bottom

                        // Compute line endpoint so the guideline extends to the TOP of the work area (y=0).
                        // altitude at top of work area corresponds to fieldElev + altRangeFt
                        double angleRad = DegToRad(rs.GlideSlopeDeg + a);
                        double endX = tdPixel + (rangeNm * pxPerNm);
                        double endY;

                        // If tan(angle) is significant, compute the distance (NM) at which this dev-line reaches the top altitude
                        double tanA = Math.Tan(angleRad);
                        if (Math.Abs(tanA) > 1e-6)
                        {
                            // s_topNm = altRangeFt / (tan(angle) * 6076.12)
                            double sTopNm = altRangeFt / (tanA * 6076.12);
                            double xTop = tdPixel + (sTopNm * pxPerNm);
                            // Prefer to draw to the top (y=0) using xTop; clamp to canvas width
                            endX = Math.Max(0, Math.Min(w, xTop));
                            // If clamped, recompute corresponding endY for visual accuracy
                            if (endX <= 0 || endX >= w)
                            {
                                // compute s for clamped X
                                double sClamped = (endX - tdPixel) / pxPerNm;
                                double altAtClamped = fieldElevFt + tanA * (sClamped * 6076.12);
                                endY = Math.Max(0, Math.Min(workH, workH - ((altAtClamped - fieldElevFt) * pxPerFt)));
                            }
                            else
                            {
                                endY = 0; // reached top
                            }
                        }
                        else
                        {
                            // Fallback: draw to the far end of the display range and compute Y there
                            double altEnd = fieldElevFt + tanA * (rangeNm * 6076.12);
                            endY = Math.Max(0, Math.Min(workH, workH - ((altEnd - fieldElevFt) * pxPerFt)));
                        }

                        // If endY wasn't set above (e.g., branch where endX clamped didn't set endY), ensure it's computed
                        if (double.IsNaN(endY))
                        {
                            double s = (endX - tdPixel) / pxPerNm;
                            double altAtX = fieldElevFt + tanA * (s * 6076.12);
                            endY = Math.Max(0, Math.Min(workH, workH - ((altAtX - fieldElevFt) * pxPerFt)));
                        }

                        line.X1 = startX; line.Y1 = startY; line.X2 = endX; line.Y2 = endY;
                        canvas.Children.Add(line);
                    }
                }
            }

            // Draw thick blue runway line at the glideslope touchdown point (bottom of triangle)
            double runwayY = workH; // At field elevation (bottom of the triangle)
            // Render runway as a thick blue bar from the left edge (sensor side) to the threshold
            // This will go past the touchdown point which sits between threshold and left edge
            double runwayStartX = 0; // left edge (sensor apex)
            double runwayEndX = thresholdX; // threshold pixel
            var runwayLine = new Line { X1 = runwayStartX, X2 = runwayEndX, Y1 = runwayY, Y2 = runwayY, Stroke = Brushes.DeepSkyBlue, StrokeThickness = 10 };
            canvas.Children.Add(runwayLine);

            // Draw TD (touchdown) marker located tdOffsetNm on the runway side of threshold
            if (tdOffsetNm > 0)
            {
                tdPixel = thresholdX - (tdOffsetNm * pxPerNm);
                // TD is labeled on the bottom axis; vertical runway tick removed per user request
            }

            // Show ground traffic tickmark (20ft AGL above field elev) - removed per user request

            // Decision height marker (T at glideslope/DH intersection, pointing UP)
            // Use the same glideslope reference as DrawGlideSlope: starts at threshold+TCH
            double tch = rs.ThrCrossingHgtFt > 0 ? rs.ThrCrossingHgtFt : 0;
            double dhAlt = rs.FieldElevFt + rs.DecisionHeightFt;
            double yDh = workH - ((dhAlt - fieldElevFt) * pxPerFt); // Y pixel for DH altitude
            
            // DH marker X position: where glideslope intersects DH altitude
            // GS starts at threshold (0 distance) at altitude (field_elev + tch)
            // So: dhAlt = (field_elev + tch) + tan(gs_angle) * distance
            // Solve for distance: distance = (dhAlt - field_elev - tch) / tan(gs_angle)
            double dhDistNm = (dhAlt - fieldElevFt - tch) / Math.Tan(gsRad) / 6076.12;
            double dhX = thresholdX + (dhDistNm * pxPerNm); // X position on glideslope (where vertical tick will be)
            double dhLineLen = pxPerNm * 1.0; // 1nm wide horizontal bar (0.5nm each side of center)
            double dhLineX1 = dhX - (dhLineLen / 2);
            double dhLineX2 = dhX + (dhLineLen / 2);
            
            // Calculate vertical extent: 200ft tall
            double dhVerticalExtentFt = 200.0;
            double dhVerticalExtentPx = dhVerticalExtentFt * pxPerFt;
            
            // T marker: horizontal bar at DH altitude, vertical line pointing UP 200ft
            var dhLine = new Line { X1 = dhLineX1, X2 = dhLineX2, Y1 = yDh, Y2 = yDh, Stroke = Brushes.LightBlue, StrokeThickness = 3 };
            canvas.Children.Add(dhLine);
            var dhTick = new Line { X1 = dhX, X2 = dhX, Y1 = yDh, Y2 = yDh - dhVerticalExtentPx, Stroke = Brushes.LightBlue, StrokeThickness = 3 };
            canvas.Children.Add(dhTick);
            var dhLabel = new TextBlock { Text = $"DH {rs.DecisionHeightFt}ft", Foreground = Brushes.LightBlue, FontWeight = FontWeights.Normal, FontSize = 11, Margin = new Thickness(dhX + 5, yDh - dhVerticalExtentPx - 15, 0, 0) };
            canvas.Children.Add(dhLabel);
        }

        private void DrawAzimuthEmpty(System.Windows.Controls.Canvas canvas)
        {
            RunwaySettings rs = GetActiveRunwayDefaults();
            double w = canvas.ActualWidth > 0 ? canvas.ActualWidth : 400;
            double h = canvas.ActualHeight > 0 ? canvas.ActualHeight : 260;

            // Border
            var border = new Rectangle(); border.Width = w; border.Height = h; border.Stroke = Brushes.Gray; border.StrokeThickness = 1; canvas.Children.Add(border);

            // Title and concise magnetic info (only magnetic runway heading and mag var)
            var title = new TextBlock(); title.Text = "AZIMUTH"; title.Foreground = Brushes.White; title.FontWeight = FontWeights.Bold; title.Margin = new Thickness(40, 2, 0, 0); canvas.Children.Add(title);
            // Compute magnetic heading and normalize to 0-359 (headings wrap: 367 -> 007).
            // Special case: 0 is displayed as 360 per user preference ("000 will show 360").
            double magHeadingRaw = rs.HeadingTrueDeg + rs.MagVariationDeg;
            int magHeadingInt = ((int)Math.Round(magHeadingRaw) % 360 + 360) % 360; // ensures 0..359
            int displayHeading = magHeadingInt == 0 ? 360 : magHeadingInt;
            int magVarInt = (int)Math.Round(rs.MagVariationDeg);
            // Display mag var with sign where West is positive (+) and East is negative (-)
            string magVarStr = magVarInt >= 0 ? "+" + magVarInt.ToString("D0") + "°" : "-" + Math.Abs(magVarInt).ToString("D0") + "°";
            var info = new TextBlock();
            info.Text = string.Format("RWY Hdg (M) {0:D3}°   Mag Var {1}", displayHeading, magVarStr);
            info.Foreground = Brushes.LightGray; info.Margin = new Thickness(130, 2, 0, 0); canvas.Children.Add(info);

            // Add "NM" label at top left for lateral scale
            var nmLabel = new TextBlock();
            nmLabel.Text = ",NM";
            nmLabel.Foreground = Brushes.LightGray;
            nmLabel.FontSize = 11;
            nmLabel.Margin = new Thickness(2, 2, 0, 0);
            canvas.Children.Add(nmLabel);

            // Centerline (horizontal) - show the 0° track (dimmed green)
            var centerline = new Line { X1 = 0, Y1 = h / 2.0, X2 = w, Y2 = h / 2.0, Stroke = new SolidColorBrush(Color.FromRgb(60, 120, 60)), StrokeThickness = 2 };
            canvas.Children.Add(centerline);
            
            // Add lateral scale labels on left (NM from centerline)
            double halfWidthNm = 1.0; // display ±1 NM lateral
            double pxPerNmY = (h / 2.0) / halfWidthNm;
            double[] lateralNm = new double[] { 1.0, 0.5, 0, -0.5, -1.0 };
            int j;
            for (j = 0; j < lateralNm.Length; j++)
            {
                double nm = lateralNm[j];
                double y = h / 2.0 - (nm * pxPerNmY);
                
                var lbl = new TextBlock();
                lbl.Foreground = (nm == 0) ? _centerlineBrush : Brushes.LightGray;
                lbl.FontSize = 10;
                if (nm == 0) lbl.Text = "Track"; else lbl.Text = nm.ToString("0.0");
                lbl.Margin = new Thickness(2, y - 6, 0, 0);
                canvas.Children.Add(lbl);
            }

            // Range grid and labels with sensor offset - we'll shift origin to TD for labeling
            double rangeNm = rs.RangeNm > 0 ? rs.RangeNm : 10.0;
            double sensorOffsetNm = rs.SensorOffsetNm > 0 ? rs.SensorOffsetNm : 0.5;
            double totalRangeNm = rangeNm + sensorOffsetNm;
            double pxPerNm = w / totalRangeNm;
            double thresholdX = sensorOffsetNm * pxPerNm;

            // Compute TD pixel
            double gsRad = DegToRad(rs.GlideSlopeDeg);
            double tch = rs.ThrCrossingHgtFt > 0 ? rs.ThrCrossingHgtFt : 0;
            double tdOffsetNm = 0;
            if (gsRad > 0.000001 && tch > 0)
            {
                double distFt = tch / Math.Tan(gsRad);
                tdOffsetNm = distFt / 6076.12;
            }
            double tdPixel = thresholdX - (tdOffsetNm * pxPerNm);

            int i;
            // (debug labels removed)
            for (i = 0; i <= (int)Math.Floor(rangeNm); i++)
            {
                double x = tdPixel + i * pxPerNm; // origin at TD
                var vline = new Line(); vline.X1 = x; vline.Y1 = 0; vline.X2 = x; vline.Y2 = h; vline.Stroke = new SolidColorBrush(Color.FromRgb(30, 100, 30)); vline.StrokeThickness = 0.5;
                if (i != 0) { var dash = new DoubleCollection(); dash.Add(3); dash.Add(4); vline.StrokeDashArray = dash; }
                canvas.Children.Add(vline);
                var lbl = new TextBlock(); lbl.Foreground = Brushes.White; lbl.FontSize = 12; lbl.Text = (i == 0) ? "TD" : (i + "NM"); lbl.Margin = new Thickness(Math.Max(0, x + 3), h - 18, 0, 0); canvas.Children.Add(lbl);
            }

            // Approach lighting: render a thin, lighter-blue line starting at threshold going toward the runway
            try
            {
                string apchCode = null;
                if (_nasrLoader != null && rs != null && !string.IsNullOrEmpty(rs.FaaLid ?? rs.Icao))
                {
                    var lookupKey = !string.IsNullOrEmpty(rs.FaaLid) ? rs.FaaLid : rs.Icao;
                    var rwd = _nasrLoader.GetRunway(lookupKey, rs.Runway);
                    if (rwd != null) apchCode = rwd.ApchLgtSystemCode;
                }
                double apchLenFt = 0.0;
                if (rs != null && rs.ApproachLightLengthFt > 0) apchLenFt = rs.ApproachLightLengthFt;
                else apchLenFt = GetApproachLightLengthFt(apchCode);
                if (apchLenFt > 0)
                {
                    double apchLenNm = apchLenFt / 6076.12;
                    double startX = thresholdX;
                    double endX = Math.Min(w, thresholdX + (apchLenNm * pxPerNm));
                    double y = h / 2.0; // centerline for azimuth
                    var apchLine = new Line { X1 = startX, X2 = endX, Y1 = y, Y2 = y, Stroke = new SolidColorBrush(Color.FromArgb(180, 135, 206, 250)), StrokeThickness = 2 };
                    apchLine.Tag = "DBG";
                    canvas.Children.Add(apchLine);
                }
            }
            catch { }

            // Azimuth wedge envelope and guide lines
            DrawAzimuthWedge(canvas, w, h, rs);

            // Azimuth deviation guideline set: originate from TD (tdPixel)
            // Draw the requested angles: 0, ±0.5°, ±1°, ±2° (but only include angles up to halfAz)
            double halfAz = GetHalfAzimuthDeg(rs);
            var baseAngles = new List<double> { 0.0, 0.5, 1.0, 2.0 };
            var angleList = baseAngles.Where(x => x <= halfAz + 1e-9).ToList();

            // Draw positive and negative sides (only if enabled)
            if (_showAzimuthDevLines)
            {
                // Precompute denominator safely (avoid div by zero)
                double tanHalfAz = Math.Tan(DegToRad(halfAz));
                for (int idx = 0; idx < angleList.Count; idx++)
                {
                    double ang = angleList[idx];
                    // Skip the 0.0 duplicate for negative side
                    for (int sign = -1; sign <= 1; sign += 2)
                    {
                        if (ang == 0.0 && sign == -1) continue; // 0 only once
                        double a = ang * sign;
                        var line = new Line();
                        if (ang == 0.0) { line.Stroke = _centerlineBrush; line.StrokeThickness = 2; }
                        else { line.Stroke = new SolidColorBrush(Color.FromRgb(160, 140, 40)); line.StrokeThickness = 1; var dash = new DoubleCollection(); dash.Add(4); dash.Add(6); line.StrokeDashArray = dash; }

                        // Start at TD on centerline
                        line.X1 = tdPixel; line.Y1 = h / 2.0;
                        line.X2 = w; // extend to right edge

                        // Compute Y using same normalization as TryProjectToAzimuth:
                        // normAy = 0.5 + (cross / (2 * maxCrossTrackNm)) where maxCrossTrackNm = tan(halfAz) * rangeNm
                        // For a guideline at angle 'a', cross_at_full_range = tan(a) * rangeNm
                        // => normalized position = 0.5 - (tan(a) / (2 * tan(halfAz)))  (sign handled by 'a')
                        double y2;
                        if (Math.Abs(tanHalfAz) < 1e-9)
                        {
                            y2 = h / 2.0; // degenerate: no spread
                        }
                        else
                        {
                            double ratio = Math.Tan(DegToRad(a)) / (2.0 * tanHalfAz);
                            // normAy = 0.5 - ratio (because positive 'a' moves upward negative in screen coords)
                            double normAy = 0.5 - ratio;
                            y2 = Math.Max(0.0, Math.Min(h, normAy * h));
                        }

                        line.Y2 = y2;
                        canvas.Children.Add(line);
                    }
                }
            }

            // Runway symbol: draw a thick blue bar from left edge (sensor side) to the threshold along centerline
            double runwayY = h / 2.0;
            double runwayStartX = 0; // left edge (sensor apex)
            double runwayEndXFull = thresholdX; // threshold pixel
            var runwayLineFull = new Line { X1 = runwayStartX, X2 = runwayEndXFull, Y1 = runwayY, Y2 = runwayY, Stroke = Brushes.DeepSkyBlue, StrokeThickness = 6 };
            canvas.Children.Add(runwayLineFull);
            // small runway tip overlay removed per user request
        }

        private void DrawVerticalWedge(System.Windows.Controls.Canvas canvas, double w, double h, RunwaySettings rs, double rangeNm)
        {
            // Leave room at bottom
            double bottomMargin = 30;
            double workH = h - bottomMargin;

            // Sensor offset to the left of threshold
            double sensorOffsetNm = rs.SensorOffsetNm > 0 ? rs.SensorOffsetNm : 0.5;
            double totalRangeNm = rangeNm + sensorOffsetNm;
            double pxPerNm = w / totalRangeNm;
            double thresholdX = sensorOffsetNm * pxPerNm;

            // Calculate altitude at full range for 6° wedge
            double altAt6DegAtFullRange = Math.Tan(DegToRad(6.0)) * (totalRangeNm * 6076.12);
            
            // Scale vertical so the 6° line reaches the top of the display
            double pxPerFt = workH / altAt6DegAtFullRange;

            // Left apex at sensor (behind threshold)
            double x0 = 0; double y0 = workH;
            
            // Top of wedge: 6° line at full range should reach top
            double yTop = 0; // top of canvas
            
            var poly = new Polygon(); poly.Stroke = Brushes.DeepSkyBlue; poly.StrokeThickness = 2; poly.Fill = null;
            var pts = new PointCollection();
            pts.Add(new Point(x0, y0)); // sensor at bottom-left
            pts.Add(new Point(w, yTop)); // full range at top (6° line)
            pts.Add(new Point(w, workH)); // full range at bottom
            poly.Points = pts;
            canvas.Children.Add(poly);
        }

        private void DrawAzimuthWedge(System.Windows.Controls.Canvas canvas, double w, double h, RunwaySettings rs)
        {
            double halfAz = GetHalfAzimuthDeg(rs);
            double midY = h / 2.0;
            double rangeNm = rs.RangeNm > 0 ? rs.RangeNm : 10.0;
            double sensorOffsetNm = rs.SensorOffsetNm > 0 ? rs.SensorOffsetNm : 0.5;
            double totalRangeNm = rangeNm + sensorOffsetNm;
            double pxPerNm = w / totalRangeNm;
            double thresholdX = sensorOffsetNm * pxPerNm;

            // Build wedge from sensor (behind threshold)
            // Use the same normalization as TryProjectToAzimuth so that max cross-track at full range
            // maps to the canvas top and bottom (normAy = 0.0 and 1.0 respectively).
            var poly = new Polygon(); poly.Stroke = Brushes.DeepSkyBlue; poly.StrokeThickness = 2; poly.Fill = null;
            var pts = new PointCollection();
            pts.Add(new Point(0, midY)); // sensor apex
            // At full range (along = rangeNm) the max cross-track used for normalization is based on rangeNm
            // which maps to normAy = 0.0 and 1.0 for the two edges. So use full canvas top/bottom.
            pts.Add(new Point(w, 0));
            pts.Add(new Point(w, h));
            poly.Points = pts;
            canvas.Children.Add(poly);

            // Threshold marker - removed per user request

            // Compute touchdown (TD) position using TCH (no centerline marker here; TD is labeled on the axis)
            double gsRad = DegToRad(rs.GlideSlopeDeg);
            double tch = rs.ThrCrossingHgtFt > 0 ? rs.ThrCrossingHgtFt : 0;
            // TD pixel computed by caller where needed
        }

        private void DrawPlanEmpty(System.Windows.Controls.Canvas canvas)
        {
            RunwaySettings rs = GetActiveRunwayDefaults();
            double w = canvas.ActualWidth > 0 ? canvas.ActualWidth : 400;
            double h = canvas.ActualHeight > 0 ? canvas.ActualHeight : 520;

            var border = new Rectangle(); border.Width = w; border.Height = h; border.Stroke = Brushes.Gray; border.StrokeThickness = 1; canvas.Children.Add(border);

            // Center on airport (middle of canvas)
            double cx = w / 2.0; double cy = h / 2.0;
            double rangeNm = rs.RangeNm > 0 ? rs.RangeNm : 10.0;
            double maxRangeNm = rangeNm + 5; // show a bit beyond
            double nmPerPx = maxRangeNm / Math.Min(w / 2.0, h / 2.0);

            // Draw range rings every 5 NM
            int i;
            for (i = 5; i <= (int)maxRangeNm; i += 5)
            {
                double r = i / nmPerPx;
                var ring = new Ellipse(); ring.Width = r * 2; ring.Height = r * 2; ring.Stroke = Brushes.DimGray; ring.StrokeThickness = 1; ring.Margin = new Thickness(cx - r, cy - r, 0, 0); canvas.Children.Add(ring);
                var lbl = new TextBlock(); lbl.Text = i + "NM"; lbl.Foreground = Brushes.Gray; lbl.FontSize = 10; lbl.Margin = new Thickness(cx + 3, cy - r - 12, 0, 0); canvas.Children.Add(lbl);
            }

            // Draw approach wedge showing monitored area
            // Approach direction is the reciprocal of runway heading (where aircraft approach FROM)
            double hdgRad = DegToRad(rs.HeadingTrueDeg);
            double approachRad = hdgRad + Math.PI; // reciprocal heading (approach course)
            double sensorOffsetNm = rs.SensorOffsetNm > 0 ? rs.SensorOffsetNm : 0.5;
            double halfAzDeg = GetHalfAzimuthDeg(rs);
            double maxAzRad = DegToRad(halfAzDeg);

            // Sensor position: from threshold, move along APPROACH direction by sensor offset
            // We want the sensor/apex on the runway side (i.e., opposite sign of the approach vector here)
            double sx = cx - (sensorOffsetNm / nmPerPx) * Math.Sin(approachRad);
            double sy = cy + (sensorOffsetNm / nmPerPx) * Math.Cos(approachRad);

            // Full range endpoint: from sensor, extend along APPROACH direction
            double fullRangeX = sx + (rangeNm / nmPerPx) * Math.Sin(approachRad);
            double fullRangeY = sy - (rangeNm / nmPerPx) * Math.Cos(approachRad);

            // Wedge edges at full range: perpendicular spread relative to APPROACH direction
            double spreadNm = rangeNm * Math.Tan(maxAzRad);
            double leftX = fullRangeX - (spreadNm / nmPerPx) * Math.Cos(approachRad);
            double leftY = fullRangeY - (spreadNm / nmPerPx) * Math.Sin(approachRad);
            double rightX = fullRangeX + (spreadNm / nmPerPx) * Math.Cos(approachRad);
            double rightY = fullRangeY + (spreadNm / nmPerPx) * Math.Sin(approachRad);
            
            var wedge = new Polygon(); wedge.Stroke = Brushes.DeepSkyBlue; wedge.StrokeThickness = 2; wedge.Fill = null;
            var wedgePts = new PointCollection();
            wedgePts.Add(new Point(sx, sy)); // sensor apex
            wedgePts.Add(new Point(leftX, leftY)); // left edge at full range
            wedgePts.Add(new Point(rightX, rightY)); // right edge at full range
            wedge.Points = wedgePts;
            canvas.Children.Add(wedge);
            
            // Draw centerline (dimmed green)
            var centerline = new Line(); centerline.X1 = sx; centerline.Y1 = sy; centerline.X2 = fullRangeX; centerline.Y2 = fullRangeY; centerline.Stroke = new SolidColorBrush(Color.FromRgb(60, 120, 60)); centerline.StrokeThickness = 1.5; canvas.Children.Add(centerline);
            
            // Draw runways for the selected airport using NASR data if available
            bool drewAnyRunways = false;
            try
            {
                if (_nasrLoader != null && _runway != null && !string.IsNullOrEmpty(_runway.Icao))
                {
                    // Use FaaLid for NASR lookup since keys are FAA LIDs, not ICAO codes
                    var lookupKey = !string.IsNullOrEmpty(_runway.FaaLid) ? _runway.FaaLid : _runway.Icao;
                    var ends = _nasrLoader.GetAirportRunways(lookupKey);

                    // local helper: normalize runway id like NASR normalization (number + optional L/R/C)
                    string NormalizeRwy(string r)
                    {
                        if (string.IsNullOrEmpty(r)) return r;
                        r = r.Trim().ToUpperInvariant();
                        var numPart = new string(r.TakeWhile(char.IsDigit).ToArray());
                        var rest = r.Substring(numPart.Length).Trim();
                        if (rest == "LEFT") rest = "L"; else if (rest == "RIGHT") rest = "R"; else if (rest == "CENTER" || rest == "CENTRE") rest = "C";
                        return numPart.TrimStart('0') + rest;
                    }

                    // helper to parse numeric and side
                    bool ParseRwy(string r, out int num, out string side)
                    {
                        num = 0; side = "";
                        if (string.IsNullOrEmpty(r)) return false;
                        var s = r.Trim().ToUpperInvariant();
                        var digits = new string(s.TakeWhile(char.IsDigit).ToArray());
                        if (!int.TryParse(digits, out num)) return false;
                        side = s.Substring(digits.Length).Trim();
                        return true;
                    }

                    var used = new HashSet<int>();
                    for (int iidx = 0; iidx < ends.Count; iidx++)
                    {
                        if (used.Contains(iidx)) continue;
                        var a = ends[iidx];
                        if (string.IsNullOrEmpty(a.RunwayId)) continue;
                        if (!ParseRwy(a.RunwayId, out int anum, out string aside)) continue;

                        // compute reciprocal number (add 18 -> opposite direction)
                        int recipNum = ((anum + 18 - 1) % 36) + 1;
                        // swap side (L<->R)
                        string recipSide = aside;
                        if (recipSide == "L") recipSide = "R"; else if (recipSide == "R") recipSide = "L";

                        string recipNorm = recipNum.ToString() + recipSide;
                        // find matching runway end
                        int found = -1;
                        for (int j = 0; j < ends.Count; j++)
                        {
                            if (j == iidx) continue;
                            if (used.Contains(j)) continue;
                            var b = ends[j];
                            if (string.IsNullOrEmpty(b.RunwayId)) continue;
                            var bn = NormalizeRwy(b.RunwayId);
                            if (bn == recipNorm)
                            {
                                found = j; break;
                            }
                        }

                        // fallback: match reciprocal number ignoring side
                        if (found < 0)
                        {
                            string recipNumStr = recipNum.ToString();
                            for (int j = 0; j < ends.Count; j++)
                            {
                                if (j == iidx) continue;
                                if (used.Contains(j)) continue;
                                var b = ends[j];
                                if (string.IsNullOrEmpty(b.RunwayId)) continue;
                                var bn = NormalizeRwy(b.RunwayId);
                                if (bn.StartsWith(recipNumStr)) { found = j; break; }
                            }
                        }

                        if (found >= 0)
                        {
                            // draw line between a and ends[found]
                            var b = ends[found];
                            double east1, north1, east2, north2;
                            GeoToEnu(_runway.ThresholdLat, _runway.ThresholdLon, a.Latitude, a.Longitude, out east1, out north1);
                            GeoToEnu(_runway.ThresholdLat, _runway.ThresholdLon, b.Latitude, b.Longitude, out east2, out north2);
                            double px1 = cx + (east1 / 1852.0) / nmPerPx;
                            double py1 = cy - (north1 / 1852.0) / nmPerPx;
                            double px2 = cx + (east2 / 1852.0) / nmPerPx;
                            double py2 = cy - (north2 / 1852.0) / nmPerPx;
                            var runwayLine = new Line { X1 = px1, Y1 = py1, X2 = px2, Y2 = py2, Stroke = Brushes.DimGray, StrokeThickness = 3 };
                            canvas.Children.Add(runwayLine);
                            used.Add(iidx); used.Add(found);
                            drewAnyRunways = true;
                        }
                    }
                }
            }
            catch { /* ignore drawing errors */ }

            if (!drewAnyRunways)
            {
                // Fallback: draw a simple runway indicator at threshold
                double rwLen = 2.0; // runway length in NM for display
                double x1 = cx; double y1 = cy;
                double x2 = cx + (rwLen / nmPerPx) * Math.Sin(hdgRad);
                double y2 = cy - (rwLen / nmPerPx) * Math.Cos(hdgRad);
                var rw = new Line(); rw.X1 = x1; rw.Y1 = y1; rw.X2 = x2; rw.Y2 = y2; rw.Stroke = Brushes.White; rw.StrokeThickness = 4; canvas.Children.Add(rw);
            }

            // Threshold marker (green)
            var thr = new Ellipse(); thr.Width = 8; thr.Height = 8; thr.Fill = _centerlineBrush; thr.Margin = new Thickness(cx - 4, cy - 4, 0, 0); canvas.Children.Add(thr);
        }

        private RunwaySettings GetActiveRunwayDefaults()
        {
            if (_runway != null) return _runway;
            var rs = new RunwaySettings();
            rs.Icao = "DEMO"; rs.Runway = "RWY"; rs.ThresholdLat = 0; rs.ThresholdLon = 0; rs.HeadingTrueDeg = 0; rs.GlideSlopeDeg = 3.0; rs.FieldElevFt = 0; rs.RangeNm = 10; rs.DecisionHeightFt = 200; rs.MaxAzimuthDeg = 10; rs.VerticalCeilingFt = 10000; rs.SensorOffsetNm = 0.5;
            rs.ThrCrossingHgtFt = 50; // Added TCH field initialization
            return rs;
        }

        // Compute sensor lat/lon given runway threshold lat/lon and a sensor offset (nm)
        // sensorOffsetNm positive means sensor is on runway side (toward the runway end when drawing)
        private void GetSensorLatLon(RunwaySettings rs, double sensorOffsetNm, out double sensorLat, out double sensorLon)
        {
            // Approach course is reciprocal of runway heading
            double hdgRad = DegToRad(rs.HeadingTrueDeg);
            double approachRad = hdgRad + Math.PI; // reciprocal (along final)
            double sensorOffsetM = sensorOffsetNm * 1852.0; // nm to meters
            double lat0Rad = DegToRad(rs.ThresholdLat);
            double dLatM = sensorOffsetM * Math.Cos(approachRad);
            double dLonM = sensorOffsetM * Math.Sin(approachRad);
            // Place sensor on the runway side (opposite the approach vector here)
            sensorLat = rs.ThresholdLat - (dLatM / 111319.9);
            sensorLon = rs.ThresholdLon - (dLonM / (111319.9 * Math.Cos(lat0Rad)));
        }

        // Interpret MaxAzimuthDeg stored in RunwaySettings as the TOTAL azimuth cone (degrees).
        // Return the half-angle (per-side) used by internal geometry and inclusion tests.
        private double GetHalfAzimuthDeg(RunwaySettings rs)
        {
            double total = (rs != null && rs.MaxAzimuthDeg > 0) ? rs.MaxAzimuthDeg : 10.0;
            return total / 2.0;
        }

        private void DrawAircraft(Dictionary<string, object> ac)
        {
            if (_runway == null) return;

            double alt = GetDouble(ac, "alt_ft", 0);
            double lat = GetDouble(ac, "lat", 0);
            double lon = GetDouble(ac, "lon", 0);
            string callsign = ac.ContainsKey("callsign") && ac["callsign"] != null ? ac["callsign"].ToString() : "";

            // Remove any previous debug overlays (tagged "DBG") to keep only one
            try
            {
                var old = VerticalScopeCanvas.Children.OfType<UIElement>().Where(c => (c is FrameworkElement fe) && fe.Tag != null && fe.Tag.ToString() == "DBG").ToList();
                foreach (var o in old) VerticalScopeCanvas.Children.Remove(o);
            }
            catch { }

            // Canvas sizes
            double vWidth = VerticalScopeCanvas.ActualWidth > 0 ? VerticalScopeCanvas.ActualWidth : 400;
            double vHeight = VerticalScopeCanvas.ActualHeight > 0 ? VerticalScopeCanvas.ActualHeight : 300;
            double aWidth = AzimuthScopeCanvas.ActualWidth > 0 ? AzimuthScopeCanvas.ActualWidth : 400;
            double aHeight = AzimuthScopeCanvas.ActualHeight > 0 ? AzimuthScopeCanvas.ActualHeight : 300;
            double pWidth = PlanViewCanvas.ActualWidth > 0 ? PlanViewCanvas.ActualWidth : 400;
            double pHeight = PlanViewCanvas.ActualHeight > 0 ? PlanViewCanvas.ActualHeight : 520;

            // Runway / sensor params
            double rangeNm = _runway.RangeNm > 0 ? _runway.RangeNm : 10.0;
            double sensorOffsetNm = _runway.SensorOffsetNm > 0 ? _runway.SensorOffsetNm : 0.5;
            double halfAzDeg = GetHalfAzimuthDeg(_runway);
            double gsDeg = _runway.GlideSlopeDeg > 0 ? _runway.GlideSlopeDeg : 3.0;
            double tchFt = _runway.ThrCrossingHgtFt > 0 ? _runway.ThrCrossingHgtFt : 50.0;
            double fieldElevFt = _runway.FieldElevFt;

            // Approach/course geometry
            double hdgRad = DegToRad(_runway.HeadingTrueDeg);
            double approachRad = hdgRad + Math.PI;

            // Sensor lat/lon: use shared helper so apex math is identical across codepaths
            double sensorLat, sensorLon;
            try
            {
                GetSensorLatLon(_runway, sensorOffsetNm, out sensorLat, out sensorLon);
            }
            catch
            {
                // Fallback to previous approximate calculation in case of unexpected error
                double sensorOffsetM = sensorOffsetNm * 1852.0;
                double lat0Rad = DegToRad(_runway.ThresholdLat);
                double dLatM = sensorOffsetM * Math.Cos(approachRad);
                double dLonM = sensorOffsetM * Math.Sin(approachRad);
                sensorLat = _runway.ThresholdLat - (dLatM / 111319.9);
                sensorLon = _runway.ThresholdLon - (dLonM / (111319.9 * Math.Cos(lat0Rad)));
            }

            // ENU relative to sensor and threshold
            double east_s = 0, north_s = 0;
            GeoToEnu(sensorLat, sensorLon, lat, lon, out east_s, out north_s);
            double east_t = 0, north_t = 0;
            GeoToEnu(_runway.ThresholdLat, _runway.ThresholdLon, lat, lon, out east_t, out north_t);

            // Project to approach coordinates
            double cosA = Math.Cos(approachRad), sinA = Math.Sin(approachRad);
            double alongTrackFromSensorNm = (north_s * cosA + east_s * sinA) / 1852.0;
            double crossTrackFromSensorNm = (-north_s * sinA + east_s * cosA) / 1852.0;
            double alongTrackFromThresholdNm = (north_t * cosA + east_t * sinA) / 1852.0;
            double crossTrackFromThresholdNm = (-north_t * sinA + east_t * cosA) / 1852.0;

            // Azimuth and elevation — compute relative to the sensor apex so wedge filters align
            double azimuthDeg = 0.0, elevationDeg = 0.0;
            if (Math.Abs(alongTrackFromSensorNm) > 0.0001)
            {
                // Use sensor-relative cross/along values so the angle originates at the sensor apex
                azimuthDeg = Math.Atan2(crossTrackFromSensorNm, alongTrackFromSensorNm) * 180.0 / Math.PI;
                double distFt = Math.Abs(alongTrackFromSensorNm) * 6076.12;
                double altRef = fieldElevFt;
                elevationDeg = Math.Atan2(alt - altRef, distFt) * 180.0 / Math.PI;
            }

            // Inclusion tests
            double includeNegBuffer = 0.3, includePosBuffer = 0.5;
            // Inclusion test should be referenced to the sensor apex: allow a small negative buffer behind sensor if necessary
            bool inAzimuthScope = Math.Abs(azimuthDeg) <= halfAzDeg && alongTrackFromSensorNm >= -includeNegBuffer && alongTrackFromSensorNm <= rangeNm + includePosBuffer;
            bool inVerticalScope = inAzimuthScope && elevationDeg <= 6.0;

            // Respect debug wedge-filter toggles: when a filter is disabled we treat the scope as allowing all targets
            bool azFiltered = _enableAzimuthWedgeFilter ? inAzimuthScope : true;
            bool vertFiltered = _enableVerticalWedgeFilter ? inVerticalScope : true;

            // Compute a ground check for Plan view (reuse threshold ENU computed above)
            bool isGroundPlan = false;
            try
            {
                double distFromThrM_plan = Math.Sqrt(east_t * east_t + north_t * north_t);
                double distanceFromThresholdFt_plan = distFromThrM_plan * 3.28084;
                double halfDegGlideAlt_plan = fieldElevFt + Math.Tan(DegToRad(0.5)) * distanceFromThresholdFt_plan;
                double groundThreshold_plan = Math.Max(fieldElevFt + 20, halfDegGlideAlt_plan);
                isGroundPlan = (alt < groundThreshold_plan);
            }
            catch { isGroundPlan = false; }

            // --- DRAW VERTICAL SCOPE ---
            try
            {
                // Use centralized projection helper so current-target vertical placement matches history-dots
                double vx = 0, vy = 0; bool seenVert = false, seenAzi = false;
                TryProjectVerticalPoint(_runway, lat, lon, alt, vWidth, vHeight, out vx, out vy, out seenVert, out seenAzi);

                // Recompute vertical display scale variables
                double totalRangeNmV = rangeNm + sensorOffsetNm;
                double bottomMargin = 30.0;
                double workH = Math.Max(0.0, vHeight - bottomMargin);

                // Draw current vertical marker (only when vertical filtering allows it)
                if (vertFiltered && vx >= 0 && vx <= vWidth && vy >= 0 && vy <= vHeight)
                {
                    double rectW = 14.0, rectH = 4.0;
                    double sensorPxX = 0, sensorPxY = workH;
                    double vxp = vx - sensorPxX, vyp = vy - sensorPxY;
                    double rotDeg = (Math.Atan2(vyp, vxp) * 180.0 / Math.PI) + 90.0;
                    var rect = new System.Windows.Shapes.Rectangle { Width = rectW, Height = rectH, Fill = Brushes.White, RadiusX = 1, RadiusY = 1 };
                    rect.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
                    rect.RenderTransform = new RotateTransform(rotDeg);
                    rect.Margin = new Thickness(vx - rectW / 2.0, vy - rectH / 2.0, 0, 0);
                    VerticalScopeCanvas.Children.Add(rect); Canvas.SetZIndex(rect, 1000);

                    // Draw vertical datablock
                    int altHundreds = (int)Math.Round(alt / 100.0);
                    int gs = (int)Math.Round(GetGroundSpeedKts(ac));
                    int gsTwoDigit = (int)Math.Round(gs / 10.0);
                    double labelX = Math.Min(Math.Max(4, vx + 8), vWidth - 80);
                    double labelY = Math.Min(Math.Max(4, vy + 6), workH - 8);
                    if (_showVerticalDatablocks)
                    {
                        var t1 = new TextBlock { Text = callsign, Foreground = Brushes.White, FontSize = 12, FontWeight = FontWeights.SemiBold };
                        t1.Margin = new Thickness(labelX, labelY, 0, 0); VerticalScopeCanvas.Children.Add(t1); Canvas.SetZIndex(t1, 1000);
                        var t2 = new TextBlock { Text = altHundreds.ToString("D3") + " " + gsTwoDigit.ToString("D2"), Foreground = Brushes.LightGray, FontSize = 11 };
                        t2.Margin = new Thickness(labelX, labelY + 16, 0, 0); VerticalScopeCanvas.Children.Add(t2); Canvas.SetZIndex(t2, 1000);
                    }
                }
            }
            catch { }

            // --- DRAW AZIMUTH SCOPE ---
            try
            {
                double totalRangeNmA = rangeNm + sensorOffsetNm;
                double maxCrossTrackNm = Math.Tan(DegToRad(halfAzDeg)) * rangeNm;
                double normAx = ((alongTrackFromThresholdNm + sensorOffsetNm) / totalRangeNmA);
                double normAy = 0.5 + (crossTrackFromThresholdNm / (2 * maxCrossTrackNm));
                normAx = Math.Max(0, Math.Min(1, normAx)); normAy = Math.Max(0, Math.Min(1, normAy));
                double curAx = normAx * aWidth, curAy = normAy * aHeight;

                // Draw current azimuth marker (only if vertical filtering allows it)
                if (curAx >= 0 && curAx <= aWidth && curAy >= 0 && curAy <= aHeight && vertFiltered)
                {
                    double rectW = 14.0, rectH = 4.0;
                    double sensorPxX = 0, sensorPxY = aHeight / 2.0;
                    double vxp = curAx - sensorPxX, vyp = curAy - sensorPxY;
                    double rotDeg = (Math.Atan2(vyp, vxp) * 180.0 / Math.PI) + 90.0;
                    var abar = new System.Windows.Shapes.Rectangle { Width = rectW, Height = rectH, Fill = Brushes.White, RadiusX = 1, RadiusY = 1 };
                    abar.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5); abar.RenderTransform = new RotateTransform(rotDeg);
                    abar.Margin = new Thickness(curAx - rectW / 2.0, curAy - rectH / 2.0, 0, 0); AzimuthScopeCanvas.Children.Add(abar); Canvas.SetZIndex(abar, 1000);

                    // datablock bottom-right of azimuth point
                    int altHundreds = (int)Math.Round(alt / 100.0);
                    int gs = (int)Math.Round(GetGroundSpeedKts(ac)); int gsTwoDigit = (int)Math.Round(gs / 10.0);
                    double aLabelX = Math.Min(Math.Max(4, curAx + 8), aWidth - 80);
                    double aLabelY = Math.Min(Math.Max(4, curAy + 6), aHeight - 8);
                    if (_showAzimuthDatablocks)
                    {
                        var at1 = new TextBlock { Text = callsign, Foreground = Brushes.White, FontSize = 12, FontWeight = FontWeights.SemiBold };
                        at1.Margin = new Thickness(aLabelX, aLabelY, 0, 0); AzimuthScopeCanvas.Children.Add(at1); Canvas.SetZIndex(at1, 1000);
                        var at2 = new TextBlock { Text = altHundreds.ToString("D3") + " " + gsTwoDigit.ToString("D2"), Foreground = Brushes.LightGray, FontSize = 11 };
                        at2.Margin = new Thickness(aLabelX, aLabelY + 16, 0, 0); AzimuthScopeCanvas.Children.Add(at2); Canvas.SetZIndex(at2, 1000);
                    }
                }
            }
            catch { }

            // --- DRAW PLAN VIEW ---
            try
            {
                double pcx = pWidth / 2.0, pcy = pHeight / 2.0;
                double maxRangeNm = rangeNm + 5;
                double nmPerPx = maxRangeNm / Math.Min(pWidth / 2.0, pHeight / 2.0);
                double eastFromThreshold = east_t, northFromThreshold = north_t;
                double eastNmP = eastFromThreshold / 1852.0, northNmP = northFromThreshold / 1852.0;
                double px = pcx + (eastNmP) / nmPerPx, py = pcy - (northNmP) / nmPerPx;

                // Draw current plan marker and datablock
                if (px >= 0 && px <= pWidth && py >= 0 && py <= pHeight && (!_hideGroundTraffic || !isGroundPlan) && !(_planAltTopHundreds > 0 && alt > _planAltTopHundreds * 100))
                {
                    double rectW = 10.0, rectH = 4.0;
                    double sensorPxX = pcx + (-sensorOffsetNm / nmPerPx) * Math.Sin(approachRad);
                    double sensorPxY = pcy + (sensorOffsetNm / nmPerPx) * Math.Cos(approachRad);
                    double vxp = px - sensorPxX, vyp = py - sensorPxY;
                    double rotDeg = (Math.Atan2(vyp, vxp) * 180.0 / Math.PI) + 90.0;
                    var rect = new Rectangle { Width = rectW, Height = rectH, Fill = Brushes.White, RadiusX = 1, RadiusY = 1 };
                    rect.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5); rect.RenderTransform = new RotateTransform(rotDeg);
                    rect.Margin = new Thickness(px - rectW / 2.0, py - rectH / 2.0, 0, 0); PlanViewCanvas.Children.Add(rect); Canvas.SetZIndex(rect, 1000);

                    // datablock bottom-right of plan point
                    int altHundreds = (int)Math.Round(alt / 100.0);
                    int gs = (int)Math.Round(GetGroundSpeedKts(ac)); int gsTwoDigit = (int)Math.Round(gs / 10.0);
                    double pLabelX = Math.Min(Math.Max(4, px + 6), pWidth - 80);
                    double pLabelY = Math.Min(Math.Max(4, py + 6), pHeight - 20);
                    if (_showPlanDatablocks)
                    {
                        var p1 = new TextBlock { Text = callsign, Foreground = Brushes.LightGray, FontSize = 11 };
                        p1.Margin = new Thickness(pLabelX, pLabelY, 0, 0); PlanViewCanvas.Children.Add(p1); Canvas.SetZIndex(p1, 1000);
                        var p2 = new TextBlock { Text = altHundreds.ToString("D3") + " " + gsTwoDigit.ToString("D2"), Foreground = Brushes.LightGray, FontSize = 11 };
                        p2.Margin = new Thickness(pLabelX, pLabelY + 16, 0, 0); PlanViewCanvas.Children.Add(p2); Canvas.SetZIndex(p2, 1000);
                    }
                }
            }
            catch { }
        }

        private static double GetDouble(Dictionary<string, object> dict, string key, double def)
        {
            if (!dict.ContainsKey(key) || dict[key] == null) return def;
            try
            {
                if (dict[key] is double) return (double)dict[key];
                if (dict[key] is float) return (float)dict[key];
                if (dict[key] is int) return (int)dict[key];
                if (dict[key] is long) return (long)dict[key];
                return Convert.ToDouble(dict[key]);
            }
            catch { return def; }
        }

        private static double GetGroundSpeedKts(Dictionary<string, object> dict)
        {
            // vPilot sends "speed_kts" field
            string[] keys = new[] { "speed_kts", "gs_kts", "gs", "ground_speed", "groundspeed", "kts" };
            foreach (var k in keys)
            {
                if (!dict.ContainsKey(k) || dict[k] == null) continue;
                var v = dict[k];
                try
                {
                    if (v is double) return (double)v;
                    if (v is float) return (float)v;
                    if (v is int) return (int)v;
                    if (v is long) return (long)v;
                    return Convert.ToDouble(v);
                }
                catch { }
            }
            return 0.0;
        }

        private void OnExitClick(object sender, RoutedEventArgs e)
        {
            _listening = false;
            try { if (_udpClient != null) _udpClient.Close(); } catch { }
            Application.Current.Shutdown();
        }

        protected override void OnClosed(EventArgs e)
        {
            _listening = false;
            try { if (_udpClient != null) _udpClient.Close(); } catch { }
            try { StopMetarRefreshTimer(); } catch { }
            base.OnClosed(e);
        }

        private void OnSelectRunwayClick(object sender, RoutedEventArgs e)
        {
            var dlg = new RunwayDialog();
            dlg.Owner = this;
            dlg.SetNASRLoader(_nasrLoader);
            if (_runway != null) dlg.SetInitial(_runway);
            bool? result = dlg.ShowDialog();
            if (result == true)
            {
                _runway = dlg.GetSettings();
                SaveRunwaySettings(_runway);
                // Display FAA LID for user, but use ICAO_ID for METAR
                string displayCode = !string.IsNullOrEmpty(_runway.FaaLid) ? _runway.FaaLid : _runway.Icao;
                RunwayText.Text = displayCode + " " + _runway.Runway;
                
                // Load METAR for newly selected runway and start auto-refresh
                LoadMetar();
                StartMetarRefreshTimer();
            }
        }

        private void OnOpenSimulatorClick(object sender, RoutedEventArgs e)
        {
            var win = new SimulatorWindow(_runway);
            win.Owner = this;
            win.Show();
        }

        private void OnShowAircraftListClick(object sender, RoutedEventArgs e)
        {
            var win = new AircraftListWindow(_aircraft);
            win.Owner = this;
            win.Show();
        }

        private static double DegToRad(double deg)
        {
            return deg * Math.PI / 180.0;
        }

        // Simple equirectangular approximation for small distances around runway threshold
        private static void GeoToEnu(double lat0, double lon0, double lat, double lon, out double east, out double north)
        {
            double rlat0 = DegToRad(lat0);
            double dlat = DegToRad(lat - lat0);
            double dlon = DegToRad(lon - lon0);
            double R = 6378137.0; // meters
            east = dlon * Math.Cos(rlat0) * R;
            north = dlat * R;
        }

        /// <summary>
        /// Project geographic coordinates to Vertical scope canvas position.
        /// Returns false if outside valid display range.
        /// </summary>
        private bool TryProjectToVertical(double lat, double lon, double alt, double canvasWidth, double canvasHeight, out double vx, out double vy)
        {
            vx = vy = 0;
            if (_runway == null) return false;
            
            double sensorOffsetNm = _runway.SensorOffsetNm > 0 ? _runway.SensorOffsetNm : 0.5;
            double rangeNm = _runway.RangeNm > 0 ? _runway.RangeNm : 10.0;
            double totalRangeNm = rangeNm + sensorOffsetNm;
            double bottomMargin = 30.0;
            double workH = Math.Max(0.0, canvasHeight - bottomMargin);
            
            double sensorLat, sensorLon;
            try { GetSensorLatLon(_runway, sensorOffsetNm, out sensorLat, out sensorLon); }
            catch { return false; }
            
            double east_t = 0, north_t = 0;
            GeoToEnu(_runway.ThresholdLat, _runway.ThresholdLon, lat, lon, out east_t, out north_t);
            
            double hdgRad = DegToRad(_runway.HeadingTrueDeg);
            double approachRad = hdgRad + Math.PI;
            double cosA = Math.Cos(approachRad), sinA = Math.Sin(approachRad);
            double alongFromThresholdNm = (north_t * cosA + east_t * sinA) / 1852.0;
            
            double altAt6DegAtFullRange = _runway.FieldElevFt + Math.Tan(DegToRad(6.0)) * (totalRangeNm * 6076.12);
            double altRangeFt = Math.Max(1.0, altAt6DegAtFullRange - _runway.FieldElevFt);
            
            double normX = ((alongFromThresholdNm + sensorOffsetNm) / totalRangeNm);
            double normAlt = (alt - _runway.FieldElevFt) / altRangeFt;
            
            normX = Math.Max(0, Math.Min(1, normX));
            normAlt = Math.Max(0, Math.Min(1, normAlt));
            
            vx = normX * canvasWidth;
            vy = workH - (normAlt * workH);
            
            return (vx >= 0 && vx <= canvasWidth && vy >= 0 && vy <= canvasHeight);
        }

        /// <summary>
        /// Project geographic coordinates to Azimuth scope canvas position.
        /// </summary>
        private bool TryProjectToAzimuth(double lat, double lon, double alt, double canvasWidth, double canvasHeight, out double ax, out double ay)
        {
            ax = ay = 0;
            if (_runway == null) return false;
            
            double sensorOffsetNm = _runway.SensorOffsetNm > 0 ? _runway.SensorOffsetNm : 0.5;
            double rangeNm = _runway.RangeNm > 0 ? _runway.RangeNm : 10.0;
            double totalRangeNmA = rangeNm + sensorOffsetNm;
            double halfAzDeg = GetHalfAzimuthDeg(_runway);
            double maxCrossTrackNm = Math.Tan(DegToRad(halfAzDeg)) * rangeNm;
            
            double east_t = 0, north_t = 0;
            GeoToEnu(_runway.ThresholdLat, _runway.ThresholdLon, lat, lon, out east_t, out north_t);
            
            double hdgRad = DegToRad(_runway.HeadingTrueDeg);
            double approachRad = hdgRad + Math.PI;
            double cosA = Math.Cos(approachRad), sinA = Math.Sin(approachRad);
            double alongFromThresholdNm = (north_t * cosA + east_t * sinA) / 1852.0;
            double crossFromThresholdNm = (-north_t * sinA + east_t * cosA) / 1852.0;
            
            double normAx = ((alongFromThresholdNm + sensorOffsetNm) / totalRangeNmA);
            double normAy = 0.5 + (crossFromThresholdNm / (2 * maxCrossTrackNm));
            
            normAx = Math.Max(0, Math.Min(1, normAx));
            normAy = Math.Max(0, Math.Min(1, normAy));
            
            ax = normAx * canvasWidth;
            ay = normAy * canvasHeight;
            
            return (ax >= 0 && ax <= canvasWidth && ay >= 0 && ay <= canvasHeight);
        }

        /// <summary>
        /// Project geographic coordinates to Plan view canvas position.
        /// </summary>
        private bool TryProjectToPlan(double lat, double lon, double alt, double canvasWidth, double canvasHeight, out double px, out double py)
        {
            px = py = 0;
            if (_runway == null) return false;
            
            double rangeNm = _runway.RangeNm > 0 ? _runway.RangeNm : 10.0;
            double maxRangeNm = rangeNm + 5;
            double pcx = canvasWidth / 2.0, pcy = canvasHeight / 2.0;
            double nmPerPx = maxRangeNm / Math.Min(canvasWidth / 2.0, canvasHeight / 2.0);
            
            double eastFromThreshold = 0, northFromThreshold = 0;
            GeoToEnu(_runway.ThresholdLat, _runway.ThresholdLon, lat, lon, out eastFromThreshold, out northFromThreshold);
            
            double eastNm = eastFromThreshold / 1852.0, northNm = northFromThreshold / 1852.0;
            
            px = pcx + (eastNm) / nmPerPx;
            py = pcy - (northNm) / nmPerPx;
            
            return (px >= 0 && px <= canvasWidth && py >= 0 && py <= canvasHeight);
        }

        // Compatibility shim: allow external callers (simulator/plugin) to clear per-callsign history
        public void ClearHistoryForCallsign(string callsign)
        {
            // No-op: history is now sweep-based, not per-callsign
            // Keep method for backward compatibility
        }

        /// <summary>
        /// Project a geographic point into the Vertical scope canvas coordinates and return whether it was within azimuth/vertical wedges.
        /// This centralizes the math used by both current-target drawing and history capture so they match precisely.
        /// </summary>
        private void TryProjectVerticalPoint(RunwaySettings rs, double lat, double lon, double alt, double canvasWidth, double canvasHeight, out double vx, out double vy, out bool seenVertical, out bool seenAzimuth)
        {
            vx = 0; vy = 0; seenVertical = false; seenAzimuth = false;
            if (rs == null) return;

            double sensorOffsetNm = rs.SensorOffsetNm > 0 ? rs.SensorOffsetNm : 0.5;
            double rangeNm = rs.RangeNm > 0 ? rs.RangeNm : 10.0;
            double totalRangeNm = rangeNm + sensorOffsetNm;
            double bottomMargin = 30.0;
            double workH = Math.Max(0.0, canvasHeight - bottomMargin);

            // compute sensor apex location
            double sensorLat, sensorLon;
            try { GetSensorLatLon(rs, sensorOffsetNm, out sensorLat, out sensorLon); }
            catch { sensorLat = rs.ThresholdLat; sensorLon = rs.ThresholdLon; }

            // ENU relative to sensor and threshold
            double east_s = 0, north_s = 0;
            GeoToEnu(sensorLat, sensorLon, lat, lon, out east_s, out north_s);
            double east_t = 0, north_t = 0;
            GeoToEnu(rs.ThresholdLat, rs.ThresholdLon, lat, lon, out east_t, out north_t);

            double hdgRad = DegToRad(rs.HeadingTrueDeg);
            double approachRad = hdgRad + Math.PI;
            double cosA = Math.Cos(approachRad), sinA = Math.Sin(approachRad);

            double alongFromSensorNm = (north_s * cosA + east_s * sinA) / 1852.0;
            double crossFromSensorNm = (-north_s * sinA + east_s * cosA) / 1852.0;
            double alongFromThresholdNm = (north_t * cosA + east_t * sinA) / 1852.0;
            double crossFromThresholdNm = (-north_t * sinA + east_t * cosA) / 1852.0;

            // angles
            double azimuthDeg = 0.0, elevationDeg = 0.0;
            if (Math.Abs(alongFromSensorNm) > 0.0001)
            {
                azimuthDeg = Math.Atan2(crossFromSensorNm, alongFromSensorNm) * 180.0 / Math.PI;
                double distFt = Math.Abs(alongFromSensorNm) * 6076.12;
                double altRef = rs.FieldElevFt;
                elevationDeg = Math.Atan2(alt - altRef, distFt) * 180.0 / Math.PI;
            }

            double includeNegBuffer = 0.3, includePosBuffer = 0.5;
            double halfAzDeg = GetHalfAzimuthDeg(rs);
            bool inAzimuthScope = Math.Abs(azimuthDeg) <= halfAzDeg && alongFromSensorNm >= -includeNegBuffer && alongFromSensorNm <= rangeNm + includePosBuffer;
            bool inVerticalScope = inAzimuthScope && elevationDeg <= 6.0;

            seenAzimuth = inAzimuthScope;
            seenVertical = inVerticalScope;

            // projection to pixels: X = along-from-threshold normalized across totalRangeNm, Y = altitude relative to 6deg ceiling
            double altAt6DegAtFullRange = rs.FieldElevFt + Math.Tan(DegToRad(6.0)) * (totalRangeNm * 6076.12);
            double altRangeFt = Math.Max(1.0, altAt6DegAtFullRange - rs.FieldElevFt);

            double normX = ((alongFromThresholdNm + sensorOffsetNm) / totalRangeNm);
            normX = Math.Max(0, Math.Min(1, normX));
            double normAlt = (alt - rs.FieldElevFt) / altRangeFt; normAlt = Math.Max(0, Math.Min(1, normAlt));

            vx = normX * canvasWidth;
            vy = workH - (normAlt * workH);
        }

        /// <summary>
        /// Map NASR approach lighting system code to nominal approach light length in feet.
        /// ALSF-1 / ALSF-2 / MALSR => 2400 ft
        /// SSALR / SSALS / SSALF => 1500 ft
        /// Unknown or null => 0
        /// </summary>
        /// <param name="apchCode">NASR APCH_LGT_SYSTEM_CODE value</param>
        /// <returns>Length in feet (0 = none)</returns>
        public static double GetApproachLightLengthFt(string apchCode)
        {
            if (string.IsNullOrEmpty(apchCode)) return 0;
            var c = apchCode.Trim().ToUpperInvariant();
            // Some NASR data may include variants or spacing, normalize common forms
            if (c.Contains("ALSF-1") || c.Contains("ALSF1") || c.Contains("ALSF-1")) return 2400.0;
            if (c.Contains("ALSF-2") || c.Contains("ALSF2") || c.Contains("ALSF-2")) return 2400.0;
            if (c.Contains("MALSR")) return 2400.0;
            if (c.Contains("SSALR") || c.Contains("SSALS") || c.Contains("SSALF")) return 1500.0;
            return 0.0;
        }

        private RunwaySettings LoadRunwaySettings()
        {
            try
            {
                string appDataPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "VATSIM-PAR-Scope");
                string settingsFile = System.IO.Path.Combine(appDataPath, "runway_settings.json");
                
                if (!System.IO.File.Exists(settingsFile))
                    return null;
                
                string json = System.IO.File.ReadAllText(settingsFile);
                return _json.Deserialize<RunwaySettings>(json);
            }
            catch
            {
                return null;
            }
        }

        private void SaveRunwaySettings(RunwaySettings settings)
        {
            if (settings == null) return;
            try
            {
                string appDataPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "VATSIM-PAR-Scope");
                System.IO.Directory.CreateDirectory(appDataPath);
                
                string settingsFile = System.IO.Path.Combine(appDataPath, "runway_settings.json");
                string json = _json.Serialize(settings);
                System.IO.File.WriteAllText(settingsFile, json);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Error saving runway settings: " + ex.Message);
            }
        }

        public class RunwaySettings
        {
            public string Icao;  // ICAO_ID for METAR lookups ONLY (e.g., KORD, PHNL, PHOG)
            public string FaaLid; // FAA airport code for display (e.g., ORD, HNL, OGG)
            public string Runway;
            public double ThresholdLat;
            public double ThresholdLon;
            public double HeadingTrueDeg;
            public double GlideSlopeDeg;
            public double ThrCrossingHgtFt; // Added TCH field
            public double FieldElevFt;
            public double RangeNm;
            public double DecisionHeightFt;
            public double MaxAzimuthDeg;
            public double VerticalCeilingFt;
            public double SensorOffsetNm;
            public double ApproachLightLengthFt;
            // Magnetic variation (signed degrees). West is positive (+), East is negative (-)
            // (i.e. Magnetic = True + MagVariationDeg when MagVariationDeg is West-positive)
            public double MagVariationDeg;
        }

        private void DrawGlideSlope(System.Windows.Controls.Canvas canvas, double w, double h, double rangeNm)
        {
            RunwaySettings rs = GetActiveRunwayDefaults();
            double bottomMargin = 30;
            double workH = h - bottomMargin;

            double sensorOffsetNm = rs.SensorOffsetNm > 0 ? rs.SensorOffsetNm : 0.5;
            double totalRangeNm = rangeNm + sensorOffsetNm;
            double pxPerNm = w / totalRangeNm;
            double thresholdX = sensorOffsetNm * pxPerNm;

            // Scale based on field elevation to 6° wedge ceiling
            double fieldElevFt = rs.FieldElevFt;
            double altAt6DegAtFullRange = fieldElevFt + Math.Tan(DegToRad(6.0)) * (totalRangeNm * 6076.12);
            double altRangeFt = altAt6DegAtFullRange - fieldElevFt;
            double pxPerFt = workH / altRangeFt;

            double gsRad = DegToRad(rs.GlideSlopeDeg);
            // GS passes through threshold at field elevation + TCH
            double tch = rs.ThrCrossingHgtFt > 0 ? rs.ThrCrossingHgtFt : 0;
            double alt0 = fieldElevFt + tch; // Altitude at threshold
            double altEnd = fieldElevFt + tch + Math.Tan(gsRad) * (rangeNm * 6076.12); // Altitude at far end

            // Convert to screen coordinates (field elevation at bottom)
            double x1 = thresholdX;
            double y1 = Math.Max(0, Math.Min(workH, workH - ((alt0 - fieldElevFt) * pxPerFt)));
            double x2 = thresholdX + (rangeNm * pxPerNm); // End of display range, not right edge
            double y2 = Math.Max(0, Math.Min(workH, workH - ((altEnd - fieldElevFt) * pxPerFt)));

            var gs = new Line();
            gs.Stroke = _centerlineBrush;
            gs.StrokeThickness = 2;
            gs.X1 = x1; gs.Y1 = y1; gs.X2 = x2; gs.Y2 = y2;
            canvas.Children.Add(gs);

            // Touchdown (where GS reaches field elevation)
            if (gsRad > 0.0001 && tch > 0)
            {
                double dTdzNm = (tch / Math.Tan(gsRad)) / 6076.12; // NM from threshold
                // TDZ marker removed - runway line now marks the touchdown point
            }
        }

        /// <summary>
        /// Update the NASR status text block with version (from filename) and last-loaded timestamp.
        /// Format: "02OCT2025 — 2025-10-02 12:34Z (2445 airports)" if data present, else "(not loaded)".
        /// </summary>
        private void UpdateNasrStatus()
        {
            try
            {
                if (NASRStatusText == null) return;
                // Ensure cache is loaded if available
                try { if (_nasrLoader != null) _nasrLoader.EnsureCacheLoaded(); } catch { }

                // Determine airport count from in-memory or on-disk cache
                int airportCount = 0;
                if (_nasrLoader != null)
                {
                    try { airportCount = _nasrLoader.GetAirportIds()?.Count ?? 0; } catch { airportCount = 0; }
                    if (airportCount <= 0)
                    {
                        try { airportCount = _nasrLoader.ReadCachedAirportIds()?.Count ?? 0; } catch { airportCount = 0; }
                    }
                }

                // If we have data despite missing LastLoadedSource, show a sensible cached status
                if (_nasrLoader == null || (string.IsNullOrEmpty(_nasrLoader.LastLoadedSource) && airportCount <= 0))
                {
                    NASRStatusText.Text = "(not loaded)";
                    return;
                }

                // Prefer metadata timestamp if cached
                string version = null;
                if (_nasrLoader.LastLoadedSource != null)
                {
                    // Try to parse file name for DD_MMM_YYYY pattern
                    try
                    {
                        var src = _nasrLoader.LastLoadedSource;
                        var fn = System.IO.Path.GetFileName(src).ToUpperInvariant();
                        // Filename may be like 02_Oct_2025_APT_CSV.zip -> normalize to 02OCT2025
                        var parts = fn.Split('_');
                        if (parts.Length >= 3)
                        {
                            string day = parts[0].PadLeft(2, '0');
                            string mon = parts[1].ToUpperInvariant();
                            string year = parts[2];
                            version = day + mon + year;
                        }
                    }
                    catch { }
                }

                string timeStr = null;
                if (_nasrLoader.LastLoadedUtc.HasValue)
                {
                    var dt = _nasrLoader.LastLoadedUtc.Value;
                    // show local-ish user-friendly time in UTC 'yyyy-MM-dd HH:mmZ'
                    timeStr = dt.ToString("yyyy-MM-dd HH:mm\"Z\"");
                }

                // Airport count if available
                string countStr = null;
                if (airportCount > 0) countStr = airportCount.ToString();

                var partsOut = new List<string>();
                if (!string.IsNullOrEmpty(version)) partsOut.Add(version);
                if (!string.IsNullOrEmpty(timeStr)) partsOut.Add(timeStr);
                if (!string.IsNullOrEmpty(countStr)) partsOut.Add(countStr + " airports");

                if (partsOut.Count == 0)
                {
                    if (!string.IsNullOrEmpty(_nasrLoader.LastLoadedSource)) NASRStatusText.Text = _nasrLoader.LastLoadedSource;
                    else if (airportCount > 0) NASRStatusText.Text = $"(cached) — {airportCount} airports";
                    else NASRStatusText.Text = "(not loaded)";
                }
                else NASRStatusText.Text = string.Join(" — ", partsOut);
            }
            catch { }
        }

        // ========================================================================
        // METAR Functionality (Phase 2 - Network Calls)
        // ========================================================================

        private void LoadMetar()
        {
            try
            {
                // Get ICAO code from current runway
                string icao = _runway?.Icao ?? string.Empty;
                
                if (string.IsNullOrEmpty(icao))
                {
                    _currentMetar = "(no runway selected)";
                    UpdateMetarDisplay();
                    return;
                }

                // Show loading status
                _currentMetar = $"(loading METAR for {icao}...)";
                UpdateMetarDisplay();

                // Fetch METAR asynchronously based on selected source
                Task.Run(() =>
                {
                    try
                    {
                        string metar = string.Empty;
                        if (_metarSource == MetarSource.NOAA)
                        {
                            metar = FetchMetarFromNOAA(icao);
                        }
                        else // VATSIM
                        {
                            metar = FetchMetarFromVATSIM(icao);
                        }

                        // Update UI on dispatcher thread
                        Dispatcher.Invoke(() =>
                        {
                            if (string.IsNullOrEmpty(metar))
                            {
                                _currentMetar = $"(no METAR for {icao})";
                            }
                            else
                            {
                                _currentMetar = CleanMetarString(metar);
                            }
                            UpdateMetarDisplay();
                        });
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"LoadMetar async error: {ex.Message}");
                        Dispatcher.Invoke(() =>
                        {
                            _currentMetar = $"(error loading {icao})";
                            UpdateMetarDisplay();
                        });
                    }
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"LoadMetar error: {ex.Message}");
                _currentMetar = "(error)";
                UpdateMetarDisplay();
            }
        }

        private string FetchMetarFromNOAA(string icao)
        {
            try
            {
                string url = $"https://aviationweather.gov/api/data/metar?ids={icao}&format=json";
                using (var client = new WebClient())
                {
                    client.Headers.Add("User-Agent", "VATSIM-PAR-Scope/1.0");
                    string response = client.DownloadString(url);
                    
                    // Parse JSON response - it's an array with one object containing "rawOb" field
                    var serializer = new JavaScriptSerializer();
                    var data = serializer.Deserialize<List<Dictionary<string, object>>>(response);
                    
                    if (data != null && data.Count > 0 && data[0].ContainsKey("rawOb"))
                    {
                        return data[0]["rawOb"].ToString();
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"FetchMetarFromNOAA error: {ex.Message}");
            }
            return string.Empty;
        }

        private string FetchMetarFromVATSIM(string icao)
        {
            try
            {
                string url = $"https://metar.vatsim.net/{icao}";
                using (var client = new WebClient())
                {
                    client.Headers.Add("User-Agent", "VATSIM-PAR-Scope/1.0");
                    string response = client.DownloadString(url);
                    
                    // VATSIM returns plain text METAR
                    return response?.Trim() ?? string.Empty;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"FetchMetarFromVATSIM error: {ex.Message}");
            }
            return string.Empty;
        }

        private void UpdateMetarDisplay()
        {
            try
            {
                if (MetarText == null) return;

                if (string.IsNullOrEmpty(_currentMetar))
                {
                    MetarText.Text = "(no data)";
                    MetarText.Foreground = Brushes.Gray;
                    return;
                }

                string displayText = _metarShowFull ? _currentMetar : AbbreviateMetar(_currentMetar);
                MetarText.Text = displayText;
                
                // Color code based on flight category
                MetarText.Foreground = GetFlightCategoryColor(_currentMetar);
            }
            catch { }
        }

        private string CleanMetarString(string metar)
        {
            if (string.IsNullOrEmpty(metar)) return metar;
            
            // Remove "METAR" prefix if present
            if (metar.StartsWith("METAR ", StringComparison.OrdinalIgnoreCase))
            {
                return metar.Substring(6).TrimStart();
            }
            
            return metar;
        }

        private Brush GetFlightCategoryColor(string metar)
        {
            if (string.IsNullOrEmpty(metar)) return Brushes.Gray;
            
            try
            {
                // CAVOK = VFR automatically
                if (metar.Contains("CAVOK")) return Brushes.Green;
                
                // Parse ceiling and visibility
                double? ceilingAgl = ParseCeiling(metar);
                double? visibilitySm = ParseVisibility(metar);
                
                // LIFR: Ceiling < 500 AGL and/or visibility < 1 SM
                if ((ceilingAgl.HasValue && ceilingAgl.Value < 500) || 
                    (visibilitySm.HasValue && visibilitySm.Value < 1))
                {
                    return Brushes.Magenta;
                }
                
                // IFR: Ceiling 500-999 AGL and/or visibility 1-3 SM
                if ((ceilingAgl.HasValue && ceilingAgl.Value >= 500 && ceilingAgl.Value < 1000) ||
                    (visibilitySm.HasValue && visibilitySm.Value >= 1 && visibilitySm.Value < 3))
                {
                    return Brushes.Red;
                }
                
                // MVFR: Ceiling 1000-3000 AGL and/or visibility 3-5 SM
                if ((ceilingAgl.HasValue && ceilingAgl.Value >= 1000 && ceilingAgl.Value <= 3000) ||
                    (visibilitySm.HasValue && visibilitySm.Value >= 3 && visibilitySm.Value <= 5))
                {
                    return Brushes.Blue;
                }
                
                // VFR: Ceiling > 3000 AGL AND visibility > 5 SM (both must qualify)
                if ((ceilingAgl.HasValue && ceilingAgl.Value > 3000 || !ceilingAgl.HasValue) &&
                    (visibilitySm.HasValue && visibilitySm.Value > 5))
                {
                    return Brushes.Green;
                }
                
                // Default if we can't determine
                return Brushes.Black;
            }
            catch
            {
                return Brushes.Black;
            }
        }

        private double? ParseCeiling(string metar)
        {
            try
            {
                var parts = metar.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                
                foreach (var part in parts)
                {
                    // Check for BKN, OVC, or VV (vertical visibility)
                    if (part.StartsWith("BKN") || part.StartsWith("OVC") || part.StartsWith("VV"))
                    {
                        // Extract the numeric part (next 3 digits after BKN/OVC/VV)
                        string code = part.Substring(0, Math.Min(3, part.Length));
                        string heightStr = part.Substring(code.Length);
                        
                        // Take first 3 digits
                        if (heightStr.Length >= 3)
                        {
                            heightStr = heightStr.Substring(0, 3);
                            if (int.TryParse(heightStr, out int heightHundreds))
                            {
                                // Convert hundreds of feet to feet AGL
                                return heightHundreds * 100.0;
                            }
                        }
                    }
                }
                
                // No ceiling found (CLR/SKC/FEW/SCT only)
                return null;
            }
            catch
            {
                return null;
            }
        }

        private double? ParseVisibility(string metar)
        {
            try
            {
                var parts = metar.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                
                // Visibility typically comes after the time (which ends in Z)
                bool foundTime = false;
                foreach (var part in parts)
                {
                    if (part.EndsWith("Z"))
                    {
                        foundTime = true;
                        continue;
                    }
                    
                    if (!foundTime) continue;
                    
                    // US format: ends with SM (statute miles)
                    if (part.EndsWith("SM"))
                    {
                        string visStr = part.Substring(0, part.Length - 2);
                        
                        // Handle fractions like "1/2SM" or "1 1/2SM"
                        if (visStr.Contains("/"))
                        {
                            // Parse fraction
                            var fractionParts = visStr.Split(new[] { ' ', '/' }, StringSplitOptions.RemoveEmptyEntries);
                            if (fractionParts.Length == 2)
                            {
                                // Simple fraction like "1/2"
                                if (double.TryParse(fractionParts[0], out double num) &&
                                    double.TryParse(fractionParts[1], out double denom) && denom != 0)
                                {
                                    return num / denom;
                                }
                            }
                            else if (fractionParts.Length == 3)
                            {
                                // Mixed fraction like "1 1/2"
                                if (double.TryParse(fractionParts[0], out double whole) &&
                                    double.TryParse(fractionParts[1], out double num) &&
                                    double.TryParse(fractionParts[2], out double denom) && denom != 0)
                                {
                                    return whole + (num / denom);
                                }
                            }
                        }
                        else if (double.TryParse(visStr, out double vis))
                        {
                            return vis;
                        }
                    }
                    // Metric format: 4 digits (meters), convert to statute miles
                    else if (part.Length == 4 && int.TryParse(part, out int visMeters))
                    {
                        // Convert meters to statute miles (1 SM = 1609.34 meters)
                        return visMeters / 1609.34;
                    }
                    
                    // Stop at first weather phenomenon or cloud layer
                    if (part.StartsWith("FEW") || part.StartsWith("SCT") || 
                        part.StartsWith("BKN") || part.StartsWith("OVC") ||
                        part.StartsWith("+") || part.StartsWith("-") || part == "VC")
                    {
                        break;
                    }
                }
                
                return null;
            }
            catch
            {
                return null;
            }
        }

        private string AbbreviateMetar(string fullMetar)
        {
            if (string.IsNullOrEmpty(fullMetar)) return string.Empty;

            try
            {
                // Abbreviated METAR: Time (ends in Z) + Wind (includes KT or MPS) + Altimeter (begins with A or Q)
                var parts = fullMetar.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                var abbreviated = new List<string>();

                foreach (var part in parts)
                {
                    // Time: ends with Z
                    if (part.EndsWith("Z"))
                    {
                        abbreviated.Add(part);
                    }
                    // Wind: contains KT or MPS
                    else if (part.Contains("KT") || part.Contains("MPS"))
                    {
                        abbreviated.Add(part);
                    }
                    // Altimeter: starts with A or Q
                    else if (part.StartsWith("A") || part.StartsWith("Q"))
                    {
                        // Check if it looks like an altimeter setting (A#### or Q####)
                        if (part.Length >= 5 && char.IsDigit(part[1]))
                        {
                            abbreviated.Add(part);
                        }
                    }
                }

                return abbreviated.Count > 0 ? string.Join(" ", abbreviated) : "(abbreviated unavailable)";
            }
            catch
            {
                return fullMetar; // fallback to full if abbreviation fails
            }
        }

        private void StartMetarRefreshTimer()
        {
            try
            {
                if (_metarRefreshTimer != null)
                {
                    _metarRefreshTimer.Stop();
                    _metarRefreshTimer.Start();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"StartMetarRefreshTimer error: {ex.Message}");
            }
        }

        private void StopMetarRefreshTimer()
        {
            try
            {
                if (_metarRefreshTimer != null)
                {
                    _metarRefreshTimer.Stop();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"StopMetarRefreshTimer error: {ex.Message}");
            }
        }

        private void OnDownloadNASRClick(object sender, RoutedEventArgs e)
        {
            if (_nasrLoader == null)
                _nasrLoader = new NASRDataLoader();

            var progressWindow = new Window
            {
                Title = "Download NASR Data",
                Width = 400,
                Height = 150,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize
            };

            var stack = new StackPanel { Margin = new Thickness(20) };
            var msgText = new TextBlock { Text = "Downloading latest FAA NASR data...", TextWrapping = TextWrapping.Wrap };
            stack.Children.Add(msgText);
            progressWindow.Content = stack;

            progressWindow.Show();

            Task.Run(() =>
            {
                string errorMsg;
                bool success = _nasrLoader.TryLoadLatestData(out errorMsg);

                Dispatcher.Invoke(() =>
                {
                    progressWindow.Close();
                    if (success)
                    {
                        int airportCount = _nasrLoader.GetAirportIds().Count;
                        MessageBox.Show(this, string.Format("NASR data loaded successfully!\n\n{0} airports in database.\nSource: {1}", airportCount, _nasrLoader.LastLoadedSource ?? "(unknown)"), "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                        try { UpdateNasrStatus(); } catch { }
                        
                        // Immediately save app state to persist NASR cache
                        try { SaveAppState(); } catch (Exception ex) { Debug.WriteLine("Error saving app state after NASR download: " + ex.Message); }
                    }
                    else
                    {
                        MessageBox.Show(this, 
                            "Failed to download NASR data:\n\n" + errorMsg,
                            "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                });
            });
        }

        // FileSystemWatcher handlers - debounce any change and reload cache
        private void NasrWatcher_Changed(object sender, FileSystemEventArgs e)
        {
            try
            {
                if (_nasrReloadTimer != null)
                {
                    _nasrReloadTimer.Stop();
                    _nasrReloadTimer.Start();
                }
            }
            catch { }
        }

        private void NasrWatcher_Renamed(object sender, RenamedEventArgs e)
        {
            try
            {
                if (_nasrReloadTimer != null)
                {
                    _nasrReloadTimer.Stop();
                    _nasrReloadTimer.Start();
                }
            }
            catch { }
        }

        private void OnLoadNASRFileClick(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog();
            dlg.Filter = "ZIP files (*.zip)|*.zip|All files (*.*)|*.*";
            dlg.Title = "Select NASR APT_CSV.zip file";

            bool result;
            bool? dlgResult = dlg.ShowDialog(this);
            result = dlgResult.HasValue && dlgResult.Value;

            if (!result)
                return;

            if (_nasrLoader == null)
                _nasrLoader = new NASRDataLoader();

            string errorMsg;
        if (_nasrLoader.TryLoadFromFile(dlg.FileName, out errorMsg))
            {
                int airportCount = _nasrLoader.GetAirportIds().Count;
                MessageBox.Show(this, 
            string.Format("NASR data loaded successfully!\n\n{0} airports in database.\nSource: {1}", airportCount, _nasrLoader.LastLoadedSource ?? "(unknown)"),
                    "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                try { UpdateNasrStatus(); } catch { }
                
                // Immediately save app state to persist NASR cache
                try { SaveAppState(); } catch (Exception ex) { Debug.WriteLine("Error saving app state after NASR file load: " + ex.Message); }
            }
            else
            {
                MessageBox.Show(this, 
                    "Failed to load NASR data:\n\n" + errorMsg,
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void LoadWindowPosition()
        {
            try
            {
                string appDataPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "VATSIM-PAR-Scope");
                string settingsFile = System.IO.Path.Combine(appDataPath, "window_position.json");
                
                if (!System.IO.File.Exists(settingsFile))
                    return;
                
                string json = System.IO.File.ReadAllText(settingsFile);
                var pos = _json.Deserialize<WindowPosition>(json);
                
                if (pos != null)
                {
                    // Restore window position - for maximized windows on secondary monitors,
                    // we need to set Left/Top BEFORE maximizing to anchor to correct screen
                    this.Left = pos.Left;
                    this.Top = pos.Top;
                    this.Width = pos.Width;
                    this.Height = pos.Height;
                    
                    // Apply maximized state AFTER setting position so WPF anchors to correct monitor
                    if (pos.IsMaximized)
                    {
                        // Force layout update before maximizing to ensure position is applied
                        this.WindowState = WindowState.Normal;
                        this.UpdateLayout();
                        this.WindowState = WindowState.Maximized;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Error loading window position: " + ex.Message);
            }
        }

        private void SaveWindowPosition()
        {
            try
            {
                string appDataPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "VATSIM-PAR-Scope");
                System.IO.Directory.CreateDirectory(appDataPath);
                
                string settingsFile = System.IO.Path.Combine(appDataPath, "window_position.json");
                
                // Capture which screen the window is on by saving screen bounds
                var screen = System.Windows.Forms.Screen.FromHandle(new System.Windows.Interop.WindowInteropHelper(this).Handle);
                
                var pos = new WindowPosition
                {
                    Left = this.Left,
                    Top = this.Top,
                    Width = this.Width,
                    Height = this.Height,
                    IsMaximized = this.WindowState == WindowState.Maximized,
                    ScreenLeft = screen.Bounds.Left,
                    ScreenTop = screen.Bounds.Top,
                    ScreenWidth = screen.Bounds.Width,
                    ScreenHeight = screen.Bounds.Height
                };
                
                string json = _json.Serialize(pos);
                System.IO.File.WriteAllText(settingsFile, json);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Error saving window position: " + ex.Message);
            }
        }

        private void LoadShowGroundSetting()
        {
            try
            {
                string appDataPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "VATSIM-PAR-Scope");
                string settingsFile = System.IO.Path.Combine(appDataPath, "show_ground.txt");
                
                if (System.IO.File.Exists(settingsFile))
                {
                    string value = System.IO.File.ReadAllText(settingsFile).Trim();
                    bool showGround = value.Equals("true", StringComparison.OrdinalIgnoreCase);
                    // Checkbox reflects "show" semantics; internal flag is "hide" so invert it
                    HideGroundCheckBox.IsChecked = showGround;
                    _hideGroundTraffic = !showGround;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Error loading show ground setting: " + ex.Message);
            }
        }

        private void SaveShowGroundSetting()
        {
            try
            {
                string appDataPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "VATSIM-PAR-Scope");
                System.IO.Directory.CreateDirectory(appDataPath);
                
                string settingsFile = System.IO.Path.Combine(appDataPath, "show_ground.txt");
                // Persist the checkbox "show" semantics so the file contains true when the UI is set to show ground
                bool showGround = !_hideGroundTraffic;
                System.IO.File.WriteAllText(settingsFile, showGround.ToString());
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Error saving show ground setting: " + ex.Message);
            }
        }

        private void LoadHistoryDotsCount()
        {
            try
            {
                string appDataPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "VATSIM-PAR-Scope");
                string settingsFile = System.IO.Path.Combine(appDataPath, "history_dots.txt");
                
                if (System.IO.File.Exists(settingsFile))
                {
                    string value = System.IO.File.ReadAllText(settingsFile).Trim();
                    if (int.TryParse(value, out int count))
                    {
                        _historyDotsCount = Math.Max(1, Math.Min(20, count)); // Clamp to 1-20
                        HistoryDotsSlider.Value = _historyDotsCount;
                        HistoryDotsLabel.Text = _historyDotsCount.ToString();
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Error loading history dots count: " + ex.Message);
            }
        }

        private void SavePlanAltTop()
        {
            try
            {
                string appDataPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "VATSIM-PAR-Scope");
                System.IO.Directory.CreateDirectory(appDataPath);
                string settingsFile = System.IO.Path.Combine(appDataPath, "plan_alt_top.txt");
                System.IO.File.WriteAllText(settingsFile, _planAltTopHundreds.ToString());
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Error saving plan alt top: " + ex.Message);
            }
        }

        private void LoadPlanAltTop()
        {
            try
            {
                string appDataPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "VATSIM-PAR-Scope");
                string settingsFile = System.IO.Path.Combine(appDataPath, "plan_alt_top.txt");
                if (System.IO.File.Exists(settingsFile))
                {
                    string value = System.IO.File.ReadAllText(settingsFile).Trim();
                    if (int.TryParse(value, out int v))
                    {
                        _planAltTopHundreds = Math.Max(1, Math.Min(9999, v));
                        if (PlanAltTopBox != null) PlanAltTopBox.Text = _planAltTopHundreds.ToString();
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Error loading plan alt top: " + ex.Message);
            }
        }

        private void OnPlanAltTopChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (PlanAltTopBox == null) return;
            if (int.TryParse(PlanAltTopBox.Text, out int v))
            {
                _planAltTopHundreds = Math.Max(1, Math.Min(9999, v));
                SavePlanAltTop();
            }
        }

        private void OnPlanAltTopUp(object sender, RoutedEventArgs e) { AdjustValue(PlanAltTopBox, 1); }
        private void OnPlanAltTopDown(object sender, RoutedEventArgs e) { AdjustValue(PlanAltTopBox, -1); }

        private void SaveHistoryDotsCount()
        {
            try
            {
                string appDataPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "VATSIM-PAR-Scope");
                System.IO.Directory.CreateDirectory(appDataPath);
                
                string settingsFile = System.IO.Path.Combine(appDataPath, "history_dots.txt");
                System.IO.File.WriteAllText(settingsFile, _historyDotsCount.ToString());
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Error saving history dots count: " + ex.Message);
            }
        }

        /// <summary>
        /// Load application state from single JSON file (app_state.json) in AppData.
        /// If not present, falls back to legacy per-file loaders.
        /// If NASR cache content is embedded, write it out to nasr_cache.json so NASRDataLoader can pick it up.
        /// </summary>
        private void LoadAppState()
        {
            string logPath = null;
            try
            {
                string appDataPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VATSIM-PAR-Scope");
                logPath = System.IO.Path.Combine(appDataPath, "app_state_log.txt");
                
                void Log(string msg)
                {
                    try 
                    { 
                        System.IO.File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {msg}\r\n"); 
                        Debug.WriteLine(msg);
                    } 
                    catch { }
                }
                
                Log("LoadAppState: Starting...");
                
                string stateFile = System.IO.Path.Combine(appDataPath, AppStateFileName);
                Log($"LoadAppState: Checking for state file at: {stateFile}");
                
                if (!System.IO.File.Exists(stateFile))
                {
                    Log("LoadAppState: State file not found, using legacy loaders");
                    // Fallback to existing per-file loaders
                    _runway = LoadRunwaySettings();
                    LoadWindowPosition();
                    LoadShowGroundSetting();
                    LoadHistoryDotsCount();
                    LoadPlanAltTop();
                    return;
                }

                Log($"LoadAppState: State file found, reading...");
                string json = System.IO.File.ReadAllText(stateFile, Encoding.UTF8);
                Log($"LoadAppState: Read {json.Length} bytes");
                
                var obj = _json.DeserializeObject(json) as Dictionary<string, object>;
                if (obj == null)
                {
                    Log("LoadAppState: Failed to deserialize, using legacy loaders");
                    // fallback
                    _runway = LoadRunwaySettings();
                    LoadWindowPosition();
                    LoadShowGroundSetting();
                    LoadHistoryDotsCount();
                    LoadPlanAltTop();
                    return;
                }

                // Restore runway
                if (obj.TryGetValue("runway", out var runObj) && runObj is Dictionary<string, object> runDict)
                {
                    try
                    {
                        string runJson = _json.Serialize(runDict);
                        _runway = _json.Deserialize<RunwaySettings>(runJson);
                    }
                    catch { _runway = LoadRunwaySettings(); }
                }
                else
                {
                    _runway = LoadRunwaySettings();
                }

                // Restore window position
                if (obj.TryGetValue("window", out var winObj) && winObj is Dictionary<string, object> winDict)
                {
                    try
                    {
                        string winJson = _json.Serialize(winDict);
                        var pos = _json.Deserialize<WindowPosition>(winJson);
                        if (pos != null)
                        {
                            // Restore position first
                            this.Left = pos.Left; 
                            this.Top = pos.Top; 
                            this.Width = pos.Width; 
                            this.Height = pos.Height;
                            
                            // For maximized windows, set position BEFORE maximizing to anchor to correct monitor
                            if (pos.IsMaximized) 
                            {
                                this.WindowState = WindowState.Normal;
                                this.UpdateLayout();
                                this.WindowState = WindowState.Maximized;
                            }
                        }
                    }
                    catch { LoadWindowPosition(); }
                }
                else
                {
                    LoadWindowPosition();
                }

                // Show ground
                if (obj.TryGetValue("show_ground", out var sg))
                {
                    try
                    {
                        bool showGround = Convert.ToBoolean(sg);
                        if (HideGroundCheckBox != null) HideGroundCheckBox.IsChecked = showGround;
                        _hideGroundTraffic = !showGround;
                    }
                    catch { LoadShowGroundSetting(); }
                }

                // History dots
                if (obj.TryGetValue("history_dots", out var hd))
                {
                    try
                    {
                        int count = Convert.ToInt32(hd);
                        _historyDotsCount = Math.Max(1, Math.Min(20, count));
                        if (HistoryDotsSlider != null) HistoryDotsSlider.Value = _historyDotsCount;
                        if (HistoryDotsLabel != null) HistoryDotsLabel.Text = _historyDotsCount.ToString();
                    }
                    catch { LoadHistoryDotsCount(); }
                }

                // Plan altitude top
                if (obj.TryGetValue("plan_alt_top", out var pat))
                {
                    try
                    {
                        int v = Convert.ToInt32(pat);
                        _planAltTopHundreds = Math.Max(1, Math.Min(9999, v));
                        if (PlanAltTopBox != null) PlanAltTopBox.Text = _planAltTopHundreds.ToString();
                    }
                    catch { LoadPlanAltTop(); }
                }

                // Radar scan interval
                if (obj.TryGetValue("radar_scan_interval", out var rsi))
                {
                    try
                    {
                        double d = Convert.ToDouble(rsi);
                        _radarScanIntervalSec = Math.Max(0.5, Math.Min(10.0, d));
                        if (ScanIntervalSlider != null) ScanIntervalSlider.Value = _radarScanIntervalSec;
                        if (ScanIntervalLabel != null) ScanIntervalLabel.Text = _radarScanIntervalSec.ToString("F1") + "s";
                    }
                    catch { }
                }

                // UI toggles
                try
                {
                    if (obj.TryGetValue("show_vertical_dev", out var sv)) _showVerticalDevLines = Convert.ToBoolean(sv);
                    if (obj.TryGetValue("show_azimuth_dev", out var sa)) _showAzimuthDevLines = Convert.ToBoolean(sa);
                    if (obj.TryGetValue("show_approach_lights", out var sal)) _showApproachLights = Convert.ToBoolean(sal);
                    if (obj.TryGetValue("enable_azimuth_wedge", out var eaz)) _enableAzimuthWedgeFilter = Convert.ToBoolean(eaz);
                    if (obj.TryGetValue("enable_vertical_wedge", out var ev)) _enableVerticalWedgeFilter = Convert.ToBoolean(ev);
                }
                catch { }

                // Restore NASR cache if embedded
                Log("LoadAppState: Checking for embedded nasr_cache...");
                if (obj.TryGetValue("nasr_cache", out var nasrobj) && nasrobj != null)
                {
                    try
                    {
                        Log("LoadAppState: NASR cache found in app_state, restoring...");
                        // Ensure AppData folder exists before writing
                        System.IO.Directory.CreateDirectory(appDataPath);
                        string nasrJson = _json.Serialize(nasrobj);
                        Log($"LoadAppState: NASR serialized ({nasrJson.Length} bytes)");
                        string cachePath = System.IO.Path.Combine(appDataPath, "nasr_cache.json");
                        Log($"LoadAppState: Writing to: {cachePath}");
                        System.IO.File.WriteAllText(cachePath, nasrJson, Encoding.UTF8);
                        Log("LoadAppState: NASR cache restored successfully");
                    }
                    catch (Exception ex) 
                    { 
                        Log($"LoadAppState: ERROR restoring NASR cache: {ex.Message}");
                        Debug.WriteLine("Error restoring embedded NASR cache: " + ex.Message); 
                    }
                }
                else
                {
                    Log("LoadAppState: No nasr_cache found in app_state");
                }
                
                Log("LoadAppState: Completed successfully");
            }
            catch (Exception ex)
            {
                string msg = $"LoadAppState: EXCEPTION - {ex.Message}";
                try { if (logPath != null) System.IO.File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {msg}\r\n"); } catch { }
                Debug.WriteLine("Error loading app state: " + ex.Message);
                // fallback to legacy loads
                try { _runway = LoadRunwaySettings(); } catch { }
                try { LoadWindowPosition(); } catch { }
                try { LoadShowGroundSetting(); } catch { }
                try { LoadHistoryDotsCount(); } catch { }
                try { LoadPlanAltTop(); } catch { }
            }
        }

        /// <summary>
        /// Save application state to a single JSON file in AppData (app_state.json).
        /// Also embeds the existing NASR cache if present.
        /// </summary>
        private void SaveAppState()
        {
            string logPath = null;
            try
            {
                string appDataPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VATSIM-PAR-Scope");
                System.IO.Directory.CreateDirectory(appDataPath);
                logPath = System.IO.Path.Combine(appDataPath, "save_state_log.txt");
                
                void Log(string msg)
                {
                    try 
                    { 
                        System.IO.File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {msg}\r\n"); 
                        Debug.WriteLine(msg);
                    } 
                    catch { }
                }
                
                Log("SaveAppState: Starting...");
                
                string stateFile = System.IO.Path.Combine(appDataPath, AppStateFileName);
                Log($"SaveAppState: Will save to: {stateFile}");

                var dump = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

                // Runway
                if (_runway != null) dump["runway"] = _runway;

                // Window - capture screen bounds for multi-monitor support
                var screen = System.Windows.Forms.Screen.FromHandle(new System.Windows.Interop.WindowInteropHelper(this).Handle);
                var pos = new WindowPosition 
                { 
                    Left = this.Left, 
                    Top = this.Top, 
                    Width = this.Width, 
                    Height = this.Height, 
                    IsMaximized = this.WindowState == WindowState.Maximized,
                    ScreenLeft = screen.Bounds.Left,
                    ScreenTop = screen.Bounds.Top,
                    ScreenWidth = screen.Bounds.Width,
                    ScreenHeight = screen.Bounds.Height
                };
                dump["window"] = pos;
                Log("SaveAppState: Window position captured");

                // UI settings
                dump["show_ground"] = !_hideGroundTraffic; // store as 'show' semantics
                dump["history_dots"] = _historyDotsCount;
                dump["plan_alt_top"] = _planAltTopHundreds;
                dump["radar_scan_interval"] = _radarScanIntervalSec;
                dump["show_vertical_dev"] = _showVerticalDevLines;
                dump["show_azimuth_dev"] = _showAzimuthDevLines;
                dump["show_approach_lights"] = _showApproachLights;
                dump["enable_azimuth_wedge"] = _enableAzimuthWedgeFilter;
                dump["enable_vertical_wedge"] = _enableVerticalWedgeFilter;
                Log("SaveAppState: UI settings captured");

                // Embed NASR cache if present on-disk
                string cachePath = System.IO.Path.Combine(appDataPath, "nasr_cache.json");
                Log($"SaveAppState: Checking for NASR cache at: {cachePath}");
                if (System.IO.File.Exists(cachePath))
                {
                    try
                    {
                        Log($"SaveAppState: NASR cache file found, reading...");
                        string nasrJson = System.IO.File.ReadAllText(cachePath, Encoding.UTF8);
                        Log($"SaveAppState: NASR cache read ({nasrJson.Length} bytes), deserializing...");
                        var nasrObj = _json.DeserializeObject(nasrJson);
                        dump["nasr_cache"] = nasrObj;
                        Log($"SaveAppState: NASR cache embedded successfully");
                    }
                    catch (Exception ex) 
                    { 
                        Log($"SaveAppState: ERROR embedding NASR cache: {ex.GetType().Name}: {ex.Message}");
                        Debug.WriteLine($"SaveAppState: Error embedding NASR cache: {ex.Message}"); 
                    }
                }
                else
                {
                    Log($"SaveAppState: NASR cache file does NOT exist - will not be embedded");
                }

                Log("SaveAppState: Serializing app state to JSON...");
                string outJson = _json.Serialize(dump);
                Log($"SaveAppState: Serialized ({outJson.Length} bytes), writing to file...");
                System.IO.File.WriteAllText(stateFile, outJson, Encoding.UTF8);
                Log("SaveAppState: File written successfully");
            }
            catch (Exception ex)
            {
                string msg = $"SaveAppState: EXCEPTION - {ex.GetType().Name}: {ex.Message}";
                try { if (logPath != null) System.IO.File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {msg}\r\n"); } catch { }
                Debug.WriteLine("Error saving app state: " + ex.Message);
            }
        }

        public class WindowPosition
        {
            public double Left { get; set; }
            public double Top { get; set; }
            public double Width { get; set; }
            public double Height { get; set; }
            public bool IsMaximized { get; set; }
            // Screen bounds to restore window to correct monitor
            public double ScreenLeft { get; set; }
            public double ScreenTop { get; set; }
            public double ScreenWidth { get; set; }
            public double ScreenHeight { get; set; }
        }

        private void UpdateConfigBoxes()
        {
            RunwaySettings rs = GetActiveRunwayDefaults();
            GlideSlopeBox.Text = rs.GlideSlopeDeg.ToString("F1");
            DecisionHeightBox.Text = rs.DecisionHeightFt.ToString("F0");
            MaxAzBox.Text = rs.MaxAzimuthDeg.ToString("F1");
            RangeBox.Text = rs.RangeNm.ToString("F1");
            ApproachLightsBox.Text = (rs.ApproachLightLengthFt > 0 ? rs.ApproachLightLengthFt.ToString("F0") : "0");
        }

        private void OnConfigChanged(object sender, TextChangedEventArgs e)
        {
            if (_runway == null) return;
            
            // Try to parse each field and update runway settings
            if (double.TryParse(GlideSlopeBox.Text, out double gs)) _runway.GlideSlopeDeg = gs;
            if (double.TryParse(DecisionHeightBox.Text, out double dh)) _runway.DecisionHeightFt = dh;
            if (double.TryParse(MaxAzBox.Text, out double maxAz)) _runway.MaxAzimuthDeg = maxAz;
            if (double.TryParse(RangeBox.Text, out double rng)) _runway.RangeNm = rng;
            if (double.TryParse(ApproachLightsBox.Text, out double al)) _runway.ApproachLightLengthFt = al;
            
            // Save the updated settings
            SaveRunwaySettings(_runway);
        }

        private void OnGlideSlopeDown(object sender, RoutedEventArgs e) { AdjustValue(GlideSlopeBox, -0.1); }
        private void OnGlideSlopeUp(object sender, RoutedEventArgs e) { AdjustValue(GlideSlopeBox, 0.1); }
        private void OnDHDown(object sender, RoutedEventArgs e) { AdjustValue(DecisionHeightBox, -50); }
        private void OnDHUp(object sender, RoutedEventArgs e) { AdjustValue(DecisionHeightBox, 50); }
        private void OnMaxAzDown(object sender, RoutedEventArgs e) { AdjustValue(MaxAzBox, -0.5); }
        private void OnMaxAzUp(object sender, RoutedEventArgs e) { AdjustValue(MaxAzBox, 0.5); }
        private void OnRangeDown(object sender, RoutedEventArgs e) { AdjustValue(RangeBox, -1); }
        private void OnRangeUp(object sender, RoutedEventArgs e) { AdjustValue(RangeBox, 1); }

        private void AdjustValue(TextBox box, double delta)
        {
            if (double.TryParse(box.Text, out double value))
            {
                value += delta;
                if (value < 0) value = 0;
                box.Text = value.ToString("F1");
            }
        }
    }
}
