using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace AudioTranscriber.App.Templates;

public sealed record CaptureResult(byte[] Jpeg, Bitmap Preview, string Fingerprint, string Source, DateTime Time)
{
    public string Caption => $"{Source} · {Preview.PixelSize.Width}×{Preview.PixelSize.Height} · {Jpeg.Length / 1024.0:N0} KB · {Time:HH:mm:ss}";
}

public sealed class CaptureException(string message) : Exception(message);

/// <summary>Screenshots of a monitor or top-level window as PNG bytes for multimodal template inputs.</summary>
public static partial class ScreenCapture
{
    public const int DefaultMaxWidth = 1280;

    public static IReadOnlyList<string> ListSources()
    {
        if (!OperatingSystem.IsWindows()) return LinuxSources();
        return WithPhysicalPixels(() =>
        {
            var sources = Monitors().Select((m, i) => $"Screen {i + 1} ({m.Width}×{m.Height})").ToList();
            sources.AddRange(Windows().Select(w => w.Title).Distinct().OrderBy(t => t, StringComparer.CurrentCultureIgnoreCase));
            return (IReadOnlyList<string>)sources;
        });
    }

    public static CaptureResult Capture(string target, int maxWidth)
    {
        target = (target ?? "").Trim();
        if (target.Length == 0) throw new CaptureException("Choose a screen or window to capture in the Table screenshot card first.");
        return OperatingSystem.IsWindows() ? CaptureWindows(target, maxWidth) : CaptureLinux(target, maxWidth);
    }

    private static CaptureResult CaptureWindows(string target, int maxWidth) => WithPhysicalPixels(() =>
    {
        Bitmap full;
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
            if (IsIconic(window.Handle)) throw new CaptureException($"The window \"{window.Title}\" is minimized. Restore it and try again.");
            if (!GetWindowRect(window.Handle, out var rect) || rect.Width <= 0 || rect.Height <= 0)
                throw new CaptureException($"Could not read the size of \"{window.Title}\".");
            full = Grab(rect.Width, rect.Height, dc => PrintWindow(window.Handle, dc, PW_RENDERFULLCONTENT),
                $"Windows could not capture \"{window.Title}\". If it runs as administrator, capture its screen instead.");
            source = window.Title;
        }
        return ResultFromBitmap(full, source, maxWidth);
    });

    private static CaptureResult CaptureLinux(string target, int maxWidth)
    {
        var temp = Path.Combine(Path.GetTempPath(), "audiotranscriber-capture-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            if (target.StartsWith("Window:", StringComparison.OrdinalIgnoreCase))
            {
                var id = target.Split(' ', 3).Skip(1).FirstOrDefault()?.Trim();
                if (string.IsNullOrWhiteSpace(id) || !TryRun("import", ["-window", id, temp], out _))
                    throw new CaptureException("Window capture on Linux needs X11 with ImageMagick 'import' and wmctrl. Capture the entire screen instead, or install imagemagick wmctrl.");
            }
            else if (!TryCaptureLinuxScreen(temp, out var message)) throw new CaptureException(message);
            awaitFile(temp);
            var png = File.ReadAllBytes(temp);
            return ResultFromEncoded(png, target.StartsWith("Window:", StringComparison.OrdinalIgnoreCase) ? target : "Entire screen", maxWidth);
        }
        finally { try { File.Delete(temp); } catch { } }

        static void awaitFile(string path)
        {
            if (!File.Exists(path) || new FileInfo(path).Length == 0) throw new CaptureException("The screenshot tool did not create an image file.");
        }
    }

    private static IReadOnlyList<string> LinuxSources()
    {
        var sources = new List<string> { "Entire screen" };
        if (TryRun("wmctrl", ["-l"], out var output))
        {
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split(' ', 4, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 4) sources.Add($"Window: {parts[0]} {parts[3].Trim()}");
            }
        }
        return sources;
    }

    private static bool TryCaptureLinuxScreen(string output, out string message)
    {
        var attempts = new (string Command, string[] Args, string Package)[]
        {
            ("grim", [output], "grim"),
            ("gnome-screenshot", ["-f", output], "gnome-screenshot"),
            ("spectacle", ["-b", "-n", "-o", output], "spectacle"),
            ("scrot", [output], "scrot"),
            ("import", ["-window", "root", output], "imagemagick"),
        };
        foreach (var (command, args, _) in attempts)
            if (TryRun(command, args, out _) && File.Exists(output) && new FileInfo(output).Length > 0)
            { message = ""; return true; }
        if (TryRunPipeline("xwd -root -silent", "convert xwd:- " + Quote(output)) && File.Exists(output) && new FileInfo(output).Length > 0)
        { message = ""; return true; }
        message = "No Linux screenshot tool was available. Install one of: grim (Wayland), gnome-screenshot, spectacle, scrot, or ImageMagick (import/convert with xwd).";
        return false;
    }

    private static CaptureResult ResultFromBitmap(Bitmap image, string source, int maxWidth)
    {
        using var stream = new MemoryStream();
        image.Save(stream);
        return ResultFromEncoded(stream.ToArray(), source, maxWidth);
    }

    private static CaptureResult ResultFromEncoded(byte[] imageBytes, string source, int maxWidth)
    {
        Bitmap preview;
        using (var input = new MemoryStream(imageBytes))
        {
            var original = new Bitmap(input);
            preview = original.PixelSize.Width > Math.Max(160, maxWidth)
                ? Bitmap.DecodeToWidth(new MemoryStream(imageBytes), Math.Max(160, maxWidth))
                : original;
        }
        using var output = new MemoryStream();
        preview.Save(output);
        var bytes = output.ToArray();
        return new CaptureResult(bytes, preview, Convert.ToHexString(SHA256.HashData(bytes))[..16], source, DateTime.Now);
    }

    private static bool TryRun(string command, IReadOnlyList<string> args, out string output)
    {
        output = "";
        try
        {
            var start = new ProcessStartInfo(command) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var arg in args) start.ArgumentList.Add(arg);
            using var process = Process.Start(start);
            if (process is null) return false;
            output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(7000);
            return process.HasExited && process.ExitCode == 0;
        }
        catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
    }

    private static bool TryRunPipeline(string first, string second)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("sh", "-c " + Quote(first + " | " + second)) { UseShellExecute = false });
            process?.WaitForExit(7000);
            return process is { HasExited: true, ExitCode: 0 };
        }
        catch { return false; }
    }

    private static string Quote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    private static (IntPtr Handle, string Title)? FindWindow(string target)
    {
        var windows = Windows();
        var head = target.Split(" - ")[0].Trim();
        foreach (var match in new Func<string, bool>[] { t => t == target, t => t.Contains(target, StringComparison.OrdinalIgnoreCase), t => head.Length >= 3 && t.Contains(head, StringComparison.OrdinalIgnoreCase) })
            if (windows.FirstOrDefault(w => match(w.Title)) is { Handle: not 0 } found) return found;
        return null;
    }

    private static Bitmap Grab(int width, int height, Func<IntPtr, bool> draw, string failure = "Windows could not capture the screen.")
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
            var result = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            using (var locked = result.Lock()) Marshal.Copy(pixels, 0, locked.Address, pixels.Length);
            return result;
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

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; public readonly int Width => Right - Left; public readonly int Height => Bottom - Top; }
    [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
    [StructLayout(LayoutKind.Sequential)] private struct BITMAPINFOHEADER { public int biSize, biWidth, biHeight; public short biPlanes, biBitCount; public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant; }
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
