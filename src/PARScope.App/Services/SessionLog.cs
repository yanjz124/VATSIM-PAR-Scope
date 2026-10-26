using System.IO;
using System.Text;

namespace PARScope.App.Services;

/// <summary>
/// Training session log: every controller transmission (as heard or typed, and how it was understood),
/// pilot and tower replies, what the pilot did, and a state snapshot of each training aircraft every two
/// seconds with the call the scope would make. Lets a session be reviewed afterwards. Files live in
/// %AppData%\VATSIM-PAR-Scope\diagnostics as training-*.log; only the most recent are kept.
/// </summary>
public sealed class SessionLog : IDisposable
{
    private const int KeepFiles = 20;
    private StreamWriter? _w;

    public string? CurrentFile { get; private set; }

    public void Write(string line)
    {
        var w = Writer();
        w?.WriteLine($"{DateTime.UtcNow:HH:mm:ss.f}Z  {line}");
    }

    private StreamWriter? Writer()
    {
        if (_w != null) return _w;
        try
        {
            Directory.CreateDirectory(TrafficRecorder.Folder);
            foreach (var old in new DirectoryInfo(TrafficRecorder.Folder).GetFiles("training-*.log").OrderByDescending(f => f.Name).Skip(KeepFiles - 1))
                old.Delete();
            CurrentFile = Path.Combine(TrafficRecorder.Folder, $"training-{DateTime.UtcNow:yyyyMMdd-HHmmss}.log");
            _w = new StreamWriter(CurrentFile, false, new UTF8Encoding(false)) { AutoFlush = true };
            _w.WriteLine($"vibe PAR training session, started {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z");
            return _w;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public void Dispose() => _w?.Dispose();
}
