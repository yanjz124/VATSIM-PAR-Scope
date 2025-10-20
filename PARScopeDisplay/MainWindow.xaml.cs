using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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

namespace PARScopeDisplay
{
    public partial class MainWindow : Window
    {
        private UdpClient _udpClient;
        private bool _listening;
        private readonly ConcurrentDictionary<string, Dictionary<string, object>> _aircraft = new ConcurrentDictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
        private DateTime _lastEvent = DateTime.MinValue;
        private DispatcherTimer _uiTimer;
        private readonly JavaScriptSerializer _json = new JavaScriptSerializer();
        private RunwaySettings _runway = null;
        private NASRDataLoader _nasrLoader = null;
    // Per-callsign history of last few seconds for each scope
    private readonly Dictionary<string, TargetHistory> _histories = new Dictionary<string, TargetHistory>(StringComparer.OrdinalIgnoreCase);
    private const int HistoryLifetimeSec = 5; // seconds before history dots expire (timeout after 5s)
    private const int HistorySampleIntervalSec = 5; // kept for potential sampling, but history is update-driven
        private bool _hideGroundTraffic = false;
        private int _historyDotsCount = 5; // Number of history dots to display (user configurable)
    private bool _showVerticalDevLines = true;
    private bool _showAzimuthDevLines = true;
    private bool _showApproachLights = true;
    private int _planAltTopHundreds = 999; // e.g. 100 -> 10000 ft top threshold
    private readonly SolidColorBrush _centerlineBrush = new SolidColorBrush(Color.FromArgb(160, 60, 120, 60)); // semi-transparent darker green

        public MainWindow()
        {
            InitializeComponent();
            
            // Initialize NASR loader and try to load cached data
            _nasrLoader = new NASRDataLoader();
            
            // Load last used runway from settings and window position
            _runway = LoadRunwaySettings();
            LoadWindowPosition();
            LoadShowGroundSetting();
            LoadHistoryDotsCount();
            LoadPlanAltTop();
            UpdateConfigBoxes();
            // Initialize view toggles from UI checkboxes (Display menu)
            OnViewToggleChanged(null, null);

            // Keyboard hooks: allow PageUp/PageDown to adjust Range
            this.PreviewKeyDown += MainWindow_PreviewKeyDown;
            
            StartUdpListener();

            _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _uiTimer.Tick += (s, e) => UpdateUi();
            _uiTimer.Start();
            
            this.Closed += (s, e) => 
            {
                SaveRunwaySettings(_runway);
                SaveWindowPosition();
                SaveShowGroundSetting();
                SaveHistoryDotsCount();
                SavePlanAltTop();
            };
        }

        private class TargetHistory
        {
            // HistoryEntry stores the point and the timestamp so we can age entries out over time
            public struct HistoryEntry
            {
                public System.Windows.Point P;
                public DateTime Time;
                public HistoryEntry(System.Windows.Point p, DateTime t) { P = p; Time = t; }
            }

