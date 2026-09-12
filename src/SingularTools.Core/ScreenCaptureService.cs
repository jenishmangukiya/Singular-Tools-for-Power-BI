using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;

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

    public static byte[]? CapturePowerBiWindow(out string? error)
    {
        error = null;
        try
        {
            if (!PowerBiDetector.FindActivePowerBiWindow(out IntPtr pbiHwnd, out _, out WindowRect rect) || pbiHwnd == IntPtr.Zero)
            {
                error = "Power BI Desktop window not found.";
                return null;
            }

            return CaptureWindow(pbiHwnd, out error);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    public static byte[]? CaptureWindow(IntPtr hWnd, out string? error)
    {
        error = null;
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
                // Fallback to desktop BitBlt if PrintWindow is unsupported
                IntPtr hdcDesktop = GetDC(IntPtr.Zero);
                try
                {
                    success = BitBlt(hdcMem, 0, 0, width, height, hdcDesktop, rect.Left, rect.Top, SRCCOPY);
                }
                finally
                {
                    ReleaseDC(IntPtr.Zero, hdcDesktop);
                }
            }

            if (!success)
            {
                error = "Failed to copy window graphics buffer.";
                return null;
            }

            // Convert HBITMAP to 32bpp BMP byte array
            return ConvertHBitmapToBmpBytes(hdcMem, hBitmap, width, height);
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
}
