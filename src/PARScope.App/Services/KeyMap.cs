using System.Windows.Input;
using PARScope.App.ViewModels;
using Trend = PARScope.App.ViewModels.MainViewModel.KeyTrend;

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
/// Something a hotkey does. Trend commands (glidepath and course calls) ignore the modifiers in their binding:
/// Shift adds "…and correcting", Ctrl "…and holding", Alt makes it "going…".
/// </summary>
public sealed record KeyCommand(string Id, string Group, string Label, KeyChord Default, KeyChord DefaultAlt, bool Trend, Action<MainViewModel, Trend> Run)
{
    public KeyCommand(string id, string group, string label, KeyChord def, Action<MainViewModel> run)
        : this(id, group, label, def, KeyChord.None, false, (vm, _) => run(vm)) { }
}

/// <summary>The scope's hotkeys: defaults, the user's changes (kept in settings) and lookup.</summary>
public sealed class KeyMap
{
    private static KeyChord K(Key k, ModifierKeys m = ModifierKeys.None) => new(k, m);
    private const ModifierKeys Shift = ModifierKeys.Shift, Ctrl = ModifierKeys.Control;

    private static KeyCommand Glide(string id, string label, Key k, int level) =>
        new(id, "Glidepath (Shift …and correcting · Ctrl …and holding · Alt going…)", label, K(k), KeyChord.None, true, (vm, t) => vm.GlidepathKey(level, t));
    private static KeyCommand Course(string id, string label, int n, int level) =>
        new(id, "Course (Shift …and correcting · Ctrl …and holding · Alt going…)", label, K(Key.D0 + n), K(Key.NumPad0 + n), true, (vm, t) => vm.CourseKey(level, t));
    private static KeyCommand Call(string id, string label, Key k, string call, ModifierKeys m = ModifierKeys.None) =>
        new(id, "Calls", label, K(k, m), vm => vm.KeyCall(call));

    /// <summary>Every hotkey command, in the order the reference shows them.</summary>
    public static readonly IReadOnlyList<KeyCommand> Commands =
    [
        new("send_suggested", "Radio", "Send the suggested call", K(Key.Enter), vm => vm.SaySuggestedCall()),
        new("heading_left", "Radio", "Turn left 1°", K(Key.Left), vm => vm.NudgeHeading(-1)),
        new("heading_right", "Radio", "Turn right 1°", K(Key.Right), vm => vm.NudgeHeading(1)),
        new("heading_left5", "Radio", "Turn left 5°", K(Key.Left, Shift), vm => vm.NudgeHeading(-5)),
        new("heading_right5", "Radio", "Turn right 5°", K(Key.Right, Shift), vm => vm.NudgeHeading(5)),
        new("begin_descent", "Radio", "Begin descent", K(Key.D), vm => vm.BeginDescent()),
        new("range", "Radio", "Range from touchdown", K(Key.R), vm => vm.SayRange()),

        Glide("gp_well_above", "Well above glidepath", Key.E, 3),
        Glide("gp_above", "Above glidepath", Key.W, 2),
        Glide("gp_slightly_above", "Slightly above glidepath", Key.Q, 1),
        Glide("gp_on", "On glidepath", Key.A, 0),
        Glide("gp_slightly_below", "Slightly below glidepath", Key.Z, -1),
        Glide("gp_below", "Below glidepath", Key.X, -2),
        Glide("gp_well_below", "Well below glidepath", Key.C, -3),

        Course("crs_well_left", "Well left of course", 1, -3),
        Course("crs_left", "Left of course", 2, -2),
        Course("crs_slightly_left", "Slightly left of course", 3, -1),
        Course("crs_on", "On course", 4, 0),
        Course("crs_slightly_right", "Slightly right of course", 5, 1),
        Course("crs_right", "Right of course", 6, 2),
        Course("crs_well_right", "Well right of course", 7, 3),

        Call("radio_check", "How do you hear me", Key.H, "HOW DO YOU HEAR ME"),
        new("approach_brief", "Calls", "Approach brief (PAR, runway, minimums)", K(Key.B), vm => vm.SayApproachBrief()),
        new("lost_comms", "Calls", "Lost communications instructions", K(Key.L), vm => vm.SayLostComms()),
        new("missed_instructions", "Calls", "Missed approach instructions", K(Key.K), vm => vm.SayMissedInstructions()),
        Call("do_not_ack", "Do not acknowledge further transmissions", Key.N, "DO NOT ACKNOWLEDGE FURTHER TRANSMISSIONS"),
        Call("wheels", "Approaching glidepath, wheels should be down", Key.G, "APPROACHING GLIDEPATH, WHEELS SHOULD BE DOWN"),
        Call("level_off", "Level off", Key.V, "LEVEL OFF YOUR AIRCRAFT, ACKNOWLEDGE"),
        Call("resume_descent", "Resume normal rate of descent", Key.V, "RESUME NORMAL RATE OF DESCENT", Shift),
        Call("execute_missed", "Execute missed approach", Key.M, "EXECUTE MISSED APPROACH"),
        Call("missed_if_not_in_sight", "If runway not in sight, execute missed approach", Key.M, "IF RUNWAY OR APPROACH LIGHTS NOT IN SIGHT, EXECUTE MISSED APPROACH", Shift),
        new("relay_clearance", "Calls", "Relay tower's clearance", K(Key.Y), vm => vm.SayTowerClearance()),
        Call("contact_tower_after", "Contact tower after landing", Key.U, "CONTACT TOWER AFTER LANDING"),

        new("tower_request", "Coordination", "Request clearance from tower", K(Key.T), vm => vm.RequestTowerCommand.Execute(null)),
        new("radar_contact", "Coordination", "Radar contact (accept handoff)", K(Key.R, Ctrl), vm => vm.AcceptHandoffCommand.Execute(null)),
        new("coordinate_missed", "Coordination", "Coordinate missed approach with approach", K(Key.M, Ctrl), vm => vm.CoordinateMissedCommand.Execute(null)),
        new("contact_approach", "Coordination", "Contact approach (switch the pilot)", K(Key.J), vm => vm.ContactApproachCommand.Execute(null)),

        new("range_in", "Scope", "Shorter range scale", K(Key.PageUp), vm => vm.StepRange(-1)),
        new("range_out", "Scope", "Longer range scale", K(Key.PageDown), vm => vm.StepRange(+1)),
        new("next_target", "Scope", "Next target", K(Key.Tab), vm => vm.CycleSelection()),
        new("deselect", "Scope", "Deselect target", K(Key.Escape), vm => vm.Select(null)),
        new("pause", "Scope", "Pause / resume training traffic", K(Key.P), vm => vm.TogglePause()),
        new("flip", "Scope", "Flip PAR / ASR", K(Key.F), vm => vm.FlipScope()),
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

    /// <summary>The command a key press runs: an exact match first, then a trend command on that key with any modifiers.</summary>
    public KeyCommand? Find(Key key, ModifierKeys mods)
    {
        var chord = new KeyChord(key, mods & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift));
        return Commands.FirstOrDefault(c => Bound(c).Contains(chord))
            ?? Commands.FirstOrDefault(c => c.Trend && Bound(c).Any(b => b.Key == key));
    }

