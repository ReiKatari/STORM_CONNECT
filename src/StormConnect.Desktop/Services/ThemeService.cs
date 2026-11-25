using System.Windows;
using System.Windows.Media;

namespace StormConnect.Desktop.Services;

public sealed record StormThemePalette(
    string Name,
    bool IsLight,
    string BgPrimary,
    string BgSecondary,
    string HeaderBg,
    string CardBg,
    string CardBorder,
    string InputBg,
    string TextPrimary,
    string TextSecondary,
    string Success,
    string Warning,
    string Error,
    bool HasAuroraFx
)
{
    public override string ToString() => Name;
}

public sealed record StormAccentOption(string Name, string PrimaryHex, string SecondaryHex)
{
    public override string ToString() => Name;
}

public sealed record StormFontOption(string DisplayName, string WpfFamily)
{
    public override string ToString() => DisplayName;
}

public static class ThemeService
{
    public static readonly IReadOnlyList<StormThemePalette> Themes =
    [
        new("MIDNIGHT", false, "#0D0B18", "#141026", "#16112B", "#1B1536", "#352B63", "#100D1E", "#FFFFFF", "#B8B2D8", "#34D399", "#FBBF24", "#FB7185", false),
        new("NIGHT", false, "#000000", "#09090D", "#0B0B10", "#121218", "#272736", "#07070A", "#FFFFFF", "#9CA3AF", "#34D399", "#FBBF24", "#F87171", false),
        new("DARK", false, "#111827", "#182235", "#1E293B", "#1F2937", "#374151", "#0F172A", "#F8FAFC", "#94A3B8", "#34D399", "#FBBF24", "#F87171", false),
        new("DAY", true, "#F1F5F9", "#FFFFFF", "#E2E8F0", "#FFFFFF", "#CBD5E1", "#F8FAFC", "#0F172A", "#475569", "#059669", "#D97706", "#DC2626", false),
        new("FANTASY", false, "#140F0B", "#211912", "#261C14", "#2C2017", "#6B4E2E", "#120D09", "#FEF3C7", "#D6B78A", "#4ADE80", "#FBBF24", "#F87171", false),
        new("CYBERPUNK", false, "#090A14", "#121426", "#151830", "#191C3A", "#3D2C6D", "#080912", "#F8FAFC", "#67E8F9", "#22D3EE", "#FACC15", "#F43F5E", false),
        new("WARHAMMER 40K", false, "#111010", "#1C1A19", "#221E1C", "#24201E", "#6B5636", "#0D0C0C", "#F5F0E6", "#BFA77A", "#4ADE80", "#EAB308", "#EF4444", false),
        new("ELEMENTAL", false, "#08161A", "#0E2329", "#102A31", "#132E36", "#235361", "#061115", "#ECFEFF", "#7DD3FC", "#34D399", "#F59E0B", "#FB7185", false),
        new("STORM", false, "#0A071A", "#120E2E", "#161138", "#1A1440", "#4A388C", "#090616", "#FFFFFF", "#C4B5FD", "#34D399", "#FBBF24", "#FB7185", true)
    ];

    public static readonly IReadOnlyList<StormAccentOption> Accents =
    [
        new("Violet", "#8B5CF6", "#06B6D4"),
        new("Cyan", "#06B6D4", "#3B82F6"),
        new("Electric Blue", "#3B82F6", "#6366F1"),
        new("Royal Indigo", "#6366F1", "#8B5CF6"),
        new("Teal Mint", "#14B8A6", "#10B981"),
        new("Emerald Green", "#10B981", "#06B6D4"),
        new("Neon Lime", "#84CC16", "#10B981"),
        new("Amber Gold", "#F59E0B", "#F97316"),
        new("Solar Orange", "#F97316", "#EF4444"),
        new("Crimson Red", "#EF4444", "#F43F5E"),
        new("Rose Pink", "#F43F5E", "#EC4899"),
        new("Magenta Fuchsia", "#EC4899", "#8B5CF6"),
        new("Platinum Silver", "#94A3B8", "#64748B")
    ];

    public static readonly IReadOnlyList<StormFontOption> Fonts =
    [
        new("Segoe UI", "Segoe UI"),
        new("Bahnschrift", "Bahnschrift"),
        new("Calibri", "Calibri"),
        new("Verdana", "Verdana"),
        new("Tahoma", "Tahoma"),
        new("Trebuchet MS", "Trebuchet MS"),
        new("Candara", "Candara"),
        new("Georgia", "Georgia"),
        new("Cascadia Code", "Cascadia Code, Consolas"),
        new("Consolas", "Consolas")
    ];

