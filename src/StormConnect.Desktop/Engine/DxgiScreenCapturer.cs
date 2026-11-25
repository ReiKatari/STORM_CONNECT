using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using StormConnect.Protocol;

namespace StormConnect.Desktop.Engine;

public sealed class CapturedFrame
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required int Stride { get; init; }
    public required byte[] BgraPixels { get; init; }
    public int CursorX { get; init; }
    public int CursorY { get; init; }
    public bool CursorVisible { get; init; }
}

/// <summary>
/// High-performance multi-monitor screen capturer using DXGI Desktop Duplication COM interop
/// with automatic seamless fallback to Win32 BitBlt (SRCCOPY | CAPTUREBLT) and virtual desktop stitching.
/// </summary>
public sealed class DxgiScreenCapturer : IDisposable
{
    private IntPtr _d3dDevice;
    private IntPtr _d3dContext;
    private IntPtr _duplication;
    private IntPtr _stagingTex;
    private int _dupMonitorIndex = -1;
    private int _dupWidth;
    private int _dupHeight;
    private bool _dxgiDisabled;
    private string _attachedDesktopName = string.Empty;
    private readonly object _lock = new();

    public static List<MonitorInfo> GetMonitors()
    {
        var screens = Screen.AllScreens;
        var list = new List<MonitorInfo>(screens.Length + 1);
        for (int i = 0; i < screens.Length; i++)
        {
            var s = screens[i];
            list.Add(new MonitorInfo(
                i,
                string.Format(Services.LocalizationService.T("MonitorDisplay"), i + 1, s.Bounds.Width, s.Bounds.Height),
                s.Bounds.X,
                s.Bounds.Y,
                s.Bounds.Width,
                s.Bounds.Height,
                s.Primary));
        }

        if (screens.Length > 1)
        {
            var v = SystemInformation.VirtualScreen;
            list.Add(new MonitorInfo(
                -1,
                string.Format(Services.LocalizationService.T("AllMonitors"), v.Width, v.Height),
                v.X,
                v.Y,
                v.Width,
                v.Height,
                false));
        }
        return list;
    }

    public static Rectangle GetTargetBounds(int monitorIndex)
    {
        var screens = Screen.AllScreens;
        if (monitorIndex == -1 || screens.Length == 0)
            return SystemInformation.VirtualScreen;
        if (monitorIndex >= 0 && monitorIndex < screens.Length)
            return screens[monitorIndex].Bounds;
        return screens[0].Bounds;
    }

    public CapturedFrame? Capture(int monitorIndex, int maxWidth = 0)
    {
        lock (_lock)
        {
            EnsureInputDesktop();
            var bounds = GetTargetBounds(monitorIndex);
            byte[]? pixels = null;
            int outW = bounds.Width;
            int outH = bounds.Height;

            if (monitorIndex >= 0 && !_dxgiDisabled)
            {
                var dx = TryCaptureDxgi(monitorIndex, bounds, maxWidth);
                if (dx is not null)
                    (pixels, outW, outH) = dx.Value;
            }

            if (pixels is null)
            {
                var gdi = CaptureGdiBitBlt(bounds, maxWidth);
                if (gdi is null) return null;
                (pixels, outW, outH) = gdi.Value;
            }

            // Read cursor position relative to active monitor bounds
            var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
            bool cursorOk = GetCursorInfo(out ci) && (ci.flags & 0x00000001) != 0;
            int cx = cursorOk ? (int)Math.Round((ci.ptScreenPos.x - bounds.X) * ((double)outW / Math.Max(1, bounds.Width))) : 0;
            int cy = cursorOk ? (int)Math.Round((ci.ptScreenPos.y - bounds.Y) * ((double)outH / Math.Max(1, bounds.Height))) : 0;

            return new CapturedFrame
            {
                Width = outW,
                Height = outH,
                Stride = outW * 4,
                BgraPixels = pixels,
                CursorX = cx,
                CursorY = cy,
                CursorVisible = cursorOk && cx >= 0 && cy >= 0 && cx < outW && cy < outH
            };
        }
    }

