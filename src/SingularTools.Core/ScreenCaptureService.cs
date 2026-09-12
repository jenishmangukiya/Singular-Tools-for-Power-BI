using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace SingularTools.Core;

public static class ScreenCaptureService
{
    private static readonly ConcurrentDictionary<string, byte[]> _pageScreenshots = new();

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int nWidth, int nHeight);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr hdcDest, int nXDest, int nYDest, int nWidth, int nHeight, IntPtr hdcSrc, int nXSrc, int nYSrc, uint dwRop);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out WindowRect lpRect);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out WindowRect lpRect);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr hbmp, uint uStartScan, uint cScanLines, [Out] byte[]? lpvBits, ref BITMAPINFO lpbi, uint uUsage);

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColors;
    }

    private const int SRCCOPY = 0x00CC0020;
    private const uint PW_RENDERFULLCONTENT = 2;
    private const uint DIB_RGB_COLORS = 0;
    private const uint BI_RGB = 0;

    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;
    private const int SW_SHOWNOACTIVATE = 4;
    private const int SW_RESTORE = 9;

    public static void SaveCaptureForPage(string pageId, byte[] bmpData)
    {
        if (string.IsNullOrEmpty(pageId) || bmpData == null || bmpData.Length == 0) return;
        _pageScreenshots[pageId] = bmpData;
    }

    public static byte[]? GetCaptureForPage(string pageId)
    {
        if (string.IsNullOrEmpty(pageId)) return null;
        return _pageScreenshots.TryGetValue(pageId, out var data) ? data : null;
    }

    public static bool HasCaptureForPage(string pageId)
    {
        if (string.IsNullOrEmpty(pageId)) return false;
        return _pageScreenshots.ContainsKey(pageId);
    }

    public static void ClearCache()
    {
        _pageScreenshots.Clear();
    }

    /// <summary>
    /// Captures the live Power BI Desktop window. Tries an off-screen window render
    /// first; if that yields a blank frame (common for GPU-composited windows),
    /// falls back to capturing the on-screen region while the palette window
    /// (paletteHwnd) is temporarily hidden so it does not occlude the report.
    /// </summary>
    public static byte[]? CapturePowerBiWindow(IntPtr paletteHwnd, out string? error)
    {
        error = null;
        try
        {
            if (!PowerBiDetector.FindActivePowerBiWindow(out IntPtr pbiHwnd, out _, out WindowRect rect) || pbiHwnd == IntPtr.Zero)
            {
                error = "Power BI Desktop window not found. Make sure Power BI Desktop is open.";
                return null;
            }

            // 1. Try rendering the window directly (works even when occluded).
            var rendered = CaptureWindow(pbiHwnd, out var renderError, out bool renderedBlank);
            if (rendered != null && !renderedBlank)
            {
                return rendered;
            }

            // 2. GPU-composited content often comes back black; grab it from the screen.
            var onScreen = CaptureScreenRegion(rect, paletteHwnd, pbiHwnd, out var screenError);
            if (onScreen != null && !IsMostlyBlankBmp(onScreen, blackOnly: true))
            {
                return onScreen;
            }

            error = renderError ?? screenError ?? "Power BI window produced an empty capture.";
            return null;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    public static byte[]? CaptureWindow(IntPtr hWnd, out string? error)
    {
        return CaptureWindow(hWnd, out error, out _);
    }

    public static byte[]? CaptureWindow(IntPtr hWnd, out string? error, out bool isBlank)
    {
        error = null;
        isBlank = false;
        if (hWnd == IntPtr.Zero)
        {
            error = "Invalid window handle.";
            return null;
        }

        if (!GetWindowRect(hWnd, out WindowRect rect) || rect.Width <= 0 || rect.Height <= 0)
        {
            error = "Could not retrieve window bounds.";
            return null;
        }

        int width = rect.Width;
        int height = rect.Height;

        IntPtr hdcWindow = GetDC(hWnd);
        IntPtr hdcMem = CreateCompatibleDC(hdcWindow);
        IntPtr hBitmap = CreateCompatibleBitmap(hdcWindow, width, height);
        IntPtr hOld = SelectObject(hdcMem, hBitmap);

        try
        {
            // Try PrintWindow first (renders hardware accelerated / background windows)
            bool success = PrintWindow(hWnd, hdcMem, PW_RENDERFULLCONTENT);
            if (!success)
            {
                // Fallback to PrintWindow standard
                success = PrintWindow(hWnd, hdcMem, 0);
            }

            if (!success)
            {
                error = "Failed to copy window graphics buffer.";
                return null;
            }

            // Convert HBITMAP to 32bpp BMP byte array
            var bytes = ConvertHBitmapToBmpBytes(hdcMem, hBitmap, width, height);
            if (bytes != null)
            {
                isBlank = IsMostlyBlankBmp(bytes);
            }
            return bytes;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
        finally
        {
            SelectObject(hdcMem, hOld);
            DeleteObject(hBitmap);
            DeleteDC(hdcMem);
            ReleaseDC(hWnd, hdcWindow);
        }
    }

    /// <summary>
    /// Captures a screen rectangle via BitBlt from the desktop. Hides the palette
    /// window and brings the target window (targetHwnd) to the foreground first, so
    /// the grab shows the report rather than whatever happened to be on top.
    /// </summary>
    public static byte[]? CaptureScreenRegion(WindowRect rect, IntPtr paletteHwnd, IntPtr targetHwnd, out string? error)
    {
        error = null;
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            error = "Invalid capture region.";
            return null;
        }

        bool hidden = false;
        try
        {
            if (paletteHwnd != IntPtr.Zero && IsWindowVisible(paletteHwnd))
            {
                ShowWindow(paletteHwnd, SW_HIDE);
                hidden = true;
            }

            if (targetHwnd != IntPtr.Zero)
            {
                ShowWindow(targetHwnd, SW_RESTORE);
                SetForegroundWindow(targetHwnd);
            }

            // Let DWM apply the composition/foreground changes before grabbing pixels.
            DwmFlush();
            Thread.Sleep(120);

            int width = rect.Width;
            int height = rect.Height;

            IntPtr hdcScreen = GetDC(IntPtr.Zero);
            IntPtr hdcMem = CreateCompatibleDC(hdcScreen);
            IntPtr hBitmap = CreateCompatibleBitmap(hdcScreen, width, height);
            IntPtr hOld = SelectObject(hdcMem, hBitmap);

            try
            {
                if (!BitBlt(hdcMem, 0, 0, width, height, hdcScreen, rect.Left, rect.Top, SRCCOPY))
                {
                    error = "Failed to copy the screen region.";
                    return null;
                }

                return ConvertHBitmapToBmpBytes(hdcMem, hBitmap, width, height);
            }
            finally
            {
                SelectObject(hdcMem, hOld);
                DeleteObject(hBitmap);
                DeleteDC(hdcMem);
                ReleaseDC(IntPtr.Zero, hdcScreen);
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
        finally
        {
            if (hidden)
            {
                ShowWindow(paletteHwnd, SW_SHOW);
                SetForegroundWindow(paletteHwnd);
            }
        }
    }

    private static byte[]? ConvertHBitmapToBmpBytes(IntPtr hdc, IntPtr hBitmap, int width, int height)
    {
        BITMAPINFO bmi = new()
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = width,
                biHeight = -height, // Negative for top-down DIB
                biPlanes = 1,
                biBitCount = 32,
                biCompression = BI_RGB,
                biSizeImage = (uint)(width * height * 4)
            }
        };

        byte[] pixelData = new byte[width * height * 4];
        int scanLines = GetDIBits(hdc, hBitmap, 0, (uint)height, pixelData, ref bmi, DIB_RGB_COLORS);
        if (scanLines <= 0) return null;

        // GDI leaves the alpha channel at 0. Windows Imaging Component would then
        // treat the whole bitmap as fully transparent (rendering as a blank image),
        // so force every pixel to be opaque.
        for (int i = 3; i < pixelData.Length; i += 4)
        {
            pixelData[i] = 255;
        }

        // Construct standard BMP in-memory stream:
        // BITMAPFILEHEADER (14 bytes) + BITMAPINFOHEADER (40 bytes) + pixel bytes
        int fileHeaderSize = 14;
        int infoHeaderSize = 40;
        int totalFileSize = fileHeaderSize + infoHeaderSize + pixelData.Length;

        using var ms = new MemoryStream(totalFileSize);
        using var writer = new BinaryWriter(ms);

        // BITMAPFILEHEADER
        writer.Write((ushort)0x4D42);          // 'BM'
        writer.Write((uint)totalFileSize);      // File size
        writer.Write((ushort)0);               // Reserved1
        writer.Write((ushort)0);               // Reserved2
        writer.Write((uint)(fileHeaderSize + infoHeaderSize)); // Offset to pixel data (54)

        // BITMAPINFOHEADER
        writer.Write((uint)infoHeaderSize);    // 40
        writer.Write(width);                   // Width
        writer.Write(-height);                 // Top-down height
        writer.Write((ushort)1);               // Planes
        writer.Write((ushort)32);              // Bits per pixel
        writer.Write((uint)0);                 // Compression (BI_RGB)
        writer.Write((uint)pixelData.Length);  // Image size
        writer.Write(0);                       // X pels per meter
        writer.Write(0);                       // Y pels per meter
        writer.Write((uint)0);                 // Colors used
        writer.Write((uint)0);                 // Important colors

        // Pixel data (BGRA)
        writer.Write(pixelData);

        return ms.ToArray();
    }

    /// <summary>
    /// Returns true when a BMP is almost entirely black, which indicates the capture
    /// did not actually render the window content. When blackOnly is false, an almost
    /// entirely white frame is also treated as blank (a PrintWindow failure mode).
    /// </summary>
    private static bool IsMostlyBlankBmp(byte[] bmpData, bool blackOnly = false)
    {
        const int headerSize = 54;
        if (bmpData == null || bmpData.Length <= headerSize) return true;

        int pixelBytes = bmpData.Length - headerSize;
        int pixelCount = pixelBytes / 4;
        if (pixelCount <= 0) return true;

        // Sample a bounded number of pixels for speed.
        int step = Math.Max(1, pixelCount / 60000);
        long sampled = 0, dark = 0, light = 0;

        for (int p = 0; p < pixelCount; p += step)
        {
            int i = headerSize + p * 4;
            if (i + 2 >= bmpData.Length) break;
            int b = bmpData[i];
            int g = bmpData[i + 1];
            int r = bmpData[i + 2];
            int v = (r + g + b) / 3;
            sampled++;
            if (v < 12) dark++;
            else if (v > 243) light++;
        }

        if (sampled == 0) return true;
        double fracDark = (double)dark / sampled;
        double fracLight = (double)light / sampled;
        if (fracDark > 0.985) return true;
        if (!blackOnly && fracLight > 0.995) return true;
        return false;
    }
}