    public static StormThemePalette CurrentTheme { get; private set; } = Themes[0];
    public static StormAccentOption CurrentAccent { get; private set; } = Accents[0];
    public static StormFontOption CurrentFont { get; private set; } = Fonts[0];

    public static event Action? ThemeChanged;

    public static void Apply(string themeName, string accentName, string fontName)
    {
        var theme = Themes.FirstOrDefault(t => string.Equals(t.Name, themeName, StringComparison.OrdinalIgnoreCase)) ?? Themes[0];
        var accent = Accents.FirstOrDefault(a => string.Equals(a.Name, accentName, StringComparison.OrdinalIgnoreCase)) ?? Accents[0];
        var font = Fonts.FirstOrDefault(f => string.Equals(f.DisplayName, fontName, StringComparison.OrdinalIgnoreCase)) ?? Fonts[0];

        CurrentTheme = theme;
        CurrentAccent = accent;
        CurrentFont = font;

        var app = System.Windows.Application.Current;
        if (app is null) return;

        var res = app.Resources;
        SetBrush(res, "ThemeBgPrimary", theme.BgPrimary);
        SetBrush(res, "ThemeBgSecondary", theme.BgSecondary);
        SetBrush(res, "ThemeHeaderBg", theme.HeaderBg);
        SetBrush(res, "ThemeCardBg", theme.CardBg);
        SetBrush(res, "ThemeCardBorder", theme.CardBorder);
        SetBrush(res, "ThemeInputBg", theme.InputBg);
        SetBrush(res, "ThemeTextPrimary", theme.TextPrimary);
        SetBrush(res, "ThemeTextSecondary", theme.TextSecondary);
        SetBrush(res, "ThemeSuccess", theme.Success);
        SetBrush(res, "ThemeWarning", theme.Warning);
        SetBrush(res, "ThemeError", theme.Error);

        var accentCol = ParseColor(accent.PrimaryHex);
        var accent2Col = ParseColor(accent.SecondaryHex);
        res["ThemeAccentColor"] = accentCol;
        res["ThemeAccent"] = new SolidColorBrush(accentCol);
        res["ThemeAccentSecondary"] = new SolidColorBrush(accent2Col);
        res["ThemeAccentSoft"] = new SolidColorBrush(System.Windows.Media.Color.FromArgb(46, accentCol.R, accentCol.G, accentCol.B));
        res["ThemeAccentHover"] = new SolidColorBrush(System.Windows.Media.Color.FromArgb(80, accentCol.R, accentCol.G, accentCol.B));

        // 3D Primary Button Gradient with guaranteed white text contrast
        var grad = new LinearGradientBrush(
            DarkenForContrast(accentCol, theme.IsLight),
            DarkenForContrast(accent2Col, theme.IsLight),
            45.0);
        res["ThemeAccentGradient"] = grad;

        var fontFamily = new System.Windows.Media.FontFamily(font.WpfFamily);
        res["ThemeFontFamily"] = fontFamily;
        res["ThemeScrollThumb"] = new SolidColorBrush(theme.IsLight
            ? System.Windows.Media.Color.FromArgb(110, 100, 116, 139)
            : System.Windows.Media.Color.FromArgb(95, 255, 255, 255));

        foreach (System.Windows.Window win in app.Windows)
        {
            win.FontFamily = fontFamily;
            System.Windows.Documents.TextElement.SetFontFamily(win, fontFamily);
        }

        ThemeChanged?.Invoke();
    }

    private static void SetBrush(ResourceDictionary res, string key, string hex)
    {
        var c = ParseColor(hex);
        res[key + "Color"] = c;
        res[key] = new SolidColorBrush(c);
    }

    public static System.Windows.Media.Color ParseColor(string hex)
    {
        return (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
    }

    private static System.Windows.Media.Color DarkenForContrast(System.Windows.Media.Color c, bool isLight)
    {
        double lum = (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;
        if (lum > 0.55)
        {
            double f = isLight ? 0.65 : 0.72;
            return System.Windows.Media.Color.FromRgb((byte)(c.R * f), (byte)(c.G * f), (byte)(c.B * f));
        }
        return c;
    }
}
