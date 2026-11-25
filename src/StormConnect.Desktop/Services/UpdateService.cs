using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace StormConnect.Desktop.Services;

public sealed record UpdateCheckResult(
    bool IsUpdateAvailable,
    string CurrentVersion,
    string LatestVersion,
    string ReleaseNotes,
    string? InstallerDownloadUrl,
    string? AssetName
);

/// <summary>
/// STORM Ecosystem Auto-Update Service (Law 9):
/// Dynamically inspects Assembly.GetName().Version, normalizes to Major.Minor.Build,
/// checks both stormgamesworld.ru autonomous registry and GitHub Releases API silently.
/// </summary>
public static class UpdateService
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(6)
    };

    static UpdateService()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("STORM-CONNECT-Updater/1.0");
    }

    public static Version GetCurrentNormalizedVersion()
    {
        var raw = typeof(UpdateService).Assembly.GetName().Version ?? new Version(1, 0, 0, 0);
        return NormalizeVersion(raw);
    }

    public static string GetCurrentVersionString()
    {
        var v = GetCurrentNormalizedVersion();
        return $"{v.Major}.{v.Minor}.{v.Build}";
    }

    public static Version NormalizeVersion(Version v)
    {
        return new Version(Math.Max(0, v.Major), Math.Max(0, v.Minor), Math.Max(0, v.Build));
    }

    public static Version? ParseVersionTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var clean = tag.Trim().TrimStart('v', 'V');
        return Version.TryParse(clean, out var parsed) ? NormalizeVersion(parsed) : null;
    }

    public static async Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken ct = default)
    {
        var curVer = GetCurrentNormalizedVersion();
        var curStr = $"{curVer.Major}.{curVer.Minor}.{curVer.Build}";

        // Try STORM Autonomous Portal first, then GitHub API
        var endpoints = new[]
        {
            "https://stormgamesworld.ru/api/programs/releases?repo=STORM_CONNECT&latest=1",
            "https://api.github.com/repos/ReiKatari/STORM_CONNECT/releases/latest"
        };

        foreach (var url in endpoints)
        {
            try
            {
                using var resp = await Http.GetAsync(url, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) continue;
                var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var relObj = root.TryGetProperty("release", out var rProp) ? rProp : root;
                string? tag = relObj.TryGetProperty("version", out var vProp) ? vProp.GetString()
                            : relObj.TryGetProperty("tag_name", out var tProp) ? tProp.GetString()
                            : null;

                var remoteVer = ParseVersionTag(tag);
                if (remoteVer is null) continue;

                string notes = relObj.TryGetProperty("notes", out var nProp) ? nProp.GetString() ?? ""
                             : relObj.TryGetProperty("body", out var bProp) ? bProp.GetString() ?? ""
                             : "";

                string? dlUrl = null;
                string? assetName = null;
                if (relObj.TryGetProperty("assets", out var assetsProp) && assetsProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var a in assetsProp.EnumerateArray())
                    {
                        var name = a.TryGetProperty("name", out var np) ? np.GetString() : null;
                        if (name is not null && name.EndsWith("_Setup.exe", StringComparison.OrdinalIgnoreCase))
                        {
                            assetName = name;
                            dlUrl = a.TryGetProperty("browser_download_url", out var up) ? up.GetString()
                                  : a.TryGetProperty("downloadUrl", out var up2) ? up2.GetString()
                                  : null;
                            break;
                        }
                    }
                }

                bool available = remoteVer > curVer;
                var latStr = $"{remoteVer.Major}.{remoteVer.Minor}.{remoteVer.Build}";
                return new UpdateCheckResult(available, curStr, latStr, notes, dlUrl, assetName);
            }
            catch
            {
                // Silent error suppression on offline / DNS failure per Law 9
            }
        }

        return new UpdateCheckResult(false, curStr, curStr, "", null, null);
    }

    public static async Task<string> DownloadInstallerAsync(string url, string fileName, IProgress<double> progress, CancellationToken ct)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), string.IsNullOrWhiteSpace(fileName) ? "STORM_CONNECT_Update_Setup.exe" : fileName);
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        long total = resp.Content.Headers.ContentLength ?? -1;

        await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var dst = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None);

        var buf = new byte[65536];
        long readTotal = 0;
        int read;
        while ((read = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, read), ct).ConfigureAwait(false);
            readTotal += read;
            if (total > 0) progress.Report(readTotal * 100.0 / total);
        }
        progress.Report(100.0);
        return tempPath;
    }

    public static void LaunchInstallerAndExit(string installerPath, TrayManager? tray)
    {
        Process.Start(new ProcessStartInfo(installerPath) { UseShellExecute = true });
        tray?.Dispose();
        System.Windows.Application.Current?.Shutdown();
    }
}
