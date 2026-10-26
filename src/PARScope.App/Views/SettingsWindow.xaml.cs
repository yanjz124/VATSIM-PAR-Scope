using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using PARScope.App.ViewModels;
using Wpf.Ui.Controls;

namespace PARScope.App.Views;

/// <summary>Configuration that is set once and left alone: siting, display, data sources, training aids, audio.</summary>
public partial class SettingsWindow : FluentWindow
{
    private readonly MainViewModel _vm;

    public SettingsWindow(MainViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        DataContext = vm;
    }

    private bool _capturingPtt;

    private void PttButton_Click(object sender, RoutedEventArgs e)
    {
        _capturingPtt = true;
        PttButton.Content = "Press a key…";
        PttHint.Text = "Esc to cancel";
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (!_capturingPtt) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key != Key.Escape) _vm.PttKey = key;
        _capturingPtt = false;
        PttButton.Content = _vm.PttKeyText;
        PttHint.Text = "";
        e.Handled = true;
    }

    private void Keys_Click(object sender, RoutedEventArgs e) => _vm.ShowKeyAssignments();

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        void Say(string s) { UpdateStatus.Text = s; UpdateStatus2.Text = s; }
        Say("Checking…");
        var release = await _vm.CheckForUpdateAsync(force: true);
        if (release == null) { Say($"Up to date ({PARScope.App.Services.Updater.DisplayVersion})"); return; }
        Say($"{release.Version} available");
        var answer = System.Windows.MessageBox.Show(this, $"vibe PAR {release.Version} is available (you have {PARScope.App.Services.Updater.DisplayVersion}).\n\nUpdate now? The scope restarts when it is done.",
            "Update available", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Information);
        if (answer == System.Windows.MessageBoxResult.Yes) { Say("Installing…"); await _vm.InstallUpdateAsync(release); }
    }

    private void ImportNasr_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "NASR APT CSV (*.zip)|*.zip", Title = "Import FAA NASR APT CSV zip" };
        if (dlg.ShowDialog(this) == true) _vm.ImportNasr(dlg.FileName);
    }
}
