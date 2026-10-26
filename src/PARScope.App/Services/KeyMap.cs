using System.Windows.Input;
using PARScope.App.ViewModels;
using Emphasis = PARScope.App.ViewModels.MainViewModel.KeyEmphasis;

namespace PARScope.App.Services;

/// <summary>A key with its modifiers, e.g. Ctrl+R.</summary>
public readonly record struct KeyChord(Key Key, ModifierKeys Mods = ModifierKeys.None)
{
    public static readonly KeyChord None = new(Key.None);
    public bool IsNone => Key == Key.None;

    public override string ToString()
    {
        if (IsNone) return "";
        var parts = new List<string>();
        if (Mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (Mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (Mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        parts.Add(KeyName(Key));
        return string.Join('+', parts);
    }

    public static string KeyName(Key k) => k switch
    {
        >= Key.D0 and <= Key.D9 => ((char)('0' + (k - Key.D0))).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => "Num " + (k - Key.NumPad0),
        Key.Left => "←", Key.Right => "→", Key.Up => "↑", Key.Down => "↓",
        Key.PageUp => "PgUp", Key.PageDown => "PgDn", Key.Return => "Enter", Key.Escape => "Esc",
        Key.LeftCtrl => "Left Ctrl", Key.RightCtrl => "Right Ctrl", Key.LeftShift => "Left Shift",
        Key.RightShift => "Right Shift", Key.LeftAlt => "Left Alt", Key.RightAlt => "Right Alt",
        Key.Oem3 => "`", Key.OemMinus => "-", Key.OemPlus => "=", Key.OemComma => ",", Key.OemPeriod => ".",
        Key.Oem2 => "/", Key.Oem1 => ";", Key.Oem7 => "'", Key.Oem4 => "[", Key.Oem6 => "]", Key.Oem5 => "\\",
        _ => k.ToString(),
    };

    /// <summary>Settings form: "Control+Shift+R" style, WPF names.</summary>
    public string Serialize() => IsNone ? "" : Mods == ModifierKeys.None ? Key.ToString() : $"{Mods.ToString().Replace(", ", "+")}+{Key}";

    public static KeyChord Parse(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return None;
        var parts = s.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var mods = ModifierKeys.None;
        foreach (var p in parts[..^1])
            if (Enum.TryParse<ModifierKeys>(p, true, out var m)) mods |= m;
        return Enum.TryParse<Key>(parts[^1], true, out var k) ? new KeyChord(k, mods) : None;
    }
}

/// <summary>
/// Something a hotkey does. Deviation commands (glidepath and course) ignore the modifiers in their binding:
/// Ctrl makes the call "slightly…", Shift "well…", Alt "going…".
/// </summary>
public sealed record KeyCommand(string Id, string Group, string Label, KeyChord Default, KeyChord DefaultAlt, bool AnyModifiers, Action<MainViewModel, Emphasis> Run)
{
    public KeyCommand(string id, string group, string label, KeyChord def, Action<MainViewModel> run, KeyChord alt = default)
        : this(id, group, label, def, alt, false, (vm, _) => run(vm)) { }
}

/// <summary>
/// The scope's hotkeys: defaults, the user's changes (kept in settings) and lookup. The defaults are laid out
/// like a game: left hand on WASD for the deviation calls and Q/E for turns, the number row for the
/// approach in the order it is flown, F to send the suggested call.
/// </summary>
public sealed class KeyMap
{
    private static KeyChord K(Key k, ModifierKeys m = ModifierKeys.None) => new(k, m);
    private const ModifierKeys Shift = ModifierKeys.Shift;
    private const string Mods = " (Ctrl slightly… · Shift well… · Alt going…)";

    private static KeyCommand Glide(string id, string label, Key k, Key alt, int side) =>
        new(id, "Glidepath" + Mods, label, K(k), K(alt), true, (vm, e) => vm.GlidepathKey(side, e));
    private static KeyCommand Course(string id, string label, Key k, Key alt, int side) =>
        new(id, "Course" + Mods, label, K(k), K(alt), true, (vm, e) => vm.CourseKey(side, e));

    /// <summary>Every hotkey command, in the order the reference shows them.</summary>
    public static readonly IReadOnlyList<KeyCommand> Commands =
    [
        new("send_suggested", "Radio", "Send the suggested call (with the right trend)", K(Key.F), vm => vm.SaySuggestedCall(), K(Key.Enter)),
        new("heading_left", "Radio", "Turn left 1°", K(Key.Q), vm => vm.NudgeHeading(-1), K(Key.Left)),
        new("heading_right", "Radio", "Turn right 1°", K(Key.E), vm => vm.NudgeHeading(1), K(Key.Right)),
        new("heading_left5", "Radio", "Turn left 5°", K(Key.Q, Shift), vm => vm.NudgeHeading(-5), K(Key.Left, Shift)),
        new("heading_right5", "Radio", "Turn right 5°", K(Key.E, Shift), vm => vm.NudgeHeading(5), K(Key.Right, Shift)),
        new("range", "Radio", "Range from touchdown", K(Key.R), vm => vm.SayRange()),
        new("wheels", "Radio", "Approaching glidepath, wheels should be down", K(Key.G), vm => vm.KeyCall("APPROACHING GLIDEPATH, WHEELS SHOULD BE DOWN")),
        new("radio_check", "Radio", "How do you hear me", K(Key.H), vm => vm.KeyCall("HOW DO YOU HEAR ME")),

        Glide("gp_above", "Above glidepath", Key.W, Key.Up, 1),
        Glide("gp_below", "Below glidepath", Key.S, Key.Down, -1),
        Glide("gp_on", "On glidepath", Key.X, Key.None, 0),

        Course("crs_left", "Left of course", Key.A, Key.None, -1),
        Course("crs_right", "Right of course", Key.D, Key.None, 1),
        Course("crs_on", "On course", Key.C, Key.None, 0),

        new("approach_step", "The approach, in order", "Approach: radar contact; after a missed approach, coordinate, then contact approach", K(Key.D1), vm => vm.ApproachStep()),
        new("approach_brief", "The approach, in order", "Approach brief: PAR to runway, missed approach and lost comms instructions", K(Key.D2), vm => vm.SayApproachBrief()),
        new("begin_descent", "The approach, in order", "Begin descent", K(Key.D3), vm => vm.BeginDescent()),
        new("do_not_ack", "The approach, in order", "Do not acknowledge further transmissions", K(Key.D4), vm => vm.KeyCall("DO NOT ACKNOWLEDGE FURTHER TRANSMISSIONS")),
        new("tower_step", "The approach, in order", "Tower: request the clearance from local, then relay it", K(Key.D5), vm => vm.TowerStep(), K(Key.T)),
        new("contact_tower_after", "The approach, in order", "Contact tower after landing", K(Key.D6), vm => vm.KeyCall("CONTACT TOWER AFTER LANDING")),

        new("execute_missed", "Missed approach and rate", "Execute missed approach", K(Key.M), vm => vm.KeyCall("EXECUTE MISSED APPROACH")),
        new("missed_if_not_in_sight", "Missed approach and rate", "If runway not in sight, execute missed approach", K(Key.M, Shift), vm => vm.KeyCall("IF RUNWAY OR APPROACH LIGHTS NOT IN SIGHT, EXECUTE MISSED APPROACH")),
        new("level_off", "Missed approach and rate", "Level off", K(Key.V), vm => vm.KeyCall("LEVEL OFF YOUR AIRCRAFT, ACKNOWLEDGE")),
        new("resume_descent", "Missed approach and rate", "Resume normal rate of descent", K(Key.V, Shift), vm => vm.KeyCall("RESUME NORMAL RATE OF DESCENT")),

        new("next_target", "Scope", "Next target", K(Key.Tab), vm => vm.CycleSelection()),
        new("deselect", "Scope", "Deselect target", K(Key.Escape), vm => vm.Select(null)),
        new("range_in", "Scope", "Shorter range scale", K(Key.PageUp), vm => vm.StepRange(-1)),
        new("range_out", "Scope", "Longer range scale", K(Key.PageDown), vm => vm.StepRange(+1)),
        new("flip", "Scope", "Flip PAR / ASR", K(Key.Z), vm => vm.FlipScope()),
        new("pause", "Scope", "Pause / resume training traffic", K(Key.P), vm => vm.TogglePause()),
        new("show_keys", "Scope", "Show key assignments", K(Key.F1), vm => vm.ShowKeyAssignments()),
    ];

    private readonly AppSettings _settings;
    private readonly Dictionary<string, (KeyChord Primary, KeyChord Alt)> _bindings = new();

    public KeyMap(AppSettings settings)
    {
        _settings = settings;
        foreach (var c in Commands)
        {
            _bindings[c.Id] = settings.KeyBindings.TryGetValue(c.Id, out var s) && s.Split('|') is { Length: 2 } p
                ? (KeyChord.Parse(p[0]), KeyChord.Parse(p[1]))
                : (c.Default, c.DefaultAlt);
        }
    }

    public event Action? Changed;

    public KeyChord Primary(KeyCommand c) => _bindings[c.Id].Primary;
    public KeyChord Alternate(KeyCommand c) => _bindings[c.Id].Alt;

    /// <summary>How a command's keys are written, e.g. "1 / Num 1"; "" when unbound.</summary>
    public string Text(string id)
    {
        var (p, a) = _bindings[id];
        return string.Join(" / ", new[] { p, a }.Where(k => !k.IsNone).Select(k => k.ToString()));
    }

    /// <summary>The command a key press runs: an exact match first, then a deviation command on that key with any modifiers.</summary>
    public KeyCommand? Find(Key key, ModifierKeys mods)
    {
        var chord = new KeyChord(key, mods & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift));
        return Commands.FirstOrDefault(c => Bound(c).Contains(chord))
            ?? Commands.FirstOrDefault(c => c.AnyModifiers && Bound(c).Any(b => b.Key == key));
    }

    private IEnumerable<KeyChord> Bound(KeyCommand c) => new[] { _bindings[c.Id].Primary, _bindings[c.Id].Alt }.Where(k => !k.IsNone);

    /// <summary>Two bindings collide if they share a key and either a deviation command (any modifiers) or the same modifiers.</summary>
    private static bool Clash(KeyChord a, bool aAny, KeyChord b, bool bAny) =>
        !a.IsNone && !b.IsNone && a.Key == b.Key && (aAny || bAny || a.Mods == b.Mods);

    /// <summary>
    /// Binds a key to a command (primary or alternate slot). Any other command using it loses it; their labels
    /// are returned so the user can see what changed.
    /// </summary>
    public IReadOnlyList<string> Set(KeyCommand cmd, bool alternate, KeyChord chord)
    {
        if (cmd.AnyModifiers) chord = chord with { Mods = ModifierKeys.None };
        var taken = new List<string>();
        foreach (var other in Commands)
        {
            var (p, a) = _bindings[other.Id];
            bool self = other.Id == cmd.Id;
            if ((!self || alternate) && Clash(chord, cmd.AnyModifiers, p, other.AnyModifiers)) { p = KeyChord.None; if (!self) taken.Add(other.Label); }
            if ((!self || !alternate) && Clash(chord, cmd.AnyModifiers, a, other.AnyModifiers)) { a = KeyChord.None; if (!self) taken.Add(other.Label); }
            _bindings[other.Id] = (p, a);
        }
        var cur = _bindings[cmd.Id];
        _bindings[cmd.Id] = alternate ? (cur.Primary, chord) : (chord, cur.Alt);
        Save();
        return taken;
    }

    /// <summary>Commands whose key (with no modifiers) is the push-to-talk key, which takes precedence.</summary>
    public IReadOnlyList<string> ShadowedBy(Key ptt) =>
        Commands.Where(c => Bound(c).Any(b => b.Key == ptt && (c.AnyModifiers || b.Mods == ModifierKeys.None))).Select(c => c.Label).ToList();

    public void ResetAll()
    {
        foreach (var c in Commands) _bindings[c.Id] = (c.Default, c.DefaultAlt);
        Save();
    }

    private void Save()
    {
        _settings.KeyBindings.Clear();
        foreach (var c in Commands)
        {
            var b = _bindings[c.Id];
            if (b != (c.Default, c.DefaultAlt)) _settings.KeyBindings[c.Id] = $"{b.Primary.Serialize()}|{b.Alt.Serialize()}";
        }
        Changed?.Invoke();
    }
}
