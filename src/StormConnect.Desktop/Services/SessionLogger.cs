using System.IO;
using System.Text;

namespace StormConnect.Desktop.Services;

/// <summary>
/// Lightweight thread-safe session diagnostics logger in AppData/Data, auto-capped at 64 KB.
/// </summary>
public static class SessionLogger
{
    private static readonly object LockObj = new();
    private static readonly string LogPath = ResolveLogPath();

    private static string ResolveLogPath()
    {
        var baseDir = AppContext.BaseDirectory;
        bool portable = File.Exists(Path.Combine(baseDir, "portable.marker")) ||
                        File.Exists(Path.Combine(baseDir, "STORM_PORTABLE.txt"));
        var dir = portable
            ? Path.Combine(baseDir, "Data")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "STORM CONNECT");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "session.log");
    }

    public static void Log(string category, string message)
    {
        try
        {
            lock (LockObj)
            {
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 64 * 1024)
                {
                    File.WriteAllText(LogPath, $"[{DateTime.Now:dd.MM.yyyy HH:mm:ss}] [LOG] Rotated{Environment.NewLine}", Encoding.UTF8);
                }
                File.AppendAllText(LogPath, $"[{DateTime.Now:dd.MM.yyyy HH:mm:ss}] [{category}] {message}{Environment.NewLine}", Encoding.UTF8);
            }
        }
        catch { }
    }
}