            public readonly Queue<HistoryEntry> Vertical = new Queue<HistoryEntry>();
            public readonly Queue<HistoryEntry> Azimuth = new Queue<HistoryEntry>();
            public readonly Queue<HistoryEntry> Plan = new Queue<HistoryEntry>();
            // Last time we recorded a time-based history sample for this target
            public DateTime LastSampleUtc = DateTime.MinValue;
            // Track last actual position to detect real data changes (not UI refresh duplicates)
            public System.Windows.Point LastVertical = new System.Windows.Point(double.NaN, double.NaN);
            public System.Windows.Point LastAzimuth = new System.Windows.Point(double.NaN, double.NaN);
            public System.Windows.Point LastPlan = new System.Windows.Point(double.NaN, double.NaN);
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
                    // ensure history bucket exists
                    if (!_histories.ContainsKey(callsign))
                        _histories[callsign] = new TargetHistory();
                }
            }
            else if (type == "delete")
            {
                string callsign = obj.ContainsKey("callsign") && obj["callsign"] != null ? obj["callsign"].ToString() : null;
                if (!string.IsNullOrEmpty(callsign))
                {
                    Dictionary<string, object> removed;
                    _aircraft.TryRemove(callsign, out removed);
                    if (_histories.ContainsKey(callsign)) _histories.Remove(callsign);
                }
            }
            else if (type == "network_disconnected" || type == "session_ended")
            {
                _aircraft.Clear();
                _histories.Clear();
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
                RunwayText.Text = _runway.Icao + " " + _runway.Runway;
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

            // (No time-based eviction: keep stored history until explicitly cleared)
            var sb = new StringBuilder();
            sb.AppendLine($"=== Traffic Data ({now:HH:mm:ss}) ===");
            sb.AppendLine($"Total Aircraft: {_aircraft.Count}");
            sb.AppendLine();
            
            // Table header with fixed-width columns
            sb.AppendLine("Callsign  Latitude    Longitude    Altitude   Speed  History");
            sb.AppendLine("--------  ----------  -----------  ---------  -----  -------");
            
            foreach (var kvp in _aircraft)
            {
                var ac = kvp.Value;
                var callsign = ac.ContainsKey("callsign") && ac["callsign"] != null ? ac["callsign"].ToString() : "";
                var hist = _histories.ContainsKey(callsign) ? _histories[callsign] : null;
                int histCount = hist != null ? hist.Vertical.Count : 0;
                
                double lat = GetDouble(ac, "lat", 0);
                double lon = GetDouble(ac, "lon", 0);
                double alt = GetDouble(ac, "alt_ft", 0);
                double gs = GetGroundSpeedKts(ac);
                
                // Note: ground-traffic filtering is now applied only to the Plan view (not here)
                
                // Format as fixed-width table row
                string row = string.Format("{0,-9} {1,10:F4}  {2,11:F4}  {3,8:F0}ft  {4,4:F0}kt  {5,3}pts",
                    callsign, lat, lon, alt, gs, histCount);
                sb.AppendLine(row);
                
                // Draw all aircraft, no timeout
                DrawAircraft(ac);
            }
            
            DebugText.Text = sb.ToString();
        }

