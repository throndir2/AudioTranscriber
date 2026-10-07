using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AudioTranscriber.App.Templates;

public sealed record CaptureResult(byte[] Jpeg, BitmapSource Preview, string Fingerprint, string Source, DateTime Time)
{
    public string Caption => $"{Source} · {Preview.PixelWidth}×{Preview.PixelHeight} · {Jpeg.Length / 1024.0:N0} KB · {Time:HH:mm:ss}";
}

public sealed class CaptureException(string message) : Exception(message);

/// <summary>Screenshots of a monitor or a top-level window (for example the browser running Roll20) as downscaled JPEG.</summary>
public static partial class ScreenCapture
{
    public const int DefaultMaxWidth = 1280;

    /// <summary>"Screen N (W×H)" for each monitor, then the titles of visible top-level windows.</summary>
    public static IReadOnlyList<string> ListSources() => WithPhysicalPixels(() =>
    {
        var sources = Monitors().Select((m, i) => $"Screen {i + 1} ({m.Width}×{m.Height})").ToList();
        sources.AddRange(Windows().Select(w => w.Title).Distinct().OrderBy(t => t, StringComparer.CurrentCultureIgnoreCase));
        return (IReadOnlyList<string>)sources;
    });

    public static CaptureResult Capture(string target, int maxWidth) => WithPhysicalPixels(() =>
    {
        target = (target ?? "").Trim();
        if (target.Length == 0) throw new CaptureException("Choose a screen or window to capture in the Table screenshot card first.");
        BitmapSource full;
        string source;
        if (ScreenTarget().Match(target) is { Success: true } screen)
        {
            var monitors = Monitors();
            var index = int.Parse(screen.Groups[1].Value) - 1;
            if (index < 0 || index >= monitors.Count) throw new CaptureException($"Screen {index + 1} is not connected ({monitors.Count} found).");
            var r = monitors[index];
            full = Grab(r.Width, r.Height, dc =>
            {
                var screenDc = GetDC(IntPtr.Zero);
                try { return BitBlt(dc, 0, 0, r.Width, r.Height, screenDc, r.Left, r.Top, SRCCOPY | CAPTUREBLT); }
                finally { ReleaseDC(IntPtr.Zero, screenDc); }
            });
            source = $"Screen {index + 1}";
        }
        else
        {
            var window = FindWindow(target) ?? throw new CaptureException($"No open window matches \"{target}\". Open it, or choose Refresh and pick it again.");
            if (IsIconic(window.Handle)) throw new CaptureException($"The window \"{window.Title}\" is minimized. Restore it (other windows may cover it) and try again.");
            if (!GetWindowRect(window.Handle, out var rect) || rect.Width <= 0 || rect.Height <= 0)
                throw new CaptureException($"Could not read the size of \"{window.Title}\".");
            full = Grab(rect.Width, rect.Height, dc => PrintWindow(window.Handle, dc, PW_RENDERFULLCONTENT),
                $"Windows could not capture \"{window.Title}\". If it runs as administrator, capture its screen instead.");
            source = window.Title;
        }
        var scaled = Downscale(full, Math.Max(160, maxWidth));
        var encoder = new JpegBitmapEncoder { QualityLevel = 85 };
        encoder.Frames.Add(BitmapFrame.Create(scaled));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return new CaptureResult(stream.ToArray(), scaled, Fingerprint(full), source, DateTime.Now);
    });

    /// <summary>Exact title first, then a title containing the text, then one sharing the part before " - " (browser titles change per tab).</summary>
    private static (IntPtr Handle, string Title)? FindWindow(string target)
    {
        var windows = Windows();
        var head = target.Split(" - ")[0].Trim();
        foreach (var match in new Func<string, bool>[]
                 {
                     t => t == target,
                     t => t.Contains(target, StringComparison.OrdinalIgnoreCase),
                     t => head.Length >= 3 && t.Contains(head, StringComparison.OrdinalIgnoreCase)
                 })
            if (windows.FirstOrDefault(w => match(w.Title)) is { Handle: not 0 } found) return found;
        return null;
    }

