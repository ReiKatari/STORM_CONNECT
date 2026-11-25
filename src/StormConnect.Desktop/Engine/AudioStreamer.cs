using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace StormConnect.Desktop.Engine;

/// <summary>
/// Zero-NuGet Win32 waveIn/waveOut PCM 16-bit 22050Hz mono audio streamer for low-latency session audio.
/// </summary>
public sealed class AudioStreamer : IDisposable
{
    private const int SampleRate = 22050;
    private const int Channels = 1;
    private const int BitsPerSample = 16;
    private const int BufferMs = 40;

    private IntPtr _hWaveOut;
    private readonly ConcurrentQueue<byte[]> _rxQueue = new();
    private CancellationTokenSource? _playCts;
    private CancellationTokenSource? _capCts;
    private bool _disposed;

    public event Action<byte[]>? AudioFrameCaptured;

    public void StartCapture()
    {
        if (_capCts is not null) return;
        _capCts = new CancellationTokenSource();
        var ct = _capCts.Token;

        Task.Run(() => CaptureLoop(ct), ct);
    }

    public void StopCapture()
    {
        _capCts?.Cancel();
        _capCts?.Dispose();
        _capCts = null;
    }

    public void StartPlayback()
    {
        if (_playCts is not null) return;
        _playCts = new CancellationTokenSource();
        var ct = _playCts.Token;
        Task.Run(() => PlaybackLoop(ct), ct);
    }

    public void StopPlayback()
    {
        _playCts?.Cancel();
        _playCts?.Dispose();
        _playCts = null;
    }

    public void EnqueuePlaybackFrame(byte[] pcmBytes)
    {
        if (_disposed || _playCts is null) return;
        while (_rxQueue.Count > 15) _rxQueue.TryDequeue(out _);
        _rxQueue.Enqueue(pcmBytes);
    }

    private void CaptureLoop(CancellationToken ct)
    {
        var fmt = CreateWaveFormat();
        if (waveInOpen(out IntPtr hWaveIn, -1 /* WAVE_MAPPER */, ref fmt, IntPtr.Zero, IntPtr.Zero, 0) != 0)
            return;

        int bufBytes = SampleRate * Channels * (BitsPerSample / 8) * BufferMs / 1000;
        const int numBufs = 3;
        var ptrs = new IntPtr[numBufs];
        var hdrPtrs = new IntPtr[numBufs];

        try
        {
            for (int i = 0; i < numBufs; i++)
            {
                ptrs[i] = Marshal.AllocHGlobal(bufBytes);
                var hdr = new WAVEHDR { lpData = ptrs[i], dwBufferLength = (uint)bufBytes };
                hdrPtrs[i] = Marshal.AllocHGlobal(Marshal.SizeOf<WAVEHDR>());
                Marshal.StructureToPtr(hdr, hdrPtrs[i], false);
                waveInPrepareHeader(hWaveIn, hdrPtrs[i], (uint)Marshal.SizeOf<WAVEHDR>());
                waveInAddBuffer(hWaveIn, hdrPtrs[i], (uint)Marshal.SizeOf<WAVEHDR>());
            }

            waveInStart(hWaveIn);
            int idx = 0;
            while (!ct.IsCancellationRequested)
            {
                var hdr = Marshal.PtrToStructure<WAVEHDR>(hdrPtrs[idx]);
                if ((hdr.dwFlags & 0x00000001 /* WHDR_DONE */) != 0)
                {
                    if (hdr.dwBytesRecorded > 0)
                    {
                        var data = new byte[hdr.dwBytesRecorded];
                        Marshal.Copy(hdr.lpData, data, 0, (int)hdr.dwBytesRecorded);
                        AudioFrameCaptured?.Invoke(data);
                    }
                    hdr.dwFlags &= ~0x00000001u;
                    hdr.dwBytesRecorded = 0;
                    Marshal.StructureToPtr(hdr, hdrPtrs[idx], false);
                    waveInAddBuffer(hWaveIn, hdrPtrs[idx], (uint)Marshal.SizeOf<WAVEHDR>());
                    idx = (idx + 1) % numBufs;
                }
                else
                {
                    Thread.Sleep(10);
                }
            }
        }
        catch { }
        finally
        {
            try { waveInReset(hWaveIn); } catch { }
            for (int i = 0; i < numBufs; i++)
            {
                if (hdrPtrs[i] != IntPtr.Zero)
                {
                    try { waveInUnprepareHeader(hWaveIn, hdrPtrs[i], (uint)Marshal.SizeOf<WAVEHDR>()); } catch { }
                    Marshal.FreeHGlobal(hdrPtrs[i]);
                }
                if (ptrs[i] != IntPtr.Zero) Marshal.FreeHGlobal(ptrs[i]);
            }
            try { waveInClose(hWaveIn); } catch { }
        }
    }