        private void OnToggleDebugClick(object sender, RoutedEventArgs e)
        {
            DebugExpander.IsExpanded = !DebugExpander.IsExpanded;
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
                // Refresh UI to apply new toggles
                UpdateUi();
            }
            catch { }
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
            var border = new Rectangle();
            border.Width = w; border.Height = h; border.Stroke = Brushes.Gray; border.StrokeThickness = 1; canvas.Children.Add(border);

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
                if (_nasrLoader != null && rs != null && !string.IsNullOrEmpty(rs.Icao))
                {
                    var rwd = _nasrLoader.GetRunway(rs.Icao, rs.Runway);
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

            // Title and info
            var title = new TextBlock(); title.Text = "AZIMUTH"; title.Foreground = Brushes.White; title.FontWeight = FontWeights.Bold; title.Margin = new Thickness(40, 2, 0, 0); canvas.Children.Add(title);
            var info = new TextBlock(); info.Text = string.Format("Max AZ Ang {0:0}°  -  RWY Hdg {1:0.0}°", rs.MaxAzimuthDeg, rs.HeadingTrueDeg); info.Foreground = Brushes.LightGray; info.Margin = new Thickness(130, 2, 0, 0); canvas.Children.Add(info);

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
                if (_nasrLoader != null && rs != null && !string.IsNullOrEmpty(rs.Icao))
                {
                    var rwd = _nasrLoader.GetRunway(rs.Icao, rs.Runway);
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
            // Only include 0.0, ±0.5°, ±1°, ±2° as requested
            double maxAz = rs.MaxAzimuthDeg > 0 ? rs.MaxAzimuthDeg : 10.0;
            var angleList = new List<double> { 0.5, 1.0, 2.0 };

            // Draw positive and negative sides (only if enabled)
            if (_showAzimuthDevLines)
            {
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
                        double yNm = Math.Tan(DegToRad(a)) * totalRangeNm; // lateral offset at full totalRange
                        double yOffset = yNm * pxPerNmY;
                        line.Y2 = Math.Max(0, Math.Min(h, h / 2.0 - yOffset)); // clamp
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
            double maxAz = rs.MaxAzimuthDeg > 0 ? rs.MaxAzimuthDeg : 10.0;
            double midY = h / 2.0;
            double rangeNm = rs.RangeNm > 0 ? rs.RangeNm : 10.0;
            double sensorOffsetNm = rs.SensorOffsetNm > 0 ? rs.SensorOffsetNm : 0.5;
            double totalRangeNm = rangeNm + sensorOffsetNm;
            double pxPerNm = w / totalRangeNm;
            double thresholdX = sensorOffsetNm * pxPerNm;

            // Build wedge from sensor (behind threshold)
            var poly = new Polygon(); poly.Stroke = Brushes.DeepSkyBlue; poly.StrokeThickness = 2; poly.Fill = null;
            var pts = new PointCollection();
            pts.Add(new Point(0, midY)); // sensor apex
            double yNm = Math.Tan(DegToRad(maxAz)) * totalRangeNm;
            double halfWidthNm = 1.0;
            double pxPerNmY = (h / 2.0) / halfWidthNm;
            double yOffset = yNm * pxPerNmY;
            // clamp to canvas height
            double topY = Math.Max(0, midY - yOffset);
            double bottomY = Math.Min(h, midY + yOffset);
            pts.Add(new Point(w, topY));
            pts.Add(new Point(w, bottomY));
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
            double maxAzDeg = rs.MaxAzimuthDeg > 0 ? rs.MaxAzimuthDeg : 10.0;
            double maxAzRad = DegToRad(maxAzDeg);

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
                    var ends = _nasrLoader.GetAirportRunways(_runway.Icao);

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
        // sensorOffsetNm positive means sensor is on runway side (to the left along runway heading when drawing)
        private void GetSensorLatLon(RunwaySettings rs, double sensorOffsetNm, out double sensorLat, out double sensorLon)
        {
            // Approach course is reciprocal of runway heading
            double hdgRad = DegToRad(rs.HeadingTrueDeg);
            double approachRad = hdgRad + Math.PI; // reciprocal
            double sensorOffsetM = sensorOffsetNm * 1852.0; // nm to meters
            double lat0Rad = DegToRad(rs.ThresholdLat);
            double dLatM = sensorOffsetM * Math.Cos(approachRad);
            double dLonM = sensorOffsetM * Math.Sin(approachRad);
            sensorLat = rs.ThresholdLat + (dLatM / 111319.9);
            sensorLon = rs.ThresholdLon + (dLonM / (111319.9 * Math.Cos(lat0Rad)));
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
            double maxAzDeg = _runway.MaxAzimuthDeg > 0 ? _runway.MaxAzimuthDeg : 10.0;
            double gsDeg = _runway.GlideSlopeDeg > 0 ? _runway.GlideSlopeDeg : 3.0;
            double tchFt = _runway.ThrCrossingHgtFt > 0 ? _runway.ThrCrossingHgtFt : 50.0;
            double fieldElevFt = _runway.FieldElevFt;

            // Approach/course geometry
            double hdgRad = DegToRad(_runway.HeadingTrueDeg);
            double approachRad = hdgRad + Math.PI;

            // Sensor lat/lon (approx)
            double sensorOffsetM = sensorOffsetNm * 1852.0;
            double lat0Rad = DegToRad(_runway.ThresholdLat);
            double dLatM = sensorOffsetM * Math.Cos(approachRad);
            double dLonM = sensorOffsetM * Math.Sin(approachRad);
            double sensorLat = _runway.ThresholdLat - (dLatM / 111319.9);
            double sensorLon = _runway.ThresholdLon - (dLonM / (111319.9 * Math.Cos(lat0Rad)));

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

            // Azimuth and elevation
            double azimuthDeg = 0.0, elevationDeg = 0.0;
            if (Math.Abs(alongTrackFromThresholdNm) > 0.0001)
            {
                azimuthDeg = Math.Atan2(crossTrackFromThresholdNm, alongTrackFromThresholdNm) * 180.0 / Math.PI;
                double distFt = Math.Abs(alongTrackFromThresholdNm) * 6076.12;
                elevationDeg = Math.Atan2(alt - (fieldElevFt + tchFt), distFt) * 180.0 / Math.PI;
            }

            // Inclusion tests
            double includeNegBuffer = 0.3, includePosBuffer = 0.5;
            bool inAzimuthScope = Math.Abs(azimuthDeg) <= maxAzDeg && alongTrackFromThresholdNm >= -sensorOffsetNm - includeNegBuffer && alongTrackFromThresholdNm <= rangeNm + includePosBuffer;
            bool inVerticalScope = inAzimuthScope && elevationDeg <= 6.0;

            // History bucket
            var hist = _histories.ContainsKey(callsign) ? _histories[callsign] : null;

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

            // Enqueue history using change-detection (matching Plan behavior) and respecting ground-hide
            if (hist != null)
            {
                bool localIsGround = false;
                try
                {
                    double distFromThrM = Math.Sqrt(east_t * east_t + north_t * north_t);
                    double distanceFromThresholdFt = distFromThrM * 3.28084;
                    double halfDegGlideAlt = fieldElevFt + Math.Tan(DegToRad(0.5)) * distanceFromThresholdFt;
                    double groundThreshold = Math.Max(fieldElevFt + 20, halfDegGlideAlt);
                    localIsGround = (alt < groundThreshold);
                }
                catch { localIsGround = false; }

                if (!_hideGroundTraffic || !localIsGround)
                {
                    var now = DateTime.UtcNow;
                    // Vertical history stored as (alongTrackFromThresholdNm, alt_ft)
                    var curV = new System.Windows.Point(alongTrackFromThresholdNm, alt);
                    bool vChanged = double.IsNaN(hist.LastVertical.X) || Math.Abs(curV.X - hist.LastVertical.X) > 0.01 || Math.Abs(curV.Y - hist.LastVertical.Y) > 50.0;
                    if (vChanged)
                    {
                        hist.Vertical.Enqueue(new TargetHistory.HistoryEntry(curV, now));
                        while (hist.Vertical.Count > 40) hist.Vertical.Dequeue();
                        hist.LastVertical = curV;
                    }

                    // Azimuth history stored as (alongTrackFromThresholdNm, crossTrackFromThresholdNm)
                    var curA = new System.Windows.Point(alongTrackFromThresholdNm, crossTrackFromThresholdNm);
                    bool aChanged = double.IsNaN(hist.LastAzimuth.X) || Math.Abs(curA.X - hist.LastAzimuth.X) > 0.01 || Math.Abs(curA.Y - hist.LastAzimuth.Y) > 0.01;
                    if (aChanged)
                    {
                        hist.Azimuth.Enqueue(new TargetHistory.HistoryEntry(curA, now));
                        while (hist.Azimuth.Count > 40) hist.Azimuth.Dequeue();
                        hist.LastAzimuth = curA;
                    }

                    // Plan history stored as (eastNm, northNm)
                    double eastNm = east_t / 1852.0;
                    double northNm = north_t / 1852.0;
                    if (!(_planAltTopHundreds > 0 && alt > _planAltTopHundreds * 100))
                    {
                        var curP = new System.Windows.Point(eastNm, northNm);
                        bool pChanged = double.IsNaN(hist.LastPlan.X) || Math.Abs(curP.X - hist.LastPlan.X) > 0.01 || Math.Abs(curP.Y - hist.LastPlan.Y) > 0.01;
                        if (pChanged)
                        {
                            hist.Plan.Enqueue(new TargetHistory.HistoryEntry(curP, now));
                            while (hist.Plan.Count > 40) hist.Plan.Dequeue();
                            hist.LastPlan = curP;
                        }
                    }

                    hist.LastSampleUtc = DateTime.UtcNow;
                }
            }

            // --- DRAW VERTICAL SCOPE ---
            try
            {
                double totalRangeNmV = rangeNm + sensorOffsetNm;
                double bottomMargin = 30.0;
                double workH = Math.Max(0.0, vHeight - bottomMargin);
                double altAt6DegAtFullRange = fieldElevFt + Math.Tan(DegToRad(6.0)) * (totalRangeNmV * 6076.12);
                double altRangeFt = Math.Max(1.0, altAt6DegAtFullRange - fieldElevFt);

                // Current vertical point in pixels (using alongTrackFromThreshold origin normalization)
                double normX = ((alongTrackFromThresholdNm + sensorOffsetNm) / totalRangeNmV);
                normX = Math.Max(0, Math.Min(1, normX));
                double normAlt = (alt - fieldElevFt) / altRangeFt; normAlt = Math.Max(0, Math.Min(1, normAlt));
                double vx = normX * vWidth;
                double vy = workH - (normAlt * workH);

                // Draw vertical history only when within azimuth sensing range (left-side gating)
                if (hist != null && inAzimuthScope)
                {
                    var historyDots = hist.Vertical.ToList();
                    int take = Math.Max(1, Math.Min(historyDots.Count, _historyDotsCount));
                    var subset = historyDots.Skip(Math.Max(0, historyDots.Count - take)).ToList();
                    for (int i = 0; i < subset.Count; i++)
                    {
                        var p = subset[i].P; // X=alongNm, Y=alt_ft
                        double normXp = ((p.X + sensorOffsetNm) / totalRangeNmV);
                        double normAltp = (p.Y - fieldElevFt) / altRangeFt;
                        normXp = Math.Max(0, Math.Min(1, normXp));
                        normAltp = Math.Max(0, Math.Min(1, normAltp));
                        double hx = normXp * vWidth;
                        double hy = workH - (normAltp * workH);
                        float alpha = 0.15f + ((float)i / Math.Max(1, subset.Count - 1)) * 0.30f;
                        var dot = new Ellipse { Width = 5, Height = 5, Fill = new SolidColorBrush(Color.FromScRgb(alpha, 1f, 1f, 1f)) };
                        dot.Margin = new Thickness(hx - 2.5, hy - 2.5, 0, 0);
                        VerticalScopeCanvas.Children.Add(dot);
                        Canvas.SetZIndex(dot, 1000);
                    }

                    // Draw datablock near most recent history (if any) but only when in vertical scope
                    if (subset.Count > 0 && inVerticalScope)
                    {
                        var last = subset.Last().P;
                        int altHundreds = (int)Math.Round(last.Y / 100.0);
                        int gs = (int)Math.Round(GetGroundSpeedKts(ac));
                        int gsTwoDigit = (int)Math.Round(gs / 10.0);
                        double normXlast = ((last.X + sensorOffsetNm) / totalRangeNmV);
                        double normYlast = (last.Y - fieldElevFt) / altRangeFt;
                        normXlast = Math.Max(0, Math.Min(1, normXlast));
                        normYlast = Math.Max(0, Math.Min(1, normYlast));
                        double tagX = normXlast * vWidth;
                        double tagY = workH - (normYlast * workH);
                        double labelX = Math.Min(Math.Max(4, tagX + 8), vWidth - 80);
                        double labelY = Math.Min(Math.Max(4, tagY + 6), workH - 8);
                        var t1 = new TextBlock { Text = callsign, Foreground = Brushes.White, FontSize = 12, FontWeight = FontWeights.SemiBold };
                        t1.Margin = new Thickness(labelX, labelY, 0, 0); VerticalScopeCanvas.Children.Add(t1); Canvas.SetZIndex(t1, 1000);
                        var t2 = new TextBlock { Text = altHundreds.ToString("D3") + " " + gsTwoDigit.ToString("D2"), Foreground = Brushes.LightGray, FontSize = 11 };
                        t2.Margin = new Thickness(labelX, labelY + 16, 0, 0); VerticalScopeCanvas.Children.Add(t2); Canvas.SetZIndex(t2, 1000);
                    }

                    // Draw current vertical marker (only when inVerticalScope)
                    if (inVerticalScope && vx >= 0 && vx <= vWidth && vy >= 0 && vy <= vHeight)
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
                    }
                }
            }
            catch { }

            // --- DRAW AZIMUTH SCOPE ---
            try
            {
                if (inAzimuthScope)
                {
                    double totalRangeNmA = rangeNm + sensorOffsetNm;
                    double maxCrossTrackNm = Math.Tan(DegToRad(maxAzDeg)) * rangeNm;
                    double normAx = ((alongTrackFromThresholdNm + sensorOffsetNm) / totalRangeNmA);
                    double normAy = 0.5 + (crossTrackFromThresholdNm / (2 * maxCrossTrackNm));
                    normAx = Math.Max(0, Math.Min(1, normAx)); normAy = Math.Max(0, Math.Min(1, normAy));
                    double curAx = normAx * aWidth, curAy = normAy * aHeight;

                    // Draw azimuth history only when within azimuth sensing range (left-side gating)
                    if (hist != null)
                    {
                        var historyDots = hist.Azimuth.ToList();
                        int take = Math.Max(1, Math.Min(historyDots.Count, _historyDotsCount));
                        var subset = historyDots.Skip(Math.Max(0, historyDots.Count - take)).ToList();
                        for (int i = 0; i < subset.Count; i++)
                        {
                            var p = subset[i].P; // X=along, Y=cross
                            double normAxp = (p.X + sensorOffsetNm) / totalRangeNmA;
                            double normAyp = 0.5 + (p.Y / (2 * maxCrossTrackNm));
                            normAxp = Math.Max(0, Math.Min(1, normAxp)); normAyp = Math.Max(0, Math.Min(1, normAyp));
                            double hx = normAxp * aWidth, hy = normAyp * aHeight;
                            float alpha = 0.15f + ((float)i / Math.Max(1, subset.Count - 1)) * 0.30f;
                            var dot = new Ellipse { Width = 5, Height = 5, Fill = new SolidColorBrush(Color.FromScRgb(alpha, 1f, 1f, 1f)) };
                            dot.Margin = new Thickness(hx - 2.5, hy - 2.5, 0, 0); AzimuthScopeCanvas.Children.Add(dot); Canvas.SetZIndex(dot, 1000);
                        }
                    }

                    // Draw current azimuth marker (only if also in vertical scope)
                    if (curAx >= 0 && curAx <= aWidth && curAy >= 0 && curAy <= aHeight && inVerticalScope)
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

                // Draw plan history (respecting plan altitude filter and hide-ground setting)
                if (hist != null && (!_hideGroundTraffic || !isGroundPlan))
                {
                    if (!(_planAltTopHundreds > 0 && alt > _planAltTopHundreds * 100))
                    {
                        var historyDots = hist.Plan.ToList();
                        int take = Math.Max(1, Math.Min(historyDots.Count, _historyDotsCount));
                        var subset = historyDots.Skip(Math.Max(0, historyDots.Count - take)).ToList();
                        for (int i = 0; i < subset.Count; i++)
                        {
                            var p = subset[i].P; // eastNm, northNm
                            double hx = pcx + (p.X) / nmPerPx;
                            double hy = pcy - (p.Y) / nmPerPx;
                            float alpha = 0.15f + ((float)i / Math.Max(1, subset.Count - 1)) * 0.30f;
                            if (hx >= 0 && hx <= pWidth && hy >= 0 && hy <= pHeight)
                            {
                                var dot = new Ellipse { Width = 4, Height = 4, Fill = new SolidColorBrush(Color.FromScRgb(alpha, 1f, 1f, 1f)) };
                                dot.Margin = new Thickness(hx - 2, hy - 2, 0, 0); PlanViewCanvas.Children.Add(dot); Canvas.SetZIndex(dot, 1000);
                            }
                        }
                    }
                }

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
                    var p1 = new TextBlock { Text = callsign, Foreground = Brushes.LightGray, FontSize = 11 };
                    p1.Margin = new Thickness(pLabelX, pLabelY, 0, 0); PlanViewCanvas.Children.Add(p1); Canvas.SetZIndex(p1, 1000);
                    var p2 = new TextBlock { Text = altHundreds.ToString("D3") + " " + gsTwoDigit.ToString("D2"), Foreground = Brushes.LightGray, FontSize = 11 };
                    p2.Margin = new Thickness(pLabelX, pLabelY + 16, 0, 0); PlanViewCanvas.Children.Add(p2); Canvas.SetZIndex(p2, 1000);
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
                RunwayText.Text = _runway.Icao + " " + _runway.Runway;
            }
        }

        private void OnOpenSimulatorClick(object sender, RoutedEventArgs e)
        {
            var win = new SimulatorWindow(_runway);
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
            public string Icao;
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
                        MessageBox.Show(this, 
                            string.Format("NASR data loaded successfully!\n\n{0} airports in database.\nSource: {1}", airportCount, _nasrLoader.LastLoadedSource ?? "(unknown)"),
                            "Success", MessageBoxButton.OK, MessageBoxImage.Information);
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
                    this.Left = pos.Left;
                    this.Top = pos.Top;
                    this.Width = pos.Width;
                    this.Height = pos.Height;
                    if (pos.IsMaximized)
                        this.WindowState = WindowState.Maximized;
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
                
                var pos = new WindowPosition
                {
                    Left = this.Left,
                    Top = this.Top,
                    Width = this.Width,
                    Height = this.Height,
                    IsMaximized = this.WindowState == WindowState.Maximized
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

        private class WindowPosition
        {
            public double Left { get; set; }
            public double Top { get; set; }
            public double Width { get; set; }
            public double Height { get; set; }
            public bool IsMaximized { get; set; }
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
