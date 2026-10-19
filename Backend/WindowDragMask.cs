using System;
using System.Runtime.InteropServices;

namespace UtilityTool.Backend
{
    // Hide only the target's drawing surface while two separate windows move.
    // Its visibility, bounds, ownership, and minimize/maximize state stay intact.
    internal sealed class WindowDragMask : IDisposable
    {
        private readonly IntPtr window;
        private readonly uint thread, process;
        private IntPtr original;
        private bool disposed;

        private WindowDragMask(IntPtr window, uint thread, uint process, IntPtr original)
        { this.window = window; this.thread = thread; this.process = process; this.original = original; }

        internal static WindowDragMask TryCreate(IntPtr window)
        {
            uint process; uint thread = GetWindowThreadProcessId(window, out process);
            if (thread == 0) return null;
            IntPtr saved = CreateRectRgn(0, 0, 0, 0);
            if (saved == IntPtr.Zero) return null;
            // No assigned region is different from an explicitly empty region.
            if (GetWindowRgn(window, saved) == 0) { DeleteObject(saved); saved = IntPtr.Zero; }
            IntPtr empty = CreateRectRgn(0, 0, 0, 0);
            if (empty == IntPtr.Zero || SetWindowRgn(window, empty, true) == 0)
            {
                if (empty != IntPtr.Zero) DeleteObject(empty);
                if (saved != IntPtr.Zero) DeleteObject(saved);
                return null;
            }
            // A successful SetWindowRgn transfers the supplied region to Windows.
            return new WindowDragMask(window, thread, process, saved);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            uint currentProcess;
            // HWNDs can be reused after an app exits; don't modify another window.
            if (GetWindowThreadProcessId(window, out currentProcess) == thread && currentProcess == process)
                if (SetWindowRgn(window, original, true) != 0) original = IntPtr.Zero;
            if (original != IntPtr.Zero) { DeleteObject(original); original = IntPtr.Zero; }
        }

        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
        [DllImport("user32.dll")] private static extern int GetWindowRgn(IntPtr window, IntPtr region);
        [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr window, IntPtr region, [MarshalAs(UnmanagedType.Bool)] bool redraw);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
        [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(IntPtr region);
    }
}
