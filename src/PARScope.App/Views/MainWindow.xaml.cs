using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
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
        RestorePlacement();

        _statusTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => _vm.RefreshStatus(), Dispatcher);
        Loaded += async (_, _) =>
        {
            CompositionTarget.Rendering += OnRendering;
            _statusTimer.Start();
            _vm.StartVoice();
            await _vm.InitializeAsync();
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
        ParView.InvalidateVisual();
        if (_vm.ShowPlanView) PlanView.InvalidateVisual();
    }

    private void ParView_PaintSurface(object? sender, SKPaintSurfaceEventArgs e)
    {
        float scale = DpiScale(ParView, e);
        var c = e.Surface.Canvas;
        c.Save();
        c.Scale(scale);
        var opt = new ParRenderOptions
        {
            RangeScaleNm = _vm.RangeScaleNm,
            LogRange = _vm.LogRange,
            ShowSafetyLimits = _vm.ShowSafetyLimits,
            ShowTerpsLateral = _vm.ShowTerpsLateral,
            ShowCourseWedge = _vm.ShowCourseWedge,
            MtiEnabled = _vm.Mti,
            ShowScan = _vm.ShowScan,
            HistoryDots = _vm.HistoryDots,
            Selected = _vm.SelectedCallsign,
            SelectedAdvisory = _vm.CurrentAdvisory,
        };
        _par.Render(c, e.Info.Width / scale, e.Info.Height / scale, _vm.Engine.Geometry, _vm.Engine.Radar, _vm.Engine.Now, opt);
        c.Restore();
    }

    private void PlanView_PaintSurface(object? sender, SKPaintSurfaceEventArgs e)
    {
        float scale = DpiScale(PlanView, e);
        var c = e.Surface.Canvas;
        c.Save();
        c.Scale(scale);
        var engine = _vm.Engine;
        _plan.Render(c, e.Info.Width / scale, e.Info.Height / scale, engine.Geometry, engine.Asr, engine.Airport,
            engine.Preset, engine.Targets, engine.Now, _vm.PlanRangeNm, _vm.SelectedCallsign, _vm.ShowAsrSweep);
        c.Restore();
    }

    private static float DpiScale(FrameworkElement el, SKPaintSurfaceEventArgs e) =>
        el.ActualWidth > 0 ? (float)(e.Info.Width / el.ActualWidth) : 1f;

    // ---- Selection ---------------------------------------------------------------------------------

    private void ParView_MouseDown(object sender, MouseButtonEventArgs e) => Pick(_par.Hits, e.GetPosition(ParView));
    private void PlanView_MouseDown(object sender, MouseButtonEventArgs e) => Pick(_plan.Hits, e.GetPosition(PlanView));

    private void Pick(IReadOnlyList<HitTarget> hits, Point p)
    {
        Keyboard.Focus(this);
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
        if (Keyboard.FocusedElement is System.Windows.Controls.TextBox) return;
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        switch (e.Key)
        {
            case Key.PageUp: _vm.StepRange(-1); break;
            case Key.PageDown: _vm.StepRange(+1); break;
            case Key.Tab: _vm.CycleSelection(); break;
            case Key.Escape: _vm.Select(null); break;
            case Key.Space:
                if (!e.IsRepeat) _vm.PressToTalk();
                break;
            case Key.D: _vm.BeginDescent(); break;
            case Key.Left: _vm.NudgeHeading(shift ? -5 : -1); break;
            case Key.Right: _vm.NudgeHeading(shift ? 5 : 1); break;
            default: return;
        }
        e.Handled = true;
    }

    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        base.OnPreviewKeyUp(e);
        if (e.Key == Key.Space && Keyboard.FocusedElement is not System.Windows.Controls.TextBox)
        {
            _vm.ReleaseToTalk();
            e.Handled = true;
        }
    }

    private void TypedBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { _vm.SendTypedCommand.Execute(null); e.Handled = true; }
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

    private void ImportNasr_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "NASR APT CSV (*.zip)|*.zip", Title = "Import FAA NASR APT CSV zip" };
        if (dlg.ShowDialog(this) == true) _vm.ImportNasr(dlg.FileName);
    }

    // ---- Layout & persistence ----------------------------------------------------------------------

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.ShowPlanView)) UpdateSidePanel();
    }

    private void UpdateSidePanel() => SideColumn.Width = _vm.ShowPlanView ? new GridLength(360) : new GridLength(0);

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
