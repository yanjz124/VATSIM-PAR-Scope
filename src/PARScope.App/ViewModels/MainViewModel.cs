using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PARScope.App.Services;
using PARScope.Core.Data;
using PARScope.Core.Geo;
using PARScope.Core.Par;
using PARScope.Core.Radar;
using PARScope.Core.Sim;
using PARScope.Core.Weather;

namespace PARScope.App.ViewModels;

public enum LinkHealth { None, Slow, Fast, Error }

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private static readonly HttpClient Http = CreateHttp();
    private readonly AppSettings _settings;
    private readonly AirportDatabase _airports;
    private readonly MetarService _metar = new(Http);
    private CancellationTokenSource? _metarCts;
    private double _lastMetarFetch = double.NegativeInfinity;

    public MainViewModel(AppSettings settings, ScopeEngine engine)
    {
        _settings = settings;
        Engine = engine;
        _airports = new AirportDatabase(AppSettings.DataFolder);

        SelectedPreset = RadarPreset.ById(settings.PresetId);
        RangeScaleNm = RangeScales.Contains(settings.RangeScaleNm) ? settings.RangeScaleNm : RangeScales[^1];
        LogRange = settings.LogRange;
        ShowClutter = settings.ShowClutter;
        Mti = settings.Mti;
        ShowSafetyLimits = settings.ShowSafetyLimits;
        ShowCourseWedge = settings.ShowCourseWedge;
        UseUkBands = settings.Bands.Standard == DeviationStandard.UkRa3291;
        UseDataFeed = settings.UseDataFeed;
        LoadAudioSettings();
        RecordTraffic = settings.RecordTraffic;
        Engine.Recorder.Enabled = RecordTraffic;
        TrainingMode = settings.TrainingMode;
        bool Panel(string key, bool def) => settings.Panels.TryGetValue(key, out var v) ? v : def;
        ExpandTarget = Panel("target", true);
        ExpandAsr = Panel("asr", true);
        ExpandWeather = Panel("weather", true);
        ExpandVoice = Panel("voice", true);
        ExpandTraining = Panel("training", false);
        ExpandSetup = Panel("setup", false);
        ExpandDiagnostics = Panel("diagnostics", false);
        ExpandAudio = Panel("audio", false);
        ShowScan = settings.ShowScan;
        ShowAdvisories = settings.ShowAdvisories;
        ShowAdvisoriesOnNetwork = settings.ShowAdvisoriesOnNetwork;
        ShowPlanView = settings.ShowPlanView;
        PlanRangeNm = settings.PlanRangeNm;
        HistoryDots = settings.HistoryDots;
        UseVatsimMetar = settings.MetarSource == MetarSource.Vatsim;
        Engine.ClutterEnabled = ShowClutter;
        SimulateErrors = settings.SimulateErrors;
        Engine.SimulateErrors = SimulateErrors;
        ScopeFonts = PARScope.App.Rendering.Ink.AvailableFamilies();
        ScopeFont = settings.ScopeFont is { } f && ScopeFonts.Contains(f) ? f : ScopeFonts.FirstOrDefault();
        Engine.MtiEnabled = Mti;
    }

    public ScopeEngine Engine { get; }
    public IReadOnlyList<RadarPreset> Presets => RadarPreset.All;
    public ObservableCollection<AirportRecord> AirportResults { get; } = [];
    public ObservableCollection<RunwayEnd> Runways { get; } = [];
    public ObservableCollection<double> RangeScales { get; } = [];
    public IReadOnlyList<double> PlanRanges { get; } = [10, 20, 40];

    // ---- Setup ---------------------------------------------------------------------------------

    public async Task InitializeAsync()
    {
        Engine.StartNetwork(_settings.UdpPort);
        Engine.StartDataFeed(Http, () => _settings.UseDataFeed);
        bool cached = await Task.Run(_airports.TryLoadCache);
        AirportDbStatus = cached ? DbStatusText() : "No airport data yet";
        if (!cached)
            await UpdateAirportsAsync();

        string? airportId = DemoAirport ?? _settings.AirportId;
        string? runwayId = DemoAirport != null ? DemoRunway : _settings.RunwayId;
        if (airportId != null && _airports.Find(airportId) is { } apt)
        {
            SelectAirport(apt);
            SelectedRunway = Runways.FirstOrDefault(r => r.RunwayId.Equals(runwayId, StringComparison.OrdinalIgnoreCase)) ?? Runways.FirstOrDefault();
        }

        if (DemoMode && Engine.Geometry != null)
        {
            TrainingMode = true;
            for (int i = 0; i < 3; i++) Engine.Training.SpawnRandom(PilotMode.SelfFlying);
            Select(Engine.Training.Aircraft[0].Callsign);
        }
    }

    public bool DemoMode { get; init; }
    public string? DemoAirport { get; init; }
    public string? DemoRunway { get; init; }

    [ObservableProperty] public partial string AirportQuery { get; set; } = "";
    [ObservableProperty] public partial AirportRecord? SelectedAirport { get; set; }
    [ObservableProperty] public partial RunwayEnd? SelectedRunway { get; set; }
    [ObservableProperty] public partial RadarPreset SelectedPreset { get; set; }
    [ObservableProperty] public partial double RangeScaleNm { get; set; }
    [ObservableProperty] public partial string AirportDbStatus { get; set; } = "";
    [ObservableProperty] public partial bool IsUpdatingAirports { get; set; }

    partial void OnAirportQueryChanged(string value)
    {
        AirportResults.Clear();
        foreach (var a in _airports.Search(value, 25)) AirportResults.Add(a);
    }

    public bool SubmitAirportQuery()
    {
        var apt = _airports.Find(AirportQuery) ?? AirportResults.FirstOrDefault();
        if (apt == null) { StatusMessage = $"Airport '{AirportQuery}' not found"; return false; }
        SelectAirport(apt);
        return true;
    }

    public void SelectAirport(AirportRecord apt)
    {
        SelectedAirport = apt;
        AirportQuery = "";
        AirportResults.Clear();
        Runways.Clear();
        foreach (var r in apt.Runways.OrderBy(r => r.RunwayId, StringComparer.Ordinal)) Runways.Add(r);
        SelectedRunway = Runways.FirstOrDefault();
    }

    partial void OnSelectedRunwayChanged(RunwayEnd? value)
    {
        if (value != null) Reconfigure();
    }

    partial void OnSelectedPresetChanged(RadarPreset oldValue, RadarPreset newValue)
    {
        // A selector briefly pushes null while its items load; keep the previous preset.
        if (newValue is null) { SelectedPreset = oldValue ?? RadarPreset.Fpn68; return; }
        var value = newValue;
        RefreshRangeScales(value);
        IsAnalogPreset = value.Style == DisplayStyle.Phosphor;
        // Each model brings its own sweep-trace default; the Scan button overrides it.
        if (oldValue is not null && oldValue.Id != value.Id)
        {
            ShowScan = value.SweepTrace;
            LogRange = value.LogRangeDefault;
        }
        _settings.PresetId = value.Id;
        Reconfigure();
    }

    private void RefreshRangeScales(RadarPreset value)
    {
        RangeScales.Clear();
        foreach (var s in value.ScalesFor(Mti)) RangeScales.Add(s);
        if (!RangeScales.Contains(RangeScaleNm))
            RangeScaleNm = RangeScales.Contains(10) ? 10 : RangeScales[^1];
        OnPropertyChanged(nameof(RangeScaleItem));
    }

    partial void OnRangeScaleNmChanged(double value)
    {
        _settings.RangeScaleNm = value;
        OnPropertyChanged(nameof(RangeScaleItem));
    }

    /// <summary>Range list binding. The list is briefly empty while the preset changes, and its null
    /// selection must not reach <see cref="RangeScaleNm"/>.</summary>
    public object? RangeScaleItem
    {
        get => RangeScales.Contains(RangeScaleNm) ? RangeScaleNm : null;
        set { if (value is double d) RangeScaleNm = d; }
    }

    private void Reconfigure()
    {
        if (SelectedAirport is not { } apt || SelectedRunway is not { } rw || SelectedPreset is null) return;
        Site = _settings.SiteFor(rw);
        Engine.Configure(apt, rw, Site, SelectedPreset);
        _settings.AirportId = apt.Id;
        _settings.RunwayId = rw.RunwayId;
        RunwayTitle = $"{apt.Id} RWY {rw.RunwayId}";
        RunwayDetail = $"{apt.Name} · final {rw.MagneticCourseDeg:000}°M ({rw.TrueHeadingDeg:000.0}°T) · thr elev {rw.ThresholdElevFt:0} ft · {rw.Source}";
        LoadSiteFields();
        _lastMetarFetch = double.NegativeInfinity;
    }

    [ObservableProperty] public partial string RunwayTitle { get; set; } = "No runway selected";
    [ObservableProperty] public partial string RunwayDetail { get; set; } = "Search for an airport to begin.";

    [RelayCommand]
    private async Task UpdateAirportsAsync()
    {
        if (IsUpdatingAirports) return;
        IsUpdatingAirports = true;
        try
        {
            var progress = new Progress<string>(s => AirportDbStatus = s);
            StatusMessage = await _airports.UpdateAsync(Http, progress, CancellationToken.None);
            AirportDbStatus = DbStatusText();
        }
        finally { IsUpdatingAirports = false; }
    }

    public void ImportNasr(string path)
    {
        try
        {
            StatusMessage = _airports.ImportNasrZip(path);
            AirportDbStatus = DbStatusText();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            StatusMessage = "NASR import failed: " + ex.Message;
        }
    }

    private string DbStatusText() =>
        $"{_airports.Count:N0} airports · {_airports.Sources}" + (_airports.UpdatedUtc is { } d ? $" · {d:dd MMM yyyy}" : "");

    // ---- Site configuration --------------------------------------------------------------------

    public ParSiteConfig Site { get; private set; } = new();

    [ObservableProperty] public partial double SiteGlidepath { get; set; }
    [ObservableProperty] public partial double SiteTch { get; set; }
    [ObservableProperty] public partial double SiteDh { get; set; }
    [ObservableProperty] public partial double SiteSetback { get; set; }
    [ObservableProperty] public partial double SiteOffset { get; set; }
    [ObservableProperty] public partial double SiteAim { get; set; }

    private void LoadSiteFields()
    {
        SiteGlidepath = Site.GlidepathDeg;
        SiteTch = Site.TchFt;
        SiteDh = Site.DecisionHeightFt;
        SiteSetback = Site.AntennaSetbackFt;
        SiteOffset = Site.AntennaOffsetFt;
        SiteAim = Site.AntennaAimDeg;
    }

    [RelayCommand]
    private void ApplySite()
    {
        if (SelectedRunway is not { } rw) return;
        var site = Site with
        {
            GlidepathDeg = Math.Clamp(SiteGlidepath, 2.0, 6.0),
            TchFt = Math.Clamp(SiteTch, 20, 100),
            DecisionHeightFt = Math.Clamp(SiteDh, 100, 1000),
            AntennaSetbackFt = Math.Clamp(SiteSetback, 0, 10000),
            AntennaOffsetFt = Math.Clamp(SiteOffset, -2500, 2500),
            AntennaAimDeg = Math.Clamp(SiteAim, 0, 15),
        };
        _settings.Sites[AppSettings.SiteKey(rw)] = site;
        Reconfigure();
        StatusMessage = $"Approach setup saved for {rw.DisplayName}";
    }

    [RelayCommand]
    private void ResetSite()
    {
        if (SelectedRunway is not { } rw) return;
        _settings.Sites.Remove(AppSettings.SiteKey(rw));
        Reconfigure();
    }

    // ---- Display options -----------------------------------------------------------------------

    [ObservableProperty] public partial bool LogRange { get; set; }
    [ObservableProperty] public partial bool ShowClutter { get; set; }
    [ObservableProperty] public partial bool SimulateErrors { get; set; }
    [ObservableProperty] public partial string? ScopeFont { get; set; }
    public IReadOnlyList<string> ScopeFonts { get; }
    [ObservableProperty] public partial bool Mti { get; set; }
    [ObservableProperty] public partial bool ShowSafetyLimits { get; set; }
    [ObservableProperty] public partial bool ShowCourseWedge { get; set; }
    /// <summary>UK RA 3291 fixed-feet deviation terms instead of the US Navy target-quarter method.</summary>
    [ObservableProperty] public partial bool UseUkBands { get; set; }
    /// <summary>Analog presets have a sweep trace; the Scan toggle only applies to them.</summary>
    [ObservableProperty] public partial bool IsAnalogPreset { get; set; }
    [ObservableProperty] public partial bool UseDataFeed { get; set; }

    /// <summary>Training master switch. Off: plain VATSIM scope, no training traffic, radio, suggested calls or microphone.</summary>
    [ObservableProperty] public partial bool TrainingMode { get; set; }
    /// <summary>Radio panel: training on and training traffic present.</summary>
    public bool ShowRadio => TrainingMode && HasTraining;

    partial void OnTrainingModeChanged(bool value)
    {
        _settings.TrainingMode = value;
        OnPropertyChanged(nameof(ShowRadio));
        if (value) StartVoice();
        else
        {
            ClearTraining();
            SuggestionsVisible = false;
        }
    }

    partial void OnHasTrainingChanged(bool value) => OnPropertyChanged(nameof(ShowRadio));

    // Side-panel sections: open/closed state is remembered.
    [ObservableProperty] public partial bool ExpandTarget { get; set; }
    [ObservableProperty] public partial bool ExpandAsr { get; set; }
    [ObservableProperty] public partial bool ExpandWeather { get; set; }
    [ObservableProperty] public partial bool ExpandVoice { get; set; }
    [ObservableProperty] public partial bool ExpandTraining { get; set; }
    [ObservableProperty] public partial bool ExpandSetup { get; set; }
    [ObservableProperty] public partial bool ExpandDiagnostics { get; set; }
    [ObservableProperty] public partial bool ExpandAudio { get; set; }
    partial void OnExpandAudioChanged(bool value) => _settings.Panels["audio"] = value;
    [ObservableProperty] public partial bool RecordTraffic { get; set; }
    /// <summary>Live per-aircraft update rates (diagnostics section).</summary>
    public ObservableCollection<string> RateLines { get; } = [];
    public string DiagnosticsFolder => TrafficRecorder.Folder;
    partial void OnExpandDiagnosticsChanged(bool value) => _settings.Panels["diagnostics"] = value;
    partial void OnRecordTrafficChanged(bool value) { _settings.RecordTraffic = value; Engine.Recorder.Enabled = value; }

    [RelayCommand]
    private void OpenDiagnostics()
    {
        Directory.CreateDirectory(TrafficRecorder.Folder);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(TrafficRecorder.Folder) { UseShellExecute = true });
    }
    partial void OnExpandTargetChanged(bool value) => _settings.Panels["target"] = value;
    partial void OnExpandAsrChanged(bool value) => _settings.Panels["asr"] = value;
    partial void OnExpandWeatherChanged(bool value) => _settings.Panels["weather"] = value;
    partial void OnExpandVoiceChanged(bool value) => _settings.Panels["voice"] = value;
    partial void OnExpandTrainingChanged(bool value) => _settings.Panels["training"] = value;
    partial void OnExpandSetupChanged(bool value) => _settings.Panels["setup"] = value;
    [ObservableProperty] public partial bool ShowScan { get; set; }
    [ObservableProperty] public partial bool ShowAdvisories { get; set; }
    [ObservableProperty] public partial bool ShowAdvisoriesOnNetwork { get; set; }
    [ObservableProperty] public partial bool HasTraining { get; set; }
    [ObservableProperty] public partial bool SuggestionsVisible { get; set; }
    [ObservableProperty] public partial bool ShowPlanView { get; set; }
    [ObservableProperty] public partial double PlanRangeNm { get; set; }
    [ObservableProperty] public partial int HistoryDots { get; set; }
    [ObservableProperty] public partial bool UseVatsimMetar { get; set; }

    partial void OnLogRangeChanged(bool value) => _settings.LogRange = value;
    partial void OnShowClutterChanged(bool value) { _settings.ShowClutter = value; Engine.ClutterEnabled = value; }
    partial void OnSimulateErrorsChanged(bool value) { _settings.SimulateErrors = value; Engine.SimulateErrors = value; }
    partial void OnScopeFontChanged(string? value) => _settings.ScopeFont = value;
    partial void OnMtiChanged(bool value)
    {
        _settings.Mti = value;
        Engine.MtiEnabled = value;
        if (SelectedPreset != null) RefreshRangeScales(SelectedPreset);
    }
    partial void OnShowSafetyLimitsChanged(bool value) => _settings.ShowSafetyLimits = value;
    partial void OnShowCourseWedgeChanged(bool value) => _settings.ShowCourseWedge = value;
    partial void OnUseDataFeedChanged(bool value) => _settings.UseDataFeed = value;
    partial void OnUseUkBandsChanged(bool value) =>
        _settings.Bands = _settings.Bands with { Standard = value ? DeviationStandard.UkRa3291 : DeviationStandard.UsNavyTargetQuarters };
    partial void OnShowScanChanged(bool value) => _settings.ShowScan = value;
    partial void OnShowAdvisoriesChanged(bool value) => _settings.ShowAdvisories = value;
    partial void OnShowAdvisoriesOnNetworkChanged(bool value) => _settings.ShowAdvisoriesOnNetwork = value;
    partial void OnShowPlanViewChanged(bool value) => _settings.ShowPlanView = value;
    partial void OnPlanRangeNmChanged(double value) => _settings.PlanRangeNm = value;
    partial void OnHistoryDotsChanged(int value) => _settings.HistoryDots = value;
    partial void OnUseVatsimMetarChanged(bool value)
    {
        _settings.MetarSource = value ? MetarSource.Vatsim : MetarSource.Noaa;
        _lastMetarFetch = double.NegativeInfinity;
    }

    public void StepRange(int direction)
    {
        int i = RangeScales.IndexOf(RangeScaleNm);
        int j = Math.Clamp(i + direction, 0, RangeScales.Count - 1);
        if (i >= 0 && j != i) RangeScaleNm = RangeScales[j];
    }

    // ---- Selection & advisory ------------------------------------------------------------------

    [ObservableProperty] public partial string? SelectedCallsign { get; set; }
    [ObservableProperty] public partial string SelectedHeader { get; set; } = "No target selected";
    [ObservableProperty] public partial string SelectedSub { get; set; } = "Click a target on the scope, or press Tab.";
    [ObservableProperty] public partial string RangeText { get; set; } = "—";
    [ObservableProperty] public partial string GlidepathText { get; set; } = "—";
    [ObservableProperty] public partial string CourseText { get; set; } = "—";
    [ObservableProperty] public partial string SpeedText { get; set; } = "—";
    [ObservableProperty] public partial string RodText { get; set; } = "—";
    [ObservableProperty] public partial string AdvisoryText { get; set; } = "";
    [ObservableProperty] public partial AdvisoryLevel AdvisoryLevel { get; set; }
    [ObservableProperty] public partial string RangeCallText { get; set; } = "";
    [ObservableProperty] public partial bool SelectedIsTraining { get; set; }

    /// <summary>Advisory for the selected target, recomputed every frame for the renderer.</summary>
    public Advisory? CurrentAdvisory { get; private set; }

    private double _selectedAt;

    public void Select(string? callsign)
    {
        SelectedCallsign = callsign;
        _selectedAt = Engine.Clock.Now;
        RefreshSelection();
    }

    public void CycleSelection()
    {
        var tracks = Engine.Radar?.Tracks.Values.OrderBy(t => t.RangeFt).Select(t => t.Callsign).ToList() ?? [];
        if (tracks.Count == 0) { Select(null); return; }
        int i = SelectedCallsign == null ? -1 : tracks.FindIndex(c => c.Equals(SelectedCallsign, StringComparison.OrdinalIgnoreCase));
        Select(tracks[(i + 1) % tracks.Count]);
    }

    /// <summary>Called every frame: keeps the advisory used by the renderer current.</summary>
    public void UpdateAdvisory()
    {
        CurrentAdvisory = null;
        if (SelectedCallsign is null || Engine.Radar is not { } radar || Engine.Geometry is not { } geo || Engine.Runway is not { } rw) return;
        if (!radar.Tracks.TryGetValue(SelectedCallsign, out var tr) || !tr.IsCorrelated) return;
        CurrentAdvisory = AdvisoryEngine.Evaluate(tr, geo, radar.Preset, _settings.Bands, rw.MagneticCourseDeg);
    }

    private void RefreshSelection()
    {
        var cs = SelectedCallsign;
        var target = cs == null ? null : Engine.Targets.FirstOrDefault(t => t.Callsign.Equals(cs, StringComparison.OrdinalIgnoreCase));
        SelectedIsTraining = cs != null && Engine.Training.Find(cs) != null;
        if (cs == null || target == null)
        {
            // A target that just spawned has no track until the next frame; one that is gone for good is dropped.
            if (cs != null && target == null && Engine.Now - _selectedAt > 3) SelectedCallsign = null;
            SelectedHeader = "No target selected";
            SelectedSub = "Click a target on the scope, or press Tab.";
            RangeText = GlidepathText = CourseText = SpeedText = RodText = "—";
            AdvisoryText = RangeCallText = "";
            AdvisoryLevel = AdvisoryLevel.Normal;
            return;
        }

        SelectedHeader = cs;
        double gs = target.GroundSpeedKt ?? target.VelocityEcef.Length / Units.MpsPerKnot;
        string src = target.IsSimulated ? "training"
            : target.IsFastActive(Engine.Now) ? $"Velocity {target.FastRateHz(Engine.Now):0.0} Hz"
            : target.IsFeed ? "data feed, 15 s updates"
            : "slow 5 s updates";
        SelectedSub = $"{target.TypeCode ?? "type unknown"} · {src}";
        SpeedText = $"{gs:0} kt";
        PilotStatus = Engine.Training.Find(cs) is { } st && Engine.Runway is { } rwy
            ? st.Status(rwy.MagneticCourseDeg, rwy.ThresholdElevFt) : "";
        bool aids = TrainingMode && (target.IsSimulated ? ShowAdvisories : ShowAdvisoriesOnNetwork);
        SuggestionsVisible = aids;

        if (CurrentAdvisory is not { } adv)
        {
            RangeText = GlidepathText = CourseText = RodText = "—";
            AdvisoryText = "Not yet correlated: needs paints in both azimuth and elevation.";
            RangeCallText = "";
            AdvisoryLevel = AdvisoryLevel.Normal;
            return;
        }

        var d = adv.Deviation;
        RangeText = $"{d.RangeNm:0.0} NM";
        GlidepathText = $"{(d.AboveGlidepathFt >= 0 ? "+" : "−")}{Math.Abs(d.AboveGlidepathFt):0} ft";
        CourseText = $"{(d.RightOfCourseFt >= 0 ? "R" : "L")} {Math.Abs(d.RightOfCourseFt):0} ft";
        RodText = $"{adv.RecommendedDescentFpm:0} fpm";
        AdvisoryText = !aids ? ""
            : Engine.Training.Find(cs) is { } simAc ? TrainingPrefix(simAc, adv) + adv.Transmission
            : adv.Transmission;
        AdvisoryLevel = adv.Level;

        // "(n) MILES FROM TOUCHDOWN" at least once each mile (7110.65 §5-12-5).
        RangeCallText = aids && Engine.Geometry is { } g ? AdvisoryEngine.PositionCall(d.RangeFt, g) ?? "" : "";
    }

    // ---- Training ------------------------------------------------------------------------------

    [ObservableProperty] public partial bool TrainingPaused { get; set; }
    [ObservableProperty] public partial bool SpawnSelfFlying { get; set; }

    partial void OnTrainingPausedChanged(bool value) => Engine.Training.Paused = value;

    [RelayCommand]
    private void SpawnTraining()
    {
        if (Engine.Geometry == null || Engine.Runway is not { } rw) { StatusMessage = "Select a runway first"; return; }
        var ac = Engine.Training.SpawnRandom(SpawnSelfFlying ? PilotMode.SelfFlying : PilotMode.ControllerDirected);
        StatusMessage = $"{ac.Callsign} ({ac.TypeCode}) handed off {ac.RangeFt / Units.FeetPerNm:0.0} NM out";
        Select(ac.Callsign);

        // The pilot switches to the final frequency and checks in (NAVAIR 00-80T-112 ¶25.3.1.1).
        string call = ac.CheckInCall(FacilityName, rw.MagneticCourseDeg, rw.ThresholdElevFt);
        _ = SayLaterAsync(1.5, () => PilotSays(ac.Callsign, call));
    }

    [RelayCommand]
    private void ClearTraining()
    {
        foreach (var a in Engine.Training.Aircraft) _tower.Forget(a.Callsign);
        Engine.ClearTraining();
        if (SelectedIsTraining) Select(null);
    }

    // ---- Radio: pilot and tower voices, radio log ---------------------------------------------

    private readonly SpeechInput _speech = new();
    private readonly RadioVoice _voice = new();
    private readonly TowerSim _tower = new();

    [ObservableProperty] public partial string VoiceState { get; set; } = "Voice not started";
    [ObservableProperty] public partial string TypedTransmission { get; set; } = "";
    [ObservableProperty] public partial string PilotStatus { get; set; } = "";
    [ObservableProperty] public partial bool PilotVoices { get; set; } = true;
    /// <summary>Seconds since the last transmission to the aircraft on final; PAR calls are due about every 5 s
    /// (7110.65 §5-12-4) and lost communications is declared after 5 s (§5-10-4).</summary>
    [ObservableProperty] public partial string TxTimerText { get; set; } = "";
    [ObservableProperty] public partial AdvisoryLevel TxTimerLevel { get; set; }
    private double _lastTxTime = double.NaN;
    [ObservableProperty] public partial LandingIntent TowerIntent { get; set; }

    public IReadOnlyList<LandingIntent> TowerIntents { get; } = Enum.GetValues<LandingIntent>();

    /// <summary>Most recent transmissions, newest last (fixed length so the panel does not resize).</summary>
    public ObservableCollection<string> RadioLog { get; } = ["", "", "", "", "", ""];

    partial void OnPilotVoicesChanged(bool value) => _voice.Enabled = value;

    /// <summary>"Miramar" from "MIRAMAR MCAS (JOE FOSS FLD)"; used in "(facility) final".</summary>
    private string FacilityName
    {
        get
        {
            var words = (SelectedAirport?.Name ?? "Approach").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string w = words.FirstOrDefault(x => x.Length > 3 && x.All(char.IsLetter)) ?? words.FirstOrDefault() ?? "Approach";
            return char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant();
        }
    }

    private void Log(string who, string text)
    {
        RadioLog.RemoveAt(0);
        RadioLog.Add($"{DateTime.UtcNow:HH:mm:ss}  {who}: {text}");
    }

    private void PilotSays(string callsign, string text)
    {
        Log(callsign, text);
        _voice.Pilot(text);
    }

    private static async Task SayLaterAsync(double seconds, Action say)
    {
        await Task.Delay(TimeSpan.FromSeconds(seconds));
        say();
    }

    private bool _voiceStarted;

    /// <summary>Starts the speech recogniser (microphone) the first time training mode is on.</summary>
    public void StartVoice()
    {
        if (!TrainingMode || _voiceStarted) return;
        _voiceStarted = true;
        _speech.StateChanged += s => App.Current.Dispatcher.BeginInvoke(() => VoiceState = s);
        _speech.Heard += text => App.Current.Dispatcher.BeginInvoke(() => ProcessTransmission(text, spoken: true));
        _speech.MicrophoneId = _settings.MicrophoneId;
        _ = _speech.UseRecognizerAsync(_settings.RecognizerId);
    }

    // ---- Audio setup: devices, voices, speech engine ------------------------------------------

    public IReadOnlyList<AudioDevice> Microphones { get; } = AudioDevices.Inputs();
    public IReadOnlyList<AudioDevice> Speakers { get; } = AudioDevices.Outputs();
    public IReadOnlyList<RecognizerOption> Recognizers => RecognizerOption.All;
    public IReadOnlyList<string> VoiceNames => _voice.Voices;

    [ObservableProperty] public partial AudioDevice? SelectedMicrophone { get; set; }
    [ObservableProperty] public partial AudioDevice? SelectedSpeaker { get; set; }
    [ObservableProperty] public partial RecognizerOption? SelectedRecognizer { get; set; }
    [ObservableProperty] public partial string? PilotVoiceName { get; set; }
    [ObservableProperty] public partial string? TowerVoiceName { get; set; }
    [ObservableProperty] public partial double MicLevel { get; set; }

    private void LoadAudioSettings()
    {
        SelectedMicrophone = Microphones.FirstOrDefault(d => d.Id == (_settings.MicrophoneId ?? "")) ?? AudioDevice.Default;
        SelectedSpeaker = Speakers.FirstOrDefault(d => d.Id == (_settings.SpeakerId ?? "")) ?? AudioDevice.Default;
        SelectedRecognizer = RecognizerOption.All.FirstOrDefault(r => r.Id == _settings.RecognizerId) ?? RecognizerOption.All[0];
        PilotVoiceName = _settings.PilotVoice is { } pv && VoiceNames.Contains(pv) ? pv : _voice.PilotVoice;
        TowerVoiceName = _settings.TowerVoice is { } tv && VoiceNames.Contains(tv) ? tv : _voice.TowerVoice;
    }

    partial void OnSelectedMicrophoneChanged(AudioDevice? value)
    {
        if (value is null) return;
        _settings.MicrophoneId = value.Id;
        _speech.MicrophoneId = value.Id;
    }

    partial void OnSelectedSpeakerChanged(AudioDevice? value)
    {
        if (value is null) return;
        _settings.SpeakerId = value.Id;
        _voice.OutputDeviceId = value.Id;
    }

    partial void OnSelectedRecognizerChanged(RecognizerOption? value)
    {
        if (value is null) return;
        _settings.RecognizerId = value.Id;
        if (_voiceStarted) _ = _speech.UseRecognizerAsync(value.Id);
    }

    partial void OnPilotVoiceNameChanged(string? value) { _settings.PilotVoice = value; _voice.PilotVoice = value; }
    partial void OnTowerVoiceNameChanged(string? value) { _settings.TowerVoice = value; _voice.TowerVoice = value; }

    [RelayCommand]
    private void TestVoices()
    {
        _voice.Pilot("Radio check, pilot voice.");
        _voice.Tower("Radio check, tower voice.");
    }

    // ---- Keyboard calls (training) -------------------------------------------------------------

    /// <summary>Standard calls bound to keys; sent to the selected training aircraft like spoken ones.</summary>
    public void KeyCall(string call)
    {
        if (!TrainingMode) return;
        ProcessTransmission(call, spoken: false);
    }

    public void PressToTalk()
    {
        if (!TrainingMode) return;
        _voice.PauseForTransmit();
        _speech.PressToTalk();
    }

    public void ReleaseToTalk()
    {
        _speech.ReleaseToTalk();
        _voice.ResumeAfterTransmit();
    }

    [RelayCommand]
    private void SendTyped()
    {
        if (string.IsNullOrWhiteSpace(TypedTransmission)) return;
        ProcessTransmission(TypedTransmission, spoken: false);
        TypedTransmission = "";
    }

    /// <summary>Sends a controller transmission to the selected training aircraft (or the only one).</summary>
    public void ProcessTransmission(string text, bool spoken)
    {
        var t = Phraseology.Parse(text);
        var target = (SelectedCallsign is { } cs ? Engine.Training.Find(cs) : null)
                     ?? (Engine.Training.Aircraft.Count == 1 ? Engine.Training.Aircraft[0] : null);
        Log("FINAL", text.ToUpperInvariant() + (t.IsEmpty ? "  (not understood)" : ""));
        _lastTxTime = Engine.Now;
        if (t.IsEmpty || target == null || Engine.Runway is not { } rw)
        {
            if (target == null && !t.IsEmpty) StatusMessage = "Select a training aircraft first";
            return;
        }
        double rod = CurrentAdvisory?.RecommendedDescentFpm ?? 700;
        var response = target.Receive(t, Engine.Now, rw.MagneticCourseDeg, rod);
        if (response.Reply is { } reply)
            _ = SayLaterAsync(1.0 + Random.Shared.NextDouble(), () => PilotSays(target.Callsign, reply));
    }

    /// <summary>Interphone request to tower for the selected training aircraft (7110.65 §5-10-13).</summary>
    [RelayCommand]
    private void RequestTower()
    {
        if (SelectedCallsign is not { } cs || Engine.Training.Find(cs) is not { } ac || Engine.Runway is not { } rw) return;
        Log("FINAL", TowerSim.RequestText(cs, ac.RangeFt / Units.FeetPerNm, TowerIntent));
        bool occupied = Engine.Training.Aircraft.Any(a => a != ac && a.Landed && !a.Finished);
        string answer = _tower.Request(cs, TowerIntent, rw.RunwayId, occupied);
        _ = SayLaterAsync(2.0, () => { Log("TOWER", answer); _voice.Tower(answer); });
    }

    public void BeginDescent()
    {
        if (SelectedCallsign is not { } cs || Engine.Training.Find(cs) is not { } ac) return;
        ac.BeginDescent(Engine.Now, CurrentAdvisory?.RecommendedDescentFpm ?? 700);
        Log("FINAL", "BEGIN DESCENT");
    }

    public void NudgeHeading(int degrees)
    {
        if (SelectedCallsign is not { } cs || Engine.Training.Find(cs) is not { } ac || Engine.Runway is not { } rw) return;
        double current = Units.Norm360(rw.MagneticCourseDeg + ac.CommandedHeadingRelDeg);
        double next = Units.Norm360(Math.Round(current) + degrees);
        ProcessTransmission($"turn {(degrees > 0 ? "right" : "left")} heading {(next == 0 ? 360 : next):000}", spoken: false);
    }

    /// <summary>Training-only lines added ahead of the glidepath/course call.</summary>
    private string TrainingPrefix(SimAircraft ac, Advisory adv)
    {
        if (Engine.Runway is not { } rw) return "";
        var answer = _tower.AnswerFor(ac.Callsign);
        if (answer == TowerAnswer.Cleared && !ac.ClearedToLand)
        {
            string wind = MetarWindPhrase;
            return $"{(wind.Length > 0 ? wind + ", " : "")}RUNWAY {rw.RunwayId}, {TowerSim.ClearanceWords(_tower.IntentFor(ac.Callsign)).ToUpperInvariant()}. ";
        }
        // No clearance by 2 miles (MCAS Cherry Point P3722.1S §6-316b).
        if (answer != TowerAnswer.Cleared && adv.Deviation.RangeNm < 2 && !adv.BelowDecisionHeight)
            return "TOWER CLEARANCE NOT RECEIVED, IF RUNWAY OR APPROACH LIGHTS NOT IN SIGHT, EXECUTE MISSED APPROACH. ";
        return "";
    }

    private string MetarWindPhrase { get; set; } = "";

    // ---- Status --------------------------------------------------------------------------------

    [ObservableProperty] public partial LinkHealth Link { get; set; }
    [ObservableProperty] public partial string LinkText { get; set; } = "Waiting for vPilot";
    [ObservableProperty] public partial string LinkDetail { get; set; } = "";
    [ObservableProperty] public partial string TrafficText { get; set; } = "";
    [ObservableProperty] public partial string StatusMessage { get; set; } = "";
    [ObservableProperty] public partial string UtcText { get; set; } = "";
    [ObservableProperty] public partial string MetarRaw { get; set; } = "";
    [ObservableProperty] public partial string MetarSpoken { get; set; } = "";
    [ObservableProperty] public partial FlightCategory MetarCategory { get; set; }
    /// <summary>Wind/altimeter line drawn on the scopes.</summary>
    public string? MetarShort { get; private set; }
    /// <summary>STARS SSA form for the ASR.</summary>
    public string? MetarStars { get; private set; }

    /// <summary>Called at a few hertz by the view.</summary>
    public void RefreshStatus()
    {
        double now = Engine.Now;
        var link = Engine.Link;
        double age = now - link.LastDatagram;
        if (link.Error != null) { Link = LinkHealth.Error; LinkText = "UDP error"; LinkDetail = link.Error; }
        else if (age > 15 && Engine.Feed is { Active: true } feed)
        {
            Link = LinkHealth.Slow;
            LinkText = "VATSIM data feed (15 s)";
            LinkDetail = feed.LastError ?? "Degraded: no vPilot plugin connected. Positions update every ~15 s and are dead-reckoned. Use vPilot (.towerview next to CRC) for live data.";
        }
        else if (age > 15) { Link = LinkHealth.None; LinkText = "Waiting for vPilot"; LinkDetail = $"Listening on UDP {_settings.UdpPort}"; }
        else
        {
            // Report what is actually arriving, not just whether the hook is installed.
            var fastTargets = Engine.Targets.Where(t => t.IsFastActive(now)).ToList();
            bool towerView = link.Callsign?.EndsWith("_TV", StringComparison.OrdinalIgnoreCase) == true;
            string who = towerView ? "vPilot tower view" : "vPilot";
            Link = fastTargets.Count > 0 ? LinkHealth.Fast : LinkHealth.Slow;
            LinkText = fastTargets.Count > 0
                ? $"{who} · 5 Hz data, {fastTargets.Count} aircraft"
                : $"{who} · 5 s updates only" + (link.FastHook == true ? " (fast hook ready)" : "");
            LinkDetail = fastTargets.Count > 0
                ? $"Velocity fast positions arriving at ~{fastTargets.Average(t => t.FastRateHz(now)):0.0} Hz per aircraft"
                : link.FastHook == true
                    ? "The plugin is listening for Velocity fast positions but none are arriving; VATSIM only sends them for aircraft near you."
                    : "The plugin's fast-position hook is not active; targets update every ~5 s and are dead-reckoned.";
            if (link.FastPerMin is double f) LinkDetail += $" Last minute: {f / 60:0.0} fast packets/s.";
        }

        int real = Engine.Targets.Count(t => !t.IsSimulated);
        int sim = Engine.Targets.Count - real;
        int fastNow = Engine.Targets.Count(t => t.IsFastActive(now));
        TrafficText = $"{real} network aircraft" + (sim > 0 ? $" · {sim} training" : "");
        UtcText = DateTime.UtcNow.ToString("HH:mm:ss") + "Z";
        HasTraining = sim > 0;
        var rates = Engine.Recorder.Summary(now);
        RateLines.Clear();
        foreach (var line in rates.Take(12)) RateLines.Add(line);
        if (rates.Count == 0) RateLines.Add("No network traffic received yet.");
        MicLevel = _speech.InputLevel;
        if (double.IsNaN(_lastTxTime) || !HasTraining) { TxTimerText = ""; TxTimerLevel = AdvisoryLevel.Normal; }
        else
        {
            double since = now - _lastTxTime;
            TxTimerText = $"Last transmission {since:0} s ago";
            TxTimerLevel = since < 5 ? AdvisoryLevel.Normal : since < 10 ? AdvisoryLevel.Caution : AdvisoryLevel.Warning;
        }

        RefreshSelection();
        _ = RefreshMetarIfDueAsync(now);
    }

    private async Task RefreshMetarIfDueAsync(double now)
    {
        if (SelectedAirport is not { } apt || now - _lastMetarFetch < 300) return;
        _lastMetarFetch = now;
        _metarCts?.Cancel();
        _metarCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            var m = await _metar.FetchAsync(apt.MetarId, UseVatsimMetar ? MetarSource.Vatsim : MetarSource.Noaa, _metarCts.Token);
            MetarRaw = m?.Raw ?? $"No METAR for {apt.MetarId}";
            MetarSpoken = m?.Spoken ?? "";
            MetarShort = m?.Short;
            MetarWindPhrase = m is null ? "" : RadioSpeech.Wind(m.WindDirDeg, m.WindSpeedKt, m.WindVariable).ToUpperInvariant();
            MetarStars = m?.StarsShort;
            MetarCategory = m?.Category ?? FlightCategory.Unknown;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            MetarRaw = "METAR unavailable";
            _lastMetarFetch = now - 240; // retry in a minute
        }
    }

    public void SaveSettings() => _settings.Save();

    private static HttpClient CreateHttp()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        // The FAA NASR server rejects requests without a browser-like user agent.
        h.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) PARScope/2.0");
        return h;
    }

    public void Dispose()
    {
        _metarCts?.Cancel();
        _speech.Dispose();
        _voice.Dispose();
        Engine.Dispose();
    }
}
