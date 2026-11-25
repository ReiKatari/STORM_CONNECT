using System.Text.Json.Serialization;

namespace StormConnect.Protocol;

public sealed record DeviceIdentity(
    string StormId,
    string HostName,
    string OsName,
    int MonitorCount,
    int DirectPort
)
{
    public static string FormatId(string rawDigits)
    {
        var digits = new string(rawDigits.Where(char.IsDigit).ToArray());
        if (digits.Length != 9) return rawDigits;
        return $"{digits[..3]} {digits[3..6]} {digits[6..9]}";
    }

    public static string NormalizeId(string input)
    {
        return new string(input.Where(char.IsDigit).ToArray());
    }
}

public sealed record MonitorInfo(
    int Index,
    string Name,
    int X,
    int Y,
    int Width,
    int Height,
    bool IsPrimary
);

public sealed class SessionPermissions
{
    public bool AllowControl { get; set; } = true;
    public bool AllowClipboard { get; set; } = true;
    public bool AllowFileTransfer { get; set; } = true;
    public bool AllowAudio { get; set; } = true;
    public bool AllowSystemActions { get; set; } = true;

    public SessionPermissions Clone() => new()
    {
        AllowControl = AllowControl,
        AllowClipboard = AllowClipboard,
        AllowFileTransfer = AllowFileTransfer,
        AllowAudio = AllowAudio,
        AllowSystemActions = AllowSystemActions
    };
}

public sealed record HelloPayload(
    string StormId,
    string HostName,
    string OsName,
    string AppVersion,
    int MonitorCount,
    bool RequiresAuth
);

public sealed record AuthChallengePayload(
    byte[] Salt,
    byte[] NonceChallenge,
    bool AllowPinConsent,
    bool AllowUnattended
);

public sealed record AuthRequestPayload(
    string ClientStormId,
    string ClientName,
    AuthMode Mode,
    string? PinOrPasswordProof,
    byte[]? ClientNonce
);

public sealed record AuthResponsePayload(
    bool Accepted,
    bool PendingConsent,
    string Reason,
    SessionPermissions Permissions
);

public sealed record ConsentDecisionPayload(
    bool Accepted,
    SessionPermissions Permissions
);

public sealed record VideoTileRect(
    int X,
    int Y,
    int Width,
    int Height,
    byte[] JpegBytes
);

public sealed record MouseEventData(
    double NormX,
    double NormY,
    byte Button,     // 0=none, 1=left, 2=right, 3=middle, 4=x1, 5=x2
    byte Action,     // 0=move, 1=down, 2=up, 3=wheel, 4=hwheel
    short WheelDelta
);

public sealed record KeyEventData(
    ushort VirtualKey,
    ushort ScanCode,
    bool IsKeyUp,
    bool IsExtended,
    char? UnicodeChar
);

public sealed record ClipboardPayload(
    bool IsImage,
    string? Text,
    byte[]? PngBytes,
    string ContentHash
);

public sealed record FileEntryInfo(
    string Name,
    string FullPath,
    bool IsDirectory,
    long SizeBytes,
    DateTime LastModifiedUtc
);

public sealed record FileListResponsePayload(
    string CurrentPath,
    string? ParentPath,
    List<FileEntryInfo> Entries,
    string? Error
);

public sealed record FileTransferInitPayload(
    string TransferId,
    bool IsUploadToRemote,
    string RemotePath,
    string FileName,
    long TotalBytes,
    string Sha256Hex
);

public sealed record FileTransferAckPayload(
    string TransferId,
    bool Success,
    long BytesReceived,
    string? Message
);

public sealed record FileOperationRequestPayload(
    FileOpKind Kind,
    string Path,
    string? TargetPath
);

public sealed record FileOperationResponsePayload(
    bool Success,
    string? Message
);

public sealed record ChatMessagePayload(
    string SenderName,
    string Text,
    DateTime TimestampUtc
);

public sealed record LanBeaconPacket(
    string StormId,
    string HostName,
    string OsName,
    int MonitorCount,
    int DirectPort,
    string LocalIp,
    long TimestampUnixMs
);

public sealed record RelayRegisterPayload(
    string StormId,
    string HostName,
    string OsName,
    int MonitorCount,
    int DirectPort,
    string? PublicUdpEndpoint
);

public sealed record RelayConnectPayload(
    string FromStormId,
    string TargetStormId
);

public sealed record RelayPairedPayload(
    string SessionToken,
    string PeerStormId,
    string PeerPublicUdp,
    bool IsInitiator
);

public sealed class SessionTelemetry
{
    public ConnectionMode Mode { get; set; } = ConnectionMode.LanDirect;
    public double Fps { get; set; }
    public int RttMs { get; set; }
    public double BandwidthMbps { get; set; }
    public double TileSavingsPercent { get; set; }
    public int ActiveMonitorIndex { get; set; }
    public int FrameWidth { get; set; }
    public int FrameHeight { get; set; }
    public long TotalBytesReceived { get; set; }
    public long TotalBytesSent { get; set; }
}

[JsonSerializable(typeof(HelloPayload))]
[JsonSerializable(typeof(AuthChallengePayload))]
[JsonSerializable(typeof(AuthRequestPayload))]
[JsonSerializable(typeof(AuthResponsePayload))]
[JsonSerializable(typeof(ConsentDecisionPayload))]
[JsonSerializable(typeof(List<MonitorInfo>))]
[JsonSerializable(typeof(ClipboardPayload))]
[JsonSerializable(typeof(FileListResponsePayload))]
[JsonSerializable(typeof(FileTransferInitPayload))]
[JsonSerializable(typeof(FileTransferAckPayload))]
[JsonSerializable(typeof(FileOperationRequestPayload))]
[JsonSerializable(typeof(FileOperationResponsePayload))]
[JsonSerializable(typeof(ChatMessagePayload))]
[JsonSerializable(typeof(LanBeaconPacket))]
[JsonSerializable(typeof(RelayRegisterPayload))]
[JsonSerializable(typeof(RelayConnectPayload))]
[JsonSerializable(typeof(RelayPairedPayload))]
public partial class StormJsonContext : JsonSerializerContext
{
}
