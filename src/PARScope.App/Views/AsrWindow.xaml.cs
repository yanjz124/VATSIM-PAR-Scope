using System.Windows;
using System.Windows.Input;
using PARScope.App.Rendering;
using PARScope.App.Services;
using PARScope.App.ViewModels;
using SkiaSharp.Views.Desktop;
using Wpf.Ui.Controls;

namespace PARScope.App.Views;

/// <summary>The ASR scope in its own window, e.g. on a second monitor. Keys work as in the main window.</summary>
public partial class AsrWindow : FluentWindow
{
    private readonly MainViewModel _vm;
    private readonly AppSettings _settings;
    private readonly MainWindow _main;
    private readonly PlanRenderer _plan = new();

    public AsrWindow(MainViewModel vm, AppSettings settings, MainWindow main)
    {
        _vm = vm;
        _settings = settings;
        _main = main;
        InitializeComponent();
        DataContext = vm;
        Width = settings.AsrWindowWidth;
        Height = settings.AsrWindowHeight;
        if (settings.AsrWindowLeft is double l && settings.AsrWindowTop is double t
            && new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight)
                .IntersectsWith(new Rect(l, t, Width, Height)))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = l; Top = t;
        }
        else WindowStartupLocation = WindowStartupLocation.CenterOwner;
    }

    public void Invalidate() => PlanView.InvalidateVisual();

    private void PlanView_PaintSurface(object? sender, SKPaintSurfaceEventArgs e) =>
        _main.PaintPlan(_plan, PlanView, e);

    private void PlanView_MouseDown(object sender, MouseButtonEventArgs e) =>
        _main.PickFrom(_plan.Hits, e.GetPosition(PlanView));

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        _main.HandleScopeKey(e);
    }

    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        base.OnPreviewKeyUp(e);
        _main.HandleScopeKeyUp(e);
    }

    public void SavePlacement()
    {
        var r = RestoreBounds;
        _settings.AsrWindowLeft = r.Left; _settings.AsrWindowTop = r.Top;
        _settings.AsrWindowWidth = r.Width; _settings.AsrWindowHeight = r.Height;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        SavePlacement();
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _plan.Dispose();
        base.OnClosed(e);
    }
}
