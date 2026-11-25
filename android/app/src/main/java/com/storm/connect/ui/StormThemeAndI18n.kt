package com.storm.connect.ui

import android.graphics.Color
import android.graphics.Typeface
import java.util.Locale

data class StormTheme(
    val name: String,
    val isLight: Boolean,
    val bgPrimary: Int,
    val bgSecondary: Int,
    val cardBg: Int,
    val cardBorder: Int,
    val textPrimary: Int,
    val textSecondary: Int
)

data class StormAccent(val name: String, val primary: Int, val secondary: Int)
data class StormFont(val name: String, val typeface: Typeface)

object StormUiCatalog {
    val themes = listOf(
        StormTheme("MIDNIGHT", false, Color.parseColor("#0D0B18"), Color.parseColor("#141026"), Color.parseColor("#1B1536"), Color.parseColor("#352B63"), Color.parseColor("#FFFFFF"), Color.parseColor("#B8B2D8")),
        StormTheme("NIGHT", false, Color.parseColor("#000000"), Color.parseColor("#09090D"), Color.parseColor("#121218"), Color.parseColor("#272736"), Color.parseColor("#FFFFFF"), Color.parseColor("#9CA3AF")),
        StormTheme("DARK", false, Color.parseColor("#111827"), Color.parseColor("#182235"), Color.parseColor("#1F2937"), Color.parseColor("#374151"), Color.parseColor("#F8FAFC"), Color.parseColor("#94A3B8")),
        StormTheme("DAY", true, Color.parseColor("#F1F5F9"), Color.parseColor("#FFFFFF"), Color.parseColor("#FFFFFF"), Color.parseColor("#CBD5E1"), Color.parseColor("#0F172A"), Color.parseColor("#475569")),
        StormTheme("FANTASY", false, Color.parseColor("#140F0B"), Color.parseColor("#211912"), Color.parseColor("#2C2017"), Color.parseColor("#6B4E2E"), Color.parseColor("#FEF3C7"), Color.parseColor("#D6B78A")),
        StormTheme("CYBERPUNK", false, Color.parseColor("#090A14"), Color.parseColor("#121426"), Color.parseColor("#191C3A"), Color.parseColor("#3D2C6D"), Color.parseColor("#F8FAFC"), Color.parseColor("#67E8F9")),
        StormTheme("WARHAMMER 40K", false, Color.parseColor("#111010"), Color.parseColor("#1C1A19"), Color.parseColor("#24201E"), Color.parseColor("#6B5636"), Color.parseColor("#F5F0E6"), Color.parseColor("#BFA77A")),
        StormTheme("ELEMENTAL", false, Color.parseColor("#08161A"), Color.parseColor("#0E2329"), Color.parseColor("#132E36"), Color.parseColor("#235361"), Color.parseColor("#ECFEFF"), Color.parseColor("#7DD3FC")),
        StormTheme("STORM", false, Color.parseColor("#0A071A"), Color.parseColor("#120E2E"), Color.parseColor("#1A1440"), Color.parseColor("#4A388C"), Color.parseColor("#FFFFFF"), Color.parseColor("#C4B5FD"))
    )

    val accents = listOf(
        StormAccent("Violet", Color.parseColor("#8B5CF6"), Color.parseColor("#06B6D4")),
        StormAccent("Cyan", Color.parseColor("#06B6D4"), Color.parseColor("#3B82F6")),
        StormAccent("Electric Blue", Color.parseColor("#3B82F6"), Color.parseColor("#6366F1")),
        StormAccent("Royal Indigo", Color.parseColor("#6366F1"), Color.parseColor("#8B5CF6")),
        StormAccent("Teal Mint", Color.parseColor("#14B8A6"), Color.parseColor("#10B981")),
        StormAccent("Emerald Green", Color.parseColor("#10B981"), Color.parseColor("#06B6D4")),
        StormAccent("Neon Lime", Color.parseColor("#84CC16"), Color.parseColor("#10B981")),
        StormAccent("Amber Gold", Color.parseColor("#F59E0B"), Color.parseColor("#F97316")),
        StormAccent("Solar Orange", Color.parseColor("#F97316"), Color.parseColor("#EF4444")),
        StormAccent("Crimson Red", Color.parseColor("#EF4444"), Color.parseColor("#F43F5E")),
        StormAccent("Rose Pink", Color.parseColor("#F43F5E"), Color.parseColor("#EC4899")),
        StormAccent("Magenta Fuchsia", Color.parseColor("#EC4899"), Color.parseColor("#8B5CF6")),
        StormAccent("Platinum Silver", Color.parseColor("#94A3B8"), Color.parseColor("#64748B"))
    )

