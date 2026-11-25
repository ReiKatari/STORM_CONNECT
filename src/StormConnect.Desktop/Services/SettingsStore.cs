using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StormConnect.Protocol;

namespace StormConnect.Desktop.Services;

public sealed class AddressBookEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string StormId { get; set; } = string.Empty;
    public string Alias { get; set; } = string.Empty;
    public string HostOrIp { get; set; } = string.Empty;
    public string MacAddress { get; set; } = string.Empty;
    public string Group { get; set; } = "Workstations";
    public bool IsFavorite { get; set; }
    public string Notes { get; set; } = string.Empty;
    public DateTime? LastConnectedUtc { get; set; }
}

public sealed class AuditLogEntry
{
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public string Peer { get; set; } = string.Empty;
    public string Mode { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
}

public sealed class AppSettings
{
    public string StormId { get; set; } = string.Empty;
    public string Language { get; set; } = "auto";
    public string Theme { get; set; } = "MIDNIGHT";
    public string Accent { get; set; } = "Violet";
    public string FontFamily { get; set; } = "Inter";
    public double UiScale { get; set; } = 1.0;

    public bool AllowIncoming { get; set; } = true;
    public bool DefaultAllowControl { get; set; } = true;
    public bool DefaultAllowClipboard { get; set; } = true;
    public bool DefaultAllowFileTransfer { get; set; } = true;
    public bool DefaultAllowAudio { get; set; } = true;
    public bool DefaultAllowSystemActions { get; set; } = true;

    public int DirectPort { get; set; } = ProtocolConstants.DefaultDirectPort;
    public string RelayUrl { get; set; } = ProtocolConstants.DefaultRelayUrl;
    public bool EnableRelay { get; set; } = true;
    public bool EnableLocalRelayHost { get; set; }
    public int JpegQuality { get; set; } = 75;
    public bool StartMinimizedToTray { get; set; }

    public string? UnattendedPasswordHash { get; set; }
    public string? UnattendedSaltBase64 { get; set; }
    public string? UnattendedProtectedRawBase64 { get; set; }

    public List<AddressBookEntry> AddressBook { get; set; } = [];
    public List<AuditLogEntry> AuditLogs { get; set; } = [];
}

public sealed class SettingsStore
{
    private readonly string _dataDir;
    private readonly string _settingsPath;

    public AppSettings Current { get; private set; } = new();
    public string DataDirectory => _dataDir;
    public bool IsPortable { get; }

    public SettingsStore()
    {
        var baseDir = AppContext.BaseDirectory;
        IsPortable = File.Exists(Path.Combine(baseDir, "portable.marker")) ||
                     File.Exists(Path.Combine(baseDir, "STORM_PORTABLE.txt"));

        _dataDir = IsPortable
            ? Path.Combine(baseDir, "Data")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "STORM CONNECT");

        Directory.CreateDirectory(_dataDir);
        _settingsPath = Path.Combine(_dataDir, "settings.json");
        Load();
    }

    public void Load()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                var json = File.ReadAllText(_settingsPath, Encoding.UTF8);
                Current = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch
        {
            Current = new AppSettings();
        }

        if (DeviceIdentity.NormalizeId(Current.StormId).Length != 9)
        {
            Current.StormId = GenerateStormId();
            Save();
        }
        else
        {
            Current.StormId = DeviceIdentity.NormalizeId(Current.StormId);
        }
    }

    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_settingsPath, json, Encoding.UTF8);
        }
        catch { }
    }

    public void SetUnattendedPassword(string? plainPassword)
    {
        if (string.IsNullOrWhiteSpace(plainPassword))
        {
            Current.UnattendedPasswordHash = null;
            Current.UnattendedSaltBase64 = null;
            Current.UnattendedProtectedRawBase64 = null;
        }
        else
        {
            var salt = RandomNumberGenerator.GetBytes(16);
            Current.UnattendedSaltBase64 = Convert.ToBase64String(salt);
            Current.UnattendedPasswordHash = CryptoSession.HashPassword(plainPassword, salt);
            try
            {
                var protectedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(plainPassword), null, DataProtectionScope.CurrentUser);
                Current.UnattendedProtectedRawBase64 = Convert.ToBase64String(protectedBytes);
            }
            catch { }
        }
        Save();
    }

    public (string? Hash, byte[]? Salt) GetUnattendedCredentials()
    {
        if (string.IsNullOrWhiteSpace(Current.UnattendedPasswordHash) || string.IsNullOrWhiteSpace(Current.UnattendedSaltBase64))
            return (null, null);
        try
        {
            return (Current.UnattendedPasswordHash, Convert.FromBase64String(Current.UnattendedSaltBase64));
        }
        catch
        {
            return (null, null);
        }
    }

    public void AddAudit(string peer, string mode, string action)
    {
        Current.AuditLogs.Insert(0, new AuditLogEntry
        {
            TimestampUtc = DateTime.UtcNow,
            Peer = peer,
            Mode = mode,
            Action = action
        });
        if (Current.AuditLogs.Count > 200)
            Current.AuditLogs.RemoveRange(200, Current.AuditLogs.Count - 200);
        Save();
    }

    private static string GenerateStormId()
    {
        // Deterministic-length 9-digit STORM ID starting from 100..999
        int first = RandomNumberGenerator.GetInt32(100, 1000);
        int mid = RandomNumberGenerator.GetInt32(100, 1000);
        int last = RandomNumberGenerator.GetInt32(100, 1000);
        return $"{first:D3}{mid:D3}{last:D3}";
    }
}