    private (byte[] Pixels, int Width, int Height)? TryCaptureDxgi(int monitorIndex, Rectangle bounds, int maxWidth)
    {
        try
        {
            if (_dupMonitorIndex != monitorIndex || _duplication == IntPtr.Zero)
            {
                ReleaseDxgi();
                if (!InitDxgiDuplication(monitorIndex, bounds))
                {
                    _dxgiDisabled = true;
                    return null;
                }
                _dupMonitorIndex = monitorIndex;
            }

            // IDXGIOutputDuplication::AcquireNextFrame (vtable slot 8)
            var acquire = Marshal.GetDelegateForFunctionPointer<AcquireNextFrameDelegate>(GetVtableSlot(_duplication, 8));
            int hr = acquire(_duplication, 16, out var frameInfo, out IntPtr desktopResource);
            if (hr == unchecked((int)0x887A0027)) // DXGI_ERROR_WAIT_TIMEOUT
                return null;
            if (hr != 0 || desktopResource == IntPtr.Zero)
            {
                ReleaseDxgi();
                return null;
            }

            try
            {
                if (frameInfo.LastPresentTime == 0)
                    return null;

                Guid iidTex2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
                Marshal.QueryInterface(desktopResource, in iidTex2D, out IntPtr desktopTex);
                if (desktopTex == IntPtr.Zero) return null;

                try
                {
                    // Validate desktopTex format (B8G8R8A8=87) & dimensions before CopyResource so HDR/scaled outputs fall back cleanly to GDI BitBlt
                    var getDesc = Marshal.GetDelegateForFunctionPointer<GetTextureDescDelegate>(GetVtableSlot(desktopTex, 10));
                    getDesc(desktopTex, out var texDesc);
                    if (texDesc.Width != _dupWidth || texDesc.Height != _dupHeight || texDesc.Format != 87)
                    {
                        ReleaseDxgi();
                        _dxgiDisabled = true;
                        return null;
                    }

                    // ID3D11DeviceContext::CopyResource (vtable slot 47)
                    var copyRes = Marshal.GetDelegateForFunctionPointer<CopyResourceDelegate>(GetVtableSlot(_d3dContext, 47));
                    copyRes(_d3dContext, _stagingTex, desktopTex);

                    // ID3D11DeviceContext::Map (vtable slot 14)
                    var mapFn = Marshal.GetDelegateForFunctionPointer<MapDelegate>(GetVtableSlot(_d3dContext, 14));
                    int mhr = mapFn(_d3dContext, _stagingTex, 0, 1 /* D3D11_MAP_READ */, 0, out var mapped);
                    if (mhr != 0) return null;

                    try
                    {
                        bool scaled = maxWidth > 0 && _dupWidth > maxWidth;
                        int outW = scaled ? Math.Max(64, maxWidth & ~1) : _dupWidth;
                        int outH = scaled ? Math.Max(64, ((int)Math.Round(_dupHeight * ((double)outW / _dupWidth))) & ~1) : _dupHeight;
                        int rowBytes = outW * 4;
                        var buf = GC.AllocateUninitializedArray<byte>(rowBytes * outH);

                        if (!scaled)
                        {
                            for (int y = 0; y < _dupHeight; y++)
                            {
                                Marshal.Copy(IntPtr.Add(mapped.pData, y * mapped.RowPitch), buf, y * rowBytes, rowBytes);
                            }
                        }
                        else
                        {
                            DownscalePointerToBgra(mapped.pData, mapped.RowPitch, _dupWidth, _dupHeight, buf, outW, outH);
                        }
                        return (buf, outW, outH);
                    }
                    finally
                    {
                        var unmapFn = Marshal.GetDelegateForFunctionPointer<UnmapDelegate>(GetVtableSlot(_d3dContext, 15));
                        unmapFn(_d3dContext, _stagingTex, 0);
                    }
                }
                finally
                {
                    Marshal.Release(desktopTex);
                }
            }
            finally
            {
                Marshal.Release(desktopResource);
                if (_duplication != IntPtr.Zero)
                {
                    // IDXGIOutputDuplication::ReleaseFrame (vtable slot 14)
                    var releaseFrame = Marshal.GetDelegateForFunctionPointer<ReleaseFrameDelegate>(GetVtableSlot(_duplication, 14));
                    releaseFrame(_duplication);
                }
            }
        }
        catch
        {
            ReleaseDxgi();
            _dxgiDisabled = true;
            return null;
        }
    }

