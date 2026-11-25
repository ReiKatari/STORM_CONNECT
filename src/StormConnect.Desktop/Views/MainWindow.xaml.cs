using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Brushes = System.Windows.Media.Brushes;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using StormConnect.Desktop.Engine;
using StormConnect.Desktop.Networking;
using StormConnect.Desktop.Services;
using StormConnect.Protocol;

namespace StormConnect.Desktop.Views;

public sealed record FileGridRow(string Name, string FullPath, bool IsDirectory, long SizeBytes, string SizeDisplay, string ModifiedDisplay);
public sealed record AddressGridRow(AddressBookEntry Entry, string Alias, string FormattedId, string HostOrIp, string Group, string MacAddress);

public partial class MainWindow : Window
{
    private readonly ClipboardWatcher _clipboardWatcher = new();
    private readonly LanDiscoveryService _lanDiscovery = new();
    private readonly EmbeddedRelayHost _embeddedRelay = new();
    private HostServer? _hostServer;
    private ClientSession? _clientSession;
    private TrayManager? _tray;

    private string _currentPin = string.Empty;
    private string _currentLocalDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private string _currentRemoteDir = "";
    private int _currentQuality = 75;
    private bool _audioEnabled = true;
    private bool _isFullscreen;
    private bool _initializingUi = true;
    private DateTime _lastMouseMoveSent = DateTime.MinValue;
    private UpdateCheckResult? _pendingUpdate;
    private CancellationTokenSource? _updateDlCts;

    private TaskCompletionSource<ConsentDecisionPayload>? _consentTcs;
    private TaskCompletionSource<string?>? _authTcs;

