using System.Runtime.InteropServices;
using System.Windows;
using StormConnect.Desktop.Services;
using StormConnect.Desktop.Views;

namespace StormConnect.Desktop;

public partial class App : System.Windows.Application
{
    public static SettingsStore Settings { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try { SetCurrentProcessExplicitAppUserModelID("STORM.CONNECT"); } catch { }

        Settings = new SettingsStore();
        LocalizationService.SetLanguage(Settings.Current.Language);
        ThemeService.Apply(Settings.Current.Theme, Settings.Current.Accent, Settings.Current.FontFamily);

        var mainWin = new MainWindow();
        MainWindow = mainWin;

        if (!e.Args.Contains("--minimized") && !Settings.Current.StartMinimizedToTray)
        {
            mainWin.Show();
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string AppID);
}
