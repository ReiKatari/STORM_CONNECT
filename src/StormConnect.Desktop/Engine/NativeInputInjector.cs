using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using StormConnect.Protocol;

namespace StormConnect.Desktop.Engine;

/// <summary>
/// Native Win32 SendInput injector supporting multi-monitor absolute coordinates,
/// buttons, vertical/horizontal wheel, scan-code + Unicode keyboard events, and quick system actions.
/// </summary>
public static class NativeInputInjector
{
    public static void InjectMouse(MouseEventData ev, int monitorIndex)
    {
        var bounds = DxgiScreenCapturer.GetTargetBounds(monitorIndex);
        int absScreenX = bounds.X + (int)Math.Round(Math.Clamp(ev.NormX, 0.0, 1.0) * Math.Max(1, bounds.Width - 1));
        int absScreenY = bounds.Y + (int)Math.Round(Math.Clamp(ev.NormY, 0.0, 1.0) * Math.Max(1, bounds.Height - 1));

        var vScreen = SystemInformation.VirtualScreen;
        int normVirtualX = (int)Math.Round((absScreenX - vScreen.X) * 65535.0 / Math.Max(1, vScreen.Width - 1));
        int normVirtualY = (int)Math.Round((absScreenY - vScreen.Y) * 65535.0 / Math.Max(1, vScreen.Height - 1));

        uint flags = MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK | MOUSEEVENTF_MOVE;
        uint mouseData = 0;

        switch (ev.Action)
        {
            case 1: // Down
                flags |= ev.Button switch
                {
                    1 => MOUSEEVENTF_LEFTDOWN,
                    2 => MOUSEEVENTF_RIGHTDOWN,
                    3 => MOUSEEVENTF_MIDDLEDOWN,
                    4 => MOUSEEVENTF_XDOWN,
                    5 => MOUSEEVENTF_XDOWN,
                    _ => 0u
                };
                if (ev.Button == 4) mouseData = 1;
                if (ev.Button == 5) mouseData = 2;
                break;

            case 2: // Up
                flags |= ev.Button switch
                {
                    1 => MOUSEEVENTF_LEFTUP,
                    2 => MOUSEEVENTF_RIGHTUP,
                    3 => MOUSEEVENTF_MIDDLEUP,
                    4 => MOUSEEVENTF_XUP,
                    5 => MOUSEEVENTF_XUP,
                    _ => 0u
                };
                if (ev.Button == 4) mouseData = 1;
                if (ev.Button == 5) mouseData = 2;
                break;

            case 3: // Wheel
                flags |= MOUSEEVENTF_WHEEL;
                mouseData = unchecked((uint)ev.WheelDelta);
                break;

            case 4: // HWheel
                flags |= MOUSEEVENTF_HWHEEL;
                mouseData = unchecked((uint)ev.WheelDelta);
                break;
        }

        var input = new INPUT
        {
            type = INPUT_MOUSE,
            u = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    dx = normVirtualX,
                    dy = normVirtualY,
                    mouseData = mouseData,
                    dwFlags = flags,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };
        SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }

