using SkiaSharp;

namespace PARScope.App.Rendering;

/// <summary>
/// Greedy data-block placement: each label tries positions around its target (NE, SE, NW, SW, then
/// progressively farther out) and takes the first that collides with nothing already placed and
/// stays on the scope. If every candidate collides, the least-overlapping one wins.
/// </summary>
internal sealed class LabelPlacer
{
    private const float Gap = 10, Step = 14;
    private readonly List<SKRect> _taken = [];
    private SKRect _bounds;

    public void Reset(SKRect bounds)
    {
        _taken.Clear();
        _bounds = bounds;
    }

    public void Reserve(SKRect r) => _taken.Add(r);

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
