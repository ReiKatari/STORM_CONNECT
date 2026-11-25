using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;
using StormConnect.Protocol;

namespace StormConnect.Desktop.Engine;

/// <summary>
/// Thread-safe clipboard monitor & applier with SHA-256 echo-suppression
/// supporting UTF-8 text and PNG images across Desktop and Android peers.
/// </summary>
public sealed class ClipboardWatcher : IDisposable
{
    private readonly IntPtr _hwnd;
    private readonly NativeClipboardWindow _window;
    private string _lastAppliedHash = string.Empty;
    private string _lastBroadcastHash = string.Empty;

    public event Action<ClipboardPayload>? LocalClipboardChanged;

    public ClipboardWatcher()
    {
        _window = new NativeClipboardWindow(OnClipboardUpdated);
        _hwnd = _window.Handle;
        AddClipboardFormatListener(_hwnd);
    }

    private void OnClipboardUpdated()
    {
        try
        {
            var payload = ReadClipboard();
            if (payload is null) return;
            if (payload.ContentHash == _lastAppliedHash || payload.ContentHash == _lastBroadcastHash)
                return;

            _lastBroadcastHash = payload.ContentHash;
            LocalClipboardChanged?.Invoke(payload);
        }
        catch { }
    }

    public static ClipboardPayload? ReadClipboard()
    {
        if (Clipboard.ContainsText())
        {
            var text = Clipboard.GetText();
            if (string.IsNullOrEmpty(text)) return null;
            var hash = ComputeSha256(Encoding.UTF8.GetBytes("T:" + text));
            return new ClipboardPayload(false, text, null, hash);
        }

        if (Clipboard.ContainsImage())
        {
            using var img = Clipboard.GetImage();
            if (img is null) return null;
            using var ms = new MemoryStream();
            img.Save(ms, ImageFormat.Png);
            var png = ms.ToArray();
            if (png.Length > 8 * 1024 * 1024) return null;
            var hash = ComputeSha256(png);
            return new ClipboardPayload(true, null, png, hash);
        }

        return null;
    }

    public void ApplyRemoteClipboard(ClipboardPayload payload)
    {
        if (payload.ContentHash == _lastAppliedHash) return;
        _lastAppliedHash = payload.ContentHash;
        _lastBroadcastHash = payload.ContentHash;

        RunOnStaThread(() =>
        {
            try
            {
                if (!payload.IsImage && payload.Text is not null)
                {
                    Clipboard.SetText(payload.Text);
                }
                else if (payload.IsImage && payload.PngBytes is not null)
                {
                    using var ms = new MemoryStream(payload.PngBytes);
                    using var img = Image.FromStream(ms);
                    Clipboard.SetImage(img);
                }
            }
            catch { }
        });
    }

    private static void RunOnStaThread(Action action)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
        {
            action();
            return;
        }
        var t = new Thread(() => action()) { IsBackground = true };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join(1000);
    }

    private static string ComputeSha256(byte[] data)
    {
        return Convert.ToHexString(SHA256.HashData(data));
    }

    public void Dispose()
    {
        try { RemoveClipboardFormatListener(_hwnd); } catch { }
        _window.DestroyHandle();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    private sealed class NativeClipboardWindow : NativeWindow
    {
        private const int WM_CLIPBOARDUPDATE = 0x031D;
        private readonly Action _callback;

        public NativeClipboardWindow(Action callback)
        {
            _callback = callback;
            CreateHandle(new CreateParams());
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_CLIPBOARDUPDATE)
                _callback();
            base.WndProc(ref m);
        }
    }
}