    val fonts = listOf(
        StormFont("Inter", Typeface.SANS_SERIF),
        StormFont("Plus Jakarta Sans", Typeface.SANS_SERIF),
        StormFont("Geist Sans", Typeface.SANS_SERIF),
        StormFont("Manrope", Typeface.SANS_SERIF),
        StormFont("Outfit", Typeface.SANS_SERIF),
        StormFont("Montserrat", Typeface.SANS_SERIF),
        StormFont("Rubik", Typeface.SANS_SERIF),
        StormFont("Orbitron", Typeface.create(Typeface.SANS_SERIF, Typeface.BOLD)),
        StormFont("JetBrains Mono", Typeface.MONOSPACE),
        StormFont("Cascadia Code", Typeface.MONOSPACE)
    )

    val languages = listOf(
        "ru" to "Русский",
        "en" to "English",
        "de" to "Deutsch",
        "fr" to "Français",
        "zh" to "简体中文",
        "ko" to "한국어",
        "ja" to "日本語",
        "es" to "Español",
        "it" to "Italiano",
        "pt" to "Português",
        "hi" to "हिन्दी"
    )

    fun detectSystemLanguage(): String {
        val code = Locale.getDefault().language.lowercase()
        return if (languages.any { it.first == code }) code else "en"
    }

    fun t(lang: String, key: String): String {
        val ru = mapOf(
            "subtitle" to "Удалённый рабочий стол и передача файлов",
            "my_id" to "ВАШ STORM ID",
            "connect" to "Подключиться",
            "disconnect" to "Отключить",
            "target_hint" to "STORM ID или IP:Порт",
            "lan_title" to "УСТРОЙСТВА В ЛОКАЛЬНОЙ СЕТИ",
            "viewer" to "Удалённый экран",
            "files" to "Файлы",
            "settings" to "Настройки",
            "update" to "Обновить",
            "check_updates" to "Проверить обновления",
            "relay_url" to "Адрес ретранслятора (WSS)",
            "monitors" to "Мониторы",
            "storage_ok" to "Папка: /storage/emulated/0/STORM CONNECT"
        )
        val en = mapOf(
            "subtitle" to "Remote Desktop, Multi-Monitor & File Transfer",
            "my_id" to "YOUR STORM ID",
            "connect" to "Connect",
            "disconnect" to "Disconnect",
            "target_hint" to "Enter STORM ID or IP:Port",
            "lan_title" to "LOCAL NETWORK DEVICES (LAN)",
            "viewer" to "Remote Viewer",
            "files" to "Files",
            "settings" to "Settings",
            "update" to "Update",
            "check_updates" to "Check Updates",
            "relay_url" to "Relay URL (WSS)",
            "monitors" to "Monitors",
            "storage_ok" to "Storage: /storage/emulated/0/STORM CONNECT"
        )
        return when (lang) {
            "ru" -> ru[key] ?: en[key] ?: key
            "de" -> mapOf("connect" to "Verbinden", "disconnect" to "Trennen", "viewer" to "Fernzugriff", "files" to "Dateien", "settings" to "Einstellungen", "update" to "Aktualisieren")[key] ?: en[key] ?: key
            "fr" -> mapOf("connect" to "Connecter", "disconnect" to "Deconnecter", "viewer" to "Ecran distant", "files" to "Fichiers", "settings" to "Parametres", "update" to "Mettre a jour")[key] ?: en[key] ?: key
            "es" -> mapOf("connect" to "Conectar", "disconnect" to "Desconectar", "viewer" to "Pantalla remota", "files" to "Archivos", "settings" to "Ajustes", "update" to "Actualizar")[key] ?: en[key] ?: key
            "it" -> mapOf("connect" to "Connetti", "disconnect" to "Disconnetti", "viewer" to "Schermo remoto", "files" to "File", "settings" to "Impostazioni", "update" to "Aggiorna")[key] ?: en[key] ?: key
            "pt" -> mapOf("connect" to "Conectar", "disconnect" to "Desconectar", "viewer" to "Tela remota", "files" to "Arquivos", "settings" to "Ajustes", "update" to "Atualizar")[key] ?: en[key] ?: key
            "zh" -> mapOf("connect" to "连接", "disconnect" to "断开", "viewer" to "远程桌面", "files" to "文件", "settings" to "设置", "update" to "更新")[key] ?: en[key] ?: key
            "ko" -> mapOf("connect" to "연결", "disconnect" to "연결 해제", "viewer" to "원격 화면", "files" to "파일", "settings" to "설정", "update" to "업데이트")[key] ?: en[key] ?: key
            "ja" -> mapOf("connect" to "接続", "disconnect" to "切断", "viewer" to "リモート画面", "files" to "ファイル", "settings" to "設定", "update" to "更新")[key] ?: en[key] ?: key
            "hi" -> mapOf("connect" to "कनेक्ट", "disconnect" to "डिस्कनेक्ट", "viewer" to "रिमोट स्क्रीन", "files" to "फाइलें", "settings" to "सेटिंग्स", "update" to "अपडेट करें")[key] ?: en[key] ?: key
            else -> en[key] ?: key
        }
    }
}
