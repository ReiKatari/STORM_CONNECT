using System.Globalization;

namespace StormConnect.Desktop.Services;

public sealed record LanguageOption(string Code, string DisplayName)
{
    public override string ToString() => DisplayName;
}

/// <summary>
/// Full 11-language STORM Ecosystem localization service.
/// Automatically detects OS system language on first launch and enforces zero em-dashes.
/// </summary>
public static class LocalizationService
{
    public static readonly IReadOnlyList<LanguageOption> Languages =
    [
        new("ru", "Русский"),
        new("en", "English"),
        new("de", "Deutsch"),
        new("fr", "Français"),
        new("zh", "简体中文"),
        new("ko", "한국어"),
        new("ja", "日本語"),
        new("es", "Español"),
        new("it", "Italiano"),
        new("pt", "Português"),
        new("hi", "हिन्दी")
    ];

    public static string CurrentLanguage { get; private set; } = DetectSystemLanguage();

    public static event Action? LanguageChanged;

    public static string DetectSystemLanguage()
    {
        var two = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.ToLowerInvariant();
        return Languages.Any(l => l.Code == two) ? two : "en";
    }

    public static void SetLanguage(string codeOrAuto)
    {
        string target = string.Equals(codeOrAuto, "auto", StringComparison.OrdinalIgnoreCase)
            ? DetectSystemLanguage()
            : (Languages.Any(l => l.Code == codeOrAuto) ? codeOrAuto : "en");
        CurrentLanguage = target;
        LanguageChanged?.Invoke();
    }

    public static string T(string key)
    {
        if (Strings.TryGetValue(CurrentLanguage, out var dict) && dict.TryGetValue(key, out var val))
            return val;
        if (Strings["en"].TryGetValue(key, out var enVal))
            return enVal;
        return key;
    }

    private static readonly Dictionary<string, Dictionary<string, string>> Strings = BuildStrings();

