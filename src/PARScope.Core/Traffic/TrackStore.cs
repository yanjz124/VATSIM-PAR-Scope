namespace PARScope.Core.Traffic;

/// <summary>Thread-safe set of tracks. Network threads call <see cref="Apply"/>; the render loop calls <see cref="Snapshot"/>.</summary>
public sealed class TrackStore
{
    /// <summary>Tracks with no report for this long are dropped (vPilot normally deletes them first).</summary>
    public const double StaleSec = 45.0;

    private readonly Dictionary<string, TargetTrack> _tracks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();

    public void Apply(TrafficMessage msg)
    {
        lock (_lock)
        {
            switch (msg)
            {
                case PositionReport r:
                    if (!_tracks.TryGetValue(r.Callsign, out var t))
                        _tracks[r.Callsign] = t = new TargetTrack(r.Callsign);
                    t.Apply(r);
                    break;
                case TargetRemoved d:
                    _tracks.Remove(d.Callsign);
                    break;
                case AllTargetsRemoved:
                    _tracks.Clear();
                    break;
            }
        }
    }

    public void RemoveWhere(Func<TargetTrack, bool> predicate)
    {
        lock (_lock)
        {
            foreach (var key in _tracks.Where(kv => predicate(kv.Value)).Select(kv => kv.Key).ToList())
                _tracks.Remove(key);
        }
    }

    public IReadOnlyList<TargetTrack> Snapshot(double now)
    {
        lock (_lock)
        {
            foreach (var key in _tracks.Where(kv => kv.Value.Age(now) > StaleSec).Select(kv => kv.Key).ToList())
                _tracks.Remove(key);
            return _tracks.Values.ToList();
        }
    }
}
