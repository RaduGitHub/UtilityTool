using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;

namespace UtilityTool.Backend
{
    internal static class AppWindows
    {
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
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumCallback callback, IntPtr unused);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
    }
}