    private static unsafe void DownscalePointerToBgra(IntPtr srcPtr, int srcPitch, int srcW, int srcH, byte[] dst, int dstW, int dstH)
    {
        byte* srcBase = (byte*)srcPtr;
        bool boxFilter = srcW >= dstW * 2 && srcH >= dstH * 2;
        fixed (byte* dstBase = dst)
        {
            for (int y = 0; y < dstH; y++)
            {
                int sy0 = (int)((long)y * srcH / dstH);
                int sy1 = boxFilter && sy0 + 1 < srcH ? sy0 + 1 : sy0;
                byte* row0 = srcBase + sy0 * srcPitch;
                byte* row1 = srcBase + sy1 * srcPitch;
                uint* dstRow = (uint*)(dstBase + y * dstW * 4);

                for (int x = 0; x < dstW; x++)
                {
                    int sx0 = (int)((long)x * srcW / dstW);
                    if (!boxFilter)
                    {
                        dstRow[x] = *(uint*)(row0 + sx0 * 4);
                    }
                    else
                    {
                        int sx1 = sx0 + 1 < srcW ? sx0 + 1 : sx0;
                        byte* p00 = row0 + sx0 * 4;
                        byte* p01 = row0 + sx1 * 4;
                        byte* p10 = row1 + sx0 * 4;
                        byte* p11 = row1 + sx1 * 4;
                        uint b = (uint)(p00[0] + p01[0] + p10[0] + p11[0]) >> 2;
                        uint g = (uint)(p00[1] + p01[1] + p10[1] + p11[1]) >> 2;
                        uint r = (uint)(p00[2] + p01[2] + p10[2] + p11[2]) >> 2;
                        dstRow[x] = b | (g << 8) | (r << 16) | 0xFF000000u;
                    }
                }
            }
        }
    }

