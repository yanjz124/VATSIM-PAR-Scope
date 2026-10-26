using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PARScope.App.Services;
using PARScope.App.ViewModels;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;

namespace PARScope.App.Views;

/// <summary>Every hotkey, grouped as on the scope: a reference to keep open, and where keys are changed.</summary>
public partial class KeysWindow : FluentWindow
{
    private readonly MainViewModel _vm;
    private readonly List<Action> _refresh = new();
    /// <summary>The key button waiting for a key press; Command null = push-to-talk.</summary>
    private (KeyCommand? Command, bool Alternate, Button Button)? _capture;

    public KeysWindow(MainViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        DataContext = vm;
        Build();
        _vm.Keys.Changed += Refresh;
        Closed += (_, _) => _vm.Keys.Changed -= Refresh;
    }

    private void Build()
    {
        AddHeader("Radio");
        var ptt = KeyButton(() => _vm.PttKeyText, () => _capture = (null, false, null!));
        AddRow("Push to talk (hold)", ptt, null);
        string group = "Radio";
        foreach (var c in KeyMap.Commands)
        {
            if (c.Group != group) AddHeader(group = c.Group);
            AddRow(c.Label,
                KeyButton(() => Text(_vm.Keys.Primary(c)), () => _capture = (c, false, null!)),
                KeyButton(() => Text(_vm.Keys.Alternate(c)), () => _capture = (c, true, null!)));
        }
    }

    private static string Text(KeyChord k) => k.IsNone ? "—" : k.ToString();

    /// <summary>A group heading; a note in brackets ("Glidepath (Shift …)") goes on its own line below.</summary>
    private void AddHeader(string text)
    {
        int open = text.IndexOf(" (", StringComparison.Ordinal);
        string name = open < 0 ? text : text[..open];
        Rows.Children.Add(new TextBlock { Text = name.ToUpperInvariant(), Style = (Style)FindResource("SectionLabel"), Margin = new Thickness(0, 12, 0, open < 0 ? 4 : 0) });
        if (open >= 0)
            Rows.Children.Add(new TextBlock { Text = text[(open + 2)..^1], Style = (Style)FindResource("MetricLabel"), Margin = new Thickness(0, 0, 0, 4), TextWrapping = TextWrapping.Wrap });
    }

    private void AddRow(string label, Button primary, Button? alternate)
    {
        var row = new Grid { Margin = new Thickness(0, 1, 0, 1) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 8, 0) });
        Grid.SetColumn(primary, 1);
        row.Children.Add(primary);
        if (alternate != null) { Grid.SetColumn(alternate, 3); row.Children.Add(alternate); }
        Rows.Children.Add(row);
    }

    private Button KeyButton(Func<string> text, Action arm)
    {
        var b = new Button { HorizontalAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(6, 3, 6, 3), Focusable = false };
        _refresh.Add(() => b.Content = text());
        b.Content = text();
        b.Click += (_, _) =>
        {
            Refresh();
            arm();
            _capture = _capture!.Value with { Button = b };
            b.Content = "Press a key…";
            Status.Text = "Esc cancels · Backspace clears";
        };
        return b;
    }

    private void Refresh()
    {
        foreach (var r in _refresh) r();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (_capture is not { } cap)
        {
            if (key == Key.Escape) Close();
            return;
        }
        e.Handled = true;
        bool modifierOnly = key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin;
        if (cap.Command == null)
        {
            // Push-to-talk can be any single key, modifiers included.
            if (key != Key.Escape)
            {
                _vm.PttKey = key;
                var shadowed = _vm.Keys.ShadowedBy(key);
                Status.Text = shadowed.Count > 0 ? $"Push-to-talk now takes {_vm.PttKeyText} from: {string.Join(", ", shadowed)}." : "";
            }
            else Status.Text = "";
            _capture = null;
            Refresh();
            return;
        }
        if (modifierOnly) return; // wait for the key itself
        _capture = null;
        var cmd = cap.Command;
        if (key == Key.Escape) { Status.Text = ""; Refresh(); return; }
        if (key is Key.Back or Key.Delete)
        {
            _vm.Keys.Set(cmd, cap.Alternate, KeyChord.None);
            Status.Text = $"{cmd.Label}: key cleared.";
            Refresh();
            return;
        }
        var mods = Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift);
        if (key == _vm.PttKey && (cmd.AnyModifiers || mods == ModifierKeys.None))
        {
            Status.Text = $"{_vm.PttKeyText} is the push-to-talk key.";
            Refresh();
            return;
        }
        var taken = _vm.Keys.Set(cmd, cap.Alternate, new KeyChord(key, mods));
        Status.Text = taken.Count > 0 ? $"Removed from: {string.Join(", ", taken)}." : "";
        Refresh();
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        _capture = null;
        _vm.Keys.ResetAll();
        Status.Text = "Hotkeys reset to the defaults.";
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
