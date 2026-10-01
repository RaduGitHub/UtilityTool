using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;

namespace UtilityTool.Backend
{
    internal static class AppWindows
    {
        internal sealed class WindowOwner : System.Windows.Forms.IWin32Window
        {
            public IntPtr Handle { get; private set; }
            internal WindowOwner(IntPtr handle) { Handle = handle; }
        }
        internal static bool IsForeground(int processId)
        {
            uint id; GetWindowThreadProcessId(GetForegroundWindow(), out id);
            return id == processId;
        }
        internal static IntPtr ForegroundWindow { get { return GetForegroundWindow(); } }
        internal static void MoveWindow(IntPtr window, Point location)
        {
            SetWindowPos(window, IntPtr.Zero, location.X, location.Y, 0, 0,
                0x0001 | 0x0004 | 0x0010 | 0x0200); // no size/z-order/activation/owner reorder
        }
        internal static void WindowCommand(IntPtr window, int command, IntPtr position)
        {
            if (command == 0xF060) PostMessage(window, 0x0010, IntPtr.Zero, IntPtr.Zero); // WM_CLOSE
            else
            {
                if ((command & 0xFFF0) == 0xF030 && IsZoomed(window)) command = 0xF120;
                PostMessage(window, 0x0112, new IntPtr(command), position); // WM_SYSCOMMAND
            }
        }
        internal static void PlaceAboveTarget(IntPtr overlay, IntPtr target, ISet<IntPtr> overlays)
        {
            // Show/resize can raise an owned window independently. Match the target's
            // actual position rather than relying on ownership to preserve its level.
            IntPtr preceding = GetWindow(target, 3); // GW_HWNDPREV
            while (preceding != IntPtr.Zero && overlays.Contains(preceding))
                preceding = GetWindow(preceding, 3);
            const uint flags = 0x0001 | 0x0002 | 0x0010 | 0x0200; // no size/move/activate/owner reorder
            if ((GetWindowStyle(overlay, -20) & 8) != 0)
                SetWindowPos(overlay, new IntPtr(-2), 0, 0, 0, 0, flags); // HWND_NOTOPMOST
            // Inserting after a topmost window promotes an ordinary window; use HWND_TOP instead.
            if (preceding != IntPtr.Zero && (GetWindowStyle(preceding, -20) & 8) != 0) preceding = IntPtr.Zero;
            if (GetWindow(overlay, 3) == preceding) return;
            SetWindowPos(overlay, preceding, 0, 0, 0, 0, flags);
        }
        internal static void PlaceBelowWindow(IntPtr window, IntPtr above)
        {
            if (GetWindow(window, 3) != above)
                SetWindowPos(window, above, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 | 0x0200);
        }
        internal static bool ClientBounds(IntPtr window, out Rectangle bounds)
        {
            Rect rect; var origin = new NativePoint();
            bounds = Rectangle.Empty;
            if (!GetClientRect(window, out rect) || !ClientToScreen(window, ref origin)) return false;
            bounds = new Rectangle(origin.X, origin.Y, rect.Right - rect.Left, rect.Bottom - rect.Top);
            return bounds.Width > 0 && bounds.Height > 0;
        }
        internal static Dictionary<IntPtr, Rectangle> Find(int processId)
        {
            var windows = new Dictionary<IntPtr, Rectangle>();
            EnumWindows(delegate(IntPtr window, IntPtr unused)
            {
                uint id;
                GetWindowThreadProcessId(window, out id);
                Rect rect;
                if (id == processId && IsWindowVisible(window) && !IsIconic(window)
                    && GetWindowRect(window, out rect) && rect.Right > rect.Left && rect.Bottom > rect.Top)
                    windows.Add(window, Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom));
                return true;
            }, IntPtr.Zero);
            return windows;
        }

        private delegate bool EnumCallback(IntPtr window, IntPtr unused);
        [StructLayout(LayoutKind.Sequential)]
        private struct Rect { internal int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct NativePoint { internal int X, Y; }
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowStyle(IntPtr window, int index);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out Rect rect);
        [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr window, ref NativePoint point);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumCallback callback, IntPtr unused);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr window);
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr command, IntPtr position);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
    }
}