    private bool InitDxgiDuplication(int monitorIndex, Rectangle expectedBounds)
    {
        int w = expectedBounds.Width;
        int h = expectedBounds.Height;
        int[] featureLevels = [0xb100, 0xb000, 0xa100]; // 11.1, 11.0, 10.1
        int hr = D3D11CreateDevice(
            IntPtr.Zero,
            1 /* D3D_DRIVER_TYPE_HARDWARE */,
            IntPtr.Zero,
            0,
            featureLevels,
            featureLevels.Length,
            7 /* D3D11_SDK_VERSION */,
            out _d3dDevice,
            out _,
            out _d3dContext);
        if (hr != 0 || _d3dDevice == IntPtr.Zero) return false;

        Guid iidDxgiDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
        if (Marshal.QueryInterface(_d3dDevice, in iidDxgiDevice, out IntPtr dxgiDevice) != 0) return false;
        try
        {
            // IDXGIDevice::GetAdapter (vtable slot 7)
            var getAdapter = Marshal.GetDelegateForFunctionPointer<GetAdapterDelegate>(GetVtableSlot(dxgiDevice, 7));
            if (getAdapter(dxgiDevice, out IntPtr adapter) != 0 || adapter == IntPtr.Zero) return false;
            try
            {
                // Enumerate DXGI outputs and match DesktopCoordinates against expectedBounds (never rely on index parity!)
                var enumOutputs = Marshal.GetDelegateForFunctionPointer<EnumOutputsDelegate>(GetVtableSlot(adapter, 7));
                IntPtr matchedOutput = IntPtr.Zero;
                for (uint idx = 0; idx < 8; idx++)
                {
                    if (enumOutputs(adapter, idx, out IntPtr candidate) != 0 || candidate == IntPtr.Zero) break;
                    var getOutDesc = Marshal.GetDelegateForFunctionPointer<GetOutputDescDelegate>(GetVtableSlot(candidate, 7));
                    if (getOutDesc(candidate, out var odesc) == 0 &&
                        odesc.DesktopCoordinates.Left == expectedBounds.Left &&
                        odesc.DesktopCoordinates.Top == expectedBounds.Top &&
                        odesc.DesktopCoordinates.Right == expectedBounds.Right &&
                        odesc.DesktopCoordinates.Bottom == expectedBounds.Bottom)
                    {
                        matchedOutput = candidate;
                        break;
                    }
                    Marshal.Release(candidate);
                }
                if (matchedOutput == IntPtr.Zero) return false;

                try
                {
                    Guid iidOutput1 = new("00cddea8-939b-4b83-a340-a685226666cc");
                    if (Marshal.QueryInterface(matchedOutput, in iidOutput1, out IntPtr output1) != 0) return false;
                    try
                    {
                        // IDXGIOutput1::DuplicateOutput (vtable slot 22)
                        var dupOut = Marshal.GetDelegateForFunctionPointer<DuplicateOutputDelegate>(GetVtableSlot(output1, 22));
                        if (dupOut(output1, _d3dDevice, out _duplication) != 0 || _duplication == IntPtr.Zero) return false;

                        var desc = new D3D11_TEXTURE2D_DESC
                        {
                            Width = (uint)w,
                            Height = (uint)h,
                            MipLevels = 1,
                            ArraySize = 1,
                            Format = 87, // DXGI_FORMAT_B8G8R8A8_UNORM
                            SampleCount = 1,
                            SampleQuality = 0,
                            Usage = 3, // D3D11_USAGE_STAGING
                            BindFlags = 0,
                            CPUAccessFlags = 0x20000, // D3D11_CPU_ACCESS_READ
                            MiscFlags = 0
                        };
                        // ID3D11Device::CreateTexture2D (vtable slot 5)
                        var createTex = Marshal.GetDelegateForFunctionPointer<CreateTexture2DDelegate>(GetVtableSlot(_d3dDevice, 5));
                        if (createTex(_d3dDevice, ref desc, IntPtr.Zero, out _stagingTex) != 0 || _stagingTex == IntPtr.Zero) return false;

                        _dupWidth = w;
                        _dupHeight = h;
                        return true;
                    }
                    finally { Marshal.Release(output1); }
                }
                finally { Marshal.Release(matchedOutput); }
            }
            finally { Marshal.Release(adapter); }
        }
        finally { Marshal.Release(dxgiDevice); }
    }

