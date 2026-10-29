using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace PARScope.App.Services;

/// <summary>A release on GitHub with a scope zip attached.</summary>
/// <param name="Changelog">The notes of every release since the running version, newest first.</param>
public sealed record ReleaseInfo(string Version, string Name, string Notes, string ZipUrl, string? SetupUrl, string PageUrl,
    IReadOnlyList<(string Version, string Notes)> Changelog);

/// <summary>
/// Update check against the GitHub releases of this project. A newer release's zip is downloaded and
/// unpacked over the install folder: files in use (the running exe, loaded libraries) are renamed to
/// *.old first, which Windows allows, and removed on the next start. The vPilot plugin is not touched.
/// </summary>
public static class Updater
{
    private const string Repo = "yanjz124/VATSIM-PAR-Scope";

    /// <summary>"2.0.0-beta.1" from the build, without the source-revision suffix.</summary>
    public static string CurrentVersion { get; } =
        (Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0")
        .Split('+')[0];

    /// <summary>Short commit hash the build came from, if known.</summary>
    public static string? BuildHash { get; } =
        (Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "")
        .Split('+') is { Length: > 1 } p && p[1].Length >= 7 ? p[1][..7] : null;

    /// <summary>"2.0.0-beta.1 (abc1234)".</summary>
    public static string DisplayVersion => BuildHash is { } h ? $"{CurrentVersion} ({h})" : CurrentVersion;

    private static string InstallFolder => AppContext.BaseDirectory;

    /// <summary>Newest release above the running version, or null. Pre-releases count only when running one.</summary>
    public static async Task<ReleaseInfo?> CheckAsync(HttpClient http, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repo}/releases?per_page=10");
        req.Headers.UserAgent.ParseAdd("PARScope-updater");
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));

        bool runningPre = CurrentVersion.Contains('-');
        ReleaseInfo? best = null;
        var changelog = new List<(string Version, string Notes)>();
        foreach (var r in doc.RootElement.EnumerateArray())
        {
            if (r.GetProperty("draft").GetBoolean()) continue;
            if (r.GetProperty("prerelease").GetBoolean() && !runningPre) continue;
            string version = r.GetProperty("tag_name").GetString()!.TrimStart('v', 'V');
            if (Compare(version, CurrentVersion) <= 0) continue;
            changelog.Add((version, r.GetProperty("body").GetString() ?? ""));
            if (best != null && Compare(version, best.Version) <= 0) continue;
            var assets = r.GetProperty("assets").EnumerateArray()
                .Select(a => (Name: a.GetProperty("name").GetString() ?? "", Url: a.GetProperty("browser_download_url").GetString() ?? ""))
                .ToList();
            string? zip = assets.FirstOrDefault(a => a.Name.StartsWith("PARScope-", StringComparison.OrdinalIgnoreCase)
                                                     && a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)).Url;
            string? setup = assets.FirstOrDefault(a => a.Name.StartsWith("PARScope-Setup-", StringComparison.OrdinalIgnoreCase)
                                                       && a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)).Url;
            if (string.IsNullOrEmpty(zip) && string.IsNullOrEmpty(setup)) continue;
            best = new ReleaseInfo(version, r.GetProperty("name").GetString() ?? version, r.GetProperty("body").GetString() ?? "",
                zip ?? "", string.IsNullOrEmpty(setup) ? null : setup, r.GetProperty("html_url").GetString() ?? "", changelog);
        }
        changelog.Sort((a, b) => Compare(b.Version, a.Version));
        return best;
    }

    /// <summary>Installed with the installer (rather than unzipped): updates then go through the installer too.</summary>
    public static bool InstalledBySetup => File.Exists(Path.Combine(InstallFolder, "unins000.exe"));

    /// <summary>
    /// Installs the release and restarts. An installer-based install runs the new installer silently (it
    /// remembers the install and vPilot folders, closes the scope, updates the plugin and starts the scope
    /// again); an unzipped copy is updated in place from the zip.
    /// </summary>
    public static async Task InstallAndRestartAsync(HttpClient http, ReleaseInfo release, IProgress<string>? progress, CancellationToken ct)
    {
        if (InstalledBySetup && release.SetupUrl is { } setupUrl)
        {
            progress?.Report($"Downloading {release.Version}…");
            string setup = Path.Combine(Path.GetTempPath(), $"PARScope-Setup-{release.Version}.exe");
            await using (var src = await http.GetStreamAsync(setupUrl, ct))
            await using (var dst = File.Create(setup))
                await src.CopyToAsync(dst, ct);
            Process.Start(new ProcessStartInfo(setup, "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /UPDATE") { UseShellExecute = true });
            System.Windows.Application.Current.Shutdown();
            return;
        }
        if (string.IsNullOrEmpty(release.ZipUrl)) throw new InvalidDataException("The release has no zip to update from.");

        string temp = Path.Combine(Path.GetTempPath(), $"PARScope-update-{release.Version}");
        if (Directory.Exists(temp)) Directory.Delete(temp, true);
        Directory.CreateDirectory(temp);
        string zipPath = Path.Combine(temp, "update.zip");

        progress?.Report($"Downloading {release.Version}…");
        await using (var src = await http.GetStreamAsync(release.ZipUrl, ct))
        await using (var dst = File.Create(zipPath))
            await src.CopyToAsync(dst, ct);

        progress?.Report("Installing…");
        string unpacked = Path.Combine(temp, "files");
        ZipFile.ExtractToDirectory(zipPath, unpacked);
        // The zip holds a single PARScope-x.y.z folder or the files directly.
        string root = Directory.GetFiles(unpacked, "PARScope.exe", SearchOption.AllDirectories).Select(Path.GetDirectoryName).FirstOrDefault()
                      ?? throw new InvalidDataException("The release zip has no PARScope.exe.");

        foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(root, file);
            if (rel.StartsWith("Plugin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue; // lives in vPilot
            string target = Path.Combine(InstallFolder, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (File.Exists(target))
            {
                string old = target + ".old";
                if (File.Exists(old)) File.Delete(old);
                File.Move(target, old); // allowed even while the file is running or loaded
            }
            File.Copy(file, target);
        }
        // Keep the new plugin next to the scope so it can be copied into vPilot by hand.
        string pluginSrc = Path.Combine(root, "Plugin");
        if (Directory.Exists(pluginSrc))
            foreach (var f in Directory.GetFiles(pluginSrc))
            {
                string dst = Path.Combine(InstallFolder, "Plugin", Path.GetFileName(f));
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.Copy(f, dst, overwrite: true);
            }

        Process.Start(new ProcessStartInfo(Path.Combine(InstallFolder, "PARScope.exe")) { UseShellExecute = true, WorkingDirectory = InstallFolder });
        System.Windows.Application.Current.Shutdown();
    }

    /// <summary>Removes files left behind by the previous update.</summary>
    public static void CleanUpOldFiles()
    {
        try
        {
            foreach (var f in Directory.GetFiles(InstallFolder, "*.old", SearchOption.AllDirectories))
                try { File.Delete(f); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Semantic-version order: 2.0.0-beta.2 &lt; 2.0.0-rc.1 &lt; 2.0.0 &lt; 2.0.1.</summary>
    public static int Compare(string a, string b)
    {
        static (Version Core, string[] Pre) Parse(string v)
        {
            var parts = v.Split('-', 2);
            if (!Version.TryParse(parts[0], out var core)) core = new Version(0, 0);
            return (core, parts.Length > 1 ? parts[1].Split('.') : []);
        }
        var (ca, pa) = Parse(a);
        var (cb, pb) = Parse(b);
        int c = ca.CompareTo(cb);
        if (c != 0) return c;
        if (pa.Length == 0 || pb.Length == 0) return pb.Length.CompareTo(pa.Length); // release beats pre-release
        for (int i = 0; i < Math.Min(pa.Length, pb.Length); i++)
        {
            c = int.TryParse(pa[i], out int na) && int.TryParse(pb[i], out int nb) ? na.CompareTo(nb) : string.CompareOrdinal(pa[i], pb[i]);
            if (c != 0) return c;
        }
        return pa.Length.CompareTo(pb.Length);
    }
}
