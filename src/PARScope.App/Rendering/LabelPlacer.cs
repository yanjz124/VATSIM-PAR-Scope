using SkiaSharp;

namespace PARScope.App.Rendering;

/// <summary>
/// Greedy data-block placement: each label tries positions around its target (NE, SE, NW, SW, then
/// progressively farther out) and takes the first that collides with nothing already placed and
/// stays on the scope. If every candidate collides, the least-overlapping one wins. A label keyed to a target
/// keeps last frame's position while it still fits, so it does not hop between spots as targets move.
/// </summary>
internal sealed class LabelPlacer
{
    private const float Gap = 10, Step = 14;
    private readonly List<SKRect> _taken = [];
    private readonly Dictionary<string, SKPoint> _lastOffset = [];
    private SKRect _bounds;

    public void Reset(SKRect bounds)
    {
        _taken.Clear();
        _bounds = bounds;
    }

    public void Reserve(SKRect r) => _taken.Add(r);

    /// <summary>Places a label for <paramref name="key"/>, keeping its previous spot (relative to the target) if that still fits.</summary>
    public SKRect Place(string key, float x, float y, float w, float h)
    {
        if (_lastOffset.TryGetValue(key, out var o))
        {
            var keep = SKRect.Create(x + o.X, y + o.Y, w, h);
            if (_bounds.Contains(keep) && !_taken.Any(t => !SKRect.Intersect(keep, t).IsEmpty))
            {
                _taken.Add(keep);
                return keep;
            }
        }
        var r = Place(x, y, w, h);
        _lastOffset[key] = new SKPoint(r.Left - x, r.Top - y);
        return r;
    }

    public SKRect Place(float x, float y, float w, float h)
    {
        SKRect best = default;
        float bestOverlap = float.MaxValue;
        foreach (var r in Candidates(x, y, w, h))
        {
            if (!_bounds.Contains(r)) continue;
            float overlap = 0;
            foreach (var t in _taken)
            {
                var i = SKRect.Intersect(r, t);
                if (!i.IsEmpty) overlap += i.Width * i.Height;
            }
            if (overlap == 0) { best = r; bestOverlap = 0; break; }
            if (overlap < bestOverlap) { best = r; bestOverlap = overlap; }
        }
        if (bestOverlap == float.MaxValue) best = SKRect.Create(x + Gap, y - Gap - h, w, h); // nothing fits on screen
        _taken.Add(best);
        return best;
    }

    private static IEnumerable<SKRect> Candidates(float x, float y, float w, float h)
    {
        for (int ring = 0; ring < 6; ring++)
        {
            float d = ring * Step;
            yield return SKRect.Create(x + Gap, y - Gap - h - d, w, h);       // NE
            yield return SKRect.Create(x + Gap, y + Gap + d, w, h);           // SE
            yield return SKRect.Create(x - Gap - w, y - Gap - h - d, w, h);   // NW
            yield return SKRect.Create(x - Gap - w, y + Gap + d, w, h);       // SW
            yield return SKRect.Create(x + Gap + d * 2, y - h / 2, w, h);     // E
        }
    }
}
