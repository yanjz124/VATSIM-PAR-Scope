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

        public MainWindow()
        {
            InitializeComponent();
            StartUdpListener();

            _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _uiTimer.Tick += (s, e) => UpdateUi();
            _uiTimer.Start();
        }

        private class TargetHistory
        {
            public readonly Queue<System.Windows.Point> Vertical = new Queue<System.Windows.Point>();
            public readonly Queue<System.Windows.Point> Azimuth = new Queue<System.Windows.Point>();
            public readonly Queue<System.Windows.Point> Plan = new Queue<System.Windows.Point>();
            public DateTime LastSampleUtc = DateTime.MinValue;
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

            // Empty scope background per PAR layout
            DrawVerticalEmpty(VerticalScopeCanvas);
            DrawAzimuthEmpty(AzimuthScopeCanvas);
            DrawPlanEmpty(PlanViewCanvas);

            var now = DateTime.UtcNow;
            foreach (var kvp in _aircraft)
            {
                var ac = kvp.Value;
                var callsign = ac.ContainsKey("callsign") && ac["callsign"] != null ? ac["callsign"].ToString() : "";
                var hist = _histories.ContainsKey(callsign) ? _histories[callsign] : null;
                if (hist != null && (now - hist.LastSampleUtc).TotalSeconds > 5)
                    continue; // time out after 5 seconds

                // If no new data for >0.5s, extrapolate position
                if (hist != null && (now - hist.LastSampleUtc).TotalSeconds > 0.5 && hist.Vertical.Count >= 2)
                {
                    // Use last two samples to estimate velocity
                    var last = hist.Vertical.Last();
                    var prev = hist.Vertical.Skip(hist.Vertical.Count - 2).First();
                    double dt = 0.5; // seconds per sample
                    double dx = last.X - prev.X;
                    double dy = last.Y - prev.Y;
                    double age = (now - hist.LastSampleUtc).TotalSeconds;
                    var fakeAc = new Dictionary<string, object>(ac);
                    // For plan view, extrapolate lat/lon
                    if (hist.Plan.Count >= 2)
                    {
                        var lastP = hist.Plan.Last();
                        var prevP = hist.Plan.Skip(hist.Plan.Count - 2).First();
                        double dpx = lastP.X - prevP.X;
                        double dpy = lastP.Y - prevP.Y;
                        // crude: move lat/lon by same screen delta (not geodetic, but matches display)
                        fakeAc["lat"] = GetDouble(ac, "lat", 0) + dpy / dt * age * 1e-5;
                        fakeAc["lon"] = GetDouble(ac, "lon", 0) + dpx / dt * age * 1e-5;
                    }
                    // For vertical, extrapolate alt
                    fakeAc["alt_ft"] = GetDouble(ac, "alt_ft", 0) + dy / dt * age * 2.0; // scale factor for realism
                    DrawAircraft(fakeAc);
                }
                else
                {
                    DrawAircraft(ac);
                }
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
            // Requirement: For TRUE_ALIGNMENT (runway heading) near 181°, the wedge should point NORTH (approach direction)
            // Approach direction is the reciprocal of runway heading
            double hdgRad = DegToRad(rs.HeadingTrueDeg);
            double approachRad = hdgRad - Math.PI; // reciprocal heading (normalize not strictly necessary for trig)
            double sensorOffsetNm = rs.SensorOffsetNm > 0 ? rs.SensorOffsetNm : 0.5;
            double maxAzDeg = rs.MaxAzimuthDeg > 0 ? rs.MaxAzimuthDeg : 10.0;
            double maxAzRad = DegToRad(maxAzDeg);

            // Sensor position: from threshold, move along APPROACH direction by sensor offset (sensor sits on approach side)
            double sx = cx + (sensorOffsetNm / nmPerPx) * Math.Sin(approachRad);
            double sy = cy - (sensorOffsetNm / nmPerPx) * Math.Cos(approachRad);

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
            if (_runway == null) return;

            double alt = GetDouble(ac, "alt_ft", 0);
            double lat = GetDouble(ac, "lat", 0);
            double lon = GetDouble(ac, "lon", 0);
            string callsign = ac.ContainsKey("callsign") && ac["callsign"] != null ? ac["callsign"].ToString() : "";

            // Compute ENU coordinates relative to threshold
            double east = 0, north = 0;
            GeoToEnu(_runway.ThresholdLat, _runway.ThresholdLon, lat, lon, out east, out north);

            // Convert to meters and rotate to approach course (reciprocal of runway heading)
            double hdgRad = DegToRad(_runway.HeadingTrueDeg);
            double approachRad = hdgRad - Math.PI;
            double cosA = Math.Cos(approachRad);
            double sinA = Math.Sin(approachRad);

            // Along-track: positive = along approach course from threshold (inbound)
            double alongTrackM = north * cosA + east * sinA;
            double alongTrackNm = alongTrackM / 1852.0;

            // Cross-track: positive = right of approach course
            double crossTrackM = -north * sinA + east * cosA;
            double crossTrackNm = crossTrackM / 1852.0;

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

            // === VERTICAL SCOPE ===
            // X-axis: along-track from threshold (centered at threshold, covers ±rangeNm)
            // Y-axis: altitude (angle above GS)
            double vertCeilingDeg = 6.0;
            double pxPerNm = vWidth / (2 * rangeNm);
            double vx = (alongTrackNm / (rangeNm)) * (vWidth / 2.0) + (vWidth / 2.0);
            
            // Altitude relative to glide slope (GS passes through field elev + TCH at threshold)
            double gsAltAtAircraft = fieldElevFt + tchFt + Math.Tan(DegToRad(gsDeg)) * alongTrackNm * 6076.12;
            double altAboveGs = alt - gsAltAtAircraft;
            
            // Convert to angle above glide slope
            double angleAboveGsDeg = 0;
            if (Math.Abs(alongTrackNm) > 0.01)
            {
                angleAboveGsDeg = Math.Atan(altAboveGs / (alongTrackNm * 6076.12)) * 180.0 / Math.PI;
            }
            
            // Map to canvas: 0° at bottom, vertCeilingDeg at top
            double vy = vHeight - (angleAboveGsDeg / vertCeilingDeg) * vHeight;
            
            // Draw history trail (up to 5 dots, fading older)
            // use existing callsign variable declared above
            var hist = _histories.ContainsKey(callsign) ? _histories[callsign] : null;
            if (hist != null)
            {
                bool doSample = (DateTime.UtcNow - hist.LastSampleUtc) > TimeSpan.FromMilliseconds(400);
                if (doSample)
                {
                    hist.Vertical.Enqueue(new System.Windows.Point(vx, vy));
                    while (hist.Vertical.Count > 10) hist.Vertical.Dequeue();
                }
                int n = 0;
                foreach (var p in hist.Vertical.Reverse())
                {
                    if (n >= 5) break;
                    double alpha = 0.6 - n * 0.1; if (alpha < 0.2) alpha = 0.2;
                    var dot = new Ellipse { Width = 5, Height = 5, Fill = new SolidColorBrush(Color.FromScRgb((float)alpha, 0f, 1f, 1f)) };
                    dot.Margin = new Thickness(p.X - 2.5, p.Y - 2.5, 0, 0);
                    VerticalScopeCanvas.Children.Add(dot);
                    n++;
                }
            }
            // Draw current point last, on top
            if (vx >= 0 && vx <= vWidth && vy >= 0 && vy <= vHeight)
            {
                var vdot = new Ellipse { Width = 8, Height = 8, Fill = Brushes.Cyan };
                vdot.Margin = new Thickness(vx - 4, vy - 4, 0, 0);
                VerticalScopeCanvas.Children.Add(vdot);
            }

            // === AZIMUTH SCOPE ===
            // X-axis: cross-track from threshold (centered at threshold, covers ±maxCrossTrackNm)
            // Y-axis: along-track from threshold (centered, covers ±rangeNm)
            double maxCrossTrackNm = Math.Tan(DegToRad(maxAzDeg)) * rangeNm;
            double ax = (crossTrackNm / maxCrossTrackNm) * (aWidth / 2.0) + (aWidth / 2.0);
            double ay = aHeight - ((alongTrackNm / rangeNm) * (aHeight / 2.0) + (aHeight / 2.0));
            if (hist != null)
            {
                bool doSample = (DateTime.UtcNow - hist.LastSampleUtc) > TimeSpan.FromMilliseconds(400);
                if (doSample)
                {
                    hist.Azimuth.Enqueue(new System.Windows.Point(ax, ay));
                    while (hist.Azimuth.Count > 10) hist.Azimuth.Dequeue();
                }
                int n = 0;
                foreach (var p in hist.Azimuth.Reverse())
                {
                    if (n >= 5) break;
                    double alpha = 0.6 - n * 0.1; if (alpha < 0.2) alpha = 0.2;
                    var dot = new Ellipse { Width = 5, Height = 5, Fill = new SolidColorBrush(Color.FromScRgb((float)alpha, 1f, 1f, 0f)) };
                    dot.Margin = new Thickness(p.X - 2.5, p.Y - 2.5, 0, 0);
                    AzimuthScopeCanvas.Children.Add(dot);
                    n++;
                }
            }
            if (ax >= 0 && ax <= aWidth && ay >= 0 && ay <= aHeight)
            {
                var adot = new Ellipse { Width = 8, Height = 8, Fill = Brushes.Yellow };
                adot.Margin = new Thickness(ax - 4, ay - 4, 0, 0);
                AzimuthScopeCanvas.Children.Add(adot);

                var label = new TextBlock { Text = callsign, Foreground = Brushes.LightGray, FontSize = 10 };
                label.Margin = new Thickness(ax + 6, ay - 6, 0, 0);
                AzimuthScopeCanvas.Children.Add(label);
            }

            // === PLAN VIEW ===
            // Simple situational awareness display centered on threshold, with history
            double pWidth = PlanViewCanvas.ActualWidth > 0 ? PlanViewCanvas.ActualWidth : 400;
            double pHeight = PlanViewCanvas.ActualHeight > 0 ? PlanViewCanvas.ActualHeight : 520;
            double pcx = pWidth / 2.0;
            double pcy = pHeight / 2.0;
            
            // Use same scale as plan view drawing (maxRangeNm covers radius)
            double maxRangeNm = rangeNm + 5;
            double nmPerPx = maxRangeNm / Math.Min(pWidth / 2.0, pHeight / 2.0);
            
            // Aircraft position in screen coordinates (north = -Y, east = +X)
            // ENU: east, north already computed in meters
            double px = pcx + (east / 1852.0) / nmPerPx;
            double py = pcy - (north / 1852.0) / nmPerPx;
            
            if (hist != null)
            {
                bool doSample = (DateTime.UtcNow - hist.LastSampleUtc) > TimeSpan.FromMilliseconds(400);
                if (doSample)
                {
                    hist.Plan.Enqueue(new System.Windows.Point(px, py));
                    while (hist.Plan.Count > 10) hist.Plan.Dequeue();
                    hist.LastSampleUtc = DateTime.UtcNow;
                }
                int n = 0;
                foreach (var p in hist.Plan.Reverse())
                {
                    if (n >= 5) break;
                    double alpha = 0.6 - n * 0.1; if (alpha < 0.2) alpha = 0.2;
                    var dot = new Ellipse { Width = 4, Height = 4, Fill = new SolidColorBrush(Color.FromScRgb((float)alpha, 1f, 0f, 1f)) };
                    dot.Margin = new Thickness(p.X - 2, p.Y - 2, 0, 0);
                    PlanViewCanvas.Children.Add(dot);
                    n++;
                }
            }
            if (px >= 0 && px <= pWidth && py >= 0 && py <= pHeight)
            {
                var pdot = new Ellipse { Width = 6, Height = 6, Fill = Brushes.Magenta };
                pdot.Margin = new Thickness(px - 3, py - 3, 0, 0);
                PlanViewCanvas.Children.Add(pdot);

                // First line: callsign
                var plabel1 = new TextBlock { Text = callsign, Foreground = Brushes.LightGray, FontSize = 9 };
                plabel1.Margin = new Thickness(px + 5, py - 10, 0, 0);
                PlanViewCanvas.Children.Add(plabel1);

                // Second line: altitude hundreds (D3) and ground speed tens (rounded)
                int altHundreds = (int)Math.Round(alt / 100.0);
                int gs = (int)Math.Round(GetGroundSpeedKts(ac));
                int gsTens = (int)Math.Round(gs / 10.0);
                string altStr = altHundreds.ToString("D3");
                string gsStr = gsTens.ToString();

                var plabel2 = new TextBlock { Text = altStr + " " + gsStr, Foreground = Brushes.LightGray, FontSize = 9 };
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
            // Check multiple common keys and numeric types
            string[] keys = new[] { "gs_kts", "gs", "ground_speed", "groundspeed", "kts" };
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
