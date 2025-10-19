using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Windows.Controls;

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

        public MainWindow()
        {
            InitializeComponent();
            StartUdpListener();

            _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _uiTimer.Tick += (s, e) => UpdateUi();
            _uiTimer.Start();
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
                LastEventText.Text = _lastEvent.ToLongTimeString();
            });

            if (type == "add" || type == "update")
            {
                string callsign = obj.ContainsKey("callsign") && obj["callsign"] != null ? obj["callsign"].ToString() : null;
                if (!string.IsNullOrEmpty(callsign))
                    _aircraft[callsign] = obj;
            }
            else if (type == "delete")
            {
                string callsign = obj.ContainsKey("callsign") && obj["callsign"] != null ? obj["callsign"].ToString() : null;
                if (!string.IsNullOrEmpty(callsign))
                {
                    Dictionary<string, object> removed;
                    _aircraft.TryRemove(callsign, out removed);
                }
            }
            else if (type == "network_disconnected" || type == "session_ended")
            {
                _aircraft.Clear();
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

            // Empty scope background per PAR layout
            DrawVerticalEmpty(VerticalScopeCanvas);
            DrawAzimuthEmpty(AzimuthScopeCanvas);
            DrawPlanEmpty(PlanViewCanvas);

            foreach (var kvp in _aircraft)
            {
                DrawAircraft(kvp.Value);
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

            // Title
            var title = new TextBlock();
            title.Text = "VERTICAL";
            title.Foreground = Brushes.White;
            title.FontWeight = FontWeights.Bold;
            title.Margin = new Thickness(6, 2, 0, 0);
            canvas.Children.Add(title);

            // Info (GS and DH)
            var info = new TextBlock();
            info.Text = string.Format("Glide Slope {0:0.0}° | DH {1:0}ft", rs.GlideSlopeDeg, rs.DecisionHeightFt);
            info.Foreground = Brushes.LightGray;
            info.Margin = new Thickness(100, 2, 0, 0);
            canvas.Children.Add(info);

            // Range grid and labels with sensor offset
            double rangeNm = rs.RangeNm > 0 ? rs.RangeNm : 10.0;
            double sensorOffsetNm = rs.SensorOffsetNm > 0 ? rs.SensorOffsetNm : 0.5;
            double totalRangeNm = rangeNm + sensorOffsetNm;
            double pxPerNm = w / totalRangeNm;
            double thresholdX = sensorOffsetNm * pxPerNm;
            int i;
            for (i = 0; i <= (int)Math.Floor(rangeNm); i++)
            {
                double x = thresholdX + i * pxPerNm;
                var vline = new Line();
                vline.X1 = x; vline.Y1 = 0; vline.X2 = x; vline.Y2 = h;
                vline.Stroke = new SolidColorBrush(Color.FromRgb(30, 100, 30));
                vline.StrokeThickness = (i % 5 == 0) ? 1.5 : 0.5;
                if (i % 5 != 0)
                {
                    var dash = new DoubleCollection(); dash.Add(3); dash.Add(4); vline.StrokeDashArray = dash;
                }
                canvas.Children.Add(vline);

                var lbl = new TextBlock();
                lbl.Foreground = Brushes.White; lbl.FontSize = 12;
                lbl.Text = (i == 0) ? "TD" : (i + "NM");
                lbl.Margin = new Thickness(Math.Max(0, x + 3), h - 18, 0, 0);
                canvas.Children.Add(lbl);
            }

            // Vertical scale labels on LEFT edge showing altitude in feet
            double bottomMargin = 30;
            double workH = h - bottomMargin;
            double altAt6DegAtFullRange = Math.Tan(DegToRad(6.0)) * (totalRangeNm * 6076.12);
            double pxPerFt = workH / altAt6DegAtFullRange;
            
            // Add "ft" label at top left
            var ftLabel = new TextBlock();
            ftLabel.Text = "ft";
            ftLabel.Foreground = Brushes.LightGray;
            ftLabel.FontSize = 11;
            ftLabel.Margin = new Thickness(2, 2, 0, 0);
            canvas.Children.Add(ftLabel);
            
            // Altitude scale in feet (every 500 ft)
            int altStep = 500;
            int maxAltFt = (int)Math.Ceiling(altAt6DegAtFullRange / altStep) * altStep;
            for (i = 0; i <= maxAltFt / altStep; i++)
            {
                int altFt = i * altStep;
                if (altFt > altAt6DegAtFullRange) break;
                
                // Y position on canvas
                double y = workH - (altFt * pxPerFt);
                if (y < 0 || y > workH) continue;
                
                var tx = new TextBlock();
                tx.Foreground = Brushes.LightGray;
                tx.FontSize = 10; 
                tx.Text = altFt.ToString();
                tx.Margin = new Thickness(2, y - 6, 0, 0);
                canvas.Children.Add(tx);
            }

            // Vertical wedge envelope: from threshold to 10 NM and up to 6° ceiling
            DrawVerticalWedge(canvas, w, h, rs, rangeNm);

            // Glide slope reference line
            DrawGlideSlope(canvas, w, h, rangeNm);

            // Decision height marker as faded horizontal line across the display (reuses pxPerFt from GS labels)
            double dhAlt = rs.FieldElevFt + rs.DecisionHeightFt;
            double yDh = Math.Max(0, Math.Min(workH, workH - dhAlt * pxPerFt));
            var dhLine = new Line(); dhLine.X1 = 0; dhLine.Y1 = yDh; dhLine.X2 = w; dhLine.Y2 = yDh; dhLine.Stroke = Brushes.CadetBlue; dhLine.StrokeThickness = 1.5; dhLine.Opacity = 0.5; canvas.Children.Add(dhLine);
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

            // Centerline (horizontal)
            var axis = new Line(); axis.Stroke = Brushes.LimeGreen; axis.StrokeThickness = 2; axis.X1 = 0; axis.Y1 = h / 2.0; axis.X2 = w; axis.Y2 = h / 2.0; canvas.Children.Add(axis);
            
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

            // Range grid and labels with sensor offset
            double rangeNm = rs.RangeNm > 0 ? rs.RangeNm : 10.0;
            double sensorOffsetNm = rs.SensorOffsetNm > 0 ? rs.SensorOffsetNm : 0.5;
            double totalRangeNm = rangeNm + sensorOffsetNm;
            double pxPerNm = w / totalRangeNm;
            double thresholdX = sensorOffsetNm * pxPerNm;
            int i;
            for (i = 0; i <= (int)Math.Floor(rangeNm); i++)
            {
                double x = thresholdX + i * pxPerNm;
                var vline = new Line(); vline.X1 = x; vline.Y1 = 0; vline.X2 = x; vline.Y2 = h; vline.Stroke = new SolidColorBrush(Color.FromRgb(30, 100, 30)); vline.StrokeThickness = (i % 5 == 0) ? 1.5 : 0.5; if (i % 5 != 0) { var dash = new DoubleCollection(); dash.Add(3); dash.Add(4); vline.StrokeDashArray = dash; } canvas.Children.Add(vline);
                var lbl = new TextBlock(); lbl.Foreground = Brushes.White; lbl.FontSize = 12; lbl.Text = (i == 0) ? "TD" : (i + "NM"); lbl.Margin = new Thickness(Math.Max(0, x + 3), h - 18, 0, 0); canvas.Children.Add(lbl);
            }

            // Azimuth wedge envelope and guide lines at ±angles
            DrawAzimuthWedge(canvas, w, h, rs);
            
            // Azimuth guide lines at ±angles (from sensor, not bleeding into wedge edges)
            double maxAz = rs.MaxAzimuthDeg > 0 ? rs.MaxAzimuthDeg : 10.0;
            double[] angs = new double[] { -maxAz, -5, -2.5, -1.25, 0, 1.25, 2.5, 5, maxAz };
            for (i = 0; i < angs.Length; i++)
            {
                double a = angs[i];
                var line = new Line();
                if (i == 4) line.Stroke = Brushes.LimeGreen; else line.Stroke = new SolidColorBrush(Color.FromRgb(160, 140, 40));
                line.StrokeThickness = (i == 4) ? 2 : 1;
                if (i != 4) { var dash = new DoubleCollection(); dash.Add(4); dash.Add(6); line.StrokeDashArray = dash; }
                line.X1 = 0; line.Y1 = h / 2.0;
                line.X2 = w;
                double yNm = Math.Tan(DegToRad(a)) * totalRangeNm; // use total range including sensor offset
                // Reuse halfWidthNm and pxPerNmY from above
                double yOffset = yNm * pxPerNmY;
                line.Y2 = Math.Max(0, Math.Min(h, h / 2.0 - yOffset)); // clamp to canvas
                canvas.Children.Add(line);
            }
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

            // DH marker at glide slope intersection
            // Calculate where DH altitude intersects the glide slope
            double dhAlt = rs.FieldElevFt + rs.DecisionHeightFt;
            double gsRad = DegToRad(rs.GlideSlopeDeg);
            // Distance from threshold where GS reaches DH altitude
            double distToReachDH = (rs.DecisionHeightFt) / Math.Tan(gsRad) / 6076.12; // in NM from threshold
            double dhX = thresholdX + distToReachDH * pxPerNm;
            double dhY = workH - dhAlt * pxPerFt;
            
            var thr = new Line(); 
            thr.X1 = dhX; thr.X2 = dhX; 
            thr.Y1 = Math.Min(workH, dhY + 30); 
            thr.Y2 = Math.Max(0, dhY - 30); 
            thr.Stroke = Brushes.LimeGreen; 
            thr.StrokeThickness = 5; 
            canvas.Children.Add(thr);
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

            // Threshold marker
            var thr = new Line(); thr.X1 = thresholdX; thr.X2 = thresholdX + 24; thr.Y1 = midY; thr.Y2 = midY; thr.Stroke = Brushes.LimeGreen; thr.StrokeThickness = 5; canvas.Children.Add(thr);
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
            double hdgRad = DegToRad(rs.HeadingTrueDeg);
            double sensorOffsetNm = rs.SensorOffsetNm > 0 ? rs.SensorOffsetNm : 0.5;
            double maxAzDeg = rs.MaxAzimuthDeg > 0 ? rs.MaxAzimuthDeg : 10.0;
            double maxAzRad = DegToRad(maxAzDeg);
            
            // Sensor position (behind threshold)
            double sx = cx - (sensorOffsetNm / nmPerPx) * Math.Sin(hdgRad);
            double sy = cy + (sensorOffsetNm / nmPerPx) * Math.Cos(hdgRad);
            
            // Full range endpoint on centerline
            double fullRangeX = cx + (rangeNm / nmPerPx) * Math.Sin(hdgRad);
            double fullRangeY = cy - (rangeNm / nmPerPx) * Math.Cos(hdgRad);
            
            // Calculate left and right wedge edges at full range
            double leftX = fullRangeX + (rangeNm / nmPerPx) * Math.Tan(maxAzRad) * Math.Cos(hdgRad);
            double leftY = fullRangeY + (rangeNm / nmPerPx) * Math.Tan(maxAzRad) * Math.Sin(hdgRad);
            double rightX = fullRangeX - (rangeNm / nmPerPx) * Math.Tan(maxAzRad) * Math.Cos(hdgRad);
            double rightY = fullRangeY - (rangeNm / nmPerPx) * Math.Tan(maxAzRad) * Math.Sin(hdgRad);
            
            var wedge = new Polygon(); wedge.Stroke = Brushes.DeepSkyBlue; wedge.StrokeThickness = 2; wedge.Fill = null;
            var wedgePts = new PointCollection();
            wedgePts.Add(new Point(sx, sy)); // sensor apex
            wedgePts.Add(new Point(leftX, leftY)); // left edge at full range
            wedgePts.Add(new Point(rightX, rightY)); // right edge at full range
            wedge.Points = wedgePts;
            canvas.Children.Add(wedge);
            
            // Draw centerline
            var centerline = new Line(); centerline.X1 = sx; centerline.Y1 = sy; centerline.X2 = fullRangeX; centerline.Y2 = fullRangeY; centerline.Stroke = Brushes.LimeGreen; centerline.StrokeThickness = 1.5; canvas.Children.Add(centerline);
            
            // Draw runway as a thick line (heading from threshold)
            double rwLen = 2.0; // runway length in NM for display
            double x1 = cx; double y1 = cy;
            double x2 = cx + (rwLen / nmPerPx) * Math.Sin(hdgRad);
            double y2 = cy - (rwLen / nmPerPx) * Math.Cos(hdgRad);
            var rw = new Line(); rw.X1 = x1; rw.Y1 = y1; rw.X2 = x2; rw.Y2 = y2; rw.Stroke = Brushes.White; rw.StrokeThickness = 4; canvas.Children.Add(rw);

            // Threshold marker
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

        private void DrawAircraft(Dictionary<string, object> ac)
        {
            // Very crude placeholders until proper PAR projection is implemented
            double alt = GetDouble(ac, "alt_ft", 0);
            double lat = GetDouble(ac, "lat", 0);
            double lon = GetDouble(ac, "lon", 0);
            string callsign = ac.ContainsKey("callsign") && ac["callsign"] != null ? ac["callsign"].ToString() : "";

            // Compute runway-relative coordinates
            double east = 0, north = 0;
            if (_runway != null)
            {
                GeoToEnu(_runway.ThresholdLat, _runway.ThresholdLon, lat, lon, out east, out north);
            }

            // Dimensions
            double vWidth = 400;
            if (VerticalScopeCanvas.ActualWidth > 0) vWidth = VerticalScopeCanvas.ActualWidth;
            double vHeight = 300;
            if (VerticalScopeCanvas.ActualHeight > 0) vHeight = VerticalScopeCanvas.ActualHeight;
            double lWidth = 400;
            if (AzimuthScopeCanvas.ActualWidth > 0) lWidth = AzimuthScopeCanvas.ActualWidth;
            double lHeight = 300;
            if (AzimuthScopeCanvas.ActualHeight > 0) lHeight = AzimuthScopeCanvas.ActualHeight;

            // Scaling
            double rangeNm = _runway != null && _runway.RangeNm > 0 ? _runway.RangeNm : 10.0; // along-track range for full width
            double halfWidthNm = 1.0; // lateral half-width displayed
            double pxPerNmV = vWidth / rangeNm;
            double pxPerNmL = lWidth / (2 * halfWidthNm);
            double pxPerFtV = vHeight / 3000.0; // 3000 ft vertical span

            // Rotate ENU to runway frame: along (Xr), cross (Yr)
            double hdgRad = _runway != null ? DegToRad(_runway.HeadingTrueDeg) : 0.0;
            double cosH = Math.Cos(hdgRad);
            double sinH = Math.Sin(hdgRad);
            double xr = (north * cosH + east * sinH) / 1852.0; // meters to NM
            double yr = (-north * sinH + east * cosH) / 1852.0; // NM cross-track, right positive
            if (_runway == null)
            {
                // Fallback: simple placeholders
                xr = (lon - Math.Floor(lon)) * rangeNm;
                yr = (lat - Math.Floor(lat)) * halfWidthNm;
            }

            // Vertical scope position
            double targetAlt = _runway != null ? (_runway.FieldElevFt + Math.Tan(DegToRad(_runway.GlideSlopeDeg)) * xr * 6076.12) : 0.0;
            double dv = alt - targetAlt;
            double vx = Math.Max(4, Math.Min(vWidth - 4, xr * pxPerNmV));
            double vy = Math.Max(4, Math.Min(vHeight - 4, vHeight - (alt / 3000.0) * vHeight));

            var vdot = new Ellipse();
            vdot.Width = 8;
            vdot.Height = 8;
            vdot.Fill = Brushes.Cyan;
            vdot.Margin = new Thickness(vx - 4, vy - 4, 0, 0);
            VerticalScopeCanvas.Children.Add(vdot);

            // Lateral scope position (centerline at middle)
            double lx = Math.Max(4, Math.Min(lWidth - 4, (yr / halfWidthNm) * (lWidth / 2.0) + (lWidth / 2.0)));
            double ly = lHeight / 2.0; // keep centered horizontally reference
            var ldot = new Ellipse();
            ldot.Width = 8;
            ldot.Height = 8;
            ldot.Fill = Brushes.Yellow;
            ldot.Margin = new Thickness(lx - 4, ly - 4, 0, 0);
            AzimuthScopeCanvas.Children.Add(ldot);

            // Label near lateral dot
            var label = new TextBlock();
            label.Text = callsign;
            label.Foreground = Brushes.LightGray;
            label.FontSize = 10;
            label.Margin = new Thickness(lx + 6, ly - 6, 0, 0);
            AzimuthScopeCanvas.Children.Add(label);
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

            // Scale to match the 6° wedge
            double altAt6DegAtFullRange = Math.Tan(DegToRad(6.0)) * (totalRangeNm * 6076.12);
            double pxPerFt = workH / altAt6DegAtFullRange;

            double gsRad = DegToRad(rs.GlideSlopeDeg);
            // GS passes through threshold at field elevation + TCH
            double tch = rs.ThrCrossingHgtFt > 0 ? rs.ThrCrossingHgtFt : 0;
            double alt0 = rs.FieldElevFt + tch;
            double altEnd = rs.FieldElevFt + Math.Tan(gsRad) * (rangeNm * 6076.12);

            double x1 = thresholdX;
            double y1 = Math.Max(0, Math.Min(workH, workH - alt0 * pxPerFt));
            double x2 = w;
            double y2 = Math.Max(0, Math.Min(workH, workH - altEnd * pxPerFt));

            var gs = new Line();
            gs.Stroke = Brushes.Yellow;
            gs.StrokeThickness = 2;
            gs.X1 = x1; gs.Y1 = y1; gs.X2 = x2; gs.Y2 = y2;
            canvas.Children.Add(gs);

            // Touchdown (where GS reaches field elevation)
            if (gsRad > 0.0001 && tch > 0)
            {
                double dTdzNm = (tch / Math.Tan(gsRad)) / 6076.12; // NM from threshold
                if (dTdzNm >= 0 && dTdzNm <= rangeNm)
                {
                    double tdX = thresholdX + dTdzNm * pxPerNm;
                    // marker line
                    var td = new Line();
                    td.Stroke = Brushes.Orange;
                    td.StrokeThickness = 1.5;
                    td.Opacity = 0.7;
                    td.X1 = tdX; td.Y1 = 0; td.X2 = tdX; td.Y2 = workH;
                    var dash = new DoubleCollection(); dash.Add(4); dash.Add(6); td.StrokeDashArray = dash;
                    canvas.Children.Add(td);

                    // label "TDZ"
                    var lbl = new TextBlock();
                    lbl.Text = "TDZ";
                    lbl.Foreground = Brushes.Orange;
                    lbl.FontSize = 11;
                    lbl.Margin = new Thickness(Math.Max(0, tdX + 3), workH + 2, 0, 0);
                    canvas.Children.Add(lbl);
                }
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
    }
}
