using System;

namespace PARScopeDisplay
{
    public class SimulatorConfig
    {
        public double SpawnBearing { get; set; }
        public double SpawnRange { get; set; }
        public double SpawnAlt { get; set; }
        public double SpawnSpeed { get; set; }
        public double SpawnHeading { get; set; }
        public int UpdateIntervalMs { get; set; }
    }

    public class UserSettings
    {
        public MainWindow.RunwaySettings Runway { get; set; }
        public MainWindow.WindowPosition WindowPosition { get; set; }
        public int HistoryDotsCount { get; set; }
        public int PlanAltTopHundreds { get; set; }
        public bool ShowGround { get; set; }
        public SimulatorConfig Simulator { get; set; }
        public string NasrLastSource { get; set; }
    }
}
