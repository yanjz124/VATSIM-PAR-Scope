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
        Keys = new KeyMap(settings);
        Keys.Changed += () =>
        {
            OnPropertyChanged(nameof(KeysHint));
            OnPropertyChanged(nameof(RadarContactButtonText));
            OnPropertyChanged(nameof(ContactApproachButtonText));
            OnPropertyChanged(nameof(CoordinateMissedButtonText));
        };

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
        ExpandVoice = Panel("voice", true);
        ExpandTraining = Panel("training", false);
        Difficulty = settings.TrainingDifficulty;
        Engine.Training.Difficulty = Difficulty;
        Engine.Training.ReturnForAnotherApproach = settings.TrainingReturnForAnother;
        settings.MaxCallsignLength = Math.Clamp(settings.MaxCallsignLength, 5, 6);
        Engine.Training.MaxCallsignLength = settings.MaxCallsignLength;
        ShowScan = settings.ShowScan;
        ShowCompassRose = settings.ShowCompassRose ?? SelectedPreset.AsrCompassRose;
        ShowAdvisories = settings.ShowAdvisories;
        ShowAdvisoriesOnNetwork = settings.ShowAdvisoriesOnNetwork;
        ShowPlanView = settings.ShowPlanView;
        ShowAsr = settings.AsrFlipped;
        AsrPoppedOut = settings.AsrPoppedOut;
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

    private (double? FromDeg, double Kt, double? GustKt)? _liveWind;

    /// <summary>Training arrivals fly in the live METAR wind (Settings › Training).</summary>
    public bool TrainingLiveWind
    {
        get => _settings.TrainingLiveWind;
        set { _settings.TrainingLiveWind = value; OnPropertyChanged(); ApplyLiveWind(); }
    }

    private void ApplyLiveWind() => Engine.Training.SurfaceWind = _settings.TrainingLiveWind ? _liveWind : null;

    /// <summary>Longest training callsign on the radar (Settings › Training).</summary>
    public IReadOnlyList<int> CallsignLengths { get; } = [5, 6];
    public int MaxCallsignLength
    {
        get => _settings.MaxCallsignLength;
        set { _settings.MaxCallsignLength = value; Engine.Training.MaxCallsignLength = value; OnPropertyChanged(); }
    }

    public bool SpeechGpu
    {
        get => _settings.SpeechGpu;
        set { _settings.SpeechGpu = value; OnPropertyChanged(); StatusMessage = "Restart the scope to apply the speech recognition change"; }
    }

    public string WindowTitle => $"vibe PAR {Updater.DisplayVersion}";

    public bool CheckForUpdates
    {
        get => _settings.CheckForUpdates;
        set { _settings.CheckForUpdates = value; OnPropertyChanged(); }
    }

    /// <summary>A newer release, if any (at start-up only when the check is enabled; <paramref name="force"/> for the button).</summary>
    public async Task<ReleaseInfo?> CheckForUpdateAsync(bool force = false)
    {
        if (!_settings.CheckForUpdates && !force) return null;
        try { return await Updater.CheckAsync(Http, new CancellationTokenSource(TimeSpan.FromSeconds(15)).Token); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException) { return null; }
    }

    public async Task InstallUpdateAsync(ReleaseInfo release)
    {
        try
        {
            await Updater.InstallAndRestartAsync(Http, release, new Progress<string>(s => StatusMessage = s), CancellationToken.None);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or InvalidDataException or TaskCanceledException)
        {
            StatusMessage = $"Update failed: {ex.Message}";
        }
    }

    public async Task InitializeAsync()
    {
        Engine.StartNetwork(_settings.UdpPort);
        Engine.StartDataFeed(Http, () => _settings.UseDataFeed);
        bool cached = await Task.Run(_airports.TryLoadCache);
        AirportDbStatus = cached ? DbStatusText() : "No airport data yet";
        if (!cached)
            await UpdateAirportsAsync();
        else if (_airports.IsStale)
            _ = UpdateAirportsAsync(); // refresh in the background; the cached data is used meanwhile

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
            // Already established on final at 4, 6.5 and 9 NM so there is something on the scope straight away.
            var rng = new Random();
            for (int i = 0; i < 3; i++)
                Engine.Training.Spawn(new SpawnOptions
                {
                    RangeNm = 4 + i * 2.5, HeightOffsetFt = rng.Next(-80, 80), CrossFt = rng.Next(-400, 400),
                    Mode = PilotMode.SelfFlying, Skill = 0.5 + rng.NextDouble() * 0.4, WindFromDeg = 220, WindKt = 8,
                });
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
            ShowCompassRose = value.AsrCompassRose;
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
        Site = _settings.SiteFor(rw, SelectedPreset);
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
    /// <summary>Runway point of intercept, feet from the threshold; 0 = computed from TCH and glidepath.</summary>
    [ObservableProperty] public partial double SiteRpi { get; set; }
    [ObservableProperty] public partial string SiteSource { get; set; } = "";

    private void LoadSiteFields()
    {
        SiteRpi = Site.RpiFt ?? 0;
        SiteSource = Site.Source is { } src ? $"Glidepath, TCH, RPI and DH: {src}"
            : "No published PAR for this runway: set glidepath, TCH and DH from the approach plate.";
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
            RpiFt = SiteRpi > 0 ? Math.Clamp(SiteRpi, 0, 5000) : null,
        };
        // Keep the published source only while its values are untouched.
        if (site.GlidepathDeg != Site.GlidepathDeg || site.TchFt != Site.TchFt || site.DecisionHeightFt != Site.DecisionHeightFt || site.RpiFt != Site.RpiFt)
            site = site with { Source = "set by hand" };
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
    [ObservableProperty] public partial bool ExpandVoice { get; set; }
    [ObservableProperty] public partial bool ExpandTraining { get; set; }
    [ObservableProperty] public partial bool RecordTraffic { get; set; }
    /// <summary>Live per-aircraft update rates (diagnostics section).</summary>
    public ObservableCollection<string> RateLines { get; } = [];
    public string DiagnosticsFolder => TrafficRecorder.Folder;
    partial void OnRecordTrafficChanged(bool value) { _settings.RecordTraffic = value; Engine.Recorder.Enabled = value; }

    [RelayCommand]
    private void OpenDiagnostics()
    {
        Directory.CreateDirectory(TrafficRecorder.Folder);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(TrafficRecorder.Folder) { UseShellExecute = true });
    }
    partial void OnExpandTargetChanged(bool value) => _settings.Panels["target"] = value;
    partial void OnExpandVoiceChanged(bool value) => _settings.Panels["voice"] = value;
    partial void OnExpandTrainingChanged(bool value) => _settings.Panels["training"] = value;
    [ObservableProperty] public partial bool ShowScan { get; set; }
    [ObservableProperty] public partial bool ShowAdvisories { get; set; }
    [ObservableProperty] public partial bool ShowAdvisoriesOnNetwork { get; set; }
    [ObservableProperty] public partial bool HasTraining { get; set; }
    [ObservableProperty] public partial bool SuggestionsVisible { get; set; }
    [ObservableProperty] public partial bool ShowPlanView { get; set; }
    /// <summary>Main scope area flipped to the ASR.</summary>
    [ObservableProperty] public partial bool ShowAsr { get; set; }
    [ObservableProperty] public partial bool AsrPoppedOut { get; set; }
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
    /// <summary>ASR azimuth scale; defaults per radar model, overridable.</summary>
    [ObservableProperty] public partial bool ShowCompassRose { get; set; }
    partial void OnShowCompassRoseChanged(bool value) => _settings.ShowCompassRose = value;
    partial void OnShowAdvisoriesChanged(bool value) => _settings.ShowAdvisories = value;
    partial void OnShowAdvisoriesOnNetworkChanged(bool value) => _settings.ShowAdvisoriesOnNetwork = value;
    partial void OnShowPlanViewChanged(bool value) => _settings.ShowPlanView = value;
    partial void OnShowAsrChanged(bool value) => _settings.AsrFlipped = value;
    partial void OnAsrPoppedOutChanged(bool value)
    {
        _settings.AsrPoppedOut = value;
        if (value) ShowAsr = false;
    }

    /// <summary>F key: flip the main scope between PAR and ASR (pop the ASR back in first if it is out).</summary>
    public void FlipScope()
    {
        if (AsrPoppedOut) AsrPoppedOut = false;
        ShowAsr = !ShowAsr;
    }
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
    /// <summary>
    /// The heading the controller knows for an aircraft, magnetic: the last one assigned to a training aircraft,
    /// or the heading a network aircraft reports. Lets suggested headings keep the wind correction.
    /// </summary>
    private double? KnownHeadingMag(string? callsign, RunwayEnd rw)
    {
        if (callsign == null) return null;
        if (Engine.Training.Find(callsign) is { } sim) return Units.Norm360(rw.MagneticCourseDeg + sim.CommandedHeadingRelDeg);
        var t = Engine.Targets.FirstOrDefault(x => x.Callsign.Equals(callsign, StringComparison.OrdinalIgnoreCase));
        return t?.HeadingDeg is double trueHdg ? Units.Norm360(trueHdg - (rw.MagVarEastDeg ?? 0)) : null;
    }

    public void UpdateAdvisory()
    {
        CurrentAdvisory = null;
        if (SelectedCallsign is null || Engine.Radar is not { } radar || Engine.Geometry is not { } geo || Engine.Runway is not { } rw) return;
        if (!radar.Tracks.TryGetValue(SelectedCallsign, out var tr) || !tr.IsCorrelated) return;
        CurrentAdvisory = AdvisoryEngine.Evaluate(tr, geo, radar.Preset, _settings.Bands, rw.MagneticCourseDeg, KnownHeadingMag(SelectedCallsign, rw),
            Engine.Training.Find(SelectedCallsign) is { GearReportedDown: true });
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
        RangeCallText = aids && Engine.Geometry is { } g ? AdvisoryEngine.PositionCall(d.RangeFt, g, CurrentAdvisory?.CoursePhrase) ?? "" : "";
    }

    // ---- Training ------------------------------------------------------------------------------

    [ObservableProperty] public partial bool TrainingPaused { get; set; }
    public IReadOnlyList<TrainingDifficulty> Difficulties { get; } = Enum.GetValues<TrainingDifficulty>();
    /// <summary>Applies to arrivals spawned from now on.</summary>
    [ObservableProperty] public partial TrainingDifficulty Difficulty { get; set; }
    public string DifficultyText => DifficultyProfile.Describe(Difficulty);
    partial void OnDifficultyChanged(TrainingDifficulty value)
    {
        _settings.TrainingDifficulty = value;
        Engine.Training.Difficulty = value;
        OnPropertyChanged(nameof(DifficultyText));
    }
    [ObservableProperty] public partial bool SpawnSelfFlying { get; set; }
    /// <summary>Optional custom callsign / ICAO type for the next spawn (blank = realistic random).</summary>
    [ObservableProperty] public partial string SpawnCallsign { get; set; } = "";
    [ObservableProperty] public partial string SpawnType { get; set; } = "";

    partial void OnTrainingPausedChanged(bool value)
    {
        Engine.Training.Paused = value;
        StatusMessage = value ? "Training paused (P to resume)" : "Training resumed";
    }

    /// <summary>P key / toolbar: freezes training aircraft, their pilots and the transmission timer.</summary>
    public void TogglePause()
    {
        if (TrainingMode) TrainingPaused = !TrainingPaused;
    }

    [RelayCommand]
    private void SpawnTraining()
    {
        if (Engine.Geometry == null || Engine.Runway is not { } rw) { StatusMessage = "Select a runway first"; return; }
        var ac = Engine.Training.SpawnRandom(SpawnSelfFlying ? PilotMode.SelfFlying : PilotMode.ControllerDirected,
            string.IsNullOrWhiteSpace(SpawnCallsign) ? null : SpawnCallsign, string.IsNullOrWhiteSpace(SpawnType) ? null : SpawnType);
        var perf = AircraftPerformance.Find(ac.TypeCode);
        StatusMessage = $"{ac.Callsign} ({ac.TypeCode}{(perf != null ? $", cat {perf.ApproachCategory}" : ", type not in FAA database")}, {ac.SpeedKt:0} kt) handed off {ac.RangeFt / Units.FeetPerNm:0.0} NM out";
        Select(ac.Callsign);
        OfferToFinal(ac, rw);
    }

    /// <summary>
    /// Approach offers the handoff on the interphone (7110.65 §5-4-5). Once final says "radar contact",
    /// approach switches the pilot, who checks in (NAVAIR 00-80T-112 ¶25.3.1.1).
    /// </summary>
    private void OfferToFinal(SimAircraft ac, RunwayEnd rw)
    {
        string handoff = _approach.OfferHandoff(ac.Callsign, ac.RangeFt / Units.FeetPerNm,
            Units.Norm360(rw.MagneticCourseDeg + ac.CommandedHeadingRelDeg), ac.AltitudeAboveThresholdFt + rw.ThresholdElevFt, Engine.Training.SimTime);
        _voice.Approach(handoff, 0.5, OnUi(() => Log("APPROACH", handoff)));
    }

    /// <summary>"(Callsign) radar contact [initials]" accepts approach's handoff; the pilot then checks in.</summary>
    private bool TryAcceptHandoff(string text, string via)
    {
        if (_approach.Pending.Count == 0 || !Phraseology.Normalize(text).Contains(" radar contact")) return false;
        var offered = Engine.Training.Aircraft.Where(a => _approach.IsPending(a.Callsign)).ToList();
        var ac = Interphone.FindAircraft(text, offered) ?? (offered.Count == 1 ? offered[0] : null);
        Log("FINAL", $"(approach line) {text.Trim()}");
        if (ac == null)
        {
            _session.Write($"          ({via}) radar contact, but which aircraft? Offered: {string.Join(", ", offered.Select(a => a.Callsign))}");
            Reply("APPROACH", "Say again callsign.");
            return true;
        }
        _session.Write($"          ({via}) accepted handoff of {ac.Callsign}");
        Reply("APPROACH", _approach.Accept(ac.Callsign) ?? "Roger.");
        _ = SayLaterAsync(7, () =>
        {
            if (Engine.Training.Find(ac.Callsign) is not { } a || Engine.Runway is not { } rw) return;
            PilotSays(a.Callsign, a.CheckInCall(FacilityName, rw.MagneticCourseDeg, rw.ThresholdElevFt));
        });
        return true;
    }

    /// <summary>Aircraft back from approach's pattern are offered for another approach.</summary>
    private void HandOffReturningAircraft()
    {
        if (Engine.Runway is not { } rw) return;
        foreach (var ac in Engine.Training.TakeReturningAircraft())
        {
            _tower.Forget(ac.Callsign);
            _approach.Forget(ac.Callsign);
            _session.Write($"NOTE     {ac.Callsign} back from approach's pattern for another approach");
            OfferToFinal(ac, rw);
        }
        var pattern = Engine.Training.InPattern.Select(p => $"{p.Callsign} {p.Leg}").ToList();
        PatternText = pattern.Count == 0 ? "" : "In approach's pattern: " + string.Join("   ", pattern);
    }

    [ObservableProperty] public partial string PatternText { get; set; } = "";

    public bool ReturnForAnotherApproach
    {
        get => _settings.TrainingReturnForAnother;
        set { _settings.TrainingReturnForAnother = value; Engine.Training.ReturnForAnotherApproach = value; OnPropertyChanged(); }
    }

    /// <summary>Approach repeats a handoff final has not answered.</summary>
    private void RepeatUnansweredHandoffs()
    {
        if (Engine.Training.Paused || _approach.DueRepeat(Engine.Training.SimTime) is not { } p) return;
        if (Engine.Training.Find(p.Callsign) == null) { _approach.Forget(p.Callsign); return; }
        _session.Write($"NOTE     handoff of {p.Callsign} not accepted; approach calls again (call {p.Calls})");
        Log("APPROACH", p.Text);
        _voice.Approach(p.Text);
    }

    [RelayCommand]
    private void ClearTraining()
    {
        foreach (var a in Engine.Training.Aircraft) { _tower.Forget(a.Callsign); _approach.Forget(a.Callsign); }
        Engine.ClearTraining();
        if (SelectedIsTraining) Select(null);
    }

    // ---- Radio: pilot and tower voices, radio log ---------------------------------------------

    private readonly SpeechInput _speech = new();
    private readonly RadioVoice _voice = new();
    private readonly TowerSim _tower = new();
    private readonly ApproachSim _approach = new();

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
        _session.Write($"{who,-8} {text}");
    }

    private readonly SessionLog _session = new();
    private double _lastSnapshot;

    /// <summary>Plain-English parse of a transmission, so the log shows what the pilot model acted on.</summary>
    private static string Understood(Transmission t)
    {
        var parts = new List<string>();
        if (t.HeadingDeg is double h) parts.Add($"heading {h:000}");
        if (t.Glidepath != GlidepathCall.None) parts.Add($"glidepath {t.Glidepath}{(t.GlidepathTrend != CallTrend.None ? "/" + t.GlidepathTrend : "")}");
        if (t.CourseCall is { } c) parts.Add($"course \"{c.ToLowerInvariant()}\" (information only)");
        if (t.BeginDescent) parts.Add("begin descent");
        if (t.MissedApproach) parts.Add("missed approach");
        if (t.WheelsDown) parts.Add("wheels down");
        if (t.Clearance is { } cl) parts.Add(TowerSim.ClearanceWords(cl));
        if (t.HowDoYouHear) parts.Add("radio check");
        if (t.DoNotAcknowledge) parts.Add("do not acknowledge");
        if (t.LevelOff) parts.Add("level off");
        if (t.ResumeDescent) parts.Add("resume normal descent");
        if (t.AcknowledgeRequested) parts.Add("acknowledge requested");
        if (t.LostCommsInstructions) parts.Add("lost communications instructions");
        if (t.MissedApproachInstructions) parts.Add("missed approach instructions");
        if (t.ContactApproach) parts.Add("contact approach");
        if (t.ContactTower) parts.Add("contact tower after landing");
        if (t.ConditionalMissedApproach) parts.Add("(conditional: if runway not in sight)");
        if (t.ApproachBrief) parts.Add("approach brief");
        if (t.NoGyroTurn != 0) parts.Add($"no-gyro turn {(t.NoGyroTurn < 0 ? "left" : "right")}");
        if (t.StopTurn) parts.Add("stop turn");
        if (t.HalfStandardRate is bool half) parts.Add(half ? "half standard rate turns" : "standard rate turns");
        if (t.NoGyroBrief) parts.Add("no-gyro approach");
        if (t.RangeInfo) parts.Add("range information");
        if (t.ClimbToFt is double climb) parts.Add($"climb {climb:0}");
        return parts.Count == 0 ? "NOTHING (not understood)" : string.Join("; ", parts);
    }

    /// <summary>Every 2 s: each training aircraft's true position against the glidepath and course, what the
    /// pilot is doing, and the call the scope would make from the radar picture.</summary>
    private void SnapshotTraining()
    {
        if (Engine.Training.Paused || Engine.Training.SimTime - _lastSnapshot < 2 || Engine.Geometry is not { } geo || Engine.Runway is not { } rw) return;
        _lastSnapshot = Engine.Training.SimTime;
        foreach (var a in Engine.Training.Aircraft)
        {
            double gp = a.HeightFt - geo.GlidepathHeightFt(Math.Max(a.RangeFt, 0));
            double hdg = Units.Norm360(rw.MagneticCourseDeg + a.HeadingRelDeg);
            double cmd = Units.Norm360(rw.MagneticCourseDeg + a.CommandedHeadingRelDeg);
            string call = Engine.Radar is { } radar && radar.Tracks.TryGetValue(a.Callsign, out var tr) && tr.IsCorrelated
                ? AdvisoryEngine.Evaluate(tr, geo, radar.Preset, _settings.Bands, rw.MagneticCourseDeg, KnownHeadingMag(a.Callsign, rw)).Transmission
                : "(not on PAR)";
            _session.Write($"STATE    {a.Callsign} {a.RangeFt / Units.FeetPerNm:0.00} NM | GP {gp:+0;-0} ft | CRS {Math.Abs(a.CrossFt):0} ft {(a.CrossFt >= 0 ? "R" : "L")} | "
                + $"HDG {hdg:000} (assigned {cmd:000}, drift {a.HeadingDriftDeg:+0.0;-0.0}) | VS {a.VerticalSpeedFpm:0} (drift {a.VsDriftFpm:+0;-0}) | "
                + $"{(a.Descending ? "descending" : "level")}{(a.NoAcknowledge ? ", no-ack" : "")}{(a.GoingAround ? ", missed" : "")}{(a.Landed ? ", landed" : "")} | "
                + $"wind {a.WindFromDeg:000}/{a.WindKt:0} | scope call: {call}");
        }
    }

    private void PilotSays(string callsign, string text)
    {
        Log(callsign, text);
        _voice.Pilot(text, callsign);
    }

    /// <summary>Wraps an action so it runs on the UI thread (voice callbacks come from the audio thread).</summary>
    private static Action OnUi(Action a) => () => App.Current.Dispatcher.BeginInvoke(a);

    private static async Task SayLaterAsync(double seconds, Action say)
    {
        await Task.Delay(TimeSpan.FromSeconds(seconds));
        say();
    }

    private bool _voiceStarted;

    /// <summary>Starts the speech recognizer (microphone) the first time training mode is on.</summary>
    public void StartVoice()
    {
        if (!TrainingMode || _voiceStarted) return;
        _voiceStarted = true;
        _speech.StateChanged += s => App.Current.Dispatcher.BeginInvoke(() => VoiceState = s);
        _speech.Heard += text => App.Current.Dispatcher.BeginInvoke(() => ProcessTransmission(text, "voice, " + (SelectedRecognizer?.Name ?? "speech")));
        _speech.HeardAfter += (_, sec) => App.Current.Dispatcher.BeginInvoke(() =>
        {
            _session.Write($"          recognized {sec:0.0} s after release");
            if (sec > 4) StatusMessage = $"Speech recognition took {sec:0} s: in Settings › Audio, try a smaller Whisper model or the graphics card";
        });
        _speech.MicrophoneId = _settings.MicrophoneId;
        _speech.PttKeyName = PttKeyText;
        _ = _speech.UseRecognizerAsync(_settings.RecognizerId);
    }

    // ---- Audio setup: devices, voices, speech engine ------------------------------------------

    public IReadOnlyList<AudioDevice> Microphones { get; } = AudioDevices.Inputs();
    public IReadOnlyList<AudioDevice> Speakers { get; } = AudioDevices.Outputs();
    public IReadOnlyList<RecognizerOption> Recognizers => RecognizerOption.All;
    public IReadOnlyList<string> VoiceNames => _voice.Voices;
    public IReadOnlyList<VoiceEngineOption> VoiceEngines => VoiceEngineOption.All;
    [ObservableProperty] public partial VoiceEngineOption? SelectedVoiceEngine { get; set; }
    [ObservableProperty] public partial string? ApproachVoiceName { get; set; }
    [ObservableProperty] public partial string VoiceEngineStatus { get; set; } = "";
    private bool _loadingVoices;

    public bool VaryPilotVoices
    {
        get => _settings.VaryPilotVoices;
        set { _settings.VaryPilotVoices = value; _voice.VaryPilotVoices = value; OnPropertyChanged(); }
    }

    public bool RadioEffectEnabled
    {
        get => _settings.RadioEffect;
        set { _settings.RadioEffect = value; _voice.RadioEffectEnabled = value; OnPropertyChanged(); }
    }

    partial void OnSelectedVoiceEngineChanged(VoiceEngineOption? value)
    {
        if (value is null || _loadingVoices) return;
        _settings.VoiceEngine = value.Id;
        _ = SwitchVoiceEngineAsync(value.Id, keepSavedVoices: false);
    }

    /// <summary>Loads a speech engine (downloading a neural model the first time) and refreshes the voice lists.</summary>
    private async Task SwitchVoiceEngineAsync(string id, bool keepSavedVoices)
    {
        try
        {
            await _voice.UseEngineAsync(id, Http, new Progress<string>(s => VoiceEngineStatus = s), CancellationToken.None);
            VoiceEngineStatus = $"{_voice.EngineName} ready";
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or DllNotFoundException or TaskCanceledException)
        {
            VoiceEngineStatus = $"Could not load voices: {ex.Message}";
            return;
        }
        _loadingVoices = true;
        OnPropertyChanged(nameof(VoiceNames));
        PilotVoiceName = keepSavedVoices && _settings.PilotVoice is { } pv && VoiceNames.Contains(pv) ? pv : _voice.PilotVoice;
        TowerVoiceName = keepSavedVoices && _settings.TowerVoice is { } tv && VoiceNames.Contains(tv) ? tv : _voice.TowerVoice;
        ApproachVoiceName = keepSavedVoices && _settings.ApproachVoice is { } av && VoiceNames.Contains(av) ? av : _voice.ApproachVoice;
        _loadingVoices = false;
    }

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
        _voice.VaryPilotVoices = _settings.VaryPilotVoices;
        _voice.RadioEffectEnabled = _settings.RadioEffect;
        _loadingVoices = true;
        SelectedVoiceEngine = VoiceEngineOption.All.FirstOrDefault(e => e.Id == _settings.VoiceEngine) ?? VoiceEngineOption.All[0];
        _loadingVoices = false;
        _ = SwitchVoiceEngineAsync(SelectedVoiceEngine.Id, keepSavedVoices: true);
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

    partial void OnPilotVoiceNameChanged(string? value) { if (value == null) return; _settings.PilotVoice = value; _voice.PilotVoice = value; }
    partial void OnTowerVoiceNameChanged(string? value) { if (value == null) return; _settings.TowerVoice = value; _voice.TowerVoice = value; }
    partial void OnApproachVoiceNameChanged(string? value) { if (value == null) return; _settings.ApproachVoice = value; _voice.ApproachVoice = value; }

    /// <summary>The voice test is playing; the button then stops it.</summary>
    [ObservableProperty] public partial bool TestingVoices { get; set; }
    public string TestVoicesText => TestingVoices ? "Stop test" : "Test voices";
    partial void OnTestingVoicesChanged(bool value) => OnPropertyChanged(nameof(TestVoicesText));
    private bool _voiceIdleHooked;

    [RelayCommand]
    private void TestVoices()
    {
        if (!_voiceIdleHooked)
        {
            _voiceIdleHooked = true;
            _voice.Idle += () => App.Current.Dispatcher.BeginInvoke(() => TestingVoices = false);
        }
        if (TestingVoices) { _voice.StopAll(); TestingVoices = false; return; }
        TestingVoices = true;
        _voice.Pilot("Miramar final, Navy one two three four, radio check.", "VV1234");
        _voice.Pilot("Approach, Banshee two one, with you at three thousand.", "BANSHEE21");
        _voice.Tower("Navy one two three four, runway two four right, cleared to land.");
        _voice.Approach("Final, approach, handoff, one two mile final, Reach four eight one, three thousand.");
    }

    // ---- Keyboard calls (training) -------------------------------------------------------------

    /// <summary>Standard calls bound to keys; sent to the selected training aircraft like spoken ones.</summary>
    public void KeyCall(string call)
    {
        if (!TrainingMode) return;
        ProcessTransmission(call, "key");
    }

    /// <summary>What a modifier adds to a deviation key: Ctrl "slightly", Shift "well", Alt "going".</summary>
    public enum KeyEmphasis { Plain, Slightly, Well, Going }

    private static string Emphasis(KeyEmphasis e) => e switch { KeyEmphasis.Slightly => "SLIGHTLY ", KeyEmphasis.Well => "WELL ", KeyEmphasis.Going => "GOING ", _ => "" };

    /// <summary>Glidepath call from a key (§5-12-4): side +1 above, −1 below, 0 on glidepath.</summary>
    public void GlidepathKey(int side, KeyEmphasis e) =>
        KeyCall(side == 0 ? "ON GLIDEPATH" : $"{Emphasis(e)}{(side > 0 ? "ABOVE" : "BELOW")} GLIDEPATH");

    /// <summary>Course call from a key: side +1 right, −1 left, 0 on course.</summary>
    public void CourseKey(int side, KeyEmphasis e) =>
        KeyCall(side == 0 ? "ON COURSE" : $"{Emphasis(e)}{(side > 0 ? "RIGHT" : "LEFT")} OF COURSE");

    /// <summary>Enter: transmit the suggested call for the selected aircraft.</summary>
    public void SaySuggestedCall()
    {
        string text = AdvisoryText.Replace("(ADVISORY) ", "").Trim();
        if (text.Length > 0) KeyCall(text);
    }

    /// <summary>R: the range or position call due now, else the range to the nearest half mile.</summary>
    public void SayRange()
    {
        if (RangeCallText.Length > 0) { KeyCall(RangeCallText); return; }
        if (CurrentAdvisory is { } a && a.Deviation.RangeNm > 0.4) KeyCall(AdvisoryEngine.RangePhrase(a.Deviation.RangeNm));
    }

    private double MissedClimbMsl => Engine.Runway is { } rw ? Math.Round((rw.ThresholdElevFt + 2000) / 100) * 100 : 2000;

    /// <summary>
    /// Approach brief in one transmission (§5-10-2, §5-10-4): the approach and runway, missed approach
    /// instructions and lost communications instructions.
    /// </summary>
    public void SayApproachBrief()
    {
        if (Engine.Runway is not { } rw) return;
        KeyCall($"THIS WILL BE A P-A-R APPROACH TO RUNWAY {rw.RunwayId}. "
            + $"IN THE EVENT OF MISSED APPROACH, CLIMB AND MAINTAIN {MissedClimbMsl:0}, FLY RUNWAY HEADING. "
            + "IF NO TRANSMISSIONS ARE RECEIVED FOR ONE MINUTE IN THE PATTERN OR FIVE SECONDS ON FINAL APPROACH, ATTEMPT CONTACT WITH TOWER AND PROCEED VFR. "
            + $"IF UNABLE, CLIMB AND MAINTAIN {MissedClimbMsl:0} AND EXECUTE MISSED APPROACH");
    }

    /// <summary>
    /// The tower key, one step at a time: request a clearance from local, then relay it to the pilot once
    /// local has cleared them (asks again if local said to continue).
    /// </summary>
    public void TowerStep()
    {
        if (KeyTarget() is not { } ac) return;
        if (_tower.AnswerFor(ac.Callsign) == TowerAnswer.Cleared)
        {
            if (ac.ClearedToLand) { StatusMessage = $"{ac.Callsign} already has the clearance"; return; }
            SayTowerClearance();
        }
        else TowerRequest(ac, TowerIntent, spoken: false);
    }

    /// <summary>
    /// The approach key, one step at a time: accept a waiting handoff ("radar contact"); after a missed
    /// approach, coordinate it with approach, then switch the pilot to approach.
    /// </summary>
    public void ApproachStep()
    {
        var offered = Engine.Training.Aircraft.Where(a => _approach.IsPending(a.Callsign)).ToList();
        var missed = CallTarget is { GoingAround: true } m ? m : Engine.Training.Aircraft.FirstOrDefault(a => a.GoingAround && !a.HandedOff);
        if (missed != null && !missed.HandedOff)
        {
            if (!_approach.IsCoordinated(missed.Callsign)) ApproachCoordination(missed, spoken: false);
            else ProcessTransmissionTo(missed, "CONTACT APPROACH");
            return;
        }
        if (offered.Count > 0) { AcceptHandoff(); return; }
        StatusMessage = "Nothing for approach: no handoff waiting and no missed approach";
    }

    /// <summary>A key call to a particular aircraft, whichever is selected.</summary>
    private void ProcessTransmissionTo(SimAircraft ac, string call)
    {
        if (TrainingMode) ProcessTransmission(call, "key", ac);
    }

    /// <summary>Y: relay tower's landing clearance with the wind (§5-10-13).</summary>
    public void SayTowerClearance()
    {
        if (KeyTarget()?.Callsign is not { } cs || Engine.Runway is not { } rw) return;
        if (_tower.AnswerFor(cs) != TowerAnswer.Cleared) { StatusMessage = $"Tower has not cleared {cs} yet (T to request)"; return; }
        string wind = MetarWindPhrase;
        KeyCall($"{(wind.Length > 0 ? wind + ", " : "")}RUNWAY {rw.RunwayId}, {TowerSim.ClearanceWords(_tower.IntentFor(cs)).ToUpperInvariant()}");
    }

    /// <summary>Accept approach's handoff without speaking: the selected aircraft if offered, else the oldest offer.</summary>
    [RelayCommand]
    private void AcceptHandoff()
    {
        var offered = Engine.Training.Aircraft.Where(a => _approach.IsPending(a.Callsign)).ToList();
        var ac = offered.FirstOrDefault(a => a.Callsign == SelectedCallsign) ?? offered.FirstOrDefault();
        if (ac == null) { StatusMessage = "No handoff waiting"; return; }
        TryAcceptHandoff($"{RadioSpeech.Callsign(ac.Callsign)}, radar contact", "button");
    }

    /// <summary>Switch the selected pilot to approach (after a missed approach).</summary>
    [RelayCommand]
    private void ContactApproach() => KeyCall("CONTACT APPROACH");

    /// <summary>Push-to-talk key, chosen in Settings › Audio.</summary>
    public System.Windows.Input.Key PttKey
    {
        get => Enum.TryParse<System.Windows.Input.Key>(_settings.PttKey, out var k) ? k : System.Windows.Input.Key.Space;
        set
        {
            _settings.PttKey = value.ToString();
            _speech.PttKeyName = PttKeyText;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PttKeyText));
            OnPropertyChanged(nameof(KeysHint));
            if (_speech.Ready) VoiceState = $"Hold {PttKeyText} to talk";
        }
    }

    public string PttKeyText => PttKey switch
    {
        System.Windows.Input.Key.Space => "Space",
        System.Windows.Input.Key.LeftCtrl => "Left Ctrl",
        System.Windows.Input.Key.RightCtrl => "Right Ctrl",
        System.Windows.Input.Key.LeftShift => "Left Shift",
        System.Windows.Input.Key.RightShift => "Right Shift",
        System.Windows.Input.Key.LeftAlt => "Left Alt",
        System.Windows.Input.Key.RightAlt => "Right Alt",
        System.Windows.Input.Key.Oem3 => "`",
        var k => k.ToString(),
    };

    /// <summary>The scope's hotkeys (reference and rebinding in the Key assignments window).</summary>
    public KeyMap Keys { get; }

    /// <summary>Asks the main window to show the key assignments.</summary>
    public event Action? KeyAssignmentsRequested;
    public void ShowKeyAssignments() => KeyAssignmentsRequested?.Invoke();

    /// <summary>A command's main key for hints and buttons, e.g. "W".</summary>
    private string KeyOf(string id)
    {
        var c = KeyMap.Commands.First(c => c.Id == id);
        var k = Keys.Primary(c).IsNone ? Keys.Alternate(c) : Keys.Primary(c);
        return k.IsNone ? "unbound" : k.ToString();
    }

    // The approach key steps through all three (handoff, coordinate, contact approach).
    public string RadarContactButtonText => $"Radar contact ({KeyOf("approach_step")})";
    public string ContactApproachButtonText => $"Contact approach ({KeyOf("approach_step")})";
    public string CoordinateMissedButtonText => $"Coordinate missed approach ({KeyOf("approach_step")})";

    public string KeysHint =>
        $"{PttKeyText} talk (start with 'Local…' or 'Approach…' to coordinate) · {KeyOf("send_suggested")} send the suggested call\n"
        + $"{KeyOf("gp_above")}/{KeyOf("gp_below")} above/below glidepath · {KeyOf("crs_left")}/{KeyOf("crs_right")} left/right of course · "
        + $"{KeyOf("gp_on")}/{KeyOf("crs_on")} on glidepath/course\n"
        + "  hold Ctrl: slightly… · Shift: well… · Alt: going…\n"
        + $"{KeyOf("heading_left")}/{KeyOf("heading_right")} turn 1° (Shift 5°) · {KeyOf("range")} range · {KeyOf("wheels")} wheels · {KeyOf("execute_missed")} missed approach\n"
        + $"{KeyOf("approach_step")}–{KeyOf("contact_tower_after")}: radar contact, brief, begin descent, do not acknowledge, tower, contact tower\n"
        + $"{KeyOf("show_keys")} shows every key.";

    /// <summary>Push-to-talk is held (drives the TX indicator).</summary>
    [ObservableProperty] public partial bool IsTransmitting { get; set; }

    public void PressToTalk()
    {
        if (!TrainingMode || IsTransmitting) return;
        IsTransmitting = true;
        _voice.PauseForTransmit();
        _speech.PressToTalk();
    }

    public void ReleaseToTalk()
    {
        if (!IsTransmitting) return;
        IsTransmitting = false;
        _speech.ReleaseToTalk();
        _voice.ResumeAfterTransmit();
    }

    [RelayCommand]
    private void SendTyped()
    {
        if (string.IsNullOrWhiteSpace(TypedTransmission)) return;
        ProcessTransmission(TypedTransmission, "typed");
        TypedTransmission = "";
    }

    /// <summary>
    /// Sends a transmission: to tower or approach if it opens by addressing them (interphone), otherwise to
    /// the selected training aircraft (or the only one) on the final frequency.
    /// </summary>
    public void ProcessTransmission(string text, string via, SimAircraft? to = null)
    {
        if (TryAcceptHandoff(text, via)) return;
        if (Interphone.Parse(text) is { } call) { ProcessInterphone(call, via); return; }

        var t = Phraseology.Parse(text);
        var target = to ?? CallTarget;
        if (target is { InPattern: true } || (target != null && _approach.IsPending(target.Callsign)))
        {
            // On approach's frequency: in the pattern, or offered but not yet accepted ("radar contact").
            Log("FINAL", text.ToUpperInvariant() + "  (no reply: still with approach)");
            StatusMessage = target.InPattern ? $"{target.Callsign} is with approach in the pattern"
                : $"{target.Callsign} is still with approach: accept the handoff first ({KeyOf("approach_step")})";
            return;
        }
        Log("FINAL", text.ToUpperInvariant() + (t.IsEmpty ? "  (not understood)" : ""));
        _session.Write($"          ({via}) to {target?.Callsign ?? "no aircraft selected"}; understood as: {Understood(t)}");
        _lastTxTime = Engine.Training.SimTime;
        if (t.IsEmpty || target == null || Engine.Runway is not { } rw)
        {
            if (target == null && !t.IsEmpty) StatusMessage = "Select a training aircraft first";
            return;
        }
        double rod = CurrentAdvisory?.RecommendedDescentFpm ?? 700;
        var response = target.Receive(t, Engine.Training.SimTime, rw.MagneticCourseDeg, rod, rw.ThresholdElevFt);
        if (t.ContactApproach && !_approach.IsCoordinated(target.Callsign))
            _session.Write($"NOTE     {target.Callsign} switched to approach without coordinating the missed approach");
        _session.Write($"          {target.Callsign} action: {response.Action}{(target.NoAcknowledge && response.Reply == null ? " (silent: do not acknowledge)" : "")}");
        if (response.Reply is { } reply)
            _voice.Pilot(reply, target.Callsign, 1.0 + Random.Shared.NextDouble(), OnUi(() => Log(target.Callsign, reply)));
    }

    /// <summary>
    /// A spoken or typed coordination call. The aircraft is the one named in the call, else the selected
    /// one; tower gets a landing request (intent from the wording, else the panel's choice), approach a
    /// missed approach coordination. Anything else gets a plain acknowledgement.
    /// </summary>
    private void ProcessInterphone(InterphoneCall call, string via)
    {
        // On the landline the tower position is "local".
        string who = call.To == Facility.Tower ? "LOCAL" : "APPROACH";
        Log("FINAL", $"({who.ToLowerInvariant()} line) {call.Text}");
        var ac = Interphone.FindAircraft(call.Text, Engine.Training.Aircraft)
                 ?? CallTarget;
        _session.Write($"          ({via}) interphone to {who.ToLowerInvariant()} about {ac?.Callsign ?? "no aircraft"}; "
            + $"understood as: {(call.Intent is { } i ? i.ToString() : call.MissedApproach ? "missed approach" : "general")}");

        if (call.To == Facility.Tower)
        {
            if (ac != null && !ac.GoingAround) TowerRequest(ac, call.Intent ?? TowerIntent, spoken: true);
            else Reply(who, "Roger.");
        }
        else if (ac != null && (ac.GoingAround || call.MissedApproach))
        {
            if (!ac.GoingAround) Reply(who, $"{RadioSpeech.Callsign(ac.Callsign)}, say again when they're missed.");
            else ApproachCoordination(ac, spoken: true);
        }
        else Reply(who, $"Roger, {_approach.SpokenInitials}.");
    }

    /// <summary>Tower or approach answers on the interphone after a couple of seconds.</summary>
    private void Reply(string who, string text)
    {
        if (who == "LOCAL") _voice.Tower(text, 2.0, OnUi(() => Log(who, text)));
        else _voice.Approach(text, 2.0, OnUi(() => Log(who, text)));
    }

    /// <summary>Interphone request to tower for the selected training aircraft (7110.65 §5-10-13).</summary>
    [RelayCommand]
    private void RequestTower()
    {
        if (KeyTarget() is not { } ac) return;
        TowerRequest(ac, TowerIntent, spoken: false);
    }

    private void TowerRequest(SimAircraft ac, LandingIntent intent, bool spoken)
    {
        if (Engine.Runway is not { } rw) return;
        if (!spoken) Log("FINAL", TowerSim.RequestText(ac.Callsign, ac.RangeFt / Units.FeetPerNm, intent));
        TowerIntent = intent;
        bool occupied = Engine.Training.Aircraft.Any(a => a != ac && a.Landed && !a.Finished);
        Reply("LOCAL", _tower.Request(ac.Callsign, intent, rw.RunwayId, occupied));
    }

    /// <summary>Interphone coordination of a missed approach with approach control, before switching the pilot.</summary>
    [RelayCommand]
    private void CoordinateMissed()
    {
        if (KeyTarget() is not { } ac) return;
        if (!ac.GoingAround) { StatusMessage = $"{ac.Callsign} has not gone missed"; return; }
        ApproachCoordination(ac, spoken: false);
    }

    private void ApproachCoordination(SimAircraft ac, bool spoken)
    {
        if (Engine.Runway is not { } rw) return;
        if (!spoken)
            Log("FINAL", ApproachSim.MissedApproachRequest(ac.Callsign, Units.Norm360(rw.MagneticCourseDeg + ac.CommandedHeadingRelDeg), ac.ClimbToFt + rw.ThresholdElevFt));
        Reply("APPROACH", _approach.Coordinate(ac.Callsign));
    }

    /// <summary>Who a call or key is for: the selected training aircraft, else the only one.</summary>
    private SimAircraft? CallTarget =>
        (SelectedCallsign is { } cs ? Engine.Training.Find(cs) : null)
        ?? (Engine.Training.Aircraft.Where(a => !a.InPattern).ToList() is [var only] ? only : null);

    /// <summary>The target for a key or button, or a status prompt to select one.</summary>
    private SimAircraft? KeyTarget()
    {
        if (CallTarget is { } ac) return ac;
        if (TrainingMode) StatusMessage = Engine.Training.Aircraft.Count == 0 ? "No training aircraft" : "Select a training aircraft first (click it or Tab)";
        return null;
    }

    /// <summary>D: "begin descent", sent like any other call so the pilot reads it back and a level-off is canceled.</summary>
    public void BeginDescent()
    {
        if (KeyTarget() is null) return;
        KeyCall("BEGIN DESCENT");
    }

    public void NudgeHeading(int degrees)
    {
        if (KeyTarget() is not { } ac || Engine.Runway is not { } rw) return;
        double current = Units.Norm360(rw.MagneticCourseDeg + ac.CommandedHeadingRelDeg);
        double next = Units.Norm360(Math.Round(current) + degrees);
        ProcessTransmission($"turn {(degrees > 0 ? "right" : "left")} heading {(next == 0 ? 360 : next):000}", "key");
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
            double since = Engine.Training.SimTime - _lastTxTime;
            TxTimerText = $"Last transmission {since:0} s ago" + (TrainingPaused ? " (paused)" : "");
            // Lost communications: 5 s on final, 30 s before descent (CF GCA procedure, as in vPAR).
            bool onFinal = SelectedCallsign is { } sc && Engine.Training.Find(sc) is { Descending: true };
            double limit = onFinal ? 5 : 30;
            TxTimerLevel = since < limit ? AdvisoryLevel.Normal : since < limit * 2 ? AdvisoryLevel.Caution : AdvisoryLevel.Warning;
        }

        RefreshSelection();
        if (HasTraining) { SnapshotTraining(); RepeatUnansweredHandoffs(); }
        HandOffReturningAircraft();
        foreach (var (cs, text) in Engine.Training.TakePilotCalls()) PilotSays(cs, text);
        _speech.Vocabulary = string.Join(", ", Engine.Training.Aircraft.Select(a => RadioSpeech.Callsign(a.Callsign)));
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
            // METAR wind is true; ATC issues it magnetic.
            int? magDir = m?.WindDirDeg is int td ? RadioSpeech.MagneticWindDeg(td, SelectedRunway?.MagVarEastDeg ?? 0) : null;
            MetarWindPhrase = m is null ? "" : RadioSpeech.Wind(magDir, m.WindSpeedKt, m.WindVariable).ToUpperInvariant();
            MetarStars = m?.StarsShort;
            MetarCategory = m?.Category ?? FlightCategory.Unknown;
            _liveWind = m is { WindSpeedKt: int kt } ? (m.WindVariable ? null : m.WindDirDeg, kt, m.GustKt) : null;
            Engine.Training.FieldWeather = (m?.CeilingFt, m?.VisibilitySm);
            ApplyLiveWind();
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
        _session.Dispose();
        Engine.Dispose();
    }
}
