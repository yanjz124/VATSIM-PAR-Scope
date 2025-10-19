using System;
using System.Globalization;
using System.Linq;
using System.Windows;

namespace PARScopeDisplay
{
    public partial class RunwayDialog : Window
    {
        private MainWindow.RunwaySettings _init;
        private NASRDataLoader _nasrLoader;

        public RunwayDialog()
        {
            InitializeComponent();
        }

        public void SetNASRLoader(NASRDataLoader loader)
        {
            _nasrLoader = loader;
            LookupButton.IsEnabled = (loader != null);
            if (loader != null)
            {
                // Populate ICAO dropdown (letters-only IDs like DCA; exclude codes with digits like 7AL9)
                var ids = loader.GetAirportIds();
                ids = ids.Where(id => !string.IsNullOrEmpty(id) && id.All(ch => ch >= 'A' && ch <= 'Z')).ToList();
                ids.Sort(StringComparer.OrdinalIgnoreCase);
                IcaoBox.ItemsSource = ids;
                IcaoBox.SelectionChanged += (s, e) => RefreshRunwayList();
                IcaoBox.IsEditable = true;
                IcaoBox.KeyUp += (s, e) => RefreshRunwayList();
                IcaoBox.LostFocus += (s, e) => RefreshRunwayList();
            }
        }

        public void SetInitial(MainWindow.RunwaySettings set)
        {
            _init = set;
            if (set == null) return;
            IcaoBox.Text = set.Icao;
            RefreshRunwayList();
            RunwayBox.Text = set.Runway;
            LatBox.Text = set.ThresholdLat.ToString(CultureInfo.InvariantCulture);
            LonBox.Text = set.ThresholdLon.ToString(CultureInfo.InvariantCulture);
            HdgBox.Text = set.HeadingTrueDeg.ToString(CultureInfo.InvariantCulture);
            GsBox.Text = set.GlideSlopeDeg.ToString(CultureInfo.InvariantCulture);
            TchBox.Text = set.ThrCrossingHgtFt.ToString(CultureInfo.InvariantCulture);
            ElevBox.Text = set.FieldElevFt.ToString(CultureInfo.InvariantCulture);
            RangeBox.Text = set.RangeNm.ToString(CultureInfo.InvariantCulture);
            DHBox.Text = set.DecisionHeightFt.ToString(CultureInfo.InvariantCulture);
            MaxAzBox.Text = set.MaxAzimuthDeg.ToString(CultureInfo.InvariantCulture);
            SensorOffsetBox.Text = set.SensorOffsetNm.ToString(CultureInfo.InvariantCulture);
        }

        public MainWindow.RunwaySettings GetSettings()
        {
            var s = new MainWindow.RunwaySettings();
            s.Icao = (IcaoBox.Text ?? "").Trim().ToUpperInvariant();
            s.Runway = (RunwayBox.Text ?? "").Trim().ToUpperInvariant();
            s.ThresholdLat = ParseDouble(LatBox.Text, _init != null ? _init.ThresholdLat : 0);
            s.ThresholdLon = ParseDouble(LonBox.Text, _init != null ? _init.ThresholdLon : 0);
            s.HeadingTrueDeg = ParseDouble(HdgBox.Text, _init != null ? _init.HeadingTrueDeg : 0);
            s.GlideSlopeDeg = ParseDouble(GsBox.Text, _init != null ? _init.GlideSlopeDeg : 3.0);
            s.ThrCrossingHgtFt = ParseDouble(TchBox.Text, _init != null ? _init.ThrCrossingHgtFt : 50.0);
            s.FieldElevFt = ParseDouble(ElevBox.Text, _init != null ? _init.FieldElevFt : 0);
            s.RangeNm = ParseDouble(RangeBox.Text, _init != null ? _init.RangeNm : 10.0);
            s.DecisionHeightFt = ParseDouble(DHBox.Text, _init != null ? _init.DecisionHeightFt : 200);
            s.MaxAzimuthDeg = ParseDouble(MaxAzBox.Text, _init != null ? _init.MaxAzimuthDeg : 10);
            s.SensorOffsetNm = ParseDouble(SensorOffsetBox.Text, _init != null ? _init.SensorOffsetNm : 0.5);
            return s;
        }

        private static double ParseDouble(string s, double defVal)
        {
            double v;
            if (double.TryParse((s ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return v;
            if (double.TryParse((s ?? "").Trim(), out v)) return v;
            return defVal;
        }

        private void OnOk(object sender, RoutedEventArgs e)
        {
            this.DialogResult = true;
            this.Close();
        }

        private void OnLookupClick(object sender, RoutedEventArgs e)
        {
            if (_nasrLoader == null)
            {
                MessageBox.Show(this, "NASR data not loaded. Please download or load NASR data first.",
                    "No Data", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string icao = (IcaoBox.Text ?? "").Trim().ToUpperInvariant();
            string runway = (RunwayBox.Text ?? "").Trim().ToUpperInvariant();

            if (string.IsNullOrEmpty(icao))
            {
                MessageBox.Show(this, "Please enter an ICAO airport code.",
                    "Missing ICAO", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrEmpty(runway))
            {
                MessageBox.Show(this, "Please enter a runway identifier (e.g., 09, 27L).",
                    "Missing Runway", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var data = _nasrLoader.GetRunway(icao, runway);
            if (data == null)
            {
                MessageBox.Show(this, 
                    string.Format("Runway {0}/{1} not found in NASR database.", icao, runway),
                    "Not Found", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Populate fields from NASR data
            LatBox.Text = data.Latitude.ToString(CultureInfo.InvariantCulture);
            LonBox.Text = data.Longitude.ToString(CultureInfo.InvariantCulture);
            HdgBox.Text = data.TrueHeading.ToString(CultureInfo.InvariantCulture);
            TchBox.Text = data.ThrCrossingHgtFt.ToString(CultureInfo.InvariantCulture);
            ElevBox.Text = data.FieldElevationFt.ToString(CultureInfo.InvariantCulture);

            MessageBox.Show(this, 
                string.Format("Runway data loaded:\n\nLat: {0:F6}\nLon: {1:F6}\nHeading: {2:F1}°\nElevation: {3:F0} ft",
                    data.Latitude, data.Longitude, data.TrueHeading, data.FieldElevationFt),
                "Success", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void RefreshRunwayList()
        {
            if (_nasrLoader == null) return;
            string icao = (IcaoBox.Text ?? "").Trim().ToUpperInvariant();
            if (string.IsNullOrEmpty(icao)) { RunwayBox.ItemsSource = null; return; }
            var ends = _nasrLoader.GetAirportRunways(icao);
            if (ends == null || ends.Count == 0)
            {
                RunwayBox.ItemsSource = null;
                return;
            }
            var ids = new System.Collections.Generic.List<string>();
            foreach (var e in ends)
            {
                string rid = (e.RunwayId ?? string.Empty).Trim().ToUpperInvariant();
                bool exists = ids.Any(x => string.Equals(x, rid, StringComparison.OrdinalIgnoreCase));
                if (!exists) ids.Add(rid);
            }
            ids.Sort(StringComparer.OrdinalIgnoreCase);
            RunwayBox.ItemsSource = ids;
        }
    }
}
