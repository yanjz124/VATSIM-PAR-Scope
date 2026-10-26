using System.IO;
using System.Text.Json;
using PARScope.Core.Par;
using PARScope.Core.Sim;
using PARScope.Core.Weather;

namespace PARScope.App.Services;

/// <summary>User settings, persisted as JSON in %AppData%\VATSIM-PAR-Scope.</summary>
public sealed class AppSettings
{
    public static string DataFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VATSIM-PAR-Scope");

    private static string FilePath => Path.Combine(DataFolder, "settings.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public int UdpPort { get; set; } = 49090;
    /// <summary>Master switch for training aids (training traffic, radio, suggested calls). Off = plain VATSIM scope.</summary>
    public bool TrainingMode { get; set; }
    public string? MicrophoneId { get; set; }
    public string? SpeakerId { get; set; }
    /// <summary>Speech engine id (see RecognizerOption.All).</summary>
    public string RecognizerId { get; set; } = "windows";
    public string? PilotVoice { get; set; }
    public string? TowerVoice { get; set; }
    /// <summary>Use the public 15 s VATSIM data feed when no vPilot plugin is streaming. Opt-in.</summary>
    public bool UseDataFeed { get; set; }
    /// <summary>Record received traffic to CSV for update-rate diagnostics.</summary>
    public bool RecordTraffic { get; set; } = true;
    public string? AirportId { get; set; }
    public string? RunwayId { get; set; }
    public string PresetId { get; set; } = "fpn68";
    public double RangeScaleNm { get; set; } = 10;
    public bool LogRange { get; set; }
    public bool ShowClutter { get; set; } = true;
    /// <summary>Radar noise, multipath, clutter and missed paints (training realism). Off by default.</summary>
    public bool SimulateErrors { get; set; }
    /// <summary>Scope font family; null = best installed (FixedDemiBold if present).</summary>
    public string? ScopeFont { get; set; }
    public bool Mti { get; set; } = true;
    public bool ShowSafetyLimits { get; set; } = true;
    public bool ShowCourseWedge { get; set; }
    public bool ShowScan { get; set; } = true;
    public bool ShowAdvisories { get; set; } = true;
    /// <summary>Suggested calls are a training aid; off for real VATSIM traffic unless asked for.</summary>
    public bool ShowAdvisoriesOnNetwork { get; set; }
    /// <summary>Side panel (selected target, training radio and traffic).</summary>
    public bool ShowPlanView { get; set; } = true;
    /// <summary>ASR scope pane beside the PAR.</summary>
    public bool ShowAsr { get; set; } = true;
    public double AsrWidth { get; set; } = 520;
    public double PlanRangeNm { get; set; } = 20;
    public double SidePanelWidth { get; set; } = 360;
    public TrainingDifficulty TrainingDifficulty { get; set; } = TrainingDifficulty.Normal;
    /// <summary>Side-panel section open/closed state.</summary>
    public Dictionary<string, bool> Panels { get; set; } = new();
    public int HistoryDots { get; set; } = 8;
    public MetarSource MetarSource { get; set; } = MetarSource.Vatsim;
    public DeviationBands Bands { get; set; } = new();

    /// <summary>Per-runway site overrides keyed "AIRPORT/RWY".</summary>
    public Dictionary<string, ParSiteConfig> Sites { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }

    public static string SiteKey(RunwayEnd r) => $"{r.AirportId}/{r.RunwayId}";

    /// <summary>
    /// Site config for a runway: saved overrides, else a 3.0° glidepath and 50 ft TCH. Airport data only
    /// carries the PAPI/VASI visual glide slope, which is not the PAR glidepath; set the real values from
    /// the approach plate.
    /// </summary>
    public ParSiteConfig SiteFor(RunwayEnd r) =>
        Sites.TryGetValue(SiteKey(r), out var s) ? s : new ParSiteConfig { TchFt = 50, GlidepathDeg = 3.0 };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // A corrupt settings file should never stop the scope from starting.
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DataFolder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch (IOException) { }
    }
}
