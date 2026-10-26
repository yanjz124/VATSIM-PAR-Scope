using System.IO.Compression;
using System.Text.Json;

namespace PARScope.Core.Data;

/// <summary>
/// Local airport/runway cache merged from OurAirports (worldwide) and FAA NASR (US, richer data;
/// wins where both describe the same airport). Stored gzip-compressed JSON in the app data folder.
/// </summary>
public sealed class AirportDatabase
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
    private readonly string _cachePath;
    private Dictionary<string, AirportRecord> _byId = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, AirportRecord> _byIcao = new(StringComparer.OrdinalIgnoreCase);

    public AirportDatabase(string dataFolder)
    {
        _cachePath = Path.Combine(dataFolder, "airports.json.gz");
    }

    /// <summary>Bump when AirportRecord/RunwayEnd gain fields, so old caches are rebuilt.</summary>
    private const int SchemaVersion = 2;

    public int Count => _byId.Count;

    /// <summary>The cache is from an older schema or older than one 28-day NASR cycle.</summary>
    public bool IsStale { get; private set; }
    public DateTime? UpdatedUtc { get; private set; }
    public string Sources { get; private set; } = "";

    public bool TryLoadCache()
    {
        if (!File.Exists(_cachePath)) return false;
        try
        {
            using var fs = File.OpenRead(_cachePath);
            using var gz = new GZipStream(fs, CompressionMode.Decompress);
            var cache = JsonSerializer.Deserialize<CacheFile>(gz, JsonOptions);
            if (cache?.Airports == null) return false;
            Index(cache.Airports);
            UpdatedUtc = cache.UpdatedUtc;
            Sources = cache.Sources ?? "";
            IsStale = cache.Schema < SchemaVersion || UpdatedUtc is not { } u || DateTime.UtcNow - u > TimeSpan.FromDays(28);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidDataException)
        {
            return false;
        }
    }

    /// <summary>Downloads both sources, merges and caches them. Either source may fail independently.</summary>
    public async Task<string> UpdateAsync(HttpClient http, IProgress<string>? progress, CancellationToken ct)
    {
        var notes = new List<string>();
        List<AirportRecord> world = [], nasr = [];

        try
        {
            progress?.Report("Downloading OurAirports (worldwide)…");
            var airports = await http.GetStringAsync(OurAirportsLoader.AirportsUrl, ct);
            var runways = await http.GetStringAsync(OurAirportsLoader.RunwaysUrl, ct);
            progress?.Report("Parsing OurAirports…");
            world = await Task.Run(() => OurAirportsLoader.Parse(new StringReader(airports), new StringReader(runways)), ct);
            notes.Add($"OurAirports {world.Count:N0}");
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            notes.Add("OurAirports failed: " + ex.Message);
        }

        foreach (var (cycle, url) in NasrLoader.CandidateUrls(DateTime.UtcNow))
        {
            try
            {
                progress?.Report($"Downloading FAA NASR cycle {cycle:dd MMM yyyy}…");
                using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!resp.IsSuccessStatusCode) continue;
                var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
                progress?.Report("Parsing FAA NASR…");
                nasr = await Task.Run(() => NasrLoader.ParseZip(new MemoryStream(bytes)), ct);
                notes.Add($"NASR {cycle:dd MMM yyyy} {nasr.Count:N0}");
                break;
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or IOException or FormatException
                                       or TaskCanceledException && !ct.IsCancellationRequested)
            {
                // Try the previous cycle rather than giving up.
                notes.Add($"NASR {cycle:dd MMM yyyy} failed: " + ex.Message);
            }
        }

        if (world.Count == 0 && nasr.Count == 0)
            return "Airport update failed. " + string.Join("; ", notes);

        var merged = Merge(world, nasr);
        Index(merged);
        UpdatedUtc = DateTime.UtcNow;
        IsStale = false;
        Sources = string.Join(", ", notes.Where(n => !n.Contains("failed")));
        SaveCache(merged);
        return $"Loaded {merged.Count:N0} airports ({string.Join("; ", notes)}).";
    }

    /// <summary>Loads a manually downloaded NASR APT CSV zip.</summary>
    public string ImportNasrZip(string path)
    {
        using var fs = File.OpenRead(path);
        var nasr = NasrLoader.ParseZip(fs);
        var merged = Merge(_byId.Values.Where(a => a.Source != "NASR").ToList(), nasr);
        Index(merged);
        UpdatedUtc = DateTime.UtcNow;
        Sources = (Sources.Length > 0 ? Sources + ", " : "") + "NASR (file)";
        SaveCache(merged);
        return $"Imported {nasr.Count:N0} NASR airports.";
    }

    public AirportRecord? Find(string id)
    {
        id = id.Trim();
        if (_byId.TryGetValue(id, out var a) || _byIcao.TryGetValue(id, out a)) return a;
        // US convenience: "NKX" ↔ "KNKX".
        if (id.Length == 3 && _byIcao.TryGetValue("K" + id, out a)) return a;
        if (id.Length == 4 && id[0] is 'K' or 'k' && _byId.TryGetValue(id[1..], out a)) return a;
        return null;
    }

    public IReadOnlyList<AirportRecord> Search(string query, int max = 30)
    {
        query = query.Trim();
        if (query.Length == 0) return [];
        var exact = Find(query);
        var results = _byId.Values
            .Where(a => a != exact && (a.Id.StartsWith(query, StringComparison.OrdinalIgnoreCase)
                        || (a.Icao?.StartsWith(query, StringComparison.OrdinalIgnoreCase) ?? false)
                        || (query.Length >= 3 && a.Name.Contains(query, StringComparison.OrdinalIgnoreCase))))
            .OrderBy(a => a.Id.Length)
            .ThenBy(a => a.Id, StringComparer.OrdinalIgnoreCase)
            .Take(max);
        return exact != null ? [exact, .. results] : results.ToList();
    }

    private static List<AirportRecord> Merge(List<AirportRecord> world, List<AirportRecord> nasr)
    {
        var result = new Dictionary<string, AirportRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in world) result[a.Icao ?? a.Id] = a;
        foreach (var a in nasr)
        {
            // NASR keys by FAA LID; drop the OurAirports duplicate filed under the ICAO code.
            if (a.Icao != null) result.Remove(a.Icao);
            result.Remove(a.Id);
            result[a.Id] = a;
        }
        return result.Values.ToList();
    }

    private void Index(IEnumerable<AirportRecord> airports)
    {
        var byId = new Dictionary<string, AirportRecord>(StringComparer.OrdinalIgnoreCase);
        var byIcao = new Dictionary<string, AirportRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in airports)
        {
            byId[a.Id] = a;
            if (!string.IsNullOrEmpty(a.Icao)) byIcao.TryAdd(a.Icao, a);
        }
        _byId = byId;
        _byIcao = byIcao;
    }

    private void SaveCache(List<AirportRecord> airports)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
        string tmp = _cachePath + ".tmp";
        using (var fs = File.Create(tmp))
        using (var gz = new GZipStream(fs, CompressionLevel.Optimal))
            JsonSerializer.Serialize(gz, new CacheFile(UpdatedUtc, Sources, airports, SchemaVersion), JsonOptions);
        File.Move(tmp, _cachePath, overwrite: true);
    }

    private sealed record CacheFile(DateTime? UpdatedUtc, string? Sources, List<AirportRecord> Airports, int Schema = 1);
}