    private static (byte[] Pixels, int Width, int Height)? CaptureGdiBitBlt(Rectangle bounds, int maxWidth)
    {
        try
        {
            bool scaled = maxWidth > 0 && bounds.Width > maxWidth;
            int dstW = scaled ? Math.Max(64, maxWidth & ~1) : bounds.Width;
            int dstH = scaled ? Math.Max(64, ((int)Math.Round(bounds.Height * ((double)dstW / bounds.Width))) & ~1) : bounds.Height;

            using var bmp = new Bitmap(dstW, dstH, PixelFormat.Format32bppArgb);
            bool copied = false;
            using (var g = Graphics.FromImage(bmp))
            {
                IntPtr hdcDst = g.GetHdc();
                try
                {
                    IntPtr hdcSrc = GetDC(IntPtr.Zero);
                    try
                    {
                        if (scaled)
                        {
                            SetStretchBltMode(hdcDst, 4 /* HALFTONE */);
                            copied = StretchBlt(hdcDst, 0, 0, dstW, dstH, hdcSrc, bounds.X, bounds.Y, bounds.Width, bounds.Height, 0x00CC0020 | 0x40000000);
                            if (!copied)
                                copied = StretchBlt(hdcDst, 0, 0, dstW, dstH, hdcSrc, bounds.X, bounds.Y, bounds.Width, bounds.Height, 0x00CC0020);
                        }
                        else
                        {
                            copied = BitBlt(hdcDst, 0, 0, dstW, dstH, hdcSrc, bounds.X, bounds.Y, 0x00CC0020 | 0x40000000);
                            if (!copied)
                                copied = BitBlt(hdcDst, 0, 0, dstW, dstH, hdcSrc, bounds.X, bounds.Y, 0x00CC0020);
                        }
                    }
                    finally
                    {
                        ReleaseDC(IntPtr.Zero, hdcSrc);
                    }

                    if (!copied)
                    {
                        IntPtr displayDc = CreateDC("DISPLAY", null, null, IntPtr.Zero);
                        if (displayDc != IntPtr.Zero)
                        {
                            try
                            {
                                if (scaled)
                                {
                                    SetStretchBltMode(hdcDst, 4);
                                    copied = StretchBlt(hdcDst, 0, 0, dstW, dstH, displayDc, bounds.X, bounds.Y, bounds.Width, bounds.Height, 0x00CC0020);
                                }
                                else
                                {
                                    copied = BitBlt(hdcDst, 0, 0, dstW, dstH, displayDc, bounds.X, bounds.Y, 0x00CC0020);
                                }
                            }
                            finally
                            {
                                DeleteDC(displayDc);
                            }
                        }
                    }
                }
                finally
                {
                    g.ReleaseHdc(hdcDst);
                }
            }

            if (!copied) return null;

            var bmpData = bmp.LockBits(new Rectangle(0, 0, dstW, dstH), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                int bytes = bmpData.Stride * dstH;
                var pixels = new byte[bytes];
                Marshal.Copy(bmpData.Scan0, pixels, 0, bytes);
                return (pixels, dstW, dstH);
            }
            finally
            {
                bmp.UnlockBits(bmpData);
            }
        }
        catch
        {
            return null;
        }
    }