    private IEnumerable<KeyChord> Bound(KeyCommand c) => new[] { _bindings[c.Id].Primary, _bindings[c.Id].Alt }.Where(k => !k.IsNone);

    /// <summary>Two bindings collide if they share a key and either a trend command (any modifiers) or the same modifiers.</summary>
    private static bool Clash(KeyChord a, bool aTrend, KeyChord b, bool bTrend) =>
        !a.IsNone && !b.IsNone && a.Key == b.Key && (aTrend || bTrend || a.Mods == b.Mods);

    /// <summary>
    /// Binds a key to a command (primary or alternate slot). Any other command using it loses it; their labels
    /// are returned so the user can see what changed.
    /// </summary>
    public IReadOnlyList<string> Set(KeyCommand cmd, bool alternate, KeyChord chord)
    {
        if (cmd.Trend) chord = chord with { Mods = ModifierKeys.None };
        var taken = new List<string>();
        foreach (var other in Commands)
        {
            var (p, a) = _bindings[other.Id];
            bool self = other.Id == cmd.Id;
            if ((!self || alternate) && Clash(chord, cmd.Trend, p, other.Trend)) { p = KeyChord.None; if (!self) taken.Add(other.Label); }
            if ((!self || !alternate) && Clash(chord, cmd.Trend, a, other.Trend)) { a = KeyChord.None; if (!self) taken.Add(other.Label); }
            _bindings[other.Id] = (p, a);
        }
        var cur = _bindings[cmd.Id];
        _bindings[cmd.Id] = alternate ? (cur.Primary, chord) : (chord, cur.Alt);
        Save();
        return taken;
    }

    /// <summary>Commands whose key (with no modifiers) is the push-to-talk key, which takes precedence.</summary>
    public IReadOnlyList<string> ShadowedBy(Key ptt) =>
        Commands.Where(c => Bound(c).Any(b => b.Key == ptt && (c.Trend || b.Mods == ModifierKeys.None))).Select(c => c.Label).ToList();

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