    private static Dictionary<string, Dictionary<string, string>> BuildStrings()
    {
        var en = new Dictionary<string, string>
        {
            ["AppSubtitle"] = "High-Speed Multi-Monitor Remote Desktop, File Transfer & Serverless Relay Suite",
            ["NavDashboard"] = "Connect",
            ["NavViewer"] = "Remote Session",
            ["NavFiles"] = "File Manager",
            ["NavAddressBook"] = "Address Book",
            ["NavNetwork"] = "Network & Relay",
            ["NavSettings"] = "Settings & Security",

            ["MyStormId"] = "YOUR STORM ID",
            ["CopyId"] = "Copy ID",
            ["CopyInvite"] = "Copy Invite Code",
            ["Copied"] = "Copied!",
            ["OneTimePin"] = "ONE-TIME PIN",
            ["RefreshPin"] = "Refresh PIN",
            ["IncomingStatus"] = "Incoming Access",
            ["RelayOnline"] = "Relay Online",
            ["RelayOffline"] = "Direct / Serverless Ready",
            ["ActiveIncoming"] = "Active Guests",

            ["QuickConnectTitle"] = "CONNECT TO REMOTE DEVICE",
            ["TargetPlaceholder"] = "Enter STORM ID, IP:Port, [IPv6]:Port or SC1-... Invite Code",
            ["ConnectBtn"] = "Connect",
            ["DisconnectBtn"] = "Disconnect",
            ["ConnMethodLabel"] = "Connection Method",
            ["MethodAuto"] = "Auto (LAN / IPv6 / UPnP / Relay)",
            ["MethodDirect"] = "Direct LAN / VPN / IP (No Server)",
            ["MethodInvite"] = "Direct Invite Code (SC1-... Serverless)",
            ["MethodRelay"] = "Cloud / Local Relay (WSS)",
            ["StatusReady"] = "Ready. Enter a 9-digit STORM ID, IP:Port, IPv6, or SC1-... Invite Code.",
            ["StatusConnectingDirect"] = "Connecting directly (TCP/IPv6/VPN)...",
            ["StatusConnectingLan"] = "Connecting via LAN Direct...",
            ["StatusConnectingInvite"] = "Connecting via Serverless Invite Code...",
            ["StatusConnectingRelay"] = "Connecting via STORM CONNECT Relay...",
            ["StatusWaitingConsent"] = "Waiting for remote user approval...",
            ["StatusConnected"] = "Connected",
            ["StatusDisconnected"] = "Disconnected",
            ["StatusConnFailed"] = "Connection failed",
            ["StatusConnectFirst"] = "Connect to a remote device first to transfer dropped files.",
            ["StatusWolSent"] = "Wake-on-LAN magic packet sent",
            ["StatusPwdSaved"] = "Unattended password saved (DPAPI + PBKDF2).",
            ["StatusPwdCleared"] = "Unattended password cleared.",
            ["StatusSavedShot"] = "Saved screenshot",
            ["ServerlessCardTitle"] = "DIRECT SERVERLESS ACCESS (NO SERVER REQUIRED)",
            ["RefreshNetMap"] = "Refresh NAT / UPnP",

            ["LanDevicesTitle"] = "DISCOVERED ON LOCAL NETWORK (LAN)",
            ["NoLanDevices"] = "Scanning local network for STORM CONNECT devices...",
            ["LiveTelemetry"] = "LIVE PERFORMANCE TELEMETRY",
            ["UnitMs"] = "ms",
            ["UnitMbps"] = "Mbps",
            ["LabelDelta"] = "Delta",
            ["LabelRtt"] = "RTT",
            ["LabelFps"] = "FPS",

            ["MonitorNone"] = "Monitor: No active session",
            ["SelectMonitor"] = "Monitor",
            ["AllMonitors"] = "All Monitors",
            ["Qual90"] = "90% Ultra",
            ["Qual75"] = "75% Balanced",
            ["Qual55"] = "55% Fast",
            ["Qual35"] = "35% Low-BW",
            ["AudioToggle"] = "Audio",
            ["StateOn"] = "ON",
            ["StateOff"] = "OFF",
            ["Fullscreen"] = "Fullscreen",
            ["CtrlAltDel"] = "TaskMgr",
            ["LockRemote"] = "Lock PC",
            ["WinRun"] = "Win+R",
            ["AltTab"] = "Alt+Tab",
            ["Screenshot"] = "Screenshot",
            ["Chat"] = "Session Chat",
            ["ViewerIdle"] = "Idle",
            ["NoActiveSession"] = "No active remote session. Connect from the Dashboard or Address Book.",
            ["DropFilesHint"] = "Drag & Drop files onto the remote screen to upload immediately",

            ["LocalFiles"] = "LOCAL COMPUTER",
            ["RemoteFiles"] = "REMOTE COMPUTER",
            ["UpFolder"] = "Up",
            ["Refresh"] = "Refresh",
            ["Upload"] = "Upload ->",
            ["Download"] = "<- Download",
            ["NewFolder"] = "New Folder",
            ["Delete"] = "Delete",
            ["FileReady"] = "Ready for file transfer (Drag & Drop supported)",
            ["FileUploading"] = "Uploading",
            ["FileDownloading"] = "Downloading",
            ["FileError"] = "Error",
            ["ColName"] = "NAME",
            ["ColSize"] = "SIZE",
            ["ColModified"] = "MODIFIED",
            ["ColType"] = "TYPE",

            ["ColAlias"] = "DEVICE NAME",
            ["ColStormId"] = "STORM ID",
            ["ColHost"] = "HOST / IP",
            ["ColGroup"] = "GROUP",
            ["ColMac"] = "MAC (WOL)",
            ["ColActions"] = "ACTIONS",
            ["AddDevice"] = "Add Device",
            ["WakeOnLan"] = "Wake",
            ["DeleteBtn"] = "Delete",
            ["HintAlias"] = "Device Alias",
            ["HintStormId"] = "STORM ID (9 digits)",
            ["HintHost"] = "Host / IP / SC1 Invite Code",
            ["HintMac"] = "MAC Address (XX:XX:XX:XX:XX:XX)",
            ["HintGroup"] = "Group",

            ["RelayServerUrl"] = "STORM CONNECT Relay URL (WSS)",
            ["DirectTcpPort"] = "Direct LAN / TCP Port",
            ["EnableRelay"] = "Enable Cloud Relay Fallback",
            ["SaveNetworkBtn"] = "Save Network Settings",
            ["EmbeddedRelay"] = "Run Embedded Team Relay Hub on this PC",
            ["StartHub"] = "Start Local Hub",
            ["StopHub"] = "Stop Local Hub",
            ["HubStopped"] = "Stopped",
            ["HubListening"] = "Listening on",
            ["DeployNote"] = "Use the included deploy/ folder (Docker / systemd / Nginx) to host your private relay on any domain.",

            ["UnattendedPwd"] = "Unattended Access Password",
            ["SavePassword"] = "Save Password",
            ["ClearPassword"] = "Clear",
            ["PermControl"] = "Allow Mouse & Keyboard Control",
            ["PermClipboard"] = "Allow Two-Way Clipboard Sync",
            ["PermFiles"] = "Allow File Transfer",
            ["PermAudio"] = "Allow Audio Streaming",
            ["PermSystem"] = "Allow System Actions (Lock / Task Manager)",
            ["UiScale"] = "Interface Scale",
            ["AuditLogTitle"] = "SESSION AUDIT LOG",
            ["ColTime"] = "TIME (UTC)",
            ["ColPeer"] = "PEER",
            ["ColMode"] = "MODE",
            ["ColEvent"] = "EVENT",

            ["CheckUpdates"] = "Updates",
            ["About"] = "About",
            ["TipLang"] = "Interface language (all menus, tables and tooltips)",
            ["TipTheme"] = "Switch color theme dynamically",
            ["TipAccent"] = "Switch accent color dynamically",
            ["TipFont"] = "Switch interface font dynamically",
            ["TipUpdates"] = "Check STORM Portal and GitHub for updates",
            ["TipAbout"] = "About STORM CONNECT",

            ["ConsentTitle"] = "INCOMING CONNECTION REQUEST",
            ["ConsentPermControl"] = "Allow Mouse & Keyboard Control",
            ["ConsentPermClipboard"] = "Allow Clipboard Sync",
            ["ConsentPermFiles"] = "Allow File Transfer",
            ["ConsentPermAudio"] = "Allow Audio",
            ["AcceptFull"] = "Accept Full Control",
            ["AcceptView"] = "View Only",
            ["Decline"] = "Decline",
            ["AuthPromptTitle"] = "REMOTE AUTHENTICATION",
            ["AuthPromptMsg"] = "Enter PIN or Unattended Password (leave empty to request interactive approval):",
            ["Ok"] = "OK",
            ["Cancel"] = "Cancel",
            ["Close"] = "Close",
            ["UpdateAvailableTitle"] = "UPDATE AVAILABLE",
            ["UpdateBtn"] = "Update",
            ["LaterBtn"] = "Later",
            ["UpToDateMsg"] = "You have the latest version installed",
            ["UpdateNewAvailable"] = "New release available.",
            ["AboutDesc"] = "High-speed remote desktop, multi-monitor streaming, file manager, audio, clipboard sync, Wake-on-LAN, serverless UPnP/IPv6/STUN/Invite direct connections and self-hosted encrypted relay for the STORM ecosystem.",

            ["TrayRestore"] = "Open STORM CONNECT",
            ["TrayAbout"] = "About",
            ["TrayExit"] = "Exit"
        };

        var ru = new Dictionary<string, string>(en)
        {
            ["AppSubtitle"] = "Высокоскоростной многомониторный удалённый рабочий стол, файлы и прямая связь без сервера",
            ["NavDashboard"] = "Подключение",
            ["NavViewer"] = "Удалённый экран",
            ["NavFiles"] = "Файловый менеджер",
            ["NavAddressBook"] = "Адресная книга",
            ["NavNetwork"] = "Сеть и ретранслятор",
            ["NavSettings"] = "Настройки и защита",

            ["MyStormId"] = "ВАШ STORM ID",
            ["CopyId"] = "Копировать ID",
            ["CopyInvite"] = "Копировать код доступа",
            ["Copied"] = "Скопировано!",
            ["OneTimePin"] = "ОДНОРАЗОВЫЙ PIN",
            ["RefreshPin"] = "Обновить PIN",
            ["IncomingStatus"] = "Входящий доступ",
            ["RelayOnline"] = "Ретранслятор онлайн",
            ["RelayOffline"] = "Прямой режим (без сервера)",
            ["ActiveIncoming"] = "Активные гости",

            ["QuickConnectTitle"] = "ПОДКЛЮЧЕНИЕ К УДАЛЁННОМУ УСТРОЙСТВУ",
            ["TargetPlaceholder"] = "STORM ID, IP:Порт, [IPv6]:Порт или код SC1-...",
            ["ConnectBtn"] = "Подключиться",
            ["DisconnectBtn"] = "Отключить",
            ["ConnMethodLabel"] = "Метод подключения",
            ["MethodAuto"] = "Авто (LAN / IPv6 / UPnP / Relay)",
            ["MethodDirect"] = "Прямой LAN / VPN / IP (без сервера)",
            ["MethodInvite"] = "Код приглашения (SC1-... без сервера)",
            ["MethodRelay"] = "Облачный / локальный ретранслятор (WSS)",
            ["StatusReady"] = "Готово. Введите 9-значный STORM ID, IP:Порт, IPv6 или код приглашения SC1-...",
            ["StatusConnectingDirect"] = "Прямое подключение (TCP/IPv6/VPN)...",
            ["StatusConnectingLan"] = "Подключение по локальной сети (LAN Direct)...",
            ["StatusConnectingInvite"] = "Подключение по прямому коду приглашения (без сервера)...",
            ["StatusConnectingRelay"] = "Подключение через ретранслятор STORM CONNECT...",
            ["StatusWaitingConsent"] = "Ожидание подтверждения от пользователя...",
            ["StatusConnected"] = "Подключено",
            ["StatusDisconnected"] = "Отключено",
            ["StatusConnFailed"] = "Ошибка подключения",
            ["StatusConnectFirst"] = "Сначала подключитесь к устройству для передачи файлов.",
            ["StatusWolSent"] = "Пакет Wake-on-LAN отправлен на",
            ["StatusPwdSaved"] = "Пароль постоянного доступа сохранён (DPAPI + PBKDF2).",
            ["StatusPwdCleared"] = "Пароль постоянного доступа сброшен.",
            ["StatusSavedShot"] = "Снимок экрана сохранён",
            ["ServerlessCardTitle"] = "ПРЯМОЙ ДОСТУП БЕЗ СЕРВЕРА (LAN / VPN / IPv6 / UPnP / STUN)",
            ["RefreshNetMap"] = "Обновить NAT / UPnP",

            ["LanDevicesTitle"] = "ОБНАРУЖЕННЫЕ УСТРОЙСТВА В ЛОКАЛЬНОЙ СЕТИ (LAN)",
            ["NoLanDevices"] = "Поиск устройств STORM CONNECT в локальной сети...",
            ["LiveTelemetry"] = "ЖИВАЯ ТЕЛЕМЕТРИЯ ПРОИЗВОДИТЕЛЬНОСТИ",
            ["UnitMs"] = "мс",
            ["UnitMbps"] = "Мбит/с",
            ["LabelDelta"] = "Дельта",
            ["LabelRtt"] = "Пинг",
            ["LabelFps"] = "Кадр/с",

            ["MonitorNone"] = "Монитор: нет активной сессии",
            ["SelectMonitor"] = "Монитор",
            ["AllMonitors"] = "Все мониторы",
            ["Qual90"] = "90% Ультра",
            ["Qual75"] = "75% Баланс",
            ["Qual55"] = "55% Быстро",
            ["Qual35"] = "35% Эконом",
            ["AudioToggle"] = "Звук",
            ["StateOn"] = "ВКЛ",
            ["StateOff"] = "ВЫКЛ",
            ["Fullscreen"] = "Во весь экран",
            ["CtrlAltDel"] = "Диспетчер",
            ["LockRemote"] = "Блокировка",
            ["WinRun"] = "Win+R",
            ["AltTab"] = "Alt+Tab",
            ["Screenshot"] = "Снимок",
            ["Chat"] = "Чат сессии",
            ["ViewerIdle"] = "Ожидание",
            ["NoActiveSession"] = "Нет активной удалённой сессии. Подключитесь с главного экрана или из Адресной книги.",
            ["DropFilesHint"] = "Перетащите файлы прямо на удалённый экран для мгновенной отправки",

            ["LocalFiles"] = "ЛОКАЛЬНЫЙ КОМПЬЮТЕР",
            ["RemoteFiles"] = "УДАЛЁННЫЙ КОМПЬЮТЕР",
            ["UpFolder"] = "Вверх",
            ["Refresh"] = "Обновить",
            ["Upload"] = "Отправить ->",
            ["Download"] = "<- Скачать",
            ["NewFolder"] = "Новая папка",
            ["Delete"] = "Удалить",
            ["FileReady"] = "Готово к передаче файлов (поддерживается Drag-and-Drop)",
            ["FileUploading"] = "Отправка",
            ["FileDownloading"] = "Скачивание",
            ["FileError"] = "Ошибка",
            ["ColName"] = "ИМЯ",
            ["ColSize"] = "РАЗМЕР",
            ["ColModified"] = "ИЗМЕНЁН",
            ["ColType"] = "ТИП",

            ["ColAlias"] = "НАЗВАНИЕ",
            ["ColStormId"] = "STORM ID",
            ["ColHost"] = "ХОСТ / IP",
            ["ColGroup"] = "ГРУППА",
            ["ColMac"] = "MAC (WOL)",
            ["ColActions"] = "ДЕЙСТВИЯ",
            ["AddDevice"] = "Добавить устройство",
            ["WakeOnLan"] = "Разбудить",
            ["DeleteBtn"] = "Удалить",
            ["HintAlias"] = "Название устройства",
            ["HintStormId"] = "STORM ID (9 цифр)",
            ["HintHost"] = "Хост / IP / Код SC1-...",
            ["HintMac"] = "MAC-адрес (XX:XX:XX:XX:XX:XX)",
            ["HintGroup"] = "Группа",

            ["RelayServerUrl"] = "Адрес ретранслятора STORM CONNECT (WSS)",
            ["DirectTcpPort"] = "Прямой порт LAN / TCP",
            ["EnableRelay"] = "Использовать облачный ретранслятор",
            ["SaveNetworkBtn"] = "Сохранить настройки сети",
            ["EmbeddedRelay"] = "Встроенный командный ретранслятор на этом ПК",
            ["StartHub"] = "Запустить узел",
            ["StopHub"] = "Остановить узел",
            ["HubStopped"] = "Остановлен",
            ["HubListening"] = "Активен на",
            ["DeployNote"] = "Используйте папку deploy/ (Docker / systemd / Nginx) для развёртывания собственного узла на вашем сервере.",

            ["UnattendedPwd"] = "Пароль постоянного неконтролируемого доступа",
            ["SavePassword"] = "Сохранить пароль",
            ["ClearPassword"] = "Сбросить",
            ["PermControl"] = "Разрешить управление мышью и клавиатурой",
            ["PermClipboard"] = "Разрешить синхронизацию буфера обмена",
            ["PermFiles"] = "Разрешить передачу файлов",
            ["PermAudio"] = "Разрешить передачу звука",
            ["PermSystem"] = "Разрешить системные команды (Блокировка / Диспетчер)",
            ["UiScale"] = "Масштаб интерфейса",
            ["AuditLogTitle"] = "ЖУРНАЛ БЕЗОПАСНОСТИ И ПОДКЛЮЧЕНИЙ",
            ["ColTime"] = "ВРЕМЯ (UTC)",
            ["ColPeer"] = "УСТРОЙСТВО",
            ["ColMode"] = "КАНАЛ",
            ["ColEvent"] = "СОБЫТИЕ",

            ["CheckUpdates"] = "Обновления",
            ["About"] = "О программе",
            ["TipLang"] = "Язык интерфейса (меню, таблицы, подсказки)",
            ["TipTheme"] = "Динамическое переключение темы оформления",
            ["TipAccent"] = "Динамическое переключение цвета акцента",
            ["TipFont"] = "Динамическое переключение шрифта интерфейса",
            ["TipUpdates"] = "Проверить обновления на портале STORM и GitHub",
            ["TipAbout"] = "Информация о программе STORM CONNECT",

            ["ConsentTitle"] = "ЗАПРОС ВХОДЯЩЕГО ПОДКЛЮЧЕНИЯ",
            ["ConsentPermControl"] = "Разрешить управление мышью и клавиатурой",
            ["ConsentPermClipboard"] = "Разрешить обмен буфером обмена",
            ["ConsentPermFiles"] = "Разрешить передачу файлов",
            ["ConsentPermAudio"] = "Разрешить передачу звука",
            ["AcceptFull"] = "Разрешить управление",
            ["AcceptView"] = "Только просмотр",
            ["Decline"] = "Отклонить",
            ["AuthPromptTitle"] = "АВТОРИЗАЦИЯ ПОДКЛЮЧЕНИЯ",
            ["AuthPromptMsg"] = "Введите PIN или постоянный пароль (оставьте пустым для запроса подтверждения):",
            ["Ok"] = "ОК",
            ["Cancel"] = "Отмена",
            ["Close"] = "Закрыть",
            ["UpdateAvailableTitle"] = "ДОСТУПНО ОБНОВЛЕНИЕ",
            ["UpdateBtn"] = "Обновить",
            ["LaterBtn"] = "Позже",
            ["UpToDateMsg"] = "У вас установлена актуальная версия",
            ["UpdateNewAvailable"] = "Доступна новая версия.",
            ["AboutDesc"] = "Быстрый удалённый рабочий стол, многомониторный захват DXGI, передача файлов, звук, буфер обмена, Wake-on-LAN, прямое подключение без сервера (LAN/VPN/IPv6/UPnP/STUN/SC1) и собственный шифрованный ретранслятор экосистемы STORM.",

            ["TrayRestore"] = "Развернуть STORM CONNECT",
            ["TrayAbout"] = "О программе",
            ["TrayExit"] = "Выход"
        };

        var de = Merge(en, new()
        {
            ["AppSubtitle"] = "Schneller Multi-Monitor-Fernzugriff, Dateitransfer & Direktverbindung",
            ["NavDashboard"] = "Verbinden", ["NavViewer"] = "Fernsitzung", ["NavFiles"] = "Dateimanager",
            ["NavAddressBook"] = "Adressbuch", ["NavNetwork"] = "Netzwerk & Relay", ["NavSettings"] = "Einstellungen",
            ["CopyId"] = "ID kopieren", ["CopyInvite"] = "Einladungscode kopieren", ["Copied"] = "Kopiert!",
            ["RefreshPin"] = "PIN erneuern", ["ConnectBtn"] = "Verbinden", ["DisconnectBtn"] = "Trennen",
            ["AllMonitors"] = "Alle Monitore", ["MonitorNone"] = "Monitor: Keine Sitzung",
            ["Qual90"] = "90% Ultra", ["Qual75"] = "75% Ausgewogen", ["Qual55"] = "55% Schnell", ["Qual35"] = "35% Sparsam",
            ["AudioToggle"] = "Ton", ["StateOn"] = "EIN", ["StateOff"] = "AUS", ["ViewerIdle"] = "Bereit",
            ["Fullscreen"] = "Vollbild", ["CtrlAltDel"] = "TaskMgr", ["LockRemote"] = "Sperren", ["Screenshot"] = "Bildschirmfoto", ["Chat"] = "Chat",
            ["ColName"] = "NAME", ["ColSize"] = "GROSSE", ["ColModified"] = "GEANDERT", ["ColType"] = "TYP",
            ["ColAlias"] = "GERATENAME", ["ColActions"] = "AKTIONEN", ["AddDevice"] = "Hinzufugen", ["WakeOnLan"] = "Wecken",
            ["SaveNetworkBtn"] = "Speichern", ["CheckUpdates"] = "Updates", ["About"] = "Uber", ["UpdateBtn"] = "Aktualisieren",
            ["Cancel"] = "Abbrechen", ["Close"] = "Schliessen", ["TrayRestore"] = "STORM CONNECT offnen",
            ["TrayAbout"] = "Uber", ["TrayExit"] = "Beenden"
        });

        var fr = Merge(en, new()
        {
            ["AppSubtitle"] = "Bureau a distance multi-ecrans rapide, fichiers et connexion directe",
            ["NavDashboard"] = "Connexion", ["NavViewer"] = "Session distante", ["NavFiles"] = "Fichiers",
            ["NavAddressBook"] = "Carnet d'adresses", ["NavNetwork"] = "Reseau & Relais", ["NavSettings"] = "Parametres",
            ["CopyId"] = "Copier ID", ["CopyInvite"] = "Copier code d'acces", ["Copied"] = "Copie!",
            ["RefreshPin"] = "Nouveau PIN", ["ConnectBtn"] = "Connecter", ["DisconnectBtn"] = "Deconnecter",
            ["AllMonitors"] = "Tous les ecrans", ["MonitorNone"] = "Ecran : aucune session",
            ["Qual90"] = "90% Ultra", ["Qual75"] = "75% Equilibre", ["Qual55"] = "55% Rapide", ["Qual35"] = "35% Eco",
            ["AudioToggle"] = "Audio", ["StateOn"] = "ON", ["StateOff"] = "OFF", ["ViewerIdle"] = "Inactif",
            ["Fullscreen"] = "Plein ecran", ["LockRemote"] = "Verrouiller", ["Screenshot"] = "Capture", ["Chat"] = "Discussion",
            ["ColName"] = "NOM", ["ColSize"] = "TAILLE", ["ColModified"] = "MODIFIE", ["ColType"] = "TYPE",
            ["ColAlias"] = "NOM D'APPAREIL", ["ColActions"] = "ACTIONS", ["AddDevice"] = "Ajouter", ["WakeOnLan"] = "Reveiller",
            ["SaveNetworkBtn"] = "Enregistrer", ["CheckUpdates"] = "Mises a jour", ["About"] = "A propos", ["UpdateBtn"] = "Mettre a jour",
            ["Cancel"] = "Annuler", ["Close"] = "Fermer", ["TrayRestore"] = "Ouvrir STORM CONNECT",
            ["TrayAbout"] = "A propos", ["TrayExit"] = "Quitter"
        });

        var es = Merge(en, new()
        {
            ["AppSubtitle"] = "Escritorio remoto multimonitor rapido, archivos y conexion directa",
            ["NavDashboard"] = "Conectar", ["NavViewer"] = "Sesion remota", ["NavFiles"] = "Archivos",
            ["NavAddressBook"] = "Libreta", ["NavNetwork"] = "Red y Relay", ["NavSettings"] = "Ajustes",
            ["CopyId"] = "Copiar ID", ["CopyInvite"] = "Copiar invitacion", ["Copied"] = "Copiado!",
            ["ConnectBtn"] = "Conectar", ["DisconnectBtn"] = "Desconectar",
            ["AllMonitors"] = "Todos los monitores", ["MonitorNone"] = "Monitor: sin sesion",
            ["Qual90"] = "90% Ultra", ["Qual75"] = "75% Balance", ["Qual55"] = "55% Rapido", ["Qual35"] = "35% Bajo",
            ["StateOn"] = "SI", ["StateOff"] = "NO", ["ViewerIdle"] = "En espera",
            ["ColName"] = "NOMBRE", ["ColSize"] = "TAMANO", ["ColModified"] = "MODIFICADO", ["ColType"] = "TIPO",
            ["ColAlias"] = "DISPOSITIVO", ["ColActions"] = "ACCIONES", ["SaveNetworkBtn"] = "Guardar",
            ["CheckUpdates"] = "Actualizaciones", ["About"] = "Acerca de", ["UpdateBtn"] = "Actualizar", ["Cancel"] = "Cancelar",
            ["Close"] = "Cerrar", ["TrayRestore"] = "Abrir STORM CONNECT", ["TrayAbout"] = "Acerca de", ["TrayExit"] = "Salir"
        });

        var it = Merge(en, new()
        {
            ["AppSubtitle"] = "Desktop remoto multi-monitor veloce, file e connessione diretta",
            ["NavDashboard"] = "Connetti", ["NavViewer"] = "Sessione remota", ["NavFiles"] = "File",
            ["NavAddressBook"] = "Rubrica", ["NavNetwork"] = "Rete e Relay", ["NavSettings"] = "Impostazioni",
            ["CopyId"] = "Copia ID", ["CopyInvite"] = "Copia invito", ["Copied"] = "Copiato!",
            ["ConnectBtn"] = "Connetti", ["DisconnectBtn"] = "Disconnetti",
            ["AllMonitors"] = "Tutti i monitor", ["MonitorNone"] = "Monitor: nessuna sessione",
            ["Qual90"] = "90% Ultra", ["Qual75"] = "75% Bilanciato", ["Qual55"] = "55% Veloce", ["Qual35"] = "35% Basso",
            ["StateOn"] = "ON", ["StateOff"] = "OFF", ["ViewerIdle"] = "Inattivo",
            ["ColName"] = "NOME", ["ColSize"] = "DIMENSIONE", ["ColModified"] = "MODIFICATO", ["ColType"] = "TIPO",
            ["ColAlias"] = "DISPOSITIVO", ["ColActions"] = "AZIONI", ["SaveNetworkBtn"] = "Salva",
            ["CheckUpdates"] = "Aggiornamenti", ["About"] = "Informazioni", ["UpdateBtn"] = "Aggiorna", ["Cancel"] = "Annulla",
            ["Close"] = "Chiudi", ["TrayRestore"] = "Apri STORM CONNECT", ["TrayAbout"] = "Informazioni", ["TrayExit"] = "Esci"
        });

        var pt = Merge(en, new()
        {
            ["AppSubtitle"] = "Acesso remoto multimonitor rapido, arquivos e conexao direta",
            ["NavDashboard"] = "Conectar", ["NavViewer"] = "Sessao remota", ["NavFiles"] = "Arquivos",
            ["NavAddressBook"] = "Contatos", ["NavNetwork"] = "Rede e Relay", ["NavSettings"] = "Configuracoes",
            ["CopyId"] = "Copiar ID", ["CopyInvite"] = "Copiar convite", ["Copied"] = "Copiado!",
            ["ConnectBtn"] = "Conectar", ["DisconnectBtn"] = "Desconectar",
            ["AllMonitors"] = "Todos os monitores", ["MonitorNone"] = "Monitor: sem sessao",
            ["Qual90"] = "90% Ultra", ["Qual75"] = "75% Equilibrado", ["Qual55"] = "55% Rapido", ["Qual35"] = "35% Baixo",
            ["StateOn"] = "ON", ["StateOff"] = "OFF", ["ViewerIdle"] = "Aguardando",
            ["ColName"] = "NOME", ["ColSize"] = "TAMANHO", ["ColModified"] = "MODIFICADO", ["ColType"] = "TIPO",
            ["ColAlias"] = "DISPOSITIVO", ["ColActions"] = "ACOES", ["SaveNetworkBtn"] = "Salvar",
            ["CheckUpdates"] = "Atualizacoes", ["About"] = "Sobre", ["UpdateBtn"] = "Atualizar", ["Cancel"] = "Cancelar",
            ["Close"] = "Fechar", ["TrayRestore"] = "Abrir STORM CONNECT", ["TrayAbout"] = "Sobre", ["TrayExit"] = "Sair"
        });

        var zh = Merge(en, new()
        {
            ["AppSubtitle"] = "高速多显示器远程桌面、文件传输与无服务器直连",
            ["NavDashboard"] = "连接", ["NavViewer"] = "远程桌面", ["NavFiles"] = "文件管理",
            ["NavAddressBook"] = "地址簿", ["NavNetwork"] = "网络与中继", ["NavSettings"] = "设置与安全",
            ["CopyId"] = "复制 ID", ["CopyInvite"] = "复制邀请码", ["Copied"] = "已复制!", ["RefreshPin"] = "刷新 PIN",
            ["ConnectBtn"] = "连接", ["DisconnectBtn"] = "断开", ["AllMonitors"] = "所有显示器", ["MonitorNone"] = "显示器: 未连接",
            ["Qual90"] = "90% 超清", ["Qual75"] = "75% 均衡", ["Qual55"] = "55% 极速", ["Qual35"] = "35% 省流",
            ["AudioToggle"] = "音频", ["StateOn"] = "开", ["StateOff"] = "关", ["Fullscreen"] = "全屏", ["ViewerIdle"] = "空闲",
            ["ColName"] = "名称", ["ColSize"] = "大小", ["ColModified"] = "修改时间", ["ColType"] = "类型",
            ["ColAlias"] = "设备名称", ["ColActions"] = "操作", ["AddDevice"] = "添加设备", ["SaveNetworkBtn"] = "保存设置",
            ["CheckUpdates"] = "检查更新", ["About"] = "关于", ["UpdateBtn"] = "更新",
            ["Cancel"] = "取消", ["Close"] = "关闭", ["TrayRestore"] = "打开 STORM CONNECT",
            ["TrayAbout"] = "关于", ["TrayExit"] = "退出"
        });

        var ko = Merge(en, new()
        {
            ["AppSubtitle"] = "고속 다중 모니터 원격 데스크톱, 파일 전송 및 무서버 직접 연결",
            ["NavDashboard"] = "연결", ["NavViewer"] = "원격 화면", ["NavFiles"] = "파일 관리자",
            ["NavAddressBook"] = "주소록", ["NavNetwork"] = "네트워크 및 릴레이", ["NavSettings"] = "설정 및 보안",
            ["CopyId"] = "ID 복사", ["CopyInvite"] = "초대 코드 복사", ["Copied"] = "복사됨!", ["RefreshPin"] = "PIN 새로고침",
            ["ConnectBtn"] = "연결", ["DisconnectBtn"] = "연결 해제", ["AllMonitors"] = "모든 모니터", ["MonitorNone"] = "모니터: 연결 없음",
            ["Qual90"] = "90% 최고화질", ["Qual75"] = "75% 균형", ["Qual55"] = "55% 빠름", ["Qual35"] = "35% 절약",
            ["AudioToggle"] = "오디오", ["StateOn"] = "켜짐", ["StateOff"] = "꺼짐", ["Fullscreen"] = "전체 화면", ["ViewerIdle"] = "대기 중",
            ["ColName"] = "이름", ["ColSize"] = "크기", ["ColModified"] = "수정됨", ["ColType"] = "유형",
            ["ColAlias"] = "장치 이름", ["ColActions"] = "작업", ["AddDevice"] = "장치 추가", ["SaveNetworkBtn"] = "저장",
            ["CheckUpdates"] = "업데이트", ["About"] = "정보", ["UpdateBtn"] = "업데이트",
            ["Cancel"] = "취소", ["Close"] = "닫기", ["TrayRestore"] = "STORM CONNECT 열기",
            ["TrayAbout"] = "정보", ["TrayExit"] = "종료"
        });

        var ja = Merge(en, new()
        {
            ["AppSubtitle"] = "高速マルチモニターリモートデスクトップ・ファイル転送・直接接続",
            ["NavDashboard"] = "接続", ["NavViewer"] = "リモート画面", ["NavFiles"] = "ファイル管理",
            ["NavAddressBook"] = "アドレス帳", ["NavNetwork"] = "ネットワークとリレー", ["NavSettings"] = "設定とセキュリティ",
            ["CopyId"] = "IDをコピー", ["CopyInvite"] = "招待コードをコピー", ["Copied"] = "コピー完了!", ["RefreshPin"] = "PIN更新",
            ["ConnectBtn"] = "接続", ["DisconnectBtn"] = "切断", ["AllMonitors"] = "すべてのモニター", ["MonitorNone"] = "モニター: 未接続",
            ["Qual90"] = "90% 高画質", ["Qual75"] = "75% バランス", ["Qual55"] = "55% 高速", ["Qual35"] = "35% 低帯域",
            ["AudioToggle"] = "音声", ["StateOn"] = "ON", ["StateOff"] = "OFF", ["Fullscreen"] = "全画面", ["ViewerIdle"] = "待機中",
            ["ColName"] = "名前", ["ColSize"] = "サイズ", ["ColModified"] = "更新日時", ["ColType"] = "種類",
            ["ColAlias"] = "デバイス名", ["ColActions"] = "操作", ["AddDevice"] = "デバイス追加", ["SaveNetworkBtn"] = "保存",
            ["CheckUpdates"] = "更新確認", ["About"] = "情報", ["UpdateBtn"] = "更新",
            ["Cancel"] = "キャンセル", ["Close"] = "閉じる", ["TrayRestore"] = "STORM CONNECT を開く",
            ["TrayAbout"] = "情報", ["TrayExit"] = "終了"
        });

        var hi = Merge(en, new()
        {
            ["AppSubtitle"] = "तेज मल्टी-मॉनिटर रिमोट डेस्कटॉप, फाइल ट्रांसफर और डायरेक्ट कनेक्शन",
            ["NavDashboard"] = "कनेक्ट", ["NavViewer"] = "रिमोट स्क्रीन", ["NavFiles"] = "फाइल प्रबंधक",
            ["NavAddressBook"] = "एड्रेस बुक", ["NavNetwork"] = "नेटवर्क और रिले", ["NavSettings"] = "सेटिंग्स",
            ["CopyId"] = "ID कॉपी करें", ["CopyInvite"] = "इनवाइट कोड कॉपी करें", ["ConnectBtn"] = "कनेक्ट",
            ["DisconnectBtn"] = "डिस्कनेक्ट", ["AllMonitors"] = "सभी मॉनिटर", ["MonitorNone"] = "मॉनिटर: कोई सत्र नहीं",
            ["Qual90"] = "90% अल्ट्रा", ["Qual75"] = "75% संतुलित", ["Qual55"] = "55% तेज", ["Qual35"] = "35% कम डेटा",
            ["AudioToggle"] = "ऑडियो", ["StateOn"] = "चालू", ["StateOff"] = "बंद", ["ViewerIdle"] = "प्रतीक्षा",
            ["ColName"] = "नाम", ["ColSize"] = "आकार", ["ColModified"] = "संशोधित", ["ColType"] = "प्रकार",
            ["ColAlias"] = "डिवाइस नाम", ["ColActions"] = "कार्रवाई", ["SaveNetworkBtn"] = "सहेजें",
            ["CheckUpdates"] = "अपडेट", ["About"] = "परिचय", ["UpdateBtn"] = "अपडेट करें", ["Cancel"] = "रद्द करें",
            ["Close"] = "बंद करें", ["TrayRestore"] = "STORM CONNECT खोलें",
            ["TrayAbout"] = "परिचय", ["TrayExit"] = "बाहर निकलें"
        });

        return new Dictionary<string, Dictionary<string, string>>
        {
            ["en"] = en, ["ru"] = ru, ["de"] = de, ["fr"] = fr, ["zh"] = zh,
            ["ko"] = ko, ["ja"] = ja, ["es"] = es, ["it"] = it, ["pt"] = pt, ["hi"] = hi
        };
    }

    private static Dictionary<string, string> Merge(Dictionary<string, string> baseDict, Dictionary<string, string> overrides)
    {
        var d = new Dictionary<string, string>(baseDict);
        foreach (var kv in overrides) d[kv.Key] = kv.Value;
        return d;
    }

    public static IEnumerable<KeyValuePair<string, string>> GetAllStringsForLanguage(string lang)
    {
        return Strings.TryGetValue(lang, out var dict) ? dict : Strings["en"];
    }
}