    private static BitmapSource Downscale(BitmapSource image, int maxWidth)
    {
        if (image.PixelWidth <= maxWidth) return image;
        var scale = (double)maxWidth / image.PixelWidth;
        var result = new TransformedBitmap(image, new ScaleTransform(scale, scale));
        result.Freeze();
        return result;
    }

    /// <summary>A 64×36 grayscale thumbnail quantized to 8 levels, hashed: unchanged screens give the same value.</summary>
    private static string Fingerprint(BitmapSource image)
    {
        var thumb = new FormatConvertedBitmap(new TransformedBitmap(image, new ScaleTransform(64.0 / image.PixelWidth, 36.0 / image.PixelHeight)),
            PixelFormats.Gray8, null, 0);
        var stride = (thumb.PixelWidth + 3) & ~3;
        var pixels = new byte[stride * thumb.PixelHeight];
        thumb.CopyPixels(pixels, stride, 0);
        for (var i = 0; i < pixels.Length; i++) pixels[i] >>= 5;
        return Convert.ToHexString(SHA256.HashData(pixels))[..16];
    }

    private static BitmapSource Grab(int width, int height, Func<IntPtr, bool> draw, string failure = "Windows could not capture the screen.")
    {
        var screenDc = GetDC(IntPtr.Zero);
        var memoryDc = CreateCompatibleDC(screenDc);
        var info = new BITMAPINFOHEADER { biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = width, biHeight = -height, biPlanes = 1, biBitCount = 32 };
        var bitmap = CreateDIBSection(screenDc, ref info, 0, out var bits, IntPtr.Zero, 0);
        try
        {
            if (bitmap == IntPtr.Zero) throw new CaptureException("Could not allocate the screenshot bitmap.");
            var old = SelectObject(memoryDc, bitmap);
            var ok = draw(memoryDc);
            GdiFlush();
            SelectObject(memoryDc, old);
            if (!ok) throw new CaptureException(failure);
            var stride = width * 4;
            var pixels = new byte[stride * height];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, pixels, stride);
            image.Freeze();
            return image;
        }
        finally
        {
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            DeleteDC(memoryDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private static List<RECT> Monitors()
    {
        var list = new List<(RECT Rect, bool Primary)>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(monitor, ref info)) list.Add((info.rcMonitor, (info.dwFlags & 1) != 0));
            return true;
        }, IntPtr.Zero);
        // Primary first, then left to right, so "Screen 1" is the main display.
        return list.OrderByDescending(m => m.Primary).ThenBy(m => m.Rect.Left).ThenBy(m => m.Rect.Top).Select(m => m.Rect).ToList();
    }

    private static List<(IntPtr Handle, string Title)> Windows()
    {
        var own = (uint)Environment.ProcessId;
        var list = new List<(IntPtr, string)>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            if ((GetWindowLongPtr(hwnd, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0) return true;
            if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == own) return true;
            var length = GetWindowTextLength(hwnd);
            if (length == 0) return true;
            var text = new StringBuilder(length + 1);
            GetWindowText(hwnd, text, text.Capacity);
            var title = text.ToString().Trim();
            if (title.Length > 0 && title != "Program Manager") list.Add((hwnd, title));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    // Per-monitor DPI awareness on the calling thread so sizes and coordinates are real pixels on every monitor.
    private static T WithPhysicalPixels<T>(Func<T> action)
    {
        var previous = SetThreadDpiAwarenessContext(new IntPtr(-4));
        try { return action(); }
        finally { if (previous != IntPtr.Zero) SetThreadDpiAwarenessContext(previous); }
    }

    [GeneratedRegex(@"^screen\s*:?\s*(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex ScreenTarget();

    private const int SRCCOPY = 0x00CC0020, CAPTUREBLT = 0x40000000, PW_RENDERFULLCONTENT = 2, GWL_EXSTYLE = -20, DWMWA_CLOAKED = 14;
    private const long WS_EX_TOOLWINDOW = 0x80;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern long GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool GdiFlush();
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dest, int x, int y, int width, int height, IntPtr source, int sx, int sy, int rop);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER info, uint usage, out IntPtr bits, IntPtr section, uint offset);
}
