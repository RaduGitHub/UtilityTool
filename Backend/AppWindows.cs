using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

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
        internal static int ProcessId(IntPtr window) { uint id; GetWindowThreadProcessId(window, out id); return (int)id; }
        internal static bool Exists(IntPtr window) { return IsWindow(window); }
        internal static bool Visible(IntPtr window) { return IsWindowVisible(window) && !IsIconic(window); }
        internal static bool IsApplicationFrame(IntPtr window)
        {
            var name = new StringBuilder(128);
            return GetClassName(window, name, name.Capacity) != 0 && name.ToString() == "ApplicationFrameWindow";
        }
        internal static HashSet<int> WindowProcessIds(IntPtr window)
        {
            var ids = new HashSet<int> { ProcessId(window) };
            // Older Store apps put their app-owned child window inside a frame
            // owned by ApplicationFrameHost. Inspect only these small host trees.
            if (IsApplicationFrame(window)) EnumChildWindows(window, delegate(IntPtr child, IntPtr unused)
            {
                if (IsWindowVisible(child)) ids.Add(ProcessId(child));
                return true;
            }, IntPtr.Zero);
            ids.Remove(0);
            return ids;
        }
        internal static HashSet<int> VisibleProcessIds()
        {
            var ids = new HashSet<int>();
            EnumWindows(delegate(IntPtr window, IntPtr unused)
            {
                if (Visible(window)) ids.UnionWith(WindowProcessIds(window));
                return true;
            }, IntPtr.Zero);
            return ids;
        }
        internal static void MoveWindow(IntPtr window, Point location)
        {
            SetWindowPos(window, IntPtr.Zero, location.X, location.Y, 0, 0,
                0x0001 | 0x0004 | 0x0010 | 0x0200); // no size/z-order/activation/owner reorder
        }
        internal static bool SetOwner(IntPtr window, IntPtr owner)
        {
            // Only our top-level curtain is changed, never the protected app.
            if (IntPtr.Size == 8) SetWindowLongPtr(window, -8, owner);
            else SetWindowLong(window, -8, owner.ToInt32());
            return GetWindow(window, 4) == owner; // GW_OWNER
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
        internal static IntPtr PreviousOutside(IntPtr window, ISet<IntPtr> overlays)
        {
            IntPtr previous = GetWindow(window, 3);
            while (previous != IntPtr.Zero && overlays.Contains(previous)) previous = GetWindow(previous, 3);
            return previous;
        }
        internal static void RestoreOrder(IntPtr window, IntPtr previous, ISet<IntPtr> overlays)
        {
            if (!Exists(window) || (previous != IntPtr.Zero && !Exists(previous)) || PreviousOutside(window, overlays) == previous) return;
            if (previous != IntPtr.Zero && (GetWindowStyle(previous, -20) & 8) != 0) previous = IntPtr.Zero;
            SetWindowPos(window, previous, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 | 0x0200);
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
        internal static bool DisplayBounds(IntPtr window, IntPtr coordinatesFor, out Rectangle bounds)
        {
            Rect frame;
            bounds = Rectangle.Empty;
            try
            {
                if (DwmGetWindowAttribute(window, 9, out frame, Marshal.SizeOf(typeof(Rect))) == 0 &&
                    frame.Right > frame.Left && frame.Bottom > frame.Top)
                {
                    var first = new NativePoint { X = frame.Left, Y = frame.Top };
                    var last = new NativePoint { X = frame.Right, Y = frame.Bottom };
                    // DWM reports physical pixels; keep all geometry in the
                    // curtain's coordinate space when display scaling differs.
                    PhysicalToLogicalPointForPerMonitorDPI(coordinatesFor, ref first);
                    PhysicalToLogicalPointForPerMonitorDPI(coordinatesFor, ref last);
                    bounds = Rectangle.FromLTRB(first.X, first.Y, last.X, last.Y);
                    return bounds.Width > 0 && bounds.Height > 0;
                }
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
            if (!GetWindowRect(window, out frame)) return false;
            bounds = Rectangle.FromLTRB(frame.Left, frame.Top, frame.Right, frame.Bottom);
            return bounds.Width > 0 && bounds.Height > 0;
        }
        internal static Rectangle FitVisibleFrame(Rectangle target, Rectangle outer, Rectangle visible)
        {
            return Rectangle.FromLTRB(target.Left - Math.Max(0, visible.Left - outer.Left),
                target.Top - Math.Max(0, visible.Top - outer.Top),
                target.Right + Math.Max(0, outer.Right - visible.Right),
                target.Bottom + Math.Max(0, outer.Bottom - visible.Bottom));
        }
        internal static Dictionary<IntPtr, Rectangle> Find(int processId)
        {
            var windows = new Dictionary<IntPtr, Rectangle>();
            EnumWindows(delegate(IntPtr window, IntPtr unused)
            {
                Rect rect;
                if (Visible(window) && (ProcessId(window) == processId ||
                    (IsApplicationFrame(window) && WindowProcessIds(window).Contains(processId)))
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
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(IntPtr window, int index, int value);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowStyle(IntPtr window, int index);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out Rect rect);
        [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr window, ref NativePoint point);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumCallback callback, IntPtr unused);
        [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumCallback callback, IntPtr unused);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int count);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr window);
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr command, IntPtr position);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out Rect value, int size);
        [DllImport("user32.dll")] private static extern bool PhysicalToLogicalPointForPerMonitorDPI(IntPtr window, ref NativePoint point);
    }
}
