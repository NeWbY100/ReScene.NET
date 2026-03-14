using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ReScene.NET.Helpers;

/// <summary>
/// Enables the Windows immersive dark mode title bar on WPF windows.
/// </summary>
internal static partial class DarkTitleBar
{
    [LibraryImport("dwmapi.dll", SetLastError = true)]
    private static partial int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    /// <summary>
    /// Enables dark mode for the window's title bar. Call from SourceInitialized or later.
    /// </summary>
    public static void Enable(Window window)
    {
        if (PresentationSource.FromVisual(window) is HwndSource source)
        {
            int value = 1;
            DwmSetWindowAttribute(source.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int));
        }
    }
}