    public static void InjectKey(KeyEventData ev)
    {
        if (ev.VirtualKey == 0 && ev.UnicodeChar is char ch)
        {
            var uni = new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = 0,
                        wScan = ch,
                        dwFlags = KEYEVENTF_UNICODE | (ev.IsKeyUp ? KEYEVENTF_KEYUP : 0u),
                        time = 0,
                        dwExtraInfo = IntPtr.Zero
                    }
                }
            };
            SendInput(1, [uni], Marshal.SizeOf<INPUT>());
            return;
        }

        uint scan = ev.ScanCode != 0 ? ev.ScanCode : MapVirtualKey(ev.VirtualKey, 0);
        uint flags = (ev.IsExtended ? KEYEVENTF_EXTENDEDKEY : 0u) | (ev.IsKeyUp ? KEYEVENTF_KEYUP : 0u);
        if (scan != 0) flags |= KEYEVENTF_SCANCODE;

        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = ev.VirtualKey,
                    wScan = (ushort)scan,
                    dwFlags = flags,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };
        SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }

    public static void ExecuteSystemAction(SystemActionKind action)
    {
        switch (action)
        {
            case SystemActionKind.LockWorkstation:
                LockWorkStation();
                break;
            case SystemActionKind.OpenTaskManager:
                Process.Start(new ProcessStartInfo("taskmgr.exe") { UseShellExecute = true });
                break;
            case SystemActionKind.OpenRunDialog:
                SendCombo(0x5B /* LWIN */, 0x52 /* R */);
                break;
            case SystemActionKind.SendWinKey:
                SendCombo(0x5B);
                break;
            case SystemActionKind.SendAltTab:
                SendCombo(0x12 /* MENU */, 0x09 /* TAB */);
                break;
            case SystemActionKind.SendPrintScreen:
                SendCombo(0x2C /* SNAPSHOT */);
                break;
            case SystemActionKind.CtrlAltDelHint:
                // Windows restricts non-system SAS injection; open Task Manager / Security screen helper
                Process.Start(new ProcessStartInfo("taskmgr.exe") { UseShellExecute = true });
                break;
            case SystemActionKind.MoveWindowNextMonitor:
                MoveActiveWindowToMonitor(1);
                break;
            case SystemActionKind.MoveWindowPrevMonitor:
                MoveActiveWindowToMonitor(-1);
                break;
        }
    }

    public static void MoveActiveWindowToMonitor(int delta)
    {
        var screens = Screen.AllScreens;
        if (screens.Length < 2) return;

        IntPtr hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
        {
            SendCombo(0x5B, 0x10, delta >= 0 ? (ushort)0x27 : (ushort)0x25);
            return;
        }

        var currentScreen = Screen.FromHandle(hwnd);
        int curIdx = Array.FindIndex(screens, s => s.DeviceName == currentScreen.DeviceName);
        if (curIdx < 0) curIdx = 0;
        int nextIdx = ((curIdx + delta) % screens.Length + screens.Length) % screens.Length;

        var srcArea = screens[curIdx].WorkingArea;
        var dstArea = screens[nextIdx].WorkingArea;

        bool wasMaximized = IsZoomed(hwnd);
        if (wasMaximized)
            ShowWindow(hwnd, 9 /* SW_RESTORE */);

        if (GetWindowRect(hwnd, out var rect))
        {
            int curW = Math.Max(200, rect.Right - rect.Left);
            int curH = Math.Max(150, rect.Bottom - rect.Top);
            int newW = Math.Min(curW, dstArea.Width);
            int newH = Math.Min(curH, dstArea.Height);

            double relX = srcArea.Width > 0 ? (double)(rect.Left - srcArea.Left) / srcArea.Width : 0.05;
            double relY = srcArea.Height > 0 ? (double)(rect.Top - srcArea.Top) / srcArea.Height : 0.05;
            int newX = Math.Clamp(dstArea.Left + (int)Math.Round(relX * dstArea.Width), dstArea.Left, Math.Max(dstArea.Left, dstArea.Right - newW));
            int newY = Math.Clamp(dstArea.Top + (int)Math.Round(relY * dstArea.Height), dstArea.Top, Math.Max(dstArea.Top, dstArea.Bottom - newH));

            SetWindowPos(hwnd, IntPtr.Zero, newX, newY, newW, newH, 0x0004 /* SWP_NOZORDER */ | 0x0010 /* SWP_NOACTIVATE */);
        }

        if (wasMaximized)
            ShowWindow(hwnd, 3 /* SW_MAXIMIZE */);
    }

    private static void SendCombo(params ushort[] vks)
    {
        foreach (var vk in vks) InjectKey(new KeyEventData(vk, 0, false, vk == 0x5B, null));
        for (int i = vks.Length - 1; i >= 0; i--) InjectKey(new KeyEventData(vks[i], 0, true, vks[i] == 0x5B, null));
    }

    private const int INPUT_MOUSE = 0;
    private const int INPUT_KEYBOARD = 1;
    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    private const uint MOUSEEVENTF_XDOWN = 0x0080;
    private const uint MOUSEEVENTF_XUP = 0x0100;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;
    private const uint MOUSEEVENTF_HWHEEL = 0x1000;
    private const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;
    private const uint KEYEVENTF_SCANCODE = 0x0008;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);

    [DllImport("user32.dll")]
    private static extern bool LockWorkStation();

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public int type;
        public InputUnion u;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }
}
