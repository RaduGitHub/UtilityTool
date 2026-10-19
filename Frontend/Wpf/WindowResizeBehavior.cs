using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace UtilityTool.Frontend.Wpf
{
    // Keep resizing native, but give rounded corners a larger diagonal grab area.
    internal sealed class WindowResizeBehavior : IDisposable
    {
        internal const double BorderSize = 6, CornerSize = 12;
        private const int HitTestMessage = 0x0084, NonClientLeftDown = 0x00A1;
        private readonly Window window;
        private readonly HwndSource source;

        internal WindowResizeBehavior(Window window)
        {
            this.window = window;
            source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
            if (source != null) source.AddHook(OnMessage);
        }

        private IntPtr OnMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message != HitTestMessage && message != NonClientLeftDown) return IntPtr.Zero;
            if (window.WindowState != WindowState.Normal ||
                (window.ResizeMode != ResizeMode.CanResize && window.ResizeMode != ResizeMode.CanResizeWithGrip)) return IntPtr.Zero;
            NativeRect bounds;
            if (!GetWindowRect(hwnd, out bounds)) return IntPtr.Zero;
            long coordinates = lParam.ToInt64();
            double x = unchecked((short)(coordinates & 0xFFFF)) - bounds.Left;
            double y = unchecked((short)((coordinates >> 16) & 0xFFFF)) - bounds.Top;
            var dpi = VisualTreeHelper.GetDpi(window);
            int hit = HitTest(x, y, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top, dpi.DpiScaleX, dpi.DpiScaleY);
            if (hit == 0) return IntPtr.Zero;
            if (message == HitTestMessage) { handled = true; return new IntPtr(hit); }
            // The Fluent title buttons also handle native clicks. Let Windows start
            // the resize loop before a button can consume a click in a corner.
            if (wParam.ToInt64() == hit) { handled = true; return DefWindowProc(hwnd, message, wParam, lParam); }
            return IntPtr.Zero;
        }

        internal static int HitTest(double x, double y, double width, double height, double scaleX, double scaleY)
        {
            if (x < 0 || y < 0 || x >= width || y >= height) return 0;
            double borderX = BorderSize * scaleX, borderY = BorderSize * scaleY;
            double cornerX = CornerSize * scaleX, cornerY = CornerSize * scaleY;
            bool leftCorner = x < cornerX, rightCorner = x >= width - cornerX;
            bool topCorner = y < cornerY, bottomCorner = y >= height - cornerY;
            bool left = x < borderX, right = x >= width - borderX;
            bool top = y < borderY, bottom = y >= height - borderY;
            // Corners extend along the rim, rather than swallowing a square of content.
            if (topCorner && leftCorner && (top || left)) return 13; // HTTOPLEFT
            if (topCorner && rightCorner && (top || right)) return 14; // HTTOPRIGHT
            if (bottomCorner && leftCorner && (bottom || left)) return 16; // HTBOTTOMLEFT
            if (bottomCorner && rightCorner && (bottom || right)) return 17; // HTBOTTOMRIGHT
            if (x < borderX) return 10; // HTLEFT
            if (x >= width - borderX) return 11; // HTRIGHT
            if (y < borderY) return 12; // HTTOP
            if (y >= height - borderY) return 15; // HTBOTTOM
            return 0;
        }

        public void Dispose() { if (source != null && !source.IsDisposed) source.RemoveHook(OnMessage); }
        [StructLayout(LayoutKind.Sequential)] private struct NativeRect { internal int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect bounds);
        [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern IntPtr DefWindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);
    }
}
