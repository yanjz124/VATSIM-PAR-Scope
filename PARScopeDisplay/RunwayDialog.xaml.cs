using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace PARScopeDisplay
{
    public partial class RunwayDialog : Window
    {
        private MainWindow.RunwaySettings _init;
        private NASRDataLoader _nasrLoader;
        private System.Collections.Generic.List<string> _allIcaoCodes;
        // Editor-only stubs disabled for normal build to avoid duplicate symbols
#if false
        private Button LookupButton;
        private ComboBox IcaoBox;
        private ComboBox RunwayBox;
        private TextBox LatBox;
        private TextBox LonBox;
        private TextBox HdgBox;
        private TextBox GsBox;
        private TextBox TchBox;
        private TextBox ElevBox;
        private TextBox RangeBox;
        private TextBox DHBox;
        private TextBox MaxAzBox;
    private TextBox SensorOffsetBox;
    private TextBox ApproachLightsBox;
#endif

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
                // Store all ICAO codes (letters-only IDs like DCA; exclude codes with digits like 7AL9)
                var ids = loader.GetAirportIds();
                // If loader reports no data, try to explicitly load cache again (best-effort)
                if ((ids == null || ids.Count == 0))
                {
                    TryLog("NASR loader GetAirportIds returned 0; attempting EnsureCacheLoaded()");
                    try { if (loader.EnsureCacheLoaded()) ids = loader.GetAirportIds(); } catch { }
                    TryLog("After EnsureCacheLoaded, GetAirportIds returned " + (ids?.Count ?? 0) + " items");
                }
                // If still empty, read cached JSON file directly as a last resort
                if (ids == null || ids.Count == 0)
                {
                    try
                    {
                        var cacheIds = loader.ReadCachedAirportIds();
                        if (cacheIds != null && cacheIds.Count > 0)
                        {
                            ids = cacheIds;
                            TryLog("ReadCachedAirportIds found " + ids.Count + " airport keys in nasr_cache.json");
                        }
                        else
                        {
                            TryLog("ReadCachedAirportIds found no keys in nasr_cache.json");
                        }
                    }
                    catch (Exception ex) { TryLog("ReadCachedAirportIds failed: " + ex.Message); }
                }
                _allIcaoCodes = ids.Where(id => !string.IsNullOrEmpty(id) && id.All(ch => ch >= 'A' && ch <= 'Z')).ToList();
                _allIcaoCodes.Sort(StringComparer.OrdinalIgnoreCase);
                
                // Initially show top 50 codes
                IcaoBox.ItemsSource = _allIcaoCodes.Take(50).ToList();
                IcaoBox.IsEditable = true;
                
                // Wire up dynamic filtering
                IcaoBox.KeyUp += IcaoBox_KeyUp;
                IcaoBox.DropDownOpened += (s, e) =>
                {
                    // When dropdown opens, clear selection to show filtered list without highlighting
                    IcaoBox.SelectedIndex = -1;
                    ClearComboSelection(IcaoBox);
                };
                IcaoBox.SelectionChanged += (s, e) =>
                {
                    if (IcaoBox.SelectedItem != null)
                        RefreshRunwayList();
                };
                IcaoBox.LostFocus += (s, e) => RefreshRunwayList();
                
                // Also clear selection and caret behavior for RunwayBox when its dropdown opens
                RunwayBox.DropDownOpened += (s, e) =>
                {
                    RunwayBox.SelectedIndex = -1;
                    ClearComboSelection(RunwayBox);
                };
                // Show NASR cache metadata if available
                try
                {
                    string info = "NASR: (not loaded)";
                    if (loader.LastLoadedUtc.HasValue || !string.IsNullOrEmpty(loader.LastLoadedSource))
                    {
                        var dt = loader.LastLoadedUtc.HasValue ? loader.LastLoadedUtc.Value.ToString("yyyy-MM-dd HH:mm UTC") : "(unknown date)";
                        var src = !string.IsNullOrEmpty(loader.LastLoadedSource) ? loader.LastLoadedSource : "(cached)";
                        info = $"NASR: {dt} — {src}";
                    }
                    NasrInfoText.Text = info;
                    if (_allIcaoCodes == null || _allIcaoCodes.Count == 0)
                    {
                        NasrInfoText.Text += " (no airports loaded)";
                        TryLog("NASR loader initialized but no airport IDs found; NasrInfoText updated.");
                    }
                }
                catch { }
            }
        }

        private void TryLog(string msg)
        {
            try
            {
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                var folder = System.IO.Path.Combine(appData, "VATSIM-PAR-Scope");
                System.IO.Directory.CreateDirectory(folder);
                var logPath = System.IO.Path.Combine(folder, "startup_log.txt");
                System.IO.File.AppendAllText(logPath, DateTime.UtcNow.ToString("o") + " " + msg + System.Environment.NewLine);
            }
            catch { }
        }

        // Resolve an ICAO-like key into one present in the NASR loader. Tries exact, then with/without leading 'K', then suffix/contains match.
        private string ResolveIcaoKey(string icao)
        {
            if (_nasrLoader == null || string.IsNullOrEmpty(icao)) return icao;
            string want = icao.ToUpperInvariant();
            // Exact
            var ends = _nasrLoader.GetAirportRunways(want);
            if (ends != null && ends.Count > 0) return want;

            // Try adding or removing leading 'K' (common US variants)
            if (!want.StartsWith("K") && want.Length == 3)
            {
                var k = "K" + want;
                ends = _nasrLoader.GetAirportRunways(k);
                if (ends != null && ends.Count > 0) return k;
            }
            if (want.StartsWith("K") && want.Length == 4)
            {
                var s = want.Substring(1);
                ends = _nasrLoader.GetAirportRunways(s);
                if (ends != null && ends.Count > 0) return s;
            }

            // Fall back to any loaded code that ends with or contains the typed text
            if (_allIcaoCodes != null && _allIcaoCodes.Count > 0)
            {
                var endsWith = _allIcaoCodes.FirstOrDefault(a => a.EndsWith(want, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrEmpty(endsWith)) return endsWith;
                var contains = _allIcaoCodes.FirstOrDefault(a => a.IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0);
                if (!string.IsNullOrEmpty(contains)) return contains;
            }

            // As a last resort, return the original (will likely be empty results)
            return want;
        }

        private void IcaoBox_KeyUp(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (_allIcaoCodes == null || _allIcaoCodes.Count == 0) return;
            
            // Ignore navigation keys
            if (e.Key == System.Windows.Input.Key.Down || e.Key == System.Windows.Input.Key.Up ||
                e.Key == System.Windows.Input.Key.Enter || e.Key == System.Windows.Input.Key.Tab)
                return;
            
            string text = (IcaoBox.Text ?? "").Trim().ToUpperInvariant();
            if (string.IsNullOrEmpty(text))
            {
                // Show top 50 when empty
                IcaoBox.ItemsSource = _allIcaoCodes.Take(50).ToList();
                IcaoBox.IsDropDownOpen = false;
            }
            else
            {
                // Filter to matches starting with typed text, then containing it
                var startsWith = _allIcaoCodes.Where(id => id.StartsWith(text, StringComparison.OrdinalIgnoreCase)).ToList();
                var contains = _allIcaoCodes.Where(id => id.IndexOf(text, StringComparison.OrdinalIgnoreCase) > 0).ToList();
                
                var matches = startsWith.Concat(contains).Distinct().Take(50).ToList();
                
                IcaoBox.ItemsSource = matches;
                IcaoBox.IsDropDownOpen = matches.Count > 0;
                
                // Clear selection to prevent text overwrite and keep caret at end
                IcaoBox.SelectedIndex = -1;
                ClearComboSelection(IcaoBox);
            }
            
            RefreshRunwayList();
        }

        public void SetInitial(MainWindow.RunwaySettings set)
        {
            _init = set;
            if (set == null) return;
            // Display FAA LID in the IcaoBox, not the ICAO_ID
            string displayCode = !string.IsNullOrEmpty(set.FaaLid) ? set.FaaLid : set.Icao;
            IcaoBox.Text = displayCode;
            ClearComboSelection(IcaoBox);
            RefreshRunwayList();
            RunwayBox.Text = set.Runway;
            ClearComboSelection(RunwayBox);
            LatBox.Text = set.ThresholdLat.ToString(CultureInfo.InvariantCulture);
            LonBox.Text = set.ThresholdLon.ToString(CultureInfo.InvariantCulture);
            HdgBox.Text = set.HeadingTrueDeg.ToString(CultureInfo.InvariantCulture);
            // show signed magnetic variation if present in settings (editable)
            MagVarBox.Text = (set.MagVariationDeg >= 0 ? "+" : "") + set.MagVariationDeg.ToString("F1", CultureInfo.InvariantCulture);
            GsBox.Text = set.GlideSlopeDeg.ToString(CultureInfo.InvariantCulture);
            TchBox.Text = set.ThrCrossingHgtFt.ToString(CultureInfo.InvariantCulture);
            ElevBox.Text = set.FieldElevFt.ToString(CultureInfo.InvariantCulture);
            RangeBox.Text = set.RangeNm.ToString(CultureInfo.InvariantCulture);
            DHBox.Text = set.DecisionHeightFt.ToString(CultureInfo.InvariantCulture);
            MaxAzBox.Text = set.MaxAzimuthDeg.ToString(CultureInfo.InvariantCulture);
            SensorOffsetBox.Text = set.SensorOffsetNm.ToString(CultureInfo.InvariantCulture);
            ApproachLightsBox.Text = (set.ApproachLightLengthFt > 0 ? set.ApproachLightLengthFt.ToString(CultureInfo.InvariantCulture) : "0");
            // Mag var is shown in the editable MagVarBox above
        }

        // Helper: clear any text selection in the editable part of a ComboBox and move caret to end
        private void ClearComboSelection(ComboBox cb)
        {
            if (cb == null) return;
            try
            {
                var tb = cb.Template.FindName("PART_EditableTextBox", cb) as TextBox;
                if (tb != null)
                {
                    int len = (tb.Text ?? string.Empty).Length;
                    tb.SelectionStart = len;
                    tb.SelectionLength = 0;
                }
            }
            catch
            {
                // ignore if template not applied yet
            }
        }

        public MainWindow.RunwaySettings GetSettings()
        {
            var s = new MainWindow.RunwaySettings();
            string faaAirportCode = (IcaoBox.Text ?? "").Trim().ToUpperInvariant();
            s.FaaLid = faaAirportCode; // Store FAA LID for display
            s.Icao = faaAirportCode; // Default to FAA code, will replace with ICAO_ID if available from NASR
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
            s.ApproachLightLengthFt = ParseDouble(ApproachLightsBox.Text, _init != null ? _init.ApproachLightLengthFt : 0.0);
            // If NASR loader available, pull airport-level magnetic variation and ICAO_ID for this airport/runway
            try
            {
                if (_nasrLoader != null && !string.IsNullOrEmpty(faaAirportCode))
                {
                    string key = ResolveIcaoKey(faaAirportCode);
                    var r = _nasrLoader.GetRunway(key, s.Runway);
                    try
                    {
                        if (r != null)
                        {
                            // Use ICAO_ID from NASR data for METAR lookups ONLY
                            // If ICAO_ID is available, use it. Otherwise fall back to FAA LID.
                            if (!string.IsNullOrEmpty(r.IcaoId))
                            {
                                s.Icao = r.IcaoId;
                            }
                            
                            s.MagVariationDeg = r.MagneticVariationDeg;
                            // If user manually entered a MagVar, prefer that value
                            if (!string.IsNullOrEmpty(MagVarBox.Text))
                            {
                                if (double.TryParse(MagVarBox.Text.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double userMv))
                                    s.MagVariationDeg = userMv;
                            }
                            // If the user did not supply lat/lon/heading/etc in the dialog,
                            // pull sensible defaults from the NASR runway data so the
                            // plan view can render runways correctly.
                            try
                            {
                                if (string.IsNullOrWhiteSpace(LatBox.Text)) s.ThresholdLat = r.Latitude;
                                if (string.IsNullOrWhiteSpace(LonBox.Text)) s.ThresholdLon = r.Longitude;
                                if (string.IsNullOrWhiteSpace(HdgBox.Text)) s.HeadingTrueDeg = r.TrueHeading;
                                if (string.IsNullOrWhiteSpace(TchBox.Text)) s.ThrCrossingHgtFt = r.ThrCrossingHgtFt;
                                if (string.IsNullOrWhiteSpace(ElevBox.Text)) s.FieldElevFt = r.FieldElevationFt;
                                if (string.IsNullOrWhiteSpace(ApproachLightsBox.Text))
                                {
                                    try { s.ApproachLightLengthFt = MainWindow.GetApproachLightLengthFt(r.ApchLgtSystemCode); } catch { }
                                }
                            }
                            catch { }
                            // Log resolved key when different
                            if (!string.Equals(key, faaAirportCode, StringComparison.OrdinalIgnoreCase))
                                TryLog($"Resolved ICAO {faaAirportCode} -> {key} for magvar lookup");
                        }
                        else
                        {
                            TryLog($"MagVar lookup failed for {faaAirportCode}/{s.Runway} (resolved {key})");
                        }
                    }
                    catch { }
                }
            }
            catch { }
            // If NASR not used, still allow the user-entered mag var to be captured
            if (string.IsNullOrEmpty(s.Icao) || _nasrLoader == null)
            {
                if (!string.IsNullOrEmpty(MagVarBox.Text))
                {
                    if (double.TryParse(MagVarBox.Text.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double userMv2))
                        s.MagVariationDeg = userMv2;
                }
            }
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

            string key = ResolveIcaoKey(icao);
            var data = _nasrLoader.GetRunway(key, runway);
            if (data == null)
            {
                TryLog($"Lookup failed for {icao}/{runway} (resolved {key})");
                MessageBox.Show(this, 
                    string.Format("Runway {0}/{1} not found in NASR database.", icao, runway),
                    "Not Found", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!string.Equals(key, icao, StringComparison.OrdinalIgnoreCase))
                TryLog($"Lookup succeeded for {icao}/{runway} using resolved key {key}");

            // Populate fields from NASR data
            LatBox.Text = data.Latitude.ToString(CultureInfo.InvariantCulture);
            LonBox.Text = data.Longitude.ToString(CultureInfo.InvariantCulture);
            HdgBox.Text = data.TrueHeading.ToString(CultureInfo.InvariantCulture);
            TchBox.Text = data.ThrCrossingHgtFt.ToString(CultureInfo.InvariantCulture);
            ElevBox.Text = data.FieldElevationFt.ToString(CultureInfo.InvariantCulture);
            // If NASR contains approach light code, compute a default length for display/override
            try
            {
                double defaultLen = 0.0;
                if (!string.IsNullOrEmpty(data.ApchLgtSystemCode))
                {
                    defaultLen = MainWindow.GetApproachLightLengthFt(data.ApchLgtSystemCode);
                }
                ApproachLightsBox.Text = (defaultLen > 0 ? defaultLen.ToString(CultureInfo.InvariantCulture) : "0");
            }
            catch { ApproachLightsBox.Text = "0"; }

            // Show magnetic variation if available (editable box)
            try
            {
                double mv = data.MagneticVariationDeg;
                MagVarBox.Text = (mv >= 0 ? "+" : "") + mv.ToString("F1", CultureInfo.InvariantCulture);
            }
            catch { MagVarBox.Text = string.Empty; }

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
            var key = ResolveIcaoKey(icao);
            var ends = _nasrLoader.GetAirportRunways(key);
            if ((ends == null || ends.Count == 0) && !string.Equals(key, icao, StringComparison.OrdinalIgnoreCase))
            {
                TryLog($"RefreshRunwayList: resolved {icao} -> {key} but no runways found for resolved key");
            }
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
            // Clear selection in the editable runway box so typed text is not highlighted
            ClearComboSelection(RunwayBox);
        }
    }
}
