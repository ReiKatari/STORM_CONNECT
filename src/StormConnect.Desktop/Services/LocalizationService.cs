using System.Globalization;

namespace StormConnect.Desktop.Services;

public sealed record LanguageOption(string Code, string DisplayName);

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
            ["AppSubtitle"] = "High-Speed Multi-Monitor Remote Desktop, File Transfer & Relay Suite",
            ["NavDashboard"] = "Connect",
            ["NavViewer"] = "Remote Session",
            ["NavFiles"] = "File Manager",
            ["NavAddressBook"] = "Address Book",
            ["NavNetwork"] = "Network & Relay",
            ["NavSettings"] = "Settings & Security",

            ["MyStormId"] = "YOUR STORM ID",
            ["CopyId"] = "Copy ID",
            ["Copied"] = "Copied!",
            ["OneTimePin"] = "ONE-TIME PIN",
            ["RefreshPin"] = "Refresh PIN",
            ["IncomingStatus"] = "Incoming Access",
            ["RelayOnline"] = "Relay Online",
            ["RelayOffline"] = "LAN / Direct Only",
            ["ActiveIncoming"] = "Active Guests",

            ["QuickConnectTitle"] = "CONNECT TO REMOTE DEVICE",
            ["TargetPlaceholder"] = "Enter 9-digit STORM ID or IP:Port...",
            ["ConnectBtn"] = "Connect",
            ["DisconnectBtn"] = "Disconnect",
            ["LanDevicesTitle"] = "DISCOVERED ON LOCAL NETWORK (LAN)",
            ["NoLanDevices"] = "Scanning local network for STORM CONNECT devices...",
            ["LiveTelemetry"] = "LIVE PERFORMANCE TELEMETRY",

            ["SelectMonitor"] = "Monitor",
            ["AllMonitors"] = "All Monitors",
            ["Quality"] = "Quality",
            ["AudioToggle"] = "Audio",
            ["Fullscreen"] = "Fullscreen",
            ["CtrlAltDel"] = "TaskMgr",
            ["LockRemote"] = "Lock PC",
            ["WinRun"] = "Win+R",
            ["AltTab"] = "Alt+Tab",
            ["Screenshot"] = "Screenshot",
            ["Chat"] = "Session Chat",
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

            ["RelayServerUrl"] = "STORM CONNECT Relay URL (WSS)",
            ["DirectTcpPort"] = "Direct LAN / TCP Port",
            ["EnableRelay"] = "Enable Cloud Relay Fallback",
            ["EmbeddedRelay"] = "Run Embedded Team Relay Hub on this PC",
            ["StartHub"] = "Start Local Hub",
            ["StopHub"] = "Stop Local Hub",
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
            ["AboutDesc"] = "High-speed remote desktop, multi-monitor streaming, file manager, audio, clipboard sync, Wake-on-LAN and self-hosted encrypted relay for the STORM ecosystem.",

            ["TrayRestore"] = "Open STORM CONNECT",
            ["TrayAbout"] = "About",
            ["TrayExit"] = "Exit"
        };

        var ru = new Dictionary<string, string>(en)
        {
            ["AppSubtitle"] = "Высокоскоростной многомониторный удалённый рабочий стол, передача файлов и ретранслятор",
            ["NavDashboard"] = "Подключение",
            ["NavViewer"] = "Удалённый экран",
            ["NavFiles"] = "Файловый менеджер",
            ["NavAddressBook"] = "Адресная книга",
            ["NavNetwork"] = "Сеть и ретранслятор",
            ["NavSettings"] = "Настройки и защита",

            ["MyStormId"] = "ВАШ STORM ID",
            ["CopyId"] = "Копировать ID",
            ["Copied"] = "Скопировано!",
            ["OneTimePin"] = "ОДНОРАЗОВЫЙ PIN",
            ["RefreshPin"] = "Обновить PIN",
            ["IncomingStatus"] = "Входящий доступ",
            ["RelayOnline"] = "Ретранслятор онлайн",
            ["RelayOffline"] = "Только LAN / Прямой IP",
            ["ActiveIncoming"] = "Активные гости",

            ["QuickConnectTitle"] = "ПОДКЛЮЧЕНИЕ К УДАЛЁННОМУ УСТРОЙСТВУ",
            ["TargetPlaceholder"] = "Введите 9-значный STORM ID или IP:Порт...",
            ["ConnectBtn"] = "Подключиться",
            ["DisconnectBtn"] = "Отключить",
            ["LanDevicesTitle"] = "ОБНАРУЖЕННЫЕ УСТРОЙСТВА В ЛОКАЛЬНОЙ СЕТИ (LAN)",
            ["NoLanDevices"] = "Поиск устройств STORM CONNECT в локальной сети...",
            ["LiveTelemetry"] = "ЖИВАЯ ТЕЛЕМЕТРИЯ ПРОИЗВОДИТЕЛЬНОСТИ",

            ["SelectMonitor"] = "Монитор",
            ["AllMonitors"] = "Все мониторы",
            ["Quality"] = "Качество",
            ["AudioToggle"] = "Звук",
            ["Fullscreen"] = "Во весь экран",
            ["CtrlAltDel"] = "Диспетчер",
            ["LockRemote"] = "Блокировка",
            ["WinRun"] = "Win+R",
            ["AltTab"] = "Alt+Tab",
            ["Screenshot"] = "Снимок",
            ["Chat"] = "Чат сессии",
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

            ["RelayServerUrl"] = "Адрес ретранслятора STORM CONNECT (WSS)",
            ["DirectTcpPort"] = "Прямой порт LAN / TCP",
            ["EnableRelay"] = "Использовать облачный ретранслятор",
            ["EmbeddedRelay"] = "Встроенный командный ретранслятор на этом ПК",
            ["StartHub"] = "Запустить узел",
            ["StopHub"] = "Остановить узел",
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
            ["AboutDesc"] = "Быстрый удалённый рабочий стол, многомониторный захват DXGI, передача файлов, звук, буфер обмена, Wake-on-LAN и собственный шифрованный ретранслятор экосистемы STORM.",

            ["TrayRestore"] = "Развернуть STORM CONNECT",
            ["TrayAbout"] = "О программе",
            ["TrayExit"] = "Выход"
        };

        var de = Merge(en, new()
        {
            ["AppSubtitle"] = "Schneller Multi-Monitor-Fernzugriff, Dateitransfer & Relay",
            ["NavDashboard"] = "Verbinden", ["NavViewer"] = "Fernsitzung", ["NavFiles"] = "Dateimanager",
            ["NavAddressBook"] = "Adressbuch", ["NavNetwork"] = "Netzwerk & Relay", ["NavSettings"] = "Einstellungen",
            ["CopyId"] = "ID kopieren", ["Copied"] = "Kopiert!", ["RefreshPin"] = "PIN erneuern",
            ["ConnectBtn"] = "Verbinden", ["DisconnectBtn"] = "Trennen", ["AllMonitors"] = "Alle Monitore",
            ["Quality"] = "Qualitat", ["AudioToggle"] = "Ton", ["Fullscreen"] = "Vollbild",
            ["ColName"] = "NAME", ["ColSize"] = "GROSSE", ["ColModified"] = "GEANDERT", ["ColType"] = "TYP",
            ["ColAlias"] = "GERATENAME", ["ColActions"] = "AKTIONEN", ["AddDevice"] = "Hinzufugen",
            ["CheckUpdates"] = "Updates", ["About"] = "Uber", ["UpdateBtn"] = "Aktualisieren",
            ["Cancel"] = "Abbrechen", ["Close"] = "Schliessen", ["TrayRestore"] = "STORM CONNECT offnen",
            ["TrayAbout"] = "Uber", ["TrayExit"] = "Beenden"
        });

        var fr = Merge(en, new()
        {
            ["AppSubtitle"] = "Bureau a distance multi-ecrans rapide, transfert de fichiers et relais",
            ["NavDashboard"] = "Connexion", ["NavViewer"] = "Session distante", ["NavFiles"] = "Fichiers",
            ["NavAddressBook"] = "Carnet d'adresses", ["NavNetwork"] = "Reseau & Relais", ["NavSettings"] = "Parametres",
            ["CopyId"] = "Copier ID", ["Copied"] = "Copie!", ["RefreshPin"] = "Nouveau PIN",
            ["ConnectBtn"] = "Connecter", ["DisconnectBtn"] = "Deconnecter", ["AllMonitors"] = "Tous les ecrans",
            ["Quality"] = "Qualite", ["AudioToggle"] = "Audio", ["Fullscreen"] = "Plein ecran",
            ["ColName"] = "NOM", ["ColSize"] = "TAILLE", ["ColModified"] = "MODIFIE", ["ColType"] = "TYPE",
            ["ColAlias"] = "NOM D'APPAREIL", ["ColActions"] = "ACTIONS", ["AddDevice"] = "Ajouter",
            ["CheckUpdates"] = "Mises a jour", ["About"] = "A propos", ["UpdateBtn"] = "Mettre a jour",
            ["Cancel"] = "Annuler", ["Close"] = "Fermer", ["TrayRestore"] = "Ouvrir STORM CONNECT",
            ["TrayAbout"] = "A propos", ["TrayExit"] = "Quitter"
        });

        var es = Merge(en, new()
        {
            ["AppSubtitle"] = "Escritorio remoto multimonitor rapido, archivos y retransmision",
            ["NavDashboard"] = "Conectar", ["NavViewer"] = "Sesion remota", ["NavFiles"] = "Archivos",
            ["NavAddressBook"] = "Libreta", ["NavNetwork"] = "Red y Relay", ["NavSettings"] = "Ajustes",
            ["CopyId"] = "Copiar ID", ["Copied"] = "Copiado!", ["ConnectBtn"] = "Conectar",
            ["DisconnectBtn"] = "Desconectar", ["AllMonitors"] = "Todos los monitores", ["Quality"] = "Calidad",
            ["ColName"] = "NOMBRE", ["ColSize"] = "TAMANO", ["ColModified"] = "MODIFICADO", ["ColType"] = "TIPO",
            ["ColAlias"] = "DISPOSITIVO", ["ColActions"] = "ACCIONES", ["CheckUpdates"] = "Actualizaciones",
            ["About"] = "Acerca de", ["UpdateBtn"] = "Actualizar", ["Cancel"] = "Cancelar",
            ["Close"] = "Cerrar", ["TrayRestore"] = "Abrir STORM CONNECT", ["TrayAbout"] = "Acerca de", ["TrayExit"] = "Salir"
        });

        var it = Merge(en, new()
        {
            ["AppSubtitle"] = "Desktop remoto multi-monitor veloce, trasferimento file e relay",
            ["NavDashboard"] = "Connetti", ["NavViewer"] = "Sessione remota", ["NavFiles"] = "File",
            ["NavAddressBook"] = "Rubrica", ["NavNetwork"] = "Rete e Relay", ["NavSettings"] = "Impostazioni",
            ["CopyId"] = "Copia ID", ["Copied"] = "Copiato!", ["ConnectBtn"] = "Connetti",
            ["DisconnectBtn"] = "Disconnetti", ["AllMonitors"] = "Tutti i monitor", ["Quality"] = "Qualita",
            ["ColName"] = "NOME", ["ColSize"] = "DIMENSIONE", ["ColModified"] = "MODIFICATO", ["ColType"] = "TIPO",
            ["ColAlias"] = "DISPOSITIVO", ["ColActions"] = "AZIONI", ["CheckUpdates"] = "Aggiornamenti",
            ["About"] = "Informazioni", ["UpdateBtn"] = "Aggiorna", ["Cancel"] = "Annulla",
            ["Close"] = "Chiudi", ["TrayRestore"] = "Apri STORM CONNECT", ["TrayAbout"] = "Informazioni", ["TrayExit"] = "Esci"
        });

        var pt = Merge(en, new()
        {
            ["AppSubtitle"] = "Acesso remoto multimonitor rapido, arquivos e retransmissao",
            ["NavDashboard"] = "Conectar", ["NavViewer"] = "Sessao remota", ["NavFiles"] = "Arquivos",
            ["NavAddressBook"] = "Contatos", ["NavNetwork"] = "Rede e Relay", ["NavSettings"] = "Configuracoes",
            ["CopyId"] = "Copiar ID", ["Copied"] = "Copiado!", ["ConnectBtn"] = "Conectar",
            ["DisconnectBtn"] = "Desconectar", ["AllMonitors"] = "Todos os monitores", ["Quality"] = "Qualidade",
            ["ColName"] = "NOME", ["ColSize"] = "TAMANHO", ["ColModified"] = "MODIFICADO", ["ColType"] = "TIPO",
            ["ColAlias"] = "DISPOSITIVO", ["ColActions"] = "ACOES", ["CheckUpdates"] = "Atualizacoes",
            ["About"] = "Sobre", ["UpdateBtn"] = "Atualizar", ["Cancel"] = "Cancelar",
            ["Close"] = "Fechar", ["TrayRestore"] = "Abrir STORM CONNECT", ["TrayAbout"] = "Sobre", ["TrayExit"] = "Sair"
        });

        var zh = Merge(en, new()
        {
            ["AppSubtitle"] = "高速多显示器远程桌面、文件传输与加密中继",
            ["NavDashboard"] = "连接", ["NavViewer"] = "远程桌面", ["NavFiles"] = "文件管理",
            ["NavAddressBook"] = "地址簿", ["NavNetwork"] = "网络与中继", ["NavSettings"] = "设置与安全",
            ["CopyId"] = "复制 ID", ["Copied"] = "已复制!", ["RefreshPin"] = "刷新 PIN",
            ["ConnectBtn"] = "连接", ["DisconnectBtn"] = "断开", ["AllMonitors"] = "所有显示器",
            ["Quality"] = "画质", ["AudioToggle"] = "音频", ["Fullscreen"] = "全屏",
            ["ColName"] = "名称", ["ColSize"] = "大小", ["ColModified"] = "修改时间", ["ColType"] = "类型",
            ["ColAlias"] = "设备名称", ["ColActions"] = "操作", ["AddDevice"] = "添加设备",
            ["CheckUpdates"] = "检查更新", ["About"] = "关于", ["UpdateBtn"] = "更新",
            ["Cancel"] = "取消", ["Close"] = "关闭", ["TrayRestore"] = "打开 STORM CONNECT",
            ["TrayAbout"] = "关于", ["TrayExit"] = "退出"
        });

        var ko = Merge(en, new()
        {
            ["AppSubtitle"] = "고속 다중 모니터 원격 데스크톱, 파일 전송 및 암호화 릴레이",
            ["NavDashboard"] = "연결", ["NavViewer"] = "원격 화면", ["NavFiles"] = "파일 관리자",
            ["NavAddressBook"] = "주소록", ["NavNetwork"] = "네트워크 및 릴레이", ["NavSettings"] = "설정 및 보안",
            ["CopyId"] = "ID 복사", ["Copied"] = "복사됨!", ["RefreshPin"] = "PIN 새로고침",
            ["ConnectBtn"] = "연결", ["DisconnectBtn"] = "연결 해제", ["AllMonitors"] = "모든 모니터",
            ["Quality"] = "품질", ["AudioToggle"] = "오디오", ["Fullscreen"] = "전체 화면",
            ["ColName"] = "이름", ["ColSize"] = "크기", ["ColModified"] = "수정됨", ["ColType"] = "유형",
            ["ColAlias"] = "장치 이름", ["ColActions"] = "작업", ["AddDevice"] = "장치 추가",
            ["CheckUpdates"] = "업데이트", ["About"] = "정보", ["UpdateBtn"] = "업데이트",
            ["Cancel"] = "취소", ["Close"] = "닫기", ["TrayRestore"] = "STORM CONNECT 열기",
            ["TrayAbout"] = "정보", ["TrayExit"] = "종료"
        });

        var ja = Merge(en, new()
        {
            ["AppSubtitle"] = "高速マルチモニターリモートデスクトップ・ファイル転送・暗号化リレー",
            ["NavDashboard"] = "接続", ["NavViewer"] = "リモート画面", ["NavFiles"] = "ファイル管理",
            ["NavAddressBook"] = "アドレス帳", ["NavNetwork"] = "ネットワークとリレー", ["NavSettings"] = "設定とセキュリティ",
            ["CopyId"] = "IDをコピー", ["Copied"] = "コピー完了!", ["RefreshPin"] = "PIN更新",
            ["ConnectBtn"] = "接続", ["DisconnectBtn"] = "切断", ["AllMonitors"] = "すべてのモニター",
            ["Quality"] = "画質", ["AudioToggle"] = "音声", ["Fullscreen"] = "全画面",
            ["ColName"] = "名前", ["ColSize"] = "サイズ", ["ColModified"] = "更新日時", ["ColType"] = "種類",
            ["ColAlias"] = "デバイス名", ["ColActions"] = "操作", ["AddDevice"] = "デバイス追加",
            ["CheckUpdates"] = "更新確認", ["About"] = "情報", ["UpdateBtn"] = "更新",
            ["Cancel"] = "キャンセル", ["Close"] = "閉じる", ["TrayRestore"] = "STORM CONNECT を開く",
            ["TrayAbout"] = "情報", ["TrayExit"] = "終了"
        });

        var hi = Merge(en, new()
        {
            ["AppSubtitle"] = "तेज मल्टी-मॉनिटर रिमोट डेस्कटॉप, फाइल ट्रांसफर और रिले",
            ["NavDashboard"] = "कनेक्ट", ["NavViewer"] = "रिमोट स्क्रीन", ["NavFiles"] = "फाइल प्रबंधक",
            ["NavAddressBook"] = "एड्रेस बुक", ["NavNetwork"] = "नेटवर्क और रिले", ["NavSettings"] = "सेटिंग्स",
            ["CopyId"] = "ID कॉपी करें", ["Copied"] = "कॉपी हो गया!", ["ConnectBtn"] = "कनेक्ट",
            ["DisconnectBtn"] = "डिस्कनेक्ट", ["AllMonitors"] = "सभी मॉनिटर", ["Quality"] = "गुणवत्ता",
            ["ColName"] = "नाम", ["ColSize"] = "आकार", ["ColModified"] = "संशोधित", ["ColType"] = "प्रकार",
            ["ColAlias"] = "डिवाइस नाम", ["ColActions"] = "कार्रवाई", ["CheckUpdates"] = "अपडेट",
            ["About"] = "परिचय", ["UpdateBtn"] = "अपडेट करें", ["Cancel"] = "रद्द करें",
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
