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
        // Whisper picks its native library once per run: the GPU (Vulkan) if allowed and it loads, else the CPU.
        Whisper.net.LibraryLoader.RuntimeOptions.RuntimeLibraryOrder = settings.SpeechGpu
            ? [Whisper.net.LibraryLoader.RuntimeLibrary.Vulkan, Whisper.net.LibraryLoader.RuntimeLibrary.Cpu, Whisper.net.LibraryLoader.RuntimeLibrary.CpuNoAvx]
            : [Whisper.net.LibraryLoader.RuntimeLibrary.Cpu, Whisper.net.LibraryLoader.RuntimeLibrary.CpuNoAvx];
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
