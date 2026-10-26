using System.IO;
using System.Text.Json;
using PARScope.Core.Par;
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
    /// <summary>Use the public 15 s VATSIM data feed when no vPilot plugin is streaming.</summary>
    public bool DataFeedFallback { get; set; } = true;
    public string? AirportId { get; set; }
    public string? RunwayId { get; set; }
    public string PresetId { get; set; } = "fpn68";
    public double RangeScaleNm { get; set; } = 10;
    public bool LogRange { get; set; }
    public bool ShowClutter { get; set; } = true;
    public bool Mti { get; set; } = true;
    public bool ShowSafetyLimits { get; set; } = true;
    public bool ShowTerpsLateral { get; set; }
    public bool ShowCourseWedge { get; set; }
    public bool ShowScan { get; set; } = true;
    public bool ShowAsrSweep { get; set; }
    public bool ShowAdvisories { get; set; } = true;
    /// <summary>Suggested calls are a training aid; off for real VATSIM traffic unless asked for.</summary>
    public bool ShowAdvisoriesOnNetwork { get; set; }
    public bool ShowPlanView { get; set; } = true;
    public double PlanRangeNm { get; set; } = 20;
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

    /// <summary>Site config for a runway: saved overrides, else defaults seeded from the runway's published TCH and glidepath.</summary>
    public ParSiteConfig SiteFor(RunwayEnd r) =>
        Sites.TryGetValue(SiteKey(r), out var s)
            ? s
            : new ParSiteConfig { TchFt = r.TchFt ?? 50, GlidepathDeg = r.GlidepathDeg ?? 3.0 };

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
