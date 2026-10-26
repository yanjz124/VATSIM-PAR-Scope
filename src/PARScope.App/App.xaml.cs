using System.Windows;
using PARScope.App.Services;
using PARScope.App.ViewModels;
using PARScope.App.Views;

namespace PARScope.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Updater.CleanUpOldFiles();
        var settings = AppSettings.Load();
        var vm = new MainViewModel(settings, new ScopeEngine())
        {
            // "--demo [AIRPORT RWY]": fill the scope with self-flying training arrivals.
            DemoMode = e.Args.Contains("--demo", StringComparer.OrdinalIgnoreCase),
            DemoAirport = e.Args.SkipWhile(a => !a.Equals("--demo", StringComparison.OrdinalIgnoreCase)).Skip(1).FirstOrDefault(a => !a.StartsWith("--")),
            DemoRunway = e.Args.SkipWhile(a => !a.Equals("--demo", StringComparison.OrdinalIgnoreCase)).Skip(2).FirstOrDefault(a => !a.StartsWith("--")),
        };
        var window = new MainWindow(vm, settings);
        MainWindow = window;
        window.Show();
    }
}