    private void EnsureInputDesktop()
    {
        try
        {
            IntPtr hInput = OpenInputDesktop(0, false, 0x01FF);
            if (hInput == IntPtr.Zero)
                hInput = OpenInputDesktop(0, false, 0x0041 /* DESKTOP_READOBJECTS | DESKTOP_SWITCHDESKTOP */);
            if (hInput == IntPtr.Zero) return;
            try
            {
                string inputName = GetDesktopName(hInput);
                string threadName = GetDesktopName(GetThreadDesktop(GetCurrentThreadId()));
                if (!string.Equals(inputName, threadName, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(inputName, _attachedDesktopName, StringComparison.OrdinalIgnoreCase))
                {
                    if (SetThreadDesktop(hInput))
                    {
                        _attachedDesktopName = inputName;
                        ReleaseDxgi(); // re-bind DXGI output duplication to the new input desktop
                    }
                }
            }
            finally
            {
                CloseDesktop(hInput);
            }
        }
        catch { }
    }

    private static string GetDesktopName(IntPtr hDesk)
    {
        if (hDesk == IntPtr.Zero) return string.Empty;
        var buf = new byte[256];
        if (GetUserObjectInformation(hDesk, 2 /* UOI_NAME */, buf, buf.Length, out int needed) && needed >= 2)
            return System.Text.Encoding.Unicode.GetString(buf, 0, needed - 2).TrimEnd('\0');
        return string.Empty;
    }

    private static IntPtr GetVtableSlot(IntPtr comPtr, int index)
    {
        IntPtr vtable = Marshal.ReadIntPtr(comPtr);
        return Marshal.ReadIntPtr(vtable, index * IntPtr.Size);
    }

    private void ReleaseDxgi()
    {
        if (_stagingTex != IntPtr.Zero) { Marshal.Release(_stagingTex); _stagingTex = IntPtr.Zero; }
        if (_duplication != IntPtr.Zero) { Marshal.Release(_duplication); _duplication = IntPtr.Zero; }
        if (_d3dContext != IntPtr.Zero) { Marshal.Release(_d3dContext); _d3dContext = IntPtr.Zero; }
        if (_d3dDevice != IntPtr.Zero) { Marshal.Release(_d3dDevice); _d3dDevice = IntPtr.Zero; }
        _dupMonitorIndex = -1;
    }

    public void Dispose()
    {
        lock (_lock) ReleaseDxgi();
    }

    #region Native Interop
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr hdcDest, int xDest, int yDest, int w, int h, IntPtr hdcSrc, int xSrc, int ySrc, int rop);
    [DllImport("gdi32.dll")] private static extern bool StretchBlt(IntPtr hdcDest, int xDest, int yDest, int wDest, int hDest, IntPtr hdcSrc, int xSrc, int ySrc, int wSrc, int hSrc, int rop);
    [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(IntPtr hdc, int mode);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateDC(string lpszDriver, string? lpszDevice, string? lpszOutput, IntPtr lpInitData);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr OpenInputDesktop(uint dwFlags, bool fInherit, uint dwDesiredAccess);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetThreadDesktop(IntPtr hDesktop);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr hDesktop);
    [DllImport("user32.dll")] private static extern IntPtr GetThreadDesktop(uint dwThreadId);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetUserObjectInformation(IntPtr hObj, int nIndex, byte[] pvInfo, int nLength, out int lpnLengthNeeded);
    [DllImport("user32.dll")] private static extern bool GetCursorInfo(out CURSORINFO pci);
    [DllImport("d3d11.dll")] private static extern int D3D11CreateDevice(IntPtr pAdapter, int driverType, IntPtr software, uint flags, int[] pFeatureLevels, int featureLevels, uint sdkVersion, out IntPtr ppDevice, out int pFeatureLevel, out IntPtr ppImmediateContext);

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct DXGI_OUTPUT_DESC
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public RECT DesktopCoordinates;
        public int AttachedToDesktop;
        public int Rotation;
        public IntPtr Monitor;
    }
    [StructLayout(LayoutKind.Sequential)] private struct CURSORINFO { public int cbSize; public int flags; public IntPtr hCursor; public POINT ptScreenPos; }
    [StructLayout(LayoutKind.Sequential)] private struct D3D11_TEXTURE2D_DESC { public uint Width, Height, MipLevels, ArraySize, Format, SampleCount, SampleQuality, Usage, BindFlags, CPUAccessFlags, MiscFlags; }
    [StructLayout(LayoutKind.Sequential)] private struct D3D11_MAPPED_SUBRESOURCE { public IntPtr pData; public int RowPitch; public int DepthPitch; }
    [StructLayout(LayoutKind.Sequential)] private struct DXGI_OUTDUPL_FRAME_INFO { public long LastPresentTime, LastMouseUpdateTime; public uint AccumulatedFrames, RectsCoalesced, ProtectedContentMaskedOut; public POINT PointerPosition; public int PointerVisible; public uint TotalMetadataBufferSize, PointerShapeBufferSize; }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetAdapterDelegate(IntPtr self, out IntPtr adapter);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int EnumOutputsDelegate(IntPtr self, uint idx, out IntPtr output);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetOutputDescDelegate(IntPtr self, out DXGI_OUTPUT_DESC desc);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int DuplicateOutputDelegate(IntPtr self, IntPtr device, out IntPtr dup);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateTexture2DDelegate(IntPtr self, ref D3D11_TEXTURE2D_DESC desc, IntPtr initData, out IntPtr tex);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void GetTextureDescDelegate(IntPtr self, out D3D11_TEXTURE2D_DESC desc);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int AcquireNextFrameDelegate(IntPtr self, uint timeoutMs, out DXGI_OUTDUPL_FRAME_INFO info, out IntPtr res);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ReleaseFrameDelegate(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void CopyResourceDelegate(IntPtr self, IntPtr dst, IntPtr src);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int MapDelegate(IntPtr self, IntPtr res, uint sub, int mapType, uint flags, out D3D11_MAPPED_SUBRESOURCE mapped);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void UnmapDelegate(IntPtr self, IntPtr res, uint sub);
    #endregion
}
