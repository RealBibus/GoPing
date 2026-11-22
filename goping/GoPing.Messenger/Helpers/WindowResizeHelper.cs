using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace GoPing.Helpers;

/// <summary>
/// Позволяет тянуть границы окна без системной рамки (WindowStyle=None).
/// Левая, правая и нижняя границы, а также нижние углы.
/// </summary>
internal static class WindowResizeHelper
{
    private const int WM_NCHITTEST = 0x0084;
    private const int HTCLIENT = 1;
    private const int HTLEFT = 10;
    private const int HTRIGHT = 11;
    private const int HTBOTTOM = 15;
    private const int HTBOTTOMLEFT = 16;
    private const int HTBOTTOMRIGHT = 17;
    private const int Edge = 8;

    public static void Attach(Window window, Func<bool>? canResize = null)
    {
        IntPtr handle = new WindowInteropHelper(window).Handle;
        HwndSource? source = HwndSource.FromHwnd(handle);
        source?.AddHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            if (msg != WM_NCHITTEST) return IntPtr.Zero;
            if (canResize is not null && !canResize()) return IntPtr.Zero;

            IntPtr defaultResult = DefWindowProc(hwnd, (uint)msg, wParam, lParam);
            if (defaultResult.ToInt64() != HTCLIENT) return defaultResult;
            if (!GetWindowRect(hwnd, out RECT rect)) return defaultResult;

            long packed = lParam.ToInt64();
            int x = unchecked((short)(packed & 0xFFFF));
            int y = unchecked((short)((packed >> 16) & 0xFFFF));

            bool left = x <= rect.Left + Edge;
            bool right = x >= rect.Right - Edge;
            bool bottom = y >= rect.Bottom - Edge;

            int hit = HTCLIENT;
            if (bottom && left) hit = HTBOTTOMLEFT;
            else if (bottom && right) hit = HTBOTTOMRIGHT;
            else if (left) hit = HTLEFT;
            else if (right) hit = HTRIGHT;
            else if (bottom) hit = HTBOTTOM;

            if (hit == HTCLIENT) return defaultResult;
            handled = true;
            return new IntPtr(hit);
        });
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
}
