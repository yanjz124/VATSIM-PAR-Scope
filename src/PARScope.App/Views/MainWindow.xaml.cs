using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PARScope.App.Rendering;
using PARScope.App.Services;
using PARScope.App.ViewModels;
using PARScope.Core.Data;
using SkiaSharp.Views.Desktop;
using Wpf.Ui.Controls;

namespace PARScope.App.Views;

public partial class MainWindow : FluentWindow
{
    private readonly MainViewModel _vm;
    private readonly AppSettings _settings;
    private readonly ParDisplayRenderer _par = new();
    private readonly PlanRenderer _plan = new();
    private readonly DispatcherTimer _statusTimer;
    private TimeSpan _lastRender;

    public MainWindow(MainViewModel vm, AppSettings settings)
    {
        _vm = vm;
        _settings = settings;
        InitializeComponent();
        DataContext = vm;
        Title = vm.WindowTitle;
        RestorePlacement();

        _statusTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => _vm.RefreshStatus(), Dispatcher);
        Loaded += async (_, _) =>
        {
            UpdateScopes();
            CompositionTarget.Rendering += OnRendering;
            _statusTimer.Start();
            _vm.StartVoice();
            await _vm.InitializeAsync();
            await OfferUpdateAsync();
        };
        vm.PropertyChanged += OnViewModelChanged;
        vm.AirportResults.CollectionChanged += (_, _) => AirportPopup.IsOpen = AirportBox.IsKeyboardFocusWithin && vm.AirportResults.Count > 0;
        UpdateSidePanel();
    }

    // ---- Frame loop --------------------------------------------------------------------------------

    private void OnRendering(object? sender, EventArgs e)
    {
        // Rendering can fire more than once per frame; only advance on a new frame time.
        var t = ((RenderingEventArgs)e).RenderingTime;
        if (t == _lastRender) return;
        _lastRender = t;

        _vm.Engine.Tick();
        _vm.UpdateAdvisory();
        if (_vm.ShowAsr) PlanView.InvalidateVisual();
        else ParView.InvalidateVisual();
        _asrWindow?.Invalidate();
    }

    private void ParView_PaintSurface(object? sender, SKPaintSurfaceEventArgs e)
    {
        float scale = DpiScale(ParView, e);
        _par.SetFont(_vm.ScopeFont);
        var c = e.Surface.Canvas;
        c.Save();
        c.Scale(scale);
        var opt = new ParRenderOptions
        {
            RangeScaleNm = _vm.RangeScaleNm,
            LogRange = _vm.LogRange,
            LogStraightLines = _vm.SelectedPreset?.LogStraightLines ?? false,
            AzimuthAntennaCentred = _vm.SelectedPreset?.AzimuthAntennaCentred ?? false,
            ShowSafetyLimits = _vm.ShowSafetyLimits,
            ShowCourseWedge = _vm.ShowCourseWedge,
            MtiEnabled = _vm.Mti,
            Weather = _vm.MetarShort,
            ShowScan = _vm.ShowScan,
            HistoryDots = _vm.HistoryDots,
            Selected = _vm.SelectedCallsign,
            SelectedAdvisory = _vm.CurrentAdvisory,
        };
        _par.Render(c, e.Info.Width / scale, e.Info.Height / scale, _vm.Engine.Geometry, _vm.Engine.Radar, _vm.Engine.Now, opt);
        c.Restore();
    }

    private void PlanView_PaintSurface(object? sender, SKPaintSurfaceEventArgs e) => PaintPlan(_plan, PlanView, e);

    /// <summary>Draws the ASR; shared by the embedded view and the pop-out window.</summary>
    internal void PaintPlan(PlanRenderer plan, FrameworkElement view, SKPaintSurfaceEventArgs e)
    {
        float scale = DpiScale(view, e);
        plan.SetFont(_vm.ScopeFont);
        var c = e.Surface.Canvas;
        c.Save();
        c.Scale(scale);
        var engine = _vm.Engine;
        plan.Render(c, e.Info.Width / scale, e.Info.Height / scale, engine.Geometry, engine.Asr, engine.Airport,
            engine.Preset, engine.Targets, engine.Now, _vm.PlanRangeNm, _vm.SelectedCallsign, _vm.MetarStars, _vm.ShowCompassRose);
        c.Restore();
    }

    private static float DpiScale(FrameworkElement el, SKPaintSurfaceEventArgs e) =>
        el.ActualWidth > 0 ? (float)(e.Info.Width / el.ActualWidth) : 1f;

    // ---- Selection ---------------------------------------------------------------------------------

    private void ParView_MouseDown(object sender, MouseButtonEventArgs e) => Pick(_par.Hits, e.GetPosition(ParView));
    private void PlanView_MouseDown(object sender, MouseButtonEventArgs e) => Pick(_plan.Hits, e.GetPosition(PlanView));

    internal void PickFrom(IReadOnlyList<HitTarget> hits, Point p) => SelectNearest(hits, p);

    private void Pick(IReadOnlyList<HitTarget> hits, Point p)
    {
        Keyboard.Focus(this);
        SelectNearest(hits, p);
    }

    private void SelectNearest(IReadOnlyList<HitTarget> hits, Point p)
    {
        var best = hits
            .Select(h => (h.Callsign, D: Math.Pow(h.X - p.X, 2) + Math.Pow(h.Y - p.Y, 2)))
            .Where(h => h.D < 22 * 22)
            .OrderBy(h => h.D)
            .Select(h => h.Callsign)
            .FirstOrDefault();
        _vm.Select(best);
    }

    // ---- Keyboard ----------------------------------------------------------------------------------

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        HandleScopeKey(e);
    }

    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        base.OnPreviewKeyUp(e);
        HandleScopeKeyUp(e);
    }

    /// <summary>Scope and radio keys; also used by the pop-out ASR window.</summary>
    internal void HandleScopeKey(KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is System.Windows.Controls.TextBox) return;
        // Alt-modified keys arrive as Key.System.
        if ((e.Key == Key.System ? e.SystemKey : e.Key) == _vm.PttKey)
        {
            if (!e.IsRepeat) _vm.PressToTalk();
            e.Handled = true;
            return;
        }
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        bool shift = mods.HasFlag(ModifierKeys.Shift), ctrl = mods.HasFlag(ModifierKeys.Control), alt = mods.HasFlag(ModifierKeys.Alt);
        // Trend modifiers for glidepath and course keys: Shift "…and correcting / coming down", Ctrl "…and holding", Alt "going…".
        var trend = alt ? MainViewModel.KeyTrend.Going : ctrl ? MainViewModel.KeyTrend.Holding : shift ? MainViewModel.KeyTrend.Correcting : MainViewModel.KeyTrend.None;
        switch (key)
        {
            case Key.PageUp: _vm.StepRange(-1); break;
            case Key.PageDown: _vm.StepRange(+1); break;
            case Key.Tab: _vm.CycleSelection(); break;
            case Key.Escape: _vm.Select(null); break;
            case Key.P: _vm.TogglePause(); break;
            case Key.F: _vm.FlipScope(); break;
            // Training calls (FAA JO 7110.65 §5-10, §5-12, §2-1-25), sent to the selected aircraft as if spoken.
            case Key.Enter: _vm.SaySuggestedCall(); break;
            case Key.Left: _vm.NudgeHeading(shift ? -5 : -1); break;
            case Key.Right: _vm.NudgeHeading(shift ? 5 : 1); break;
            case Key.Q: _vm.GlidepathKey(1, trend); break;
            case Key.W: _vm.GlidepathKey(2, trend); break;
            case Key.E: _vm.GlidepathKey(3, trend); break;
            case Key.A: _vm.GlidepathKey(0, trend); break;
            case Key.Z: _vm.GlidepathKey(-1, trend); break;
            case Key.X: _vm.GlidepathKey(-2, trend); break;
            case Key.C: _vm.GlidepathKey(-3, trend); break;
            case Key.D1 or Key.NumPad1: _vm.CourseKey(-3, trend); break;
            case Key.D2 or Key.NumPad2: _vm.CourseKey(-2, trend); break;
            case Key.D3 or Key.NumPad3: _vm.CourseKey(-1, trend); break;
            case Key.D4 or Key.NumPad4: _vm.CourseKey(0, trend); break;
            case Key.D5 or Key.NumPad5: _vm.CourseKey(1, trend); break;
            case Key.D6 or Key.NumPad6: _vm.CourseKey(2, trend); break;
            case Key.D7 or Key.NumPad7: _vm.CourseKey(3, trend); break;
            case Key.H: _vm.KeyCall("HOW DO YOU HEAR ME"); break;
            case Key.B: _vm.SayApproachBrief(); break;
            case Key.L: _vm.SayLostComms(); break;
            case Key.K: _vm.SayMissedInstructions(); break;
            case Key.N: _vm.KeyCall("DO NOT ACKNOWLEDGE FURTHER TRANSMISSIONS"); break;
            case Key.G: _vm.KeyCall("APPROACHING GLIDEPATH, WHEELS SHOULD BE DOWN"); break;
            case Key.D: _vm.BeginDescent(); break;
            case Key.R when ctrl: _vm.AcceptHandoffCommand.Execute(null); break;
            case Key.R: _vm.SayRange(); break;
            case Key.V: _vm.KeyCall(shift ? "RESUME NORMAL RATE OF DESCENT" : "LEVEL OFF YOUR AIRCRAFT, ACKNOWLEDGE"); break;
            case Key.M when ctrl: _vm.CoordinateMissedCommand.Execute(null); break;
            case Key.M: _vm.KeyCall(shift ? "IF RUNWAY OR APPROACH LIGHTS NOT IN SIGHT, EXECUTE MISSED APPROACH" : "EXECUTE MISSED APPROACH"); break;
            case Key.Y: _vm.SayTowerClearance(); break;
            case Key.J: _vm.ContactApproachCommand.Execute(null); break;
            case Key.U: _vm.KeyCall("CONTACT TOWER AFTER LANDING"); break;
            case Key.T: _vm.RequestTowerCommand.Execute(null); break;
            default: return;
        }
        e.Handled = true;
    }

    internal void HandleScopeKeyUp(KeyEventArgs e)
    {
        if ((e.Key == Key.System ? e.SystemKey : e.Key) == _vm.PttKey && Keyboard.FocusedElement is not System.Windows.Controls.TextBox)
        {
            _vm.ReleaseToTalk();
            e.Handled = true;
        }
    }

    private void TypedBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { _vm.SendTypedCommand.Execute(null); e.Handled = true; Keyboard.Focus(this); } // hotkeys work again straight away
        else if (e.Key == Key.Escape) Keyboard.Focus(this);
    }

    private void AirportBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (_vm.SubmitAirportQuery()) { AirportPopup.IsOpen = false; Keyboard.Focus(this); }
            e.Handled = true;
        }
        else if (e.Key == Key.Down && AirportPopup.IsOpen)
        {
            AirportList.SelectedIndex = 0;
            (AirportList.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem)?.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            AirportPopup.IsOpen = false;
        }
    }

    private void AirportBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!AirportPopup.IsKeyboardFocusWithin) AirportPopup.IsOpen = false;
    }

    private void AirportList_Pick(object sender, MouseButtonEventArgs e) => PickAirport();

    private void AirportList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { PickAirport(); e.Handled = true; }
    }

    private void PickAirport()
    {
        if (AirportList.SelectedItem is AirportRecord apt)
        {
            _vm.SelectAirport(apt);
            AirportPopup.IsOpen = false;
            Keyboard.Focus(this);
        }
    }

    private async Task OfferUpdateAsync()
    {
        if (await _vm.CheckForUpdateAsync() is not { } release) return;
        var answer = System.Windows.MessageBox.Show(this,
            $"vibe PAR {release.Version} is available (you have {Updater.DisplayVersion}).\n\n"
            + "Update now? The scope restarts when it is done. "
            + (Updater.InstalledBySetup ? "The vPilot plugin is updated too (close vPilot first if the plugin changed)."
                : "The vPilot plugin is not changed; if the release notes say it was updated, copy it from the Plugin folder into vPilot."),
            "Update available", System.Windows.MessageBoxButton.YesNo, MessageBoxImage.Information);
        if (answer == System.Windows.MessageBoxResult.Yes) await _vm.InstallUpdateAsync(release);
    }

    private void PttPill_Down(object sender, MouseButtonEventArgs e)
    {
        PttPill.CaptureMouse();
        _vm.PressToTalk();
    }

    private void PttPill_Up(object sender, MouseEventArgs e)
    {
        if (e is MouseButtonEventArgs) PttPill.ReleaseMouseCapture();
        else if (PttPill.IsMouseCaptured) return; // still held: leaving the pill does not end the transmission
        _vm.ReleaseToTalk();
    }

    private SettingsWindow? _settingsWindow;

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (_settingsWindow is { IsLoaded: true }) { _settingsWindow.Activate(); return; }
        _settingsWindow = new SettingsWindow(_vm) { Owner = this };
        _settingsWindow.Closed += (_, _) => { _settingsWindow = null; Keyboard.Focus(this); };
        _settingsWindow.Show();
    }

    // ---- Layout & persistence ----------------------------------------------------------------------

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.ShowPlanView)) UpdateSidePanel();
        if (e.PropertyName is nameof(MainViewModel.ShowAsr) or nameof(MainViewModel.AsrPoppedOut)) UpdateScopes();
    }

    private void UpdateSidePanel()
    {
        SideColumn.Width = _vm.ShowPlanView ? new GridLength(Math.Clamp(_settings.SidePanelWidth, 300, 700)) : new GridLength(0);
        SideSplitter.Visibility = _vm.ShowPlanView ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SideSplitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e) =>
        _settings.SidePanelWidth = SideColumn.ActualWidth;

    private AsrWindow? _asrWindow;

    /// <summary>The main scope area shows the PAR or, flipped, the ASR; the ASR may also be in its own window.</summary>
    private void UpdateScopes()
    {
        ParView.Visibility = _vm.ShowAsr ? Visibility.Collapsed : Visibility.Visible;
        AsrHost.Visibility = _vm.ShowAsr ? Visibility.Visible : Visibility.Collapsed;

        if (_vm.AsrPoppedOut && _asrWindow == null && IsLoaded)
        {
            _asrWindow = new AsrWindow(_vm, _settings, this) { Owner = this };
            _asrWindow.Closed += (_, _) => { _asrWindow = null; _vm.AsrPoppedOut = false; };
            _asrWindow.Show();
        }
        else if (!_vm.AsrPoppedOut && _asrWindow != null)
        {
            var w = _asrWindow;
            _asrWindow = null;
            w.Close();
        }
    }

    private void RestorePlacement()
    {
        if (_settings.WindowWidth is double w && _settings.WindowHeight is double h && _settings.WindowLeft is double l && _settings.WindowTop is double t)
        {
            // Only restore if the saved rectangle is still on a connected screen.
            var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            if (screen.IntersectsWith(new Rect(l, t, w, h)))
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = l; Top = t; Width = w; Height = h;
            }
        }
        if (_settings.WindowMaximized) WindowState = WindowState.Maximized;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        var r = RestoreBounds;
        _settings.WindowLeft = r.Left; _settings.WindowTop = r.Top;
        _settings.WindowWidth = r.Width; _settings.WindowHeight = r.Height;
        _settings.WindowMaximized = WindowState == WindowState.Maximized;
        if (_asrWindow != null) { _asrWindow.SavePlacement(); _settings.AsrPoppedOut = true; }
        _vm.SaveSettings();
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        CompositionTarget.Rendering -= OnRendering;
        _statusTimer.Stop();
        _vm.Dispose();
        _par.Dispose();
        _plan.Dispose();
        base.OnClosed(e);
    }
}