    private void PlaybackLoop(CancellationToken ct)
    {
        var fmt = CreateWaveFormat();
        if (waveOutOpen(out _hWaveOut, -1 /* WAVE_MAPPER */, ref fmt, IntPtr.Zero, IntPtr.Zero, 0) != 0)
            return;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (_rxQueue.TryDequeue(out var pcm))
                {
                    IntPtr dataPtr = Marshal.AllocHGlobal(pcm.Length);
                    Marshal.Copy(pcm, 0, dataPtr, pcm.Length);
                    var hdr = new WAVEHDR { lpData = dataPtr, dwBufferLength = (uint)pcm.Length };
                    IntPtr hdrPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WAVEHDR>());
                    Marshal.StructureToPtr(hdr, hdrPtr, false);

                    waveOutPrepareHeader(_hWaveOut, hdrPtr, (uint)Marshal.SizeOf<WAVEHDR>());
                    waveOutWrite(_hWaveOut, hdrPtr, (uint)Marshal.SizeOf<WAVEHDR>());

                    // Asynchronous cleanup after buffer finishes
                    _ = Task.Delay(BufferMs * 3, CancellationToken.None).ContinueWith(_ =>
                    {
                        try
                        {
                            if (_hWaveOut != IntPtr.Zero)
                                waveOutUnprepareHeader(_hWaveOut, hdrPtr, (uint)Marshal.SizeOf<WAVEHDR>());
                        }
                        catch { }
                        Marshal.FreeHGlobal(hdrPtr);
                        Marshal.FreeHGlobal(dataPtr);
                    });
                }
                else
                {
                    Thread.Sleep(8);
                }
            }
        }
        catch { }
        finally
        {
            if (_hWaveOut != IntPtr.Zero)
            {
                try { waveOutReset(_hWaveOut); waveOutClose(_hWaveOut); } catch { }
                _hWaveOut = IntPtr.Zero;
            }
        }
    }

    private static WAVEFORMATEX CreateWaveFormat() => new()
    {
        wFormatTag = 1, // PCM
        nChannels = Channels,
        nSamplesPerSec = SampleRate,
        wBitsPerSample = BitsPerSample,
        nBlockAlign = Channels * (BitsPerSample / 8),
        nAvgBytesPerSec = SampleRate * Channels * (BitsPerSample / 8),
        cbSize = 0
    };

    public void Dispose()
    {
        _disposed = true;
        StopCapture();
        StopPlayback();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEFORMATEX
    {
        public ushort wFormatTag, nChannels;
        public uint nSamplesPerSec, nAvgBytesPerSec;
        public ushort nBlockAlign, wBitsPerSample, cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEHDR
    {
        public IntPtr lpData;
        public uint dwBufferLength, dwBytesRecorded;
        public IntPtr dwUser;
        public uint dwFlags, dwLoops;
        public IntPtr lpNext, reserved;
    }

    [DllImport("winmm.dll")] private static extern int waveInOpen(out IntPtr hWaveIn, int uDeviceID, ref WAVEFORMATEX lpFormat, IntPtr dwCallback, IntPtr dwInstance, int dwFlags);
    [DllImport("winmm.dll")] private static extern int waveInPrepareHeader(IntPtr hWaveIn, IntPtr lpWaveInHdr, uint uSize);
    [DllImport("winmm.dll")] private static extern int waveInUnprepareHeader(IntPtr hWaveIn, IntPtr lpWaveInHdr, uint uSize);
    [DllImport("winmm.dll")] private static extern int waveInAddBuffer(IntPtr hWaveIn, IntPtr lpWaveInHdr, uint uSize);
    [DllImport("winmm.dll")] private static extern int waveInStart(IntPtr hWaveIn);
    [DllImport("winmm.dll")] private static extern int waveInReset(IntPtr hWaveIn);
    [DllImport("winmm.dll")] private static extern int waveInClose(IntPtr hWaveIn);

    [DllImport("winmm.dll")] private static extern int waveOutOpen(out IntPtr hWaveOut, int uDeviceID, ref WAVEFORMATEX lpFormat, IntPtr dwCallback, IntPtr dwInstance, int dwFlags);
    [DllImport("winmm.dll")] private static extern int waveOutPrepareHeader(IntPtr hWaveOut, IntPtr lpWaveOutHdr, uint uSize);
    [DllImport("winmm.dll")] private static extern int waveOutUnprepareHeader(IntPtr hWaveOut, IntPtr lpWaveOutHdr, uint uSize);
    [DllImport("winmm.dll")] private static extern int waveOutWrite(IntPtr hWaveOut, IntPtr lpWaveOutHdr, uint uSize);
    [DllImport("winmm.dll")] private static extern int waveOutReset(IntPtr hWaveOut);
    [DllImport("winmm.dll")] private static extern int waveOutClose(IntPtr hWaveOut);
}
