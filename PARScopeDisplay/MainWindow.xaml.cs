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
        private bool _hideGroundTraffic = false;
        private int _historyDotsCount = 5; // Number of history dots to display (user configurable)

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
            UpdateConfigBoxes();
            
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
            };
        }

        private class TargetHistory
        {
            public readonly Queue<System.Windows.Point> Vertical = new Queue<System.Windows.Point>();
            public readonly Queue<System.Windows.Point> Azimuth = new Queue<System.Windows.Point>();
            public readonly Queue<System.Windows.Point> Plan = new Queue<System.Windows.Point>();
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
                
                // Skip ground traffic if checkbox is unchecked
                // Ground = below whichever is higher: (field elev + 20ft) or (0.5° glideslope altitude)
                if (!_hideGroundTraffic && _runway != null)
                {
                    double fieldElevFt = _runway.FieldElevFt;
                    
                    // Calculate distance from threshold to determine 0.5° glideslope altitude
                    double eastFromThreshold = 0, northFromThreshold = 0;
                    GeoToEnu(_runway.ThresholdLat, _runway.ThresholdLon, lat, lon, out eastFromThreshold, out northFromThreshold);
                    double distanceFromThresholdFt = Math.Sqrt(eastFromThreshold * eastFromThreshold + northFromThreshold * northFromThreshold) * 3.28084; // meters to feet
                    
                    // Altitude at 0.5° glideslope at this distance
                    double halfDegGlideAlt = fieldElevFt + Math.Tan(DegToRad(0.5)) * distanceFromThresholdFt;
                    
                    // Ground threshold is the higher of: field elev + 20ft, or 0.5° glideslope altitude
                    double groundThreshold = Math.Max(fieldElevFt + 20, halfDegGlideAlt);
                    
                    if (alt < groundThreshold)
                        continue; // Skip this aircraft
                }
                
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
            _hideGroundTraffic = HideGroundCheckBox.IsChecked == true;
        }

        private void OnHistoryDotsChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            _historyDotsCount = (int)HistoryDotsSlider.Value;
            if (HistoryDotsLabel != null)
            {
                HistoryDotsLabel.Text = _historyDotsCount.ToString();
            }
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

            // Glide slope reference line
            DrawGlideSlope(canvas, w, h, rangeNm);

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

            // Centerline (horizontal) - removed per user request
            
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
                lbl.Foreground = (nm == 0) ? Brushes.LimeGreen : Brushes.LightGray;
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

            // Azimuth wedge envelope and guide lines
            DrawAzimuthWedge(canvas, w, h, rs);

            // Azimuth deviation guideline set:
            // originate from TD (tdPixel) and draw lines at ±0.5°, ±1°, ±2°, then every 2° until maxAz
            double maxAz = rs.MaxAzimuthDeg > 0 ? rs.MaxAzimuthDeg : 10.0;
            // Build angle list starting from smallest intervals requested
            var angleList = new List<double>();
            // Add the symmetric small angles
            angleList.Add(0.0);
            angleList.Add(0.5);
            angleList.Add(1.0);
            angleList.Add(2.0);
            // then every 2 degrees starting at 4 up to maxAz
            for (double a = 4.0; a <= maxAz; a += 2.0) angleList.Add(a);

            // Draw positive and negative sides
            for (int idx = 0; idx < angleList.Count; idx++)
            {
                double ang = angleList[idx];
                // Skip the 0.0 duplicate for negative side
                for (int sign = -1; sign <= 1; sign += 2)
                {
                    if (ang == 0.0 && sign == -1) continue; // 0 only once
                    double a = ang * sign;
                    var line = new Line();
                    if (ang == 0.0) { line.Stroke = Brushes.LimeGreen; line.StrokeThickness = 2; }
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
            
            // Draw centerline (green)
            var centerline = new Line(); centerline.X1 = sx; centerline.Y1 = sy; centerline.X2 = fullRangeX; centerline.Y2 = fullRangeY; centerline.Stroke = Brushes.LimeGreen; centerline.StrokeThickness = 1.5; canvas.Children.Add(centerline);
            
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
            var thr = new Ellipse(); thr.Width = 8; thr.Height = 8; thr.Fill = Brushes.LimeGreen; thr.Margin = new Thickness(cx - 4, cy - 4, 0, 0); canvas.Children.Add(thr);
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
            if (_runway == null)
            {
                Debug.WriteLine("DrawAircraft: _runway is null, skipping");
                return;
            }

            double alt = GetDouble(ac, "alt_ft", 0);
            double lat = GetDouble(ac, "lat", 0);
            double lon = GetDouble(ac, "lon", 0);
            string callsign = ac.ContainsKey("callsign") && ac["callsign"] != null ? ac["callsign"].ToString() : "";

            Debug.WriteLine($"DrawAircraft: {callsign} lat={lat}, lon={lon}, alt={alt}, ThresholdLat={_runway.ThresholdLat}, ThresholdLon={_runway.ThresholdLon}");

            // Remove any previous debug overlays (tagged "DBG") to keep only one
            try
            {
                var old = VerticalScopeCanvas.Children.OfType<UIElement>().Where(c => (c is FrameworkElement fe) && fe.Tag != null && fe.Tag.ToString() == "DBG").ToList();
                foreach (var o in old) VerticalScopeCanvas.Children.Remove(o);
            }
            catch { }

            // Canvas dimensions
            double vWidth = VerticalScopeCanvas.ActualWidth > 0 ? VerticalScopeCanvas.ActualWidth : 400;
            double vHeight = VerticalScopeCanvas.ActualHeight > 0 ? VerticalScopeCanvas.ActualHeight : 300;
            double aWidth = AzimuthScopeCanvas.ActualWidth > 0 ? AzimuthScopeCanvas.ActualWidth : 400;
            double aHeight = AzimuthScopeCanvas.ActualHeight > 0 ? AzimuthScopeCanvas.ActualHeight : 300;

            double rangeNm = _runway.RangeNm > 0 ? _runway.RangeNm : 10.0;
            double sensorOffsetNm = _runway.SensorOffsetNm > 0 ? _runway.SensorOffsetNm : 0.5;
            double maxAzDeg = _runway.MaxAzimuthDeg > 0 ? _runway.MaxAzimuthDeg : 10.0;
            double gsDeg = _runway.GlideSlopeDeg > 0 ? _runway.GlideSlopeDeg : 3.0;
            double tchFt = _runway.ThrCrossingHgtFt > 0 ? _runway.ThrCrossingHgtFt : 50.0;
            double fieldElevFt = _runway.FieldElevFt;

            // Calculate sensor position: 0.5nm past threshold along approach course (opposite of runway heading)
            double hdgRad = DegToRad(_runway.HeadingTrueDeg);
            double approachRad = hdgRad + Math.PI; // Reciprocal of runway heading (approach course)
            
            // Sensor lat/lon: 0.5nm from threshold. Place sensor on the runway side to match plan view.
            double sensorOffsetM = sensorOffsetNm * 1852.0; // meters
            double lat0Rad = DegToRad(_runway.ThresholdLat);
            double dLatM = sensorOffsetM * Math.Cos(approachRad);
            double dLonM = sensorOffsetM * Math.Sin(approachRad);
            // subtract to place sensor on runway side (consistent with plan view sign)
            double sensorLat = _runway.ThresholdLat - (dLatM / 111319.9); // meters to degrees
            double sensorLon = _runway.ThresholdLon - (dLonM / (111319.9 * Math.Cos(lat0Rad)));
            
            // Compute ENU coordinates relative to SENSOR (not threshold)
            double east = 0, north = 0;
            GeoToEnu(sensorLat, sensorLon, lat, lon, out east, out north);

            // Rotate to approach course coordinate system
            double cosA = Math.Cos(approachRad);
            double sinA = Math.Sin(approachRad);

            // Along-track: positive = from sensor toward approach (inbound to runway)
            // This is the distance from sensor along the approach course
            double alongTrackM = north * cosA + east * sinA;
            double alongTrackNm = alongTrackM / 1852.0;

            // Cross-track: positive = right of approach course (from pilot's perspective looking at runway)
            double crossTrackM = -north * sinA + east * cosA;
            double crossTrackNm = crossTrackM / 1852.0;

            // ALSO compute along-track and cross-track relative to THRESHOLD to avoid origin mismatch
            double eastT = 0, northT = 0;
            GeoToEnu(_runway.ThresholdLat, _runway.ThresholdLon, lat, lon, out eastT, out northT);
            // Project relative to approach course (approachRad defined earlier)
            double alongTrackFromThresholdM = northT * cosA + eastT * sinA;
            double alongTrackFromThresholdNm = alongTrackFromThresholdM / 1852.0;
            double crossTrackFromThresholdM = -northT * sinA + eastT * cosA;
            double crossTrackFromThresholdNm = crossTrackFromThresholdM / 1852.0;
            double altAboveFieldFt = alt - fieldElevFt;
            double azimuthDeg = 0;
            double elevationDeg = 0;
            if (Math.Abs(alongTrackFromThresholdNm) > 0.01)
            {
                azimuthDeg = Math.Atan2(crossTrackFromThresholdNm, alongTrackFromThresholdNm) * 180.0 / Math.PI;
                elevationDeg = Math.Atan(altAboveFieldFt / (alongTrackFromThresholdNm * 6076.12)) * 180.0 / Math.PI;
            }
            double elevationDegFromThreshold = elevationDeg; // kept for compatibility

            // (removed debug overlay)

            // Filtering for vertical and azimuth scopes:
            // Check if aircraft is within the scope range (from -sensorOffset to +rangeNm from sensor)
            // This allows aircraft approaching from far out to be displayed
            // Allow negative elevation angles to catch aircraft on or landing on the runway
            // Use threshold-based along-track for inclusion; allow a small buffer so targets don't disappear
            double includeNegBuffer = 0.3; // allow slightly past the sensor plane toward runway
            double includePosBuffer = 0.5; // allow slightly beyond configured range
            bool inAzimuthScope = Math.Abs(azimuthDeg) <= maxAzDeg && alongTrackFromThresholdNm >= -sensorOffsetNm - includeNegBuffer && alongTrackFromThresholdNm <= rangeNm + includePosBuffer;
            // Vertical scope additionally requires elevation within vertical limits (use elevationDeg computed from threshold)
            bool inVerticalScope = inAzimuthScope && elevationDeg >= -1.0 && elevationDeg <= 6.0;

            Debug.WriteLine($"DrawAircraft: {callsign} alongTrack={alongTrackNm:F2}nm (from sensor), crossTrack={crossTrackNm:F2}nm, az={azimuthDeg:F1}°, elev={elevationDeg:F1}°, alt={alt:F0}ft MSL, inAzimuth={inAzimuthScope}, inVertical={inVerticalScope}");

            // Get history object for this callsign (used by all three scopes)
            var hist = _histories.ContainsKey(callsign) ? _histories[callsign] : null;

            // === VERTICAL SCOPE ===
            // Only draw if aircraft is in the vertical sensing area
            if (inVerticalScope)
            {
                // X-axis: distance from sensor (0 to rangeNm)
                // Y-axis: altitude MSL, with 0° (field elevation) as horizontal reference
                // Sensor is at origin (0,0) which represents (0nm distance, field elevation)
                // The canvas shows totalRangeNm = rangeNm + sensorOffsetNm
                
                double totalRangeNm = rangeNm + sensorOffsetNm;
                double pxPerNm = vWidth / totalRangeNm;
                // Sensor is at X=0 in our coordinate system, but on canvas it's at sensorOffsetNm pixels from left
                double normX = ((alongTrackNm + sensorOffsetNm) / (rangeNm + sensorOffsetNm)); // 0=apex/left, 1=right
                
                // Calculate altitude scale the same way as the background grid
                double altAt6DegAtFullRange = fieldElevFt + Math.Tan(DegToRad(6.0)) * (totalRangeNm * 6076.12);
                double altRangeFt = altAt6DegAtFullRange - fieldElevFt;
                double normAlt = (alt - fieldElevFt) / altRangeFt; // Use the same scale as the background
                normAlt = Math.Max(0, Math.Min(1, normAlt));
                normX = Math.Max(0, Math.Min(1, normX));
                double vx = normX * vWidth;
                double vy = vHeight - (normAlt * vHeight);
                // Store history in normalized coordinates
                if (hist != null)
                {
                    // Store vertical history in physical units: X=alongTrackNm, Y=altitude MSL (ft)
                    var currentPhys = new System.Windows.Point(alongTrackNm, alt);
                    // Use tolerances: 0.01 NM (~18.5m) for position, 50 ft for altitude
                    if (double.IsNaN(hist.LastVertical.X) ||
                        Math.Abs(currentPhys.X - hist.LastVertical.X) > 0.01 ||
                        Math.Abs(currentPhys.Y - hist.LastVertical.Y) > 50.0)
                    {
                        hist.Vertical.Enqueue(currentPhys);
                        while (hist.Vertical.Count > 20) hist.Vertical.Dequeue();
                        hist.LastVertical = currentPhys;
                    }
                    int totalCount = hist.Vertical.Count;
                    if (totalCount > 1)
                    {
                        int dotsToShow = Math.Min(_historyDotsCount, totalCount - 1);
                        var historyDots = hist.Vertical.Skip(Math.Max(0, totalCount - dotsToShow - 1)).Take(dotsToShow).ToList();
                        for (int i = 0; i < historyDots.Count; i++)
                        {
                            var p = historyDots[i];
                            // p.X = alongTrackNm, p.Y = altitude MSL (ft)
                            // Reuse totalRangeNm, altAt6DegAtFullRange and altRangeFt declared earlier in this vertical block
                            double normXp = (p.X + sensorOffsetNm) / totalRangeNm;
                            double normAltp = (p.Y - fieldElevFt) / altRangeFt;
                            normXp = Math.Max(0, Math.Min(1, normXp));
                            normAltp = Math.Max(0, Math.Min(1, normAltp));
                            double hx = normXp * vWidth;
                            double hy = vHeight - (normAltp * vHeight);
                            float alpha = 0.15f + ((float)i / Math.Max(1, historyDots.Count - 1)) * 0.30f;
                            var dot = new Ellipse { Width = 5, Height = 5, Fill = new SolidColorBrush(Color.FromScRgb(alpha, 1f, 1f, 1f)) };
                            dot.Margin = new Thickness(hx - 2.5, hy - 2.5, 0, 0);
                            VerticalScopeCanvas.Children.Add(dot);
                        }
                    }
                }
                // Draw current point last, on top
                if (vx >= 0 && vx <= vWidth && vy >= 0 && vy <= vHeight)
                {
                    var vdot = new Ellipse { Width = 8, Height = 8, Fill = Brushes.White };
                    vdot.Margin = new Thickness(vx - 4, vy - 4, 0, 0);
                    VerticalScopeCanvas.Children.Add(vdot);
                }
            }

            // === AZIMUTH SCOPE ===
            // Only draw if aircraft is in the azimuth sensing area
            if (inAzimuthScope)
            {
                // Azimuth scope: sensor at LEFT, approach course pointing RIGHT
                // Use same coordinate system as vertical scope for X-axis consistency
                double totalRangeNm = rangeNm + sensorOffsetNm;
                // Map: -sensorOffset (left edge) to +rangeNm (right edge)
                double normAx = ((alongTrackNm + sensorOffsetNm) / totalRangeNm);
                
                // Y-axis: centerline at middle (aHeight/2), deviations above/below
                // Positive cross-track (right of course) = toward bottom
                // Negative cross-track (left of course) = toward top
                double maxCrossTrackNm = Math.Tan(DegToRad(maxAzDeg)) * rangeNm;
                double normAy = 0.5 + (crossTrackNm / (2 * maxCrossTrackNm));
                normAx = Math.Max(0, Math.Min(1, normAx));
                normAy = Math.Max(0, Math.Min(1, normAy));
                double curAx = normAx * aWidth;
                double curAy = normAy * aHeight;
                if (hist != null)
                {
                    // Store azimuth history in physical units: X=alongTrackNm, Y=crossTrackNm
                    var currentPhysAz = new System.Windows.Point(alongTrackNm, crossTrackNm);
                    // Tolerances: 0.01 NM for along and cross track
                    if (double.IsNaN(hist.LastAzimuth.X) ||
                        Math.Abs(currentPhysAz.X - hist.LastAzimuth.X) > 0.01 ||
                        Math.Abs(currentPhysAz.Y - hist.LastAzimuth.Y) > 0.01)
                    {
                        hist.Azimuth.Enqueue(currentPhysAz);
                        while (hist.Azimuth.Count > 20) hist.Azimuth.Dequeue();
                        hist.LastAzimuth = currentPhysAz;
                    }
                    int totalCount = hist.Azimuth.Count;
                    if (totalCount > 1)
                    {
                        int dotsToShow = Math.Min(_historyDotsCount, totalCount - 1);
                        var historyDots = hist.Azimuth.Skip(Math.Max(0, totalCount - dotsToShow - 1)).Take(dotsToShow).ToList();
                        for (int i = 0; i < historyDots.Count; i++)
                        {
                            var p = historyDots[i];
                            // p.X = alongTrackNm, p.Y = crossTrackNm
                            // Reuse totalRangeNm and maxCrossTrackNm declared earlier in this azimuth block
                            double normAxp = (p.X + sensorOffsetNm) / totalRangeNm;
                            double normAyp = 0.5 + (p.Y / (2 * maxCrossTrackNm));
                            normAxp = Math.Max(0, Math.Min(1, normAxp));
                            normAyp = Math.Max(0, Math.Min(1, normAyp));
                            double hx = normAxp * aWidth;
                            double hy = normAyp * aHeight;
                            float alpha = 0.15f + ((float)i / Math.Max(1, historyDots.Count - 1)) * 0.30f;
                            var dot = new Ellipse { Width = 5, Height = 5, Fill = new SolidColorBrush(Color.FromScRgb(alpha, 1f, 1f, 1f)) };
                            dot.Margin = new Thickness(hx - 2.5, hy - 2.5, 0, 0);
                            AzimuthScopeCanvas.Children.Add(dot);
                        }
                    }
                }
                if (curAx >= 0 && curAx <= aWidth && curAy >= 0 && curAy <= aHeight)
                {
                    var adot = new Ellipse { Width = 8, Height = 8, Fill = Brushes.White };
                    adot.Margin = new Thickness(curAx - 4, curAy - 4, 0, 0);
                    AzimuthScopeCanvas.Children.Add(adot);
                    var label = new TextBlock { Text = callsign, Foreground = Brushes.LightGray, FontSize = 11 };
                    label.Margin = new Thickness(curAx + 6, curAy - 6, 0, 0);
                    AzimuthScopeCanvas.Children.Add(label);
                }
            }

            // === PLAN VIEW ===
            // Traditional north-up radar showing all targets in vicinity (no approach course filtering)
            // Plan view is centered on THRESHOLD for situational awareness
            double pWidth = PlanViewCanvas.ActualWidth > 0 ? PlanViewCanvas.ActualWidth : 400;
            double pHeight = PlanViewCanvas.ActualHeight > 0 ? PlanViewCanvas.ActualHeight : 520;
            double pcx = pWidth / 2.0;
            double pcy = pHeight / 2.0;
            
            // Use a fixed range for the plan view (e.g., rangeNm + 5nm for buffer)
            double maxRangeNm = rangeNm + 5;
            double nmPerPx = maxRangeNm / Math.Min(pWidth / 2.0, pHeight / 2.0);
            
            // Calculate ENU relative to THRESHOLD (not sensor) for plan view
            double eastFromThreshold = 0, northFromThreshold = 0;
            GeoToEnu(_runway.ThresholdLat, _runway.ThresholdLon, lat, lon, out eastFromThreshold, out northFromThreshold);

            // Convert ENU meters to nautical miles for normalized storage
            double eastNm = eastFromThreshold / 1852.0;
            double northNm = northFromThreshold / 1852.0;

            // Aircraft position in screen coordinates (north = -Y, east = +X)
            double px = pcx + (eastNm) / nmPerPx;
            double py = pcy - (northNm) / nmPerPx;

            if (hist != null)
            {
                // Store history in NM-relative coordinates (eastNm, northNm) so resizing or range changes reproject correctly
                var currentNm = new System.Windows.Point(eastNm, northNm);
                // Only add to history if position changed significantly (use ~0.01 NM ~ 18.5m tolerance)
                if (double.IsNaN(hist.LastPlan.X) || 
                    Math.Abs(currentNm.X - hist.LastPlan.X) > 0.01 || 
                    Math.Abs(currentNm.Y - hist.LastPlan.Y) > 0.01)
                {
                    hist.Plan.Enqueue(currentNm);
                    while (hist.Plan.Count > 20) hist.Plan.Dequeue(); // Keep last 20 actual positions
                    hist.LastPlan = currentNm;
                }

                // Draw only the selected number of history dots
                int totalCount = hist.Plan.Count;
                if (totalCount > 1)
                {
                    int dotsToShow = Math.Min(_historyDotsCount, totalCount - 1);
                    var historyDots = hist.Plan.Skip(Math.Max(0, totalCount - dotsToShow - 1)).Take(dotsToShow).ToList();

                    for (int i = 0; i < historyDots.Count; i++)
                    {
                        var p = historyDots[i];
                        // p is in NM (eastNm, northNm) — reproject to pixels for current canvas size and range
                        double hx = pcx + (p.X) / nmPerPx;
                        double hy = pcy - (p.Y) / nmPerPx;
                        float alpha = 0.15f + ((float)i / Math.Max(1, historyDots.Count - 1)) * 0.30f;
                        // Only draw history dots within canvas bounds
                        if (hx >= 0 && hx <= pWidth && hy >= 0 && hy <= pHeight)
                        {
                            var dot = new Ellipse { Width = 4, Height = 4, Fill = new SolidColorBrush(Color.FromScRgb(alpha, 1f, 1f, 1f)) };
                            dot.Margin = new Thickness(hx - 2, hy - 2, 0, 0);
                            PlanViewCanvas.Children.Add(dot);
                        }
                    }
                }
            }

            // Only draw on plan view if within canvas bounds
            if (px >= 0 && px <= pWidth && py >= 0 && py <= pHeight)
            {
                var pdot = new Ellipse { Width = 6, Height = 6, Fill = Brushes.White };
                pdot.Margin = new Thickness(px - 3, py - 3, 0, 0);
                PlanViewCanvas.Children.Add(pdot);

                // First line: callsign
                var plabel1 = new TextBlock { Text = callsign, Foreground = Brushes.LightGray, FontSize = 11 };
                plabel1.Margin = new Thickness(px + 5, py - 12, 0, 0);
                PlanViewCanvas.Children.Add(plabel1);

                // Second line: altitude hundreds (D3) and ground speed 2-digit (250kt → 25, 87kt → 09)
                int altHundreds = (int)Math.Round(alt / 100.0);
                int gs = (int)Math.Round(GetGroundSpeedKts(ac));
                int gsTwoDigit = (int)Math.Round(gs / 10.0); // Divide by 10 and round
                string altStr = altHundreds.ToString("D3");
                string gsStr = gsTwoDigit.ToString("D2"); // Always 2 digits with leading zero

                var plabel2 = new TextBlock { Text = altStr + " " + gsStr, Foreground = Brushes.LightGray, FontSize = 11 };
                plabel2.Margin = new Thickness(px + 5, py + 2, 0, 0);
                PlanViewCanvas.Children.Add(plabel2);
            }
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
            gs.Stroke = Brushes.LimeGreen;
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
                    HideGroundCheckBox.IsChecked = showGround;
                    _hideGroundTraffic = showGround;
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
                System.IO.File.WriteAllText(settingsFile, _hideGroundTraffic.ToString());
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
        }

        private void OnConfigChanged(object sender, TextChangedEventArgs e)
        {
            if (_runway == null) return;
            
            // Try to parse each field and update runway settings
            if (double.TryParse(GlideSlopeBox.Text, out double gs)) _runway.GlideSlopeDeg = gs;
            if (double.TryParse(DecisionHeightBox.Text, out double dh)) _runway.DecisionHeightFt = dh;
            if (double.TryParse(MaxAzBox.Text, out double maxAz)) _runway.MaxAzimuthDeg = maxAz;
            if (double.TryParse(RangeBox.Text, out double rng)) _runway.RangeNm = rng;
            
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
