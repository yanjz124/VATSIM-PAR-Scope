using System.Windows;
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

    private void ImportNasr_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "NASR APT CSV (*.zip)|*.zip", Title = "Import FAA NASR APT CSV zip" };
        if (dlg.ShowDialog(this) == true) _vm.ImportNasr(dlg.FileName);
    }
}
