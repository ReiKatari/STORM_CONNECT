namespace StormConnect.Protocol;

public static class ProtocolConstants
{
    public const uint Magic = 0x4E4F4353; // "SCON" in little-endian
    public const byte ProtocolVersion = 1;

    public const int DefaultDirectPort = 7440;
    public const int DefaultRelayPort = 7443;
    public const int DefaultUdpPunchPort = 7444;
    public const int DefaultLanDiscoveryPort = 7445;

    public const int MaxPacketPayloadBytes = 32 * 1024 * 1024; // 32 MB safety limit
    public const int FileChunkBytes = 256 * 1024; // 256 KB streaming chunks
    public const int TileSize = 64;

    public const string DefaultRelayUrl = "";
    public const string DefaultStunServer = "stun.l.google.com:19302";

    public static readonly string[] PublicMqttWssBrokers =
    [
        "wss://broker.emqx.io:8084/mqtt",
        "wss://broker.hivemq.com:8884/mqtt",
        "wss://test.mosquitto.org:8081/mqtt"
    ];
}

public enum PacketType : byte
{
    Hello = 1,
    KeyExchange = 2,
    AuthRequest = 3,
    AuthResponse = 4,
    ConsentRequest = 5,
    ConsentDecision = 6,
    MonitorList = 7,
    MonitorSelect = 8,
    VideoFrameFull = 9,
    VideoFrameTiles = 10,
    CursorUpdate = 11,
    InputMouse = 12,
    InputKeyboard = 13,
    ClipboardData = 14,
    FileListReq = 15,
    FileListResp = 16,
    FileTransferInit = 17,
    FileTransferChunk = 18,
    FileTransferAck = 19,
    FileOperationReq = 20,
    FileOperationResp = 21,
    AudioFrame = 22,
    ChatMessage = 23,
    Ping = 24,
    Pong = 25,
    SessionAction = 26,
    RelayRegister = 27,
    RelayRegistered = 28,
    RelayConnect = 29,
    RelayPaired = 30,
    RelayError = 31,
    UdpPunchEndpoint = 32,
    Disconnect = 33
}

public enum ConnectionMode : byte
{
    LanDirect = 0,
    DirectIp = 1,
    P2pUdp = 2,
    WssRelay = 3
}

public enum AuthMode : byte
{
    PinOrConsent = 0,
    UnattendedPassword = 1
}

public enum SystemActionKind : byte
{
    CtrlAltDelHint = 1,
    LockWorkstation = 2,
    OpenTaskManager = 3,
    OpenRunDialog = 4,
    SendWinKey = 5,
    SendAltTab = 6,
    SendPrintScreen = 7,
    RequestKeyFrame = 8
}

public enum FileOpKind : byte
{
    CreateDirectory = 1,
    DeletePath = 2,
    RenamePath = 3
}