    public MainWindow()
    {
        InitializeComponent();

        // Clamp default window size to screen WorkArea (Law 17)
        var wa = SystemParameters.WorkArea;
        Width = Math.Min(1440, wa.Width - 32);
        Height = Math.Min(860, wa.Height - 32);

        RegeneratePin();
        InitializeSelectors();
        InitializeTrayAndServices();
        ApplyLocalization();
        ApplyThemeVisuals();
        LoadSettingsToUi();
        LoadLocalDirectory(_currentLocalDir);
        RefreshAddressBookGrid();
        RefreshAuditGrid();

        _initializingUi = false;

        // Silent background Auto-Update check (Law 9)
        _ = CheckUpdatesSilentlyAsync();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        EnableDarkDwmTitleBar(hwnd);

        // UAC UIPI Drag-and-Drop bypass (Law 22)
        ChangeWindowMessageFilterEx(hwnd, 0x0233 /* WM_DROPFILES */, 1 /* MSGFLT_ALLOW */, IntPtr.Zero);
        ChangeWindowMessageFilterEx(hwnd, 0x0049 /* WM_COPYGLOBALDATA */, 1, IntPtr.Zero);
        ChangeWindowMessageFilterEx(hwnd, 0x004A /* WM_COPYDATA */, 1, IntPtr.Zero);
        DragAcceptFiles(hwnd, true);
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProcHook);
    }

    private IntPtr WndProcHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_DROPFILES = 0x0233;
        if (msg == WM_DROPFILES)
        {
            uint count = DragQueryFile(wParam, 0xFFFFFFFF, null, 0);
            var paths = new List<string>();
            for (uint i = 0; i < count; i++)
            {
                var sb = new System.Text.StringBuilder(1024);
                if (DragQueryFile(wParam, i, sb, sb.Capacity) > 0)
                    paths.Add(sb.ToString());
            }
            DragFinish(wParam);
            if (paths.Count > 0) _ = HandleDroppedPathsAsync(paths);
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void InitializeSelectors()
    {
        CmbLang.ItemsSource = LocalizationService.Languages;
        CmbLang.DisplayMemberPath = "DisplayName";
        CmbLang.SelectedItem = LocalizationService.Languages.FirstOrDefault(l => l.Code == LocalizationService.CurrentLanguage)
                               ?? LocalizationService.Languages[0];

        CmbTheme.ItemsSource = ThemeService.Themes;
        CmbTheme.DisplayMemberPath = "Name";
        CmbTheme.SelectedItem = ThemeService.CurrentTheme;

        CmbAccent.ItemsSource = ThemeService.Accents;
        CmbAccent.DisplayMemberPath = "Name";
        CmbAccent.SelectedItem = ThemeService.CurrentAccent;

        CmbFont.ItemsSource = ThemeService.Fonts;
        CmbFont.DisplayMemberPath = "DisplayName";
        CmbFont.SelectedItem = ThemeService.CurrentFont;

        CmbQuality.ItemsSource = new[] { "90% HD", "75% Balanced", "55% Fast", "35% Low-BW" };
        CmbQuality.SelectedIndex = 1;
    }

    private void InitializeTrayAndServices()
    {
        var s = App.Settings.Current;
        TxtMyStormId.Text = DeviceIdentity.FormatId(s.StormId);
        TxtWindowTitle.Text = $"STORM CONNECT {UpdateService.GetCurrentVersionString()}";
        TxtAboutVersion.Text = UpdateService.GetCurrentVersionString();

        _tray = new TrayManager(
            onRestore: () => Dispatcher.Invoke(() =>
            {
                Show();
                if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                Activate();
            }),
            onAbout: () => Dispatcher.Invoke(() =>
            {
                Show();
                Activate();
                ModalAbout.Visibility = Visibility.Visible;
            }),
            onExit: () => Dispatcher.Invoke(ShutdownCompletely));

        _hostServer = new HostServer(
            s.StormId,
            () => _currentPin,
            () => App.Settings.GetUnattendedCredentials(),
            () => new SessionPermissions
            {
                AllowControl = App.Settings.Current.DefaultAllowControl,
                AllowClipboard = App.Settings.Current.DefaultAllowClipboard,
                AllowFileTransfer = App.Settings.Current.DefaultAllowFileTransfer,
                AllowAudio = App.Settings.Current.DefaultAllowAudio,
                AllowSystemActions = App.Settings.Current.DefaultAllowSystemActions
            },
            () => App.Settings.Current.AllowIncoming,
            PromptHostConsentAsync,
            _clipboardWatcher);

        _hostServer.RelayStatusChanged += online => Dispatcher.Invoke(() => UpdateRelayBadge(online));
        _hostServer.ClientConnected += ctx => Dispatcher.Invoke(() =>
        {
            TxtActiveGuestsBadge.Text = $"{LocalizationService.T("ActiveIncoming")}: {_hostServer.ActiveClientCount}";
            App.Settings.AddAudit($"{ctx.ClientName} ({ctx.ClientStormId})", ctx.Mode.ToString(), "Incoming Connected");
            RefreshAuditGrid();
            _tray?.ShowBalloon("STORM CONNECT", $"{ctx.ClientName} connected ({ctx.Mode})");
        });
        _hostServer.ClientDisconnected += _ => Dispatcher.Invoke(() =>
        {
            TxtActiveGuestsBadge.Text = $"{LocalizationService.T("ActiveIncoming")}: {_hostServer.ActiveClientCount}";
        });
        _hostServer.ChatReceived += msg => Dispatcher.Invoke(() => AppendChatMessage(msg));
        _hostServer.Start(s.DirectPort, s.RelayUrl, s.EnableRelay);

        _lanDiscovery.DevicesUpdated += devices => Dispatcher.Invoke(() => RefreshLanDevices(devices));
        _lanDiscovery.Start(s.StormId, () => new LanBeaconPacket(
            s.StormId,
            Environment.MachineName,
            "Windows",
            DxgiScreenCapturer.GetMonitors().Count(m => m.Index >= 0),
            s.DirectPort,
            LanDiscoveryService.GetPrimaryLocalIp(),
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
    }

    private void LoadSettingsToUi()
    {
        var s = App.Settings.Current;
        ChkAllowIncoming.IsChecked = s.AllowIncoming;
        TxtRelayUrl.Text = s.RelayUrl;
        TxtDirectPort.Text = s.DirectPort.ToString();
        ChkEnableRelay.IsChecked = s.EnableRelay;
        ChkPermControl.IsChecked = s.DefaultAllowControl;
        ChkPermClipboard.IsChecked = s.DefaultAllowClipboard;
        ChkPermFiles.IsChecked = s.DefaultAllowFileTransfer;
        ChkPermAudio.IsChecked = s.DefaultAllowAudio;
        ChkPermSystem.IsChecked = s.DefaultAllowSystemActions;
        SliderUiScale.Value = Math.Clamp(s.UiScale, 0.80, 1.35);
        UiScaleTransform.ScaleX = SliderUiScale.Value;
        UiScaleTransform.ScaleY = SliderUiScale.Value;
        TxtScaleVal.Text = $"{Math.Round(SliderUiScale.Value * 100)}%";
    }

    private void ApplyLocalization()
    {
        string T(string k) => LocalizationService.T(k);
        TxtSubtitle.Text = T("AppSubtitle");
        TxtBtnUpdates.Text = T("CheckUpdates");
        TxtBtnAbout.Text = T("About");
        CmbLang.ToolTip = T("TipLang");
        CmbTheme.ToolTip = T("TipTheme");
        CmbAccent.ToolTip = T("TipAccent");
        CmbFont.ToolTip = T("TipFont");
        BtnUpdates.ToolTip = T("TipUpdates");
        BtnAbout.ToolTip = T("TipAbout");

        TxtNavDashboard.Text = T("NavDashboard");
        TxtNavViewer.Text = T("NavViewer");
        TxtNavFiles.Text = T("NavFiles");
        TxtNavAddressBook.Text = T("NavAddressBook");
        TxtNavNetwork.Text = T("NavNetwork");
        TxtNavSettings.Text = T("NavSettings");

        TxtLabelMyId.Text = T("MyStormId");
        TxtBtnCopyId.Text = T("CopyId");
        TxtLabelPin.Text = T("OneTimePin");
        TxtBtnRefreshPin.Text = T("RefreshPin");
        ChkAllowIncoming.Content = T("IncomingStatus");
        TxtLabelQuickConnect.Text = T("QuickConnectTitle");
        TxtBtnConnect.Text = T("ConnectBtn");
        TxtLabelLanDevices.Text = T("LanDevicesTitle");
        TxtNoLanDevices.Text = T("NoLanDevices");
        TxtLabelTelemetry.Text = T("LiveTelemetry");

        TxtBtnTaskMgr.Text = T("CtrlAltDel");
        TxtBtnLockRemote.Text = T("LockRemote");
        TxtBtnScreenshot.Text = T("Screenshot");
        TxtBtnChat.Text = T("Chat");
        TxtBtnFullscreen.Text = T("Fullscreen");
        TxtBtnDisconnect.Text = T("DisconnectBtn");
        TxtNoActiveSession.Text = T("NoActiveSession");
        TxtDropFilesHint.Text = T("DropFilesHint");
        TxtBtnAudio.Text = $"{T("AudioToggle")}: {(_audioEnabled ? "ON" : "OFF")}";

        TxtLabelLocalFiles.Text = T("LocalFiles");
        TxtLabelRemoteFiles.Text = T("RemoteFiles");
        TxtLocalUp.Text = T("UpFolder");
        TxtRemoteUp.Text = T("UpFolder");
        TxtRemoteRefresh.Text = T("Refresh");
        TxtUploadBtn.Text = T("Upload");
        TxtDownloadBtn.Text = T("Download");
        ColLocalName.Header = T("ColName");
        ColLocalSize.Header = T("ColSize");
        ColLocalMod.Header = T("ColModified");
        ColRemoteName.Header = T("ColName");
        ColRemoteSize.Header = T("ColSize");
        ColRemoteMod.Header = T("ColModified");

        TxtBtnAddDevice.Text = T("AddDevice");
        ColBookAlias.Header = T("ColAlias");
        ColBookStormId.Header = T("ColStormId");
        ColBookHost.Header = T("ColHost");
        ColBookGroup.Header = T("ColGroup");
        ColBookMac.Header = T("ColMac");
        ColBookActions.Header = T("ColActions");

        TxtLabelRelayUrl.Text = T("RelayServerUrl");
        TxtLabelDirectPort.Text = T("DirectTcpPort");
        ChkEnableRelay.Content = T("EnableRelay");
        TxtLabelEmbeddedRelay.Text = T("EmbeddedRelay");
        TxtDeployNote.Text = T("DeployNote");
        TxtBtnEmbeddedRelay.Text = _embeddedRelay.IsRunning ? T("StopHub") : T("StartHub");

        TxtLabelUnattended.Text = T("UnattendedPwd");
        TxtBtnSavePwd.Text = T("SavePassword");
        TxtBtnClearPwd.Text = T("ClearPassword");
        ChkPermControl.Content = T("PermControl");
        ChkPermClipboard.Content = T("PermClipboard");
        ChkPermFiles.Content = T("PermFiles");
        ChkPermAudio.Content = T("PermAudio");
        ChkPermSystem.Content = T("PermSystem");
        TxtLabelUiScale.Text = T("UiScale");
        TxtLabelAuditLog.Text = T("AuditLogTitle");
        ColAuditTime.Header = T("ColTime");
        ColAuditPeer.Header = T("ColPeer");
        ColAuditMode.Header = T("ColMode");
        ColAuditAction.Header = T("ColEvent");

        TxtConsentTitle.Text = T("ConsentTitle");
        TxtConsentAccept.Text = T("AcceptFull");
        TxtConsentViewOnly.Text = T("AcceptView");
        TxtConsentDecline.Text = T("Decline");
        TxtAuthTitle.Text = T("AuthPromptTitle");
        TxtAuthMsg.Text = T("AuthPromptMsg");
        TxtAuthOk.Text = T("Ok");
        TxtAuthCancel.Text = T("Cancel");
        TxtUpdateTitle.Text = T("UpdateAvailableTitle");
        TxtUpdateInstall.Text = T("UpdateBtn");
        TxtUpdateLater.Text = T("LaterBtn");
        TxtAboutDesc.Text = T("AboutDesc");
        TxtAboutClose.Text = T("Close");

        UpdateRelayBadge(_hostServer?.IsRelayConnected == true);
        TxtActiveGuestsBadge.Text = $"{T("ActiveIncoming")}: {_hostServer?.ActiveClientCount ?? 0}";
    }

    private void ApplyThemeVisuals()
    {
        AuroraBackdrop.Visibility = ThemeService.CurrentTheme.HasAuroraFx ? Visibility.Visible : Visibility.Collapsed;
        TelemetryChart.Redraw();
    }

    private void UpdateRelayBadge(bool online)
    {
        TxtRelayBadge.Text = online ? LocalizationService.T("RelayOnline") : LocalizationService.T("RelayOffline");
        DotRelayStatus.Fill = (Brush)FindResource(online ? "ThemeSuccess" : "ThemeWarning");
    }

    private void RegeneratePin()
    {
        _currentPin = RandomNumberGenerator.GetInt32(100_000, 1_000_000).ToString();
        TxtMyPin.Text = _currentPin;
    }

    private void RefreshLanDevices(IReadOnlyList<DiscoveredDevice> devices)
    {
        LanDevicesPanel.Children.Clear();
        TxtNoLanDevices.Visibility = devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var d in devices)
        {
            var card = new Border
            {
                Background = (Brush)FindResource("ThemeBgSecondary"),
                BorderBrush = (Brush)FindResource("ThemeCardBorder"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(14, 10, 14, 10),
                Margin = new Thickness(0, 0, 10, 10),
                MinWidth = 230
            };

            var stack = new StackPanel();
            stack.Children.Add(new TextBlock { Text = d.HostName, FontWeight = FontWeights.Bold, FontSize = 14 });
            stack.Children.Add(new TextBlock
            {
                Text = $"{DeviceIdentity.FormatId(d.StormId)} | {d.LocalIp}",
                FontSize = 12,
                Foreground = (Brush)FindResource("ThemeAccent"),
                Margin = new Thickness(0, 3, 0, 2)
            });
            stack.Children.Add(new TextBlock
            {
                Text = $"{d.OsName} | {d.MonitorCount} Display(s)",
                FontSize = 11.5,
                Foreground = (Brush)FindResource("ThemeTextSecondary"),
                Margin = new Thickness(0, 0, 0, 8)
            });

            var btn = new Button
            {
                Style = (Style)FindResource("StormPrimaryBtn"),
                Padding = new Thickness(12, 6, 12, 6),
                Content = new TextBlock { Text = LocalizationService.T("ConnectBtn"), Foreground = Brushes.White, FontSize = 12 }
            };
            btn.Click += (_, _) => _ = ConnectToTargetAsync(d.StormId);
            stack.Children.Add(btn);
            card.Child = stack;
            LanDevicesPanel.Children.Add(card);
        }
    }

    private async Task ConnectToTargetAsync(string target)
    {
        if (string.IsNullOrWhiteSpace(target)) return;

        if (_clientSession is not null)
            await _clientSession.DisposeAsync();

        var s = App.Settings.Current;
        _clientSession = new ClientSession(s.StormId, _clipboardWatcher);
        _clientSession.StatusChanged += msg => Dispatcher.Invoke(() => TxtConnectStatus.Text = msg);
        _clientSession.FrameUpdated += bmp =>
        {
            ImgRemoteScreen.Source = bmp;
            ViewerEmptyState.Visibility = Visibility.Collapsed;
        };
        _clientSession.MonitorsUpdated += mons => Dispatcher.Invoke(() =>
        {
            CmbMonitorSelect.ItemsSource = mons;
            CmbMonitorSelect.DisplayMemberPath = "Name";
            if (mons.Count > 0) CmbMonitorSelect.SelectedIndex = 0;
        });
        _clientSession.TelemetryUpdated += tel => Dispatcher.Invoke(() =>
        {
            BadgeFps.Text = $"FPS: {tel.Fps:0.#}";
            BadgeRtt.Text = $"RTT: {tel.RttMs} ms";
            BadgeMbps.Text = $"{tel.BandwidthMbps:0.##} Mbps";
            BadgeSavings.Text = $"Delta: {tel.TileSavingsPercent:0.#}%";
            TxtViewerHud.Text = $"{tel.Mode} | {tel.FrameWidth}x{tel.FrameHeight} | {tel.Fps:0.#} FPS | {tel.RttMs} ms";
            TelemetryChart.PushSample(tel.Fps, tel.BandwidthMbps);
        });
        _clientSession.FileListReceived += fl => Dispatcher.Invoke(() => PopulateRemoteFiles(fl));
        _clientSession.FileTransferProgressed += prog => Dispatcher.Invoke(() =>
        {
            double pct = prog.TotalBytes > 0 ? prog.BytesDone * 100.0 / prog.TotalBytes : 100.0;
            PbFileTransfer.Value = Math.Clamp(pct, 0, 100);
            TxtFileTransferStatus.Text = prog.Error is not null
                ? $"Error: {prog.Error}"
                : $"{(prog.IsUpload ? "Upload" : "Download")} {prog.FileName}: {pct:0.#}% ({prog.SpeedMbps:0.##} Mbps)";
            if (prog.Completed && prog.IsUpload)
                _ = _clientSession.RequestFileListAsync(_currentRemoteDir);
        });
        _clientSession.ChatReceived += msg => Dispatcher.Invoke(() => AppendChatMessage(msg));
        _clientSession.Disconnected += reason => Dispatcher.Invoke(() =>
        {
            TxtConnectStatus.Text = $"Disconnected: {reason}";
            TxtViewerHud.Text = "Disconnected";
            ViewerEmptyState.Visibility = Visibility.Visible;
        });

        try
        {
            BtnConnect.IsEnabled = false;
            await _clientSession.ConnectAsync(
                target,
                s.RelayUrl,
                _lanDiscovery.CurrentDevices,
                PromptOutgoingAuthAsync);

            App.Settings.AddAudit(target, _clientSession.Telemetry.Mode.ToString(), "Outgoing Connected");
            RefreshAuditGrid();
            NavViewer.IsChecked = true;
            RemoteViewportHost.Focus();
            _ = _clientSession.RequestFileListAsync("");
        }
        catch (Exception ex)
        {
            TxtConnectStatus.Text = $"Connection failed: {ex.Message}";
        }
        finally
        {
            BtnConnect.IsEnabled = true;
        }
    }

    private Task<ConsentDecisionPayload> PromptHostConsentAsync(IncomingConsentRequest req)
    {
        return Dispatcher.InvokeAsync(() =>
        {
            Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();

            _consentTcs?.TrySetResult(new ConsentDecisionPayload(false, req.ProposedPermissions));
            _consentTcs = new TaskCompletionSource<ConsentDecisionPayload>();

            TxtConsentDetails.Text = $"{req.ClientName} ({DeviceIdentity.FormatId(req.ClientStormId)})\n{req.RemoteEndpoint} [{req.Mode}]";
            ChkConsentControl.IsChecked = req.ProposedPermissions.AllowControl;
            ChkConsentClipboard.IsChecked = req.ProposedPermissions.AllowClipboard;
            ChkConsentFiles.IsChecked = req.ProposedPermissions.AllowFileTransfer;
            ChkConsentAudio.IsChecked = req.ProposedPermissions.AllowAudio;
            ModalConsent.Visibility = Visibility.Visible;
            return _consentTcs.Task;
        }).Task.Unwrap();
    }

    private Task<string?> PromptOutgoingAuthAsync(string hostName, bool allowUnattended)
    {
        return Dispatcher.InvokeAsync(() =>
        {
            _authTcs?.TrySetResult(null);
            _authTcs = new TaskCompletionSource<string?>();
            PwdRemoteAuth.Password = string.Empty;
            ModalAuth.Visibility = Visibility.Visible;
            PwdRemoteAuth.Focus();
            return _authTcs.Task;
        }).Task.Unwrap();
    }

    #region Header, Navigation & Modals
    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (PanelDashboard is null) return;
        PanelDashboard.Visibility = NavDashboard.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PanelViewer.Visibility = NavViewer.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PanelFiles.Visibility = NavFiles.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PanelAddressBook.Visibility = NavAddressBook.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PanelNetwork.Visibility = NavNetwork.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PanelSettings.Visibility = NavSettings.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width < 1310)
        {
            Grid.SetRow(HeaderControlsPanel, 1);
            Grid.SetColumn(HeaderControlsPanel, 0);
            Grid.SetColumnSpan(HeaderControlsPanel, 2);
            HeaderControlsPanel.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
            HeaderControlsPanel.Margin = new Thickness(0, 10, 0, 0);
        }
        else
        {
            Grid.SetRow(HeaderControlsPanel, 0);
            Grid.SetColumn(HeaderControlsPanel, 1);
            Grid.SetColumnSpan(HeaderControlsPanel, 1);
            HeaderControlsPanel.HorizontalAlignment = System.Windows.HorizontalAlignment.Right;
            HeaderControlsPanel.Margin = new Thickness(0);
        }
    }

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        // Mandatory ESC Modal Dismissal (Law 16)
        if (e.Key == Key.Escape)
        {
            if (ModalAbout.Visibility == Visibility.Visible) { ModalAbout.Visibility = Visibility.Collapsed; e.Handled = true; return; }
            if (ModalUpdate.Visibility == Visibility.Visible) { ModalUpdate.Visibility = Visibility.Collapsed; e.Handled = true; return; }
            if (ModalAddDevice.Visibility == Visibility.Visible) { ModalAddDevice.Visibility = Visibility.Collapsed; e.Handled = true; return; }
            if (ModalAuth.Visibility == Visibility.Visible)
            {
                ModalAuth.Visibility = Visibility.Collapsed;
                _authTcs?.TrySetResult(null);
                e.Handled = true;
                return;
            }
            if (ModalConsent.Visibility == Visibility.Visible)
            {
                ModalConsent.Visibility = Visibility.Collapsed;
                _consentTcs?.TrySetResult(new ConsentDecisionPayload(false, new SessionPermissions()));
                e.Handled = true;
                return;
            }
            if (_isFullscreen)
            {
                ToggleFullscreen(false);
                e.Handled = true;
            }
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_tray is not null && !_tray.IsQuittingFromTray)
        {
            e.Cancel = true;
            Hide();
        }
    }

    private async void ShutdownCompletely()
    {
        try { if (_clientSession is not null) await _clientSession.DisposeAsync(); } catch { }
        try { if (_hostServer is not null) await _hostServer.DisposeAsync(); } catch { }
        try { await _embeddedRelay.DisposeAsync(); } catch { }
        try { _lanDiscovery.Dispose(); } catch { }
        try { _clipboardWatcher.Dispose(); } catch { }
        try { _tray?.Dispose(); } catch { }
        System.Windows.Application.Current.Shutdown();
    }

    private void BtnMinimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void BtnMaximize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        IconMaxRestore.Data = WindowState == WindowState.Maximized
            ? Geometry.Parse("M 0 3 L 7 3 L 7 10 L 0 10 Z M 3 3 L 3 0 L 10 0 L 10 7 L 7 7")
            : Geometry.Parse("M 0 0 L 10 0 L 10 10 L 0 10 Z");
    }
    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    private void CmbLang_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializingUi || CmbLang.SelectedItem is not LanguageOption opt) return;
        App.Settings.Current.Language = opt.Code;
        App.Settings.Save();
        LocalizationService.SetLanguage(opt.Code);
        ApplyLocalization();
    }

    private void CmbTheme_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializingUi || CmbTheme.SelectedItem is not StormThemePalette t) return;
        App.Settings.Current.Theme = t.Name;
        App.Settings.Save();
        ThemeService.Apply(t.Name, ThemeService.CurrentAccent.Name, ThemeService.CurrentFont.DisplayName);
        ApplyThemeVisuals();
    }

    private void CmbAccent_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializingUi || CmbAccent.SelectedItem is not StormAccentOption a) return;
        App.Settings.Current.Accent = a.Name;
        App.Settings.Save();
        ThemeService.Apply(ThemeService.CurrentTheme.Name, a.Name, ThemeService.CurrentFont.DisplayName);
        ApplyThemeVisuals();
    }

    private void CmbFont_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializingUi || CmbFont.SelectedItem is not StormFontOption f) return;
        App.Settings.Current.FontFamily = f.DisplayName;
        App.Settings.Save();
        ThemeService.Apply(ThemeService.CurrentTheme.Name, ThemeService.CurrentAccent.Name, f.DisplayName);
        ApplyThemeVisuals();
    }
    #endregion

    #region Dashboard, Consent & Auth Handlers
    private void BtnCopyId_Click(object sender, RoutedEventArgs e)
    {
        try { System.Windows.Clipboard.SetText(App.Settings.Current.StormId); } catch { }
        TxtBtnCopyId.Text = LocalizationService.T("Copied");
        _ = Task.Delay(1500).ContinueWith(_ => Dispatcher.Invoke(() => TxtBtnCopyId.Text = LocalizationService.T("CopyId")));
    }

    private void BtnRefreshPin_Click(object sender, RoutedEventArgs e) => RegeneratePin();

    private void ChkAllowIncoming_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializingUi) return;
        App.Settings.Current.AllowIncoming = ChkAllowIncoming.IsChecked == true;
        App.Settings.Save();
    }

    private void BtnConnect_Click(object sender, RoutedEventArgs e) => _ = ConnectToTargetAsync(TxtTargetInput.Text);
    private void TxtTargetInput_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter) _ = ConnectToTargetAsync(TxtTargetInput.Text);
    }

    private void BtnConsentAccept_Click(object sender, RoutedEventArgs e)
    {
        ModalConsent.Visibility = Visibility.Collapsed;
        _consentTcs?.TrySetResult(new ConsentDecisionPayload(true, new SessionPermissions
        {
            AllowControl = ChkConsentControl.IsChecked == true,
            AllowClipboard = ChkConsentClipboard.IsChecked == true,
            AllowFileTransfer = ChkConsentFiles.IsChecked == true,
            AllowAudio = ChkConsentAudio.IsChecked == true,
            AllowSystemActions = App.Settings.Current.DefaultAllowSystemActions
        }));
    }

    private void BtnConsentViewOnly_Click(object sender, RoutedEventArgs e)
    {
        ModalConsent.Visibility = Visibility.Collapsed;
        _consentTcs?.TrySetResult(new ConsentDecisionPayload(true, new SessionPermissions
        {
            AllowControl = false,
            AllowClipboard = ChkConsentClipboard.IsChecked == true,
            AllowFileTransfer = ChkConsentFiles.IsChecked == true,
            AllowAudio = ChkConsentAudio.IsChecked == true,
            AllowSystemActions = false
        }));
    }

    private void BtnConsentDecline_Click(object sender, RoutedEventArgs e)
    {
        ModalConsent.Visibility = Visibility.Collapsed;
        _consentTcs?.TrySetResult(new ConsentDecisionPayload(false, new SessionPermissions()));
    }

    private void BtnAuthOk_Click(object sender, RoutedEventArgs e)
    {
        ModalAuth.Visibility = Visibility.Collapsed;
        _authTcs?.TrySetResult(PwdRemoteAuth.Password);
    }

    private void BtnAuthCancel_Click(object sender, RoutedEventArgs e)
    {
        ModalAuth.Visibility = Visibility.Collapsed;
        _authTcs?.TrySetResult(null);
    }

    private void PwdRemoteAuth_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter) BtnAuthOk_Click(sender, e);
    }
    #endregion

    #region Remote Viewer Input & Toolbar
    private void CmbMonitorSelect_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_clientSession is null || CmbMonitorSelect.SelectedItem is not MonitorInfo m) return;
        _ = _clientSession.SelectMonitorAsync(m.Index, _currentQuality);
    }

    private void CmbQuality_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _currentQuality = CmbQuality.SelectedIndex switch { 0 => 90, 1 => 75, 2 => 55, 3 => 35, _ => 75 };
        if (_clientSession is not null && CmbMonitorSelect.SelectedItem is MonitorInfo m)
            _ = _clientSession.SelectMonitorAsync(m.Index, _currentQuality);
    }

    private void BtnAudioToggle_Click(object sender, RoutedEventArgs e)
    {
        _audioEnabled = !_audioEnabled;
        _clientSession?.SetAudioEnabled(_audioEnabled);
        TxtBtnAudio.Text = $"{LocalizationService.T("AudioToggle")}: {(_audioEnabled ? "ON" : "OFF")}";
    }

    private void BtnTaskMgr_Click(object sender, RoutedEventArgs e) => _ = _clientSession?.SendSystemActionAsync(SystemActionKind.OpenTaskManager);
    private void BtnLockRemote_Click(object sender, RoutedEventArgs e) => _ = _clientSession?.SendSystemActionAsync(SystemActionKind.LockWorkstation);
    private void BtnWinRun_Click(object sender, RoutedEventArgs e) => _ = _clientSession?.SendSystemActionAsync(SystemActionKind.OpenRunDialog);
    private void BtnAltTab_Click(object sender, RoutedEventArgs e) => _ = _clientSession?.SendSystemActionAsync(SystemActionKind.SendAltTab);

    private void BtnScreenshot_Click(object sender, RoutedEventArgs e)
    {
        if (_clientSession?.FrameBitmap is not WriteableBitmap bmp) return;
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "STORM CONNECT");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"STORM_CONNECT_{DateTime.Now:yyyyMMdd_HHmmss}.png");
        using var fs = new FileStream(path, FileMode.Create);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        enc.Save(fs);
        TxtViewerHud.Text = $"Saved screenshot: {Path.GetFileName(path)}";
    }

    private void BtnToggleChat_Click(object sender, RoutedEventArgs e)
    {
        PanelChatDrawer.Visibility = PanelChatDrawer.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
    }

    private void BtnSendChat_Click(object sender, RoutedEventArgs e)
    {
        var txt = TxtChatInput.Text.Trim();
        if (string.IsNullOrEmpty(txt)) return;
        TxtChatInput.Text = string.Empty;
        var msg = new ChatMessagePayload(Environment.MachineName, txt, DateTime.UtcNow);
        AppendChatMessage(msg);
        if (_clientSession?.IsConnected == true) _ = _clientSession.SendChatAsync(txt);
        else _ = _hostServer?.SendChatAsync(txt);
    }

    private void TxtChatInput_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter) BtnSendChat_Click(sender, e);
    }

    private void AppendChatMessage(ChatMessagePayload msg)
    {
        var b = new Border
        {
            Background = (Brush)FindResource("ThemeBgSecondary"),
            BorderBrush = (Brush)FindResource("ThemeCardBorder"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(0, 0, 0, 6)
        };
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = $"{msg.SenderName} ({msg.TimestampUtc.ToLocalTime():HH:mm})", FontSize = 10.5, Foreground = (Brush)FindResource("ThemeAccent"), FontWeight = FontWeights.Bold });
        sp.Children.Add(new TextBlock { Text = msg.Text, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
        b.Child = sp;
        ChatMessagesPanel.Children.Add(b);
    }

    private void BtnFullscreen_Click(object sender, RoutedEventArgs e) => ToggleFullscreen(!_isFullscreen);
    private void ToggleFullscreen(bool full)
    {
        _isFullscreen = full;
        WindowState = full ? WindowState.Maximized : WindowState.Normal;
    }

    private async void BtnDisconnect_Click(object sender, RoutedEventArgs e)
    {
        if (_clientSession is not null)
        {
            await _clientSession.DisposeAsync();
            _clientSession = null;
        }
        ViewerEmptyState.Visibility = Visibility.Visible;
        NavDashboard.IsChecked = true;
    }

    private (double Nx, double Ny)? GetNormalizedRemoteCoords(MouseEventArgs e)
    {
        if (ImgRemoteScreen.Source is not BitmapSource bmp) return null;
        double cw = ImgRemoteScreen.ActualWidth;
        double ch = ImgRemoteScreen.ActualHeight;
        if (cw <= 0 || ch <= 0) return null;
        var pos = e.GetPosition(ImgRemoteScreen);
        return (Math.Clamp(pos.X / cw, 0, 1), Math.Clamp(pos.Y / ch, 0, 1));
    }

    private void RemoteScreen_MouseMove(object sender, MouseEventArgs e)
    {
        if (_clientSession is null || !_clientSession.IsConnected) return;
        if ((DateTime.UtcNow - _lastMouseMoveSent).TotalMilliseconds < 14) return;
        var c = GetNormalizedRemoteCoords(e);
        if (c is null) return;
        _lastMouseMoveSent = DateTime.UtcNow;
        _ = _clientSession.SendMouseAsync(new MouseEventData(c.Value.Nx, c.Value.Ny, 0, 0, 0));
    }

    private void RemoteScreen_MouseDown(object sender, MouseButtonEventArgs e)
    {
        RemoteViewportHost.Focus();
        if (_clientSession is null || !_clientSession.IsConnected) return;
        var c = GetNormalizedRemoteCoords(e);
        if (c is null) return;
        byte btn = e.ChangedButton switch
        {
            MouseButton.Left => 1,
            MouseButton.Right => 2,
            MouseButton.Middle => 3,
            MouseButton.XButton1 => 4,
            MouseButton.XButton2 => 5,
            _ => 0
        };
        if (btn > 0) _ = _clientSession.SendMouseAsync(new MouseEventData(c.Value.Nx, c.Value.Ny, btn, 1, 0));
    }

    private void RemoteScreen_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_clientSession is null || !_clientSession.IsConnected) return;
        var c = GetNormalizedRemoteCoords(e);
        if (c is null) return;
        byte btn = e.ChangedButton switch
        {
            MouseButton.Left => 1,
            MouseButton.Right => 2,
            MouseButton.Middle => 3,
            MouseButton.XButton1 => 4,
            MouseButton.XButton2 => 5,
            _ => 0
        };
        if (btn > 0) _ = _clientSession.SendMouseAsync(new MouseEventData(c.Value.Nx, c.Value.Ny, btn, 2, 0));
    }

    private void RemoteScreen_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_clientSession is null || !_clientSession.IsConnected) return;
        var c = GetNormalizedRemoteCoords(e);
        if (c is null) return;
        _ = _clientSession.SendMouseAsync(new MouseEventData(c.Value.Nx, c.Value.Ny, 0, 3, (short)e.Delta));
    }

    private void RemoteScreen_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (_clientSession is null || !_clientSession.IsConnected) return;
        var k = e.Key == Key.System ? e.SystemKey : e.Key;
        if (k == Key.Escape && _isFullscreen) return;
        ushort vk = (ushort)KeyInterop.VirtualKeyFromKey(k);
        bool ext = k is Key.Insert or Key.Delete or Key.Home or Key.End or Key.PageUp or Key.PageDown or Key.Left or Key.Right or Key.Up or Key.Down or Key.LWin or Key.RWin;
        _ = _clientSession.SendKeyAsync(new KeyEventData(vk, 0, false, ext, null));
        e.Handled = true;
    }

    private void RemoteScreen_KeyUp(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (_clientSession is null || !_clientSession.IsConnected) return;
        var k = e.Key == Key.System ? e.SystemKey : e.Key;
        ushort vk = (ushort)KeyInterop.VirtualKeyFromKey(k);
        bool ext = k is Key.Insert or Key.Delete or Key.Home or Key.End or Key.PageUp or Key.PageDown or Key.Left or Key.Right or Key.Up or Key.Down or Key.LWin or Key.RWin;
        _ = _clientSession.SendKeyAsync(new KeyEventData(vk, 0, true, ext, null));
        e.Handled = true;
    }

    private void RemoteScreen_TextInput(object sender, TextCompositionEventArgs e)
    {
        // Virtual keys already handled in KeyDown/KeyUp
    }
    #endregion

    #region File Manager & Drag-and-Drop
    private void LoadLocalDirectory(string path)
    {
        _currentLocalDir = path;
        TxtLocalPath.Text = path;
        var list = FileSystemHandler.ListDirectory(path);
        GridLocalFiles.ItemsSource = list.Entries.Select(e => new FileGridRow(
            e.IsDirectory ? $"[{e.Name}]" : e.Name,
            e.FullPath,
            e.IsDirectory,
            e.SizeBytes,
            e.IsDirectory ? "<DIR>" : FormatBytes(e.SizeBytes),
            e.LastModifiedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"))).ToList();
    }

    private void PopulateRemoteFiles(FileListResponsePayload list)
    {
        _currentRemoteDir = list.CurrentPath;
        TxtRemotePath.Text = list.CurrentPath;
        GridRemoteFiles.ItemsSource = list.Entries.Select(e => new FileGridRow(
            e.IsDirectory ? $"[{e.Name}]" : e.Name,
            e.FullPath,
            e.IsDirectory,
            e.SizeBytes,
            e.IsDirectory ? "<DIR>" : FormatBytes(e.SizeBytes),
            e.LastModifiedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"))).ToList();
    }

    private void GridLocalFiles_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (GridLocalFiles.SelectedItem is FileGridRow r && r.IsDirectory)
            LoadLocalDirectory(r.FullPath);
    }

    private void GridRemoteFiles_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (GridRemoteFiles.SelectedItem is FileGridRow r && r.IsDirectory && _clientSession is not null)
            _ = _clientSession.RequestFileListAsync(r.FullPath);
    }

    private void BtnLocalUp_Click(object sender, RoutedEventArgs e)
    {
        var p = string.IsNullOrWhiteSpace(_currentLocalDir) ? null : Directory.GetParent(_currentLocalDir)?.FullName;
        LoadLocalDirectory(p ?? "");
    }

    private void BtnRemoteUp_Click(object sender, RoutedEventArgs e)
    {
        if (_clientSession is null) return;
        var p = string.IsNullOrWhiteSpace(_currentRemoteDir) ? null : Path.GetDirectoryName(_currentRemoteDir.TrimEnd('\\', '/'));
        _ = _clientSession.RequestFileListAsync(p ?? "");
    }

    private void BtnRemoteRefresh_Click(object sender, RoutedEventArgs e) => _ = _clientSession?.RequestFileListAsync(_currentRemoteDir);
    private void TxtLocalPath_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter) LoadLocalDirectory(TxtLocalPath.Text);
    }
    private void TxtRemotePath_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _clientSession is not null) _ = _clientSession.RequestFileListAsync(TxtRemotePath.Text);
    }

    private async void BtnUploadSelected_Click(object sender, RoutedEventArgs e)
    {
        if (_clientSession is null || GridLocalFiles.SelectedItem is not FileGridRow r || r.IsDirectory) return;
        await _clientSession.UploadFileAsync(r.FullPath, _currentRemoteDir);
    }

    private async void BtnDownloadSelected_Click(object sender, RoutedEventArgs e)
    {
        if (_clientSession is null || GridRemoteFiles.SelectedItem is not FileGridRow r || r.IsDirectory) return;
        await _clientSession.RequestFileDownloadAsync(r.FullPath, r.Name);
    }

    private void Window_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)
            ? System.Windows.DragDropEffects.Copy
            : System.Windows.DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetData(System.Windows.DataFormats.FileDrop) is string[] files && files.Length > 0)
            await HandleDroppedPathsAsync(files);
    }

    private async Task HandleDroppedPathsAsync(IEnumerable<string> paths)
    {
        if (_clientSession is null || !_clientSession.IsConnected)
        {
            TxtConnectStatus.Text = "Connect to a remote device first to transfer dropped files.";
            return;
        }
        string dest = string.IsNullOrWhiteSpace(_currentRemoteDir) ? @"C:\Users\Public\Downloads" : _currentRemoteDir;
        foreach (var p in paths)
        {
            if (File.Exists(p))
                await _clientSession.UploadFileAsync(p, dest);
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.#} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):0.##} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):0.##} GB";
    }
    #endregion

    #region Address Book, Network, Settings, Updates, About
    private void RefreshAddressBookGrid()
    {
        string q = TxtBookSearch.Text.Trim();
        var items = App.Settings.Current.AddressBook
            .Where(x => string.IsNullOrEmpty(q) ||
                        x.Alias.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        x.StormId.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        x.HostOrIp.Contains(q, StringComparison.OrdinalIgnoreCase))
            .Select(x => new AddressGridRow(x, x.Alias, DeviceIdentity.FormatId(x.StormId), x.HostOrIp, x.Group, x.MacAddress))
            .ToList();
        GridAddressBook.ItemsSource = items;
    }

    private void TxtBookSearch_TextChanged(object sender, TextChangedEventArgs e) => RefreshAddressBookGrid();
    private void BtnAddDevice_Click(object sender, RoutedEventArgs e) => ModalAddDevice.Visibility = Visibility.Visible;
    private void BtnAddDevCancel_Click(object sender, RoutedEventArgs e) => ModalAddDevice.Visibility = Visibility.Collapsed;

    private void BtnAddDevSave_Click(object sender, RoutedEventArgs e)
    {
        App.Settings.Current.AddressBook.Add(new AddressBookEntry
        {
            Alias = string.IsNullOrWhiteSpace(TxtAddAlias.Text) ? "Remote Device" : TxtAddAlias.Text.Trim(),
            StormId = DeviceIdentity.NormalizeId(TxtAddStormId.Text),
            HostOrIp = TxtAddHost.Text.Trim(),
            MacAddress = TxtAddMac.Text.Trim(),
            Group = string.IsNullOrWhiteSpace(TxtAddGroup.Text) ? "Workstations" : TxtAddGroup.Text.Trim()
        });
        App.Settings.Save();
        ModalAddDevice.Visibility = Visibility.Collapsed;
        RefreshAddressBookGrid();
    }

    private void BtnBookConnect_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: AddressGridRow row })
        {
            string target = !string.IsNullOrWhiteSpace(row.Entry.StormId) ? row.Entry.StormId : row.Entry.HostOrIp;
            TxtTargetInput.Text = target;
            _ = ConnectToTargetAsync(target);
        }
    }

    private async void BtnBookWol_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: AddressGridRow row } && !string.IsNullOrWhiteSpace(row.Entry.MacAddress))
        {
            try
            {
                await WakeOnLan.SendAsync(row.Entry.MacAddress);
                TxtConnectStatus.Text = $"Wake-on-LAN magic packet sent to {row.Entry.MacAddress}";
            }
            catch (Exception ex) { TxtConnectStatus.Text = $"WOL error: {ex.Message}"; }
        }
    }

    private void BtnBookDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: AddressGridRow row })
        {
            App.Settings.Current.AddressBook.Remove(row.Entry);
            App.Settings.Save();
            RefreshAddressBookGrid();
        }
    }

    private void BtnSaveNetwork_Click(object sender, RoutedEventArgs e)
    {
        var s = App.Settings.Current;
        s.RelayUrl = TxtRelayUrl.Text.Trim();
        if (int.TryParse(TxtDirectPort.Text.Trim(), out int port) && port is > 0 and < 65536)
            s.DirectPort = port;
        s.EnableRelay = ChkEnableRelay.IsChecked == true;
        App.Settings.Save();
        _hostServer?.UpdateRelay(s.RelayUrl, s.EnableRelay);
    }

    private async void BtnToggleEmbeddedRelay_Click(object sender, RoutedEventArgs e)
    {
        if (_embeddedRelay.IsRunning)
        {
            await _embeddedRelay.StopAsync();
            TxtBtnEmbeddedRelay.Text = LocalizationService.T("StartHub");
            TxtEmbeddedRelayStats.Text = "Stopped";
        }
        else
        {
            await _embeddedRelay.StartAsync();
            TxtBtnEmbeddedRelay.Text = LocalizationService.T("StopHub");
            TxtEmbeddedRelayStats.Text = $"Listening on ws://0.0.0.0:{_embeddedRelay.Port}/connect-relay";
        }
    }

    private void BtnSaveUnattended_Click(object sender, RoutedEventArgs e)
    {
        App.Settings.SetUnattendedPassword(PwdUnattended.Password);
        PwdUnattended.Password = string.Empty;
        TxtConnectStatus.Text = "Unattended password saved (DPAPI + PBKDF2).";
    }

    private void BtnClearUnattended_Click(object sender, RoutedEventArgs e)
    {
        App.Settings.SetUnattendedPassword(null);
        TxtConnectStatus.Text = "Unattended password cleared.";
    }

    private void Perm_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializingUi) return;
        var s = App.Settings.Current;
        s.DefaultAllowControl = ChkPermControl.IsChecked == true;
        s.DefaultAllowClipboard = ChkPermClipboard.IsChecked == true;
        s.DefaultAllowFileTransfer = ChkPermFiles.IsChecked == true;
        s.DefaultAllowAudio = ChkPermAudio.IsChecked == true;
        s.DefaultAllowSystemActions = ChkPermSystem.IsChecked == true;
        App.Settings.Save();
    }

    private void SliderUiScale_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_initializingUi || UiScaleTransform is null) return;
        double scale = Math.Round(e.NewValue, 2);
        UiScaleTransform.ScaleX = scale;
        UiScaleTransform.ScaleY = scale;
        TxtScaleVal.Text = $"{Math.Round(scale * 100)}%";
        App.Settings.Current.UiScale = scale;
        App.Settings.Save();
    }

    private void RefreshAuditGrid()
    {
        GridAuditLog.ItemsSource = null;
        GridAuditLog.ItemsSource = App.Settings.Current.AuditLogs.ToList();
    }

    private async Task CheckUpdatesSilentlyAsync()
    {
        var res = await UpdateService.CheckForUpdatesAsync();
        if (res.IsUpdateAvailable)
        {
            Dispatcher.Invoke(() => ShowUpdateModal(res));
        }
    }

    private async void BtnUpdates_Click(object sender, RoutedEventArgs e)
    {
        var res = await UpdateService.CheckForUpdatesAsync();
        ShowUpdateModal(res);
    }

    private void ShowUpdateModal(UpdateCheckResult res)
    {
        _pendingUpdate = res;
        if (res.IsUpdateAvailable)
        {
            TxtUpdateSummary.Text = $"{res.CurrentVersion} -> {res.LatestVersion}";
            TxtUpdateNotes.Text = string.IsNullOrWhiteSpace(res.ReleaseNotes) ? "New release available." : res.ReleaseNotes;
            BtnUpdateInstall.Visibility = string.IsNullOrWhiteSpace(res.InstallerDownloadUrl) ? Visibility.Collapsed : Visibility.Visible;
        }
        else
        {
            TxtUpdateSummary.Text = $"{LocalizationService.T("UpToDateMsg")} ({res.CurrentVersion})";
            TxtUpdateNotes.Text = "";
            BtnUpdateInstall.Visibility = Visibility.Collapsed;
        }
        PbUpdateDownload.Visibility = Visibility.Collapsed;
        ModalUpdate.Visibility = Visibility.Visible;
    }

    private async void BtnUpdateInstall_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingUpdate?.InstallerDownloadUrl is null) return;
        BtnUpdateInstall.IsEnabled = false;
        PbUpdateDownload.Visibility = Visibility.Visible;
        _updateDlCts = new CancellationTokenSource();
        try
        {
            var path = await UpdateService.DownloadInstallerAsync(
                _pendingUpdate.InstallerDownloadUrl,
                _pendingUpdate.AssetName ?? "STORM_CONNECT_Setup.exe",
                new Progress<double>(p => PbUpdateDownload.Value = p),
                _updateDlCts.Token);
            UpdateService.LaunchInstallerAndExit(path, _tray);
        }
        catch (Exception ex)
        {
            TxtUpdateSummary.Text = $"Download error: {ex.Message}";
            BtnUpdateInstall.IsEnabled = true;
        }
    }

    private void BtnUpdateLater_Click(object sender, RoutedEventArgs e)
    {
        _updateDlCts?.Cancel();
        ModalUpdate.Visibility = Visibility.Collapsed;
    }

    private void BtnAbout_Click(object sender, RoutedEventArgs e) => ModalAbout.Visibility = Visibility.Visible;
    private void BtnAboutClose_Click(object sender, RoutedEventArgs e) => ModalAbout.Visibility = Visibility.Collapsed;
    #endregion

    #region Native DWM & UIPI Helpers
    private static void EnableDarkDwmTitleBar(IntPtr hwnd)
    {
        try
        {
            int val = 1;
            DwmSetWindowAttribute(hwnd, 20, ref val, sizeof(int));
        }
        catch { }
    }

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool ChangeWindowMessageFilterEx(IntPtr hWnd, uint msg, uint action, IntPtr changeInfo);
    [DllImport("shell32.dll")] private static extern void DragAcceptFiles(IntPtr hwnd, bool fAccept);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern uint DragQueryFile(IntPtr hDrop, uint iFile, System.Text.StringBuilder? lpszFile, int cch);
    [DllImport("shell32.dll")] private static extern void DragFinish(IntPtr hDrop);
    #endregion
}
