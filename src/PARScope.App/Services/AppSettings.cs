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
    /// <summary>Push-to-talk key (WPF Key name).</summary>
    public string PttKey { get; set; } = "Space";
    /// <summary>Hotkeys changed from their defaults: command id → "primary|alternate" (see KeyMap).</summary>
    public Dictionary<string, string> KeyBindings { get; set; } = new();
    public string? SpeakerId { get; set; }
    /// <summary>Speech engine id (see RecognizerOption.All).</summary>
    public string RecognizerId { get; set; } = "windows";
    /// <summary>Run Whisper on the graphics card when possible (applies at start-up).</summary>
    public bool SpeechGpu { get; set; } = true;
    public string? PilotVoice { get; set; }
    public string? TowerVoice { get; set; }
    public string? ApproachVoice { get; set; }
    /// <summary>Voice of the AI final controller.</summary>
    public string? FinalVoice { get; set; }
    /// <summary>Speech engine for pilot/tower/approach voices (see VoiceEngineOption.All).</summary>
    public string VoiceEngine { get; set; } = "windows";
    /// <summary>Each pilot speaks with their own voice.</summary>
    public bool VaryPilotVoices { get; set; } = true;
    /// <summary>Band-limit and squelch the voices like a radio.</summary>
    public bool RadioEffect { get; set; } = true;
    /// <summary>Use the public 15 s VATSIM data feed when no vPilot plugin is streaming. Opt-in.</summary>
    public bool UseDataFeed { get; set; }
    /// <summary>CRC feed capture: Off, RawSocket or Npcap (see CrcCaptureMode). Off by default.</summary>
    public string CrcFeed { get; set; } = "Auto";
    /// <summary>Source priority when several deliver the same aircraft, e.g. "Crc,VPilot,Feed" (SourceGroup names).</summary>
    public string SourcePriority { get; set; } = "Crc,VPilot,Feed";
    /// <summary>Listen for xPilot's visual-machine stream on TCP 53100 (local only).</summary>
    public bool XPilotVisual { get; set; } = true;
    /// <summary>Look for a newer release on GitHub at start-up.</summary>
    public bool CheckForUpdates { get; set; } = true;
    /// <summary>Training arrivals use the live METAR surface wind instead of a random one.</summary>
    public bool TrainingLiveWind { get; set; } = true;
    /// <summary>Longest generated training callsign on the radar (5 to 7, a flight plan's limit); longer callwords are abbreviated.</summary>
    public int MaxCallsignLength { get; set; } = 7;
    /// <summary>The old default of 5 has been moved to 6 once (a 5 chosen afterwards is kept).</summary>
    public bool CallsignLengthMigrated { get; set; }
    /// <summary>The old default of 6 has been moved to 7 once (a 6 chosen afterwards is kept).</summary>
    public bool CallsignLength7Migrated { get; set; }
    /// <summary>Missed approaches, touch and goes and low approaches come round again for another approach.</summary>
    public bool TrainingReturnForAnother { get; set; } = true;
    /// <summary>Where approach hands arrivals over: FinalOnly, Mixed or BaseAndDogleg (HandoffMix).</summary>
    public string TrainingHandoffs { get; set; } = "FinalOnly";
    /// <summary>An AI final controller works the training arrivals while you watch. Opt-in.</summary>
    public bool AiFinalController { get; set; }
    /// <summary>The AI final controller uses carrier (CCA) wording: soft headings, lock-on, "at minimums". Opt-in.</summary>
    public bool AiNavyPhraseology { get; set; }
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
    /// <summary>Scope text size, 1 = default (Settings › Display).</summary>
    public double ScopeTextScale { get; set; } = 1.0;
    /// <summary>Scope target symbols, history dots and brackets, 1 = default (Settings › Display).</summary>
    public double ScopeSymbolScale { get; set; } = 1.0;
    public bool Mti { get; set; } = true;
    public bool ShowSafetyLimits { get; set; } = true;
    public bool ShowCourseWedge { get; set; }
    public bool ShowScan { get; set; } = true;
    /// <summary>ASR compass rose; null = the radar model's default.</summary>
    public bool? ShowCompassRose { get; set; }
    public bool ShowAdvisories { get; set; } = true;
    /// <summary>Suggested calls are a training aid; off for real VATSIM traffic unless asked for.</summary>
    public bool ShowAdvisoriesOnNetwork { get; set; }
    /// <summary>Side panel (selected target, training radio and traffic).</summary>
    public bool ShowPlanView { get; set; } = true;
    /// <summary>The main scope area shows the ASR instead of the PAR (flip).</summary>
    public bool AsrFlipped { get; set; }
    /// <summary>ASR in its own window, with its placement.</summary>
    public bool AsrPoppedOut { get; set; }
    public double? AsrWindowLeft { get; set; }
    public double? AsrWindowTop { get; set; }
    public double AsrWindowWidth { get; set; } = 720;
    public double AsrWindowHeight { get; set; } = 760;
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
    /// Site config for a runway: saved overrides; else the published PAR glidepath, TCH, RPI and DH from the
    /// FAA radar minimums (US military and joint-use fields); else a 3.0° glidepath and 50 ft TCH. Airport data
    /// only carries the PAPI/VASI visual glide slope, which is not the PAR glidepath.
    /// </summary>
    public ParSiteConfig SiteFor(RunwayEnd r, PARScope.Core.Radar.RadarPreset? preset = null)
    {
        if (Sites.TryGetValue(SiteKey(r), out var s)) return s;
        var site = new ParSiteConfig { TchFt = 50, GlidepathDeg = 3.0 };
        if (preset?.DefaultAntennaAimDeg is double aim) site = site with { AntennaAimDeg = aim };
        // Published PAR glidepath/TCH/RPI/DH where the FAA radar minimums list this runway.
        return PARScope.Core.Par.PublishedPar.Find(r.Icao ?? r.AirportId, r.RunwayId) is { } pub ? PARScope.Core.Par.PublishedPar.Apply(site, pub) : site;
    }

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
