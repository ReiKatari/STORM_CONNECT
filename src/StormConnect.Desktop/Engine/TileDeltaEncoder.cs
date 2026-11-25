using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using StormConnect.Protocol;

namespace StormConnect.Desktop.Engine;

public sealed class EncodedVideoUpdate
{
    public required bool IsFullFrame { get; init; }
    public required int FrameWidth { get; init; }
    public required int FrameHeight { get; init; }
    public required List<VideoTileRect> Rects { get; init; }
    public required double TileSavingsPercent { get; init; }
}

/// <summary>
/// Splits captured BGRA frames into 64x64 tiles, detects dirty tiles using fast 64-bit FNV-1a sampling,
/// merges adjacent dirty tiles into tight bounding rectangles, and encodes JPEG slices in parallel.
/// </summary>
public sealed class TileDeltaEncoder
{
    private ulong[]? _prevHashes;
    private int _prevWidth;
    private int _prevHeight;
    private int _cols;
    private int _rows;
    private static readonly ImageCodecInfo? JpegCodec = ImageCodecInfo.GetImageEncoders().FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid);

    public void Reset()
    {
        _prevHashes = null;
        _prevWidth = 0;
        _prevHeight = 0;
    }

    public EncodedVideoUpdate? EncodeWithSizeBudget(CapturedFrame frame, int jpegQuality, bool forceFullFrame, int maxWidth, int maxPacketBytes)
    {
        var upd = Encode(frame, jpegQuality, forceFullFrame, maxWidth);
        if (upd is null || maxPacketBytes <= 0) return upd;

        if (EstimateWireSize(upd) <= maxPacketBytes)
            return upd;

        int baseW = maxWidth > 0 ? maxWidth : frame.Width;
        int[] tryWidths =
        [
            baseW,
            Math.Min(baseW, 960),
            Math.Min(baseW, 800),
            Math.Min(baseW, 640),
            Math.Min(baseW, 480)
        ];
        int[] tryQualities = [Math.Min(jpegQuality, 42), 32, 24, 18, 12];

        foreach (int w in tryWidths.Distinct())
        {
            foreach (int q in tryQualities)
            {
                Reset();
                var candidate = Encode(frame, q, forceFullFrame: upd.IsFullFrame, w);
                if (candidate is not null && EstimateWireSize(candidate) <= maxPacketBytes)
                    return candidate;
                if (candidate is not null) upd = candidate;
            }
        }
        return upd;
    }

    private static int EstimateWireSize(EncodedVideoUpdate upd)
    {
        int len = 10;
        foreach (var r in upd.Rects) len += 20 + r.JpegBytes.Length;
        return len;
    }

    public EncodedVideoUpdate? Encode(CapturedFrame frame, int jpegQuality, bool forceFullFrame = false, int maxWidth = 0)
    {
        if (maxWidth > 0 && frame.Width > maxWidth)
        {
            frame = DownscaleFrame(frame, maxWidth);
        }

        int w = frame.Width;
        int h = frame.Height;
        int tile = ProtocolConstants.TileSize;
        int cols = (w + tile - 1) / tile;
        int rows = (h + tile - 1) / tile;
        int totalTiles = cols * rows;

        bool sizeChanged = _prevHashes is null || _prevWidth != w || _prevHeight != h;
        bool full = forceFullFrame || sizeChanged;

        var newHashes = new ulong[totalTiles];
        var dirty = new bool[totalTiles];
        int dirtyCount = 0;

        for (int ty = 0; ty < rows; ty++)
        {
            int py0 = ty * tile;
            int py1 = Math.Min(h, py0 + tile);
            for (int tx = 0; tx < cols; tx++)
            {
                int px0 = tx * tile;
                int px1 = Math.Min(w, px0 + tile);
                ulong hash = ComputeTileHash(frame.BgraPixels, frame.Stride, px0, py0, px1, py1);
                int idx = ty * cols + tx;
                newHashes[idx] = hash;
                if (full || _prevHashes![idx] != hash)
                {
                    dirty[idx] = true;
                    dirtyCount++;
                }
            }
        }

        _prevHashes = newHashes;
        _prevWidth = w;
        _prevHeight = h;
        _cols = cols;
        _rows = rows;

        if (!full && dirtyCount == 0)
            return null;

        // If > 65% of tiles changed, send a single full frame for maximum compression efficiency
        if (full || dirtyCount > totalTiles * 0.65)
        {
            var fullJpeg = EncodeRegionJpeg(frame.BgraPixels, w, h, frame.Stride, 0, 0, w, h, jpegQuality);
            return new EncodedVideoUpdate
            {
                IsFullFrame = true,
                FrameWidth = w,
                FrameHeight = h,
                Rects = [new VideoTileRect(0, 0, w, h, fullJpeg)],
                TileSavingsPercent = 0
            };
        }

        // Merge horizontally adjacent dirty tiles per row into rectangular regions
        var rects = new List<Rectangle>();
        for (int ty = 0; ty < rows; ty++)
        {
            int tx = 0;
            while (tx < cols)
            {
                if (!dirty[ty * cols + tx]) { tx++; continue; }
                int startTx = tx;
                while (tx < cols && dirty[ty * cols + tx]) tx++;
                int rx = startTx * tile;
                int ry = ty * tile;
                int rw = Math.Min(w, tx * tile) - rx;
                int rh = Math.Min(h, (ty + 1) * tile) - ry;
                rects.Add(new Rectangle(rx, ry, rw, rh));
            }
        }

        var encodedTiles = new VideoTileRect[rects.Count];
        var popts = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 4) };
        Parallel.For(0, rects.Count, popts, i =>
        {
            var r = rects[i];
            var jpg = EncodeRegionJpeg(frame.BgraPixels, w, h, frame.Stride, r.X, r.Y, r.Width, r.Height, jpegQuality);
            encodedTiles[i] = new VideoTileRect(r.X, r.Y, r.Width, r.Height, jpg);
        });

        double savings = Math.Round((1.0 - (double)dirtyCount / totalTiles) * 100.0, 1);
        return new EncodedVideoUpdate
        {
            IsFullFrame = false,
            FrameWidth = w,
            FrameHeight = h,
            Rects = [.. encodedTiles],
            TileSavingsPercent = savings
        };
    }

    public static byte[] SerializeVideoUpdate(EncodedVideoUpdate update)
    {
        // Binary wire layout: [4B Width][4B Height][2B RectCount] then per rect: [4B X][4B Y][4B W][4B H][4B JpegLen][JpegBytes]
        int totalLen = 10;
        foreach (var r in update.Rects) totalLen += 20 + r.JpegBytes.Length;

        var buf = new byte[totalLen];
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(0, 4), update.FrameWidth);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(4, 4), update.FrameHeight);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(8, 2), (ushort)update.Rects.Count);

        int offset = 10;
        foreach (var r in update.Rects)
        {
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(offset, 4), r.X);
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(offset + 4, 4), r.Y);
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(offset + 8, 4), r.Width);
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(offset + 12, 4), r.Height);
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(offset + 16, 4), r.JpegBytes.Length);
            Buffer.BlockCopy(r.JpegBytes, 0, buf, offset + 20, r.JpegBytes.Length);
            offset += 20 + r.JpegBytes.Length;
        }
        return buf;
    }

    public static (int Width, int Height, List<VideoTileRect> Rects) DeserializeVideoUpdate(byte[] data)
    {
        int w = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(0, 4));
        int h = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4, 4));
        int count = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(8, 2));
        var rects = new List<VideoTileRect>(count);

        int offset = 10;
        for (int i = 0; i < count; i++)
        {
            int x = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4));
            int y = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset + 4, 4));
            int rw = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset + 8, 4));
            int rh = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset + 12, 4));
            int jlen = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset + 16, 4));
            var jpg = new byte[jlen];
            Buffer.BlockCopy(data, offset + 20, jpg, 0, jlen);
            rects.Add(new VideoTileRect(x, y, rw, rh, jpg));
            offset += 20 + jlen;
        }
        return (w, h, rects);
    }

    private static unsafe ulong ComputeTileHash(byte[] pixels, int stride, int x0, int y0, int x1, int y1)
    {
        ulong hash = 14695981039346656037UL;
        fixed (byte* basePtr = pixels)
        {
            for (int y = y0; y < y1; y += 2)
            {
                byte* row = basePtr + y * stride + x0 * 4;
                int span = (x1 - x0);
                for (int x = 0; x < span; x += 2)
                {
                    uint px = *(uint*)(row + x * 4);
                    hash ^= px;
                    hash *= 1099511628211UL;
                }
            }
        }
        return hash;
    }

    private static byte[] EncodeRegionJpeg(byte[] pixels, int fullW, int fullH, int stride, int rx, int ry, int rw, int rh, int quality)
    {
        using var regionBmp = new Bitmap(rw, rh, PixelFormat.Format24bppRgb);
        var dstData = regionBmp.LockBits(new Rectangle(0, 0, rw, rh), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        try
        {
            for (int y = 0; y < rh; y++)
            {
                int srcOff = (ry + y) * stride + rx * 4;
                IntPtr dstRow = IntPtr.Add(dstData.Scan0, y * dstData.Stride);
                // Convert 32bpp BGRA -> 24bpp BGR directly
                CopyBgraToBgrRow(pixels, srcOff, dstRow, rw);
            }
        }
        finally
        {
            regionBmp.UnlockBits(dstData);
        }

        using var ms = new MemoryStream(Math.Max(1024, rw * rh / 8));
        if (JpegCodec is not null)
        {
            using var encParams = new EncoderParameters(1);
            encParams.Param[0] = new EncoderParameter(Encoder.Quality, Math.Clamp(quality, 15, 98));
            regionBmp.Save(ms, JpegCodec, encParams);
        }
        else
        {
            regionBmp.Save(ms, ImageFormat.Jpeg);
        }
        return ms.ToArray();
    }

    private static CapturedFrame DownscaleFrame(CapturedFrame src, int maxWidth)
    {
        int dstW = Math.Max(64, maxWidth & ~1);
        int dstH = Math.Max(64, ((int)Math.Round(src.Height * ((double)dstW / src.Width))) & ~1);

        using var srcBmp = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
        var srcData = srcBmp.LockBits(new Rectangle(0, 0, src.Width, src.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            int copyBytes = Math.Min(src.BgraPixels.Length, Math.Abs(srcData.Stride) * src.Height);
            Marshal.Copy(src.BgraPixels, 0, srcData.Scan0, copyBytes);
        }
        finally
        {
            srcBmp.UnlockBits(srcData);
        }

        using var dstBmp = new Bitmap(dstW, dstH, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(dstBmp))
        {
            g.InterpolationMode = InterpolationMode.Bilinear;
            g.CompositingQuality = CompositingQuality.HighSpeed;
            g.PixelOffsetMode = PixelOffsetMode.HighSpeed;
            g.DrawImage(srcBmp, new Rectangle(0, 0, dstW, dstH), new Rectangle(0, 0, src.Width, src.Height), GraphicsUnit.Pixel);
        }

        var dstData = dstBmp.LockBits(new Rectangle(0, 0, dstW, dstH), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int len = dstData.Stride * dstH;
            var outPx = new byte[len];
            Marshal.Copy(dstData.Scan0, outPx, 0, len);
            return new CapturedFrame
            {
                Width = dstW,
                Height = dstH,
                Stride = dstData.Stride,
                BgraPixels = outPx,
                CursorX = (int)Math.Round(src.CursorX * ((double)dstW / src.Width)),
                CursorY = (int)Math.Round(src.CursorY * ((double)dstH / src.Height)),
                CursorVisible = src.CursorVisible
            };
        }
        finally
        {
            dstBmp.UnlockBits(dstData);
        }
    }

    private static unsafe void CopyBgraToBgrRow(byte[] src, int srcOffset, IntPtr dstPtr, int pixelCount)
    {
        byte* dst = (byte*)dstPtr;
        fixed (byte* sBase = src)
        {
            byte* s = sBase + srcOffset;
            for (int i = 0; i < pixelCount; i++)
            {
                dst[0] = s[0];
                dst[1] = s[1];
                dst[2] = s[2];
                dst += 3;
                s += 4;
            }
        }
    }
}
