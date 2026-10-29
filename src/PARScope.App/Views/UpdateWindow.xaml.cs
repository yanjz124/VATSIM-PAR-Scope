using System.Windows;
using System.Windows.Controls;
using PARScope.App.Services;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace PARScope.App.Views;

/// <summary>Offers a newer release with the notes of every release since the running one, newest first.</summary>
public partial class UpdateWindow : FluentWindow
{
    private UpdateWindow(ReleaseInfo release)
    {
        InitializeComponent();
        Heading.Text = $"vibe PAR {release.Version} is available (you have {Updater.DisplayVersion}). Update now? The scope restarts when it is done.";
        PluginNote.Text = Updater.InstalledBySetup
            ? "The vPilot plugin is updated too (close vPilot first if the plugin changed)."
            : "The vPilot plugin is not changed; if the notes say it was updated, copy it from the Plugin folder into vPilot.";
        var log = release.Changelog.Count > 0 ? release.Changelog : [(release.Version, release.Notes)];
        foreach (var (version, notes) in log)
        {
            Notes.Children.Add(new TextBlock { Text = version, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, Notes.Children.Count > 0 ? 14 : 0, 0, 4) });
            AddMarkdown(notes.Trim().Length > 0 ? notes : "(no notes)");
        }
    }

    /// <summary>The release notes' markdown as plain text: headings bold, bullets as bullets, emphasis dropped.</summary>
    private void AddMarkdown(string markdown)
    {
        foreach (var raw in markdown.Replace("\r", "").Split('\n'))
        {
            string line = raw.TrimEnd().Replace("**", "").Replace("`", "");
            if (line.Length == 0) continue;
            int indent = line.Length - line.TrimStart().Length;
            string text = line.TrimStart();
            if (text.StartsWith('#'))
            {
                Notes.Children.Add(new TextBlock { Text = text.TrimStart('#', ' '), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 2), TextWrapping = TextWrapping.Wrap });
                continue;
            }
            bool bullet = text.StartsWith("- ") || text.StartsWith("* ");
            Notes.Children.Add(new TextBlock
            {
                Text = bullet ? "•  " + text[2..] : text,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(bullet ? 8 + indent * 6 : 0, 1, 0, 1),
            });
        }
    }

    private void Update_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Later_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    /// <summary>Shows the offer; true to install.</summary>
    public static bool Ask(Window owner, ReleaseInfo release) =>
        new UpdateWindow(release) { Owner = owner }.ShowDialog() == true;
}
