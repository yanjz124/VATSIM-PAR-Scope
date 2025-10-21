using System;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;

namespace PARScopeDisplay
{
    public partial class AircraftListWindow : Window
    {
        private readonly ConcurrentDictionary<string, System.Collections.Generic.Dictionary<string, object>> _aircraft;
        private readonly DispatcherTimer _updateTimer;
        private readonly ObservableCollection<AircraftInfo> _aircraftList = new ObservableCollection<AircraftInfo>();

        public AircraftListWindow(ConcurrentDictionary<string, System.Collections.Generic.Dictionary<string, object>> aircraft)
        {
            InitializeComponent();

            _aircraft = aircraft;
            AircraftDataGrid.ItemsSource = _aircraftList;

            // Update aircraft list every 500ms
            _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _updateTimer.Tick += UpdateAircraftList;
            _updateTimer.Start();

            // Initial update
            UpdateAircraftList(null, null);

            this.Closed += (s, e) => _updateTimer.Stop();
        }

        private void UpdateAircraftList(object sender, EventArgs e)
        {
            try
            {
                // Clear and repopulate the list
                _aircraftList.Clear();

                foreach (var kvp in _aircraft)
                {
                    var ac = kvp.Value;
                    var aircraftInfo = new AircraftInfo
                    {
                        Callsign = GetString(ac, "callsign", ""),
                        Latitude = GetDouble(ac, "lat", 0),
                        Longitude = GetDouble(ac, "lon", 0),
                        AltitudeFt = GetDouble(ac, "alt_ft", 0),
                        GroundSpeedKts = GetGroundSpeedKts(ac),
                        Heading = GetDouble(ac, "heading", 0),
                        Squawk = GetString(ac, "squawk", "")
                    };
                    _aircraftList.Add(aircraftInfo);
                }

                // Update header info
                AircraftCountText.Text = _aircraft.Count.ToString();
                LastUpdateText.Text = DateTime.Now.ToString("HH:mm:ss");
            }
            catch { }
        }

        private static string GetString(System.Collections.Generic.Dictionary<string, object> dict, string key, string def)
        {
            if (!dict.ContainsKey(key) || dict[key] == null) return def;
            return dict[key].ToString();
        }

        private static double GetDouble(System.Collections.Generic.Dictionary<string, object> dict, string key, double def)
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

        private static double GetGroundSpeedKts(System.Collections.Generic.Dictionary<string, object> dict)
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

        public class AircraftInfo : INotifyPropertyChanged
        {
            private string _callsign;
            private double _latitude;
            private double _longitude;
            private double _altitudeFt;
            private double _groundSpeedKts;
            private double _heading;
            private string _squawk;

            public string Callsign
            {
                get => _callsign;
                set { _callsign = value; OnPropertyChanged(nameof(Callsign)); }
            }

            public double Latitude
            {
                get => _latitude;
                set { _latitude = value; OnPropertyChanged(nameof(Latitude)); }
            }

            public double Longitude
            {
                get => _longitude;
                set { _longitude = value; OnPropertyChanged(nameof(Longitude)); }
            }

            public double AltitudeFt
            {
                get => _altitudeFt;
                set { _altitudeFt = value; OnPropertyChanged(nameof(AltitudeFt)); }
            }

            public double GroundSpeedKts
            {
                get => _groundSpeedKts;
                set { _groundSpeedKts = value; OnPropertyChanged(nameof(GroundSpeedKts)); }
            }

            public double Heading
            {
                get => _heading;
                set { _heading = value; OnPropertyChanged(nameof(Heading)); }
            }

            public string Squawk
            {
                get => _squawk;
                set { _squawk = value; OnPropertyChanged(nameof(Squawk)); }
            }

            public event PropertyChangedEventHandler PropertyChanged;

            protected virtual void OnPropertyChanged(string propertyName)
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            }
        }
    }
}