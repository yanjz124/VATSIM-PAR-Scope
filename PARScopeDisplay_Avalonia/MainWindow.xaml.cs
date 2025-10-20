using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using System.Threading.Tasks;
using PARScopeShared;

namespace PARScopeDisplay_Avalonia
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();

            // Load NASR data in background (demo)
            Task.Run(() =>
            {
                var loader = new NASRDataLoader();
                string err;
                // Try loading existing cache (no network here)
                bool ok = loader.TryLoadFromFile("/tmp/placeholder.zip", out err);
                Dispatcher.UIThread.Post(() =>
                {
                    var st = this.FindControl<TextBlock>("StatusText");
                    if (st != null)
                        st.Text = ok ? $"Loaded data from {loader.LastLoadedSource}" : "NASR data not loaded (no cache)";
                });
            });
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }
    }
}
