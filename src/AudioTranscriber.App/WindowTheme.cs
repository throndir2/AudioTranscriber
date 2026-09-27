using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace AudioTranscriber.App;

/// <summary>Dark Windows frames (caption, border) for every app window, matching the in-app theme.</summary>
internal static class WindowTheme
{
    private const int DarkModeLegacy = 19, DarkMode = 20, BorderColor = 34, CaptionColor = 35, TextColor = 36;

    public static void Register() =>
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) => Apply((Window)sender)));

    public static void Apply(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        // Unsupported attributes (older Windows builds) just return an error and are ignored.
        if (Set(hwnd, DarkMode, 1) != 0) Set(hwnd, DarkModeLegacy, 1);
        Set(hwnd, CaptionColor, ColorRef(0x0D, 0x0A, 0x08));
        Set(hwnd, TextColor, ColorRef(0xE8, 0xC8, 0x72));
        Set(hwnd, BorderColor, ColorRef(0x38, 0x2D, 0x23));
    }

    /// <summary>SM_CXPADDEDBORDER in device-independent pixels.</summary>
    public static double PaddedBorder(Visual visual) =>
        GetSystemMetrics(92) / VisualTreeHelper.GetDpi(visual).DpiScaleX;

    private static int ColorRef(byte r, byte g, byte b) => r | (g << 8) | (b << 16);

    private static int Set(IntPtr hwnd, int attribute, int value) =>
        DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int));

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
