using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace StormConnect.Desktop.Services;

/// <summary>
/// STORM Close-to-Tray & Exit-Only-from-Tray manager.
/// Renders a dark/themed context menu with Segoe UI 9pt and zero icons/emojis per STORM Law 1.
/// </summary>
public sealed class TrayManager : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _restoreItem;
    private readonly ToolStripMenuItem _aboutItem;
    private readonly ToolStripMenuItem _exitItem;

    public bool IsQuittingFromTray { get; private set; }

    public TrayManager(Action onRestore, Action onAbout, Action onExit)
    {
        _menu = new ContextMenuStrip
        {
            ShowImageMargin = false,
            Font = new Font("Segoe UI", 9f, FontStyle.Regular),
            Renderer = new StormTrayRenderer()
        };

        _restoreItem = new ToolStripMenuItem(LocalizationService.T("TrayRestore"))
        {
            Font = new Font("Segoe UI", 9f, FontStyle.Bold)
        };
        _restoreItem.Click += (_, _) => onRestore();

        _aboutItem = new ToolStripMenuItem(LocalizationService.T("TrayAbout"));
        _aboutItem.Click += (_, _) => onAbout();

        _exitItem = new ToolStripMenuItem(LocalizationService.T("TrayExit"));
        _exitItem.Click += (_, _) =>
        {
            IsQuittingFromTray = true;
            _notifyIcon!.Visible = false;
            onExit();
        };

        _menu.Items.Add(_restoreItem);
        _menu.Items.Add(_aboutItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_exitItem);

        _notifyIcon = new NotifyIcon
        {
            Text = "STORM CONNECT",
            Icon = LoadTrayIcon(),
            ContextMenuStrip = _menu,
            Visible = true
        };

        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) onRestore();
        };

        LocalizationService.LanguageChanged += RefreshLocalization;
        ThemeService.ThemeChanged += RefreshTheme;
    }

    public void RefreshLocalization()
    {
        _restoreItem.Text = LocalizationService.T("TrayRestore");
        _aboutItem.Text = LocalizationService.T("TrayAbout");
        _exitItem.Text = LocalizationService.T("TrayExit");
    }

    public void RefreshTheme()
    {
        _menu.Invalidate();
    }

    public void ShowBalloon(string title, string text)
    {
        try { _notifyIcon.ShowBalloonTip(3000, title, text, ToolTipIcon.Info); } catch { }
    }

    private static Icon LoadTrayIcon()
    {
        try
        {
            var icoPath = Path.Combine(AppContext.BaseDirectory, "AppIcon.ico");
            if (File.Exists(icoPath))
                return new Icon(icoPath, SystemInformation.SmallIconSize);
            return Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? SystemIcons.Application;
        }
        catch
        {
            return SystemIcons.Application;
        }
    }

    public void Dispose()
    {
        LocalizationService.LanguageChanged -= RefreshLocalization;
        ThemeService.ThemeChanged -= RefreshTheme;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
    }

    private sealed class StormTrayRenderer : ToolStripProfessionalRenderer
    {
        public StormTrayRenderer() : base(new StormTrayColors()) { }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            var bg = ToGdi(ThemeService.CurrentTheme.HeaderBg);
            using var b = new SolidBrush(bg);
            e.Graphics.FillRectangle(b, e.AffectedBounds);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            var accent = ToGdi(ThemeService.CurrentAccent.PrimaryHex);
            using var p = new Pen(accent, 1f);
            e.Graphics.DrawRectangle(p, 0, 0, e.AffectedBounds.Width - 1, e.AffectedBounds.Height - 1);
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            var rect = new Rectangle(2, 1, e.Item.Width - 4, e.Item.Height - 2);
            if (e.Item.Selected)
            {
                var hover = ToGdi(ThemeService.CurrentTheme.IsLight ? "#E2E8F0" : "#2E2652");
                using var b = new SolidBrush(hover);
                e.Graphics.FillRectangle(b, rect);
            }
            else
            {
                var bg = ToGdi(ThemeService.CurrentTheme.HeaderBg);
                using var b = new SolidBrush(bg);
                e.Graphics.FillRectangle(b, rect);
            }
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = ToGdi(ThemeService.CurrentTheme.TextPrimary);
            base.OnRenderItemText(e);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            var sep = ToGdi(ThemeService.CurrentTheme.CardBorder);
            using var p = new Pen(sep, 1f);
            int y = e.Item.Height / 2;
            e.Graphics.DrawLine(p, 6, y, e.Item.Width - 6, y);
        }

        private static Color ToGdi(string hex) => ColorTranslator.FromHtml(hex);
    }

    private sealed class StormTrayColors : ProfessionalColorTable
    {
        public override Color MenuStripGradientBegin => ColorTranslator.FromHtml(ThemeService.CurrentTheme.HeaderBg);
        public override Color MenuStripGradientEnd => ColorTranslator.FromHtml(ThemeService.CurrentTheme.HeaderBg);
        public override Color ToolStripDropDownBackground => ColorTranslator.FromHtml(ThemeService.CurrentTheme.HeaderBg);
    }
}
