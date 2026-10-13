using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace UtilityTool.Backend
{
    // Out-of-context notifications: no injected DLL and no active polling loop.
    // Construct and dispose on the UI thread, which supplies the message loop.
    internal sealed class WindowEventWatcher : IDisposable
    {
        internal const uint Create = 0x8000, Destroy = 0x8001, Show = 0x8002, Hide = 0x8003;
        internal const uint Foreground = 3, Location = 0x800B, Minimize = 0x0016, Restore = 0x0017;
        private readonly Action<IntPtr, uint> changed;
        private readonly WinEventProc callback;
        private readonly List<IntPtr> hooks = new List<IntPtr>();
        private readonly int expectedHooks;
        private GCHandle callbackRoot;
        private bool disposed;
        internal bool Active { get { return !disposed && hooks.Count == expectedHooks; } }
        internal bool WatchesOwnForeground { get; private set; }

        internal WindowEventWatcher(Action<IntPtr, uint> changed, int process = 0, bool trackLocation = false, bool watchOwnForeground = false)
        {
            this.changed = changed; callback = OnEvent; callbackRoot = GCHandle.Alloc(callback);
            WatchesOwnForeground = watchOwnForeground;
            expectedHooks = trackLocation ? 4 : 3;
            Add(Create, Hide, (uint)process);
            Add(Foreground, Foreground, 0, watchOwnForeground);
            Add(Minimize, Restore, (uint)process); // minimize start/end
            if (trackLocation) Add(Location, Location, (uint)process);
        }
        private void Add(uint first, uint last, uint process, bool includeOwn = false)
        {
            // Only the overlay's foreground hook observes our curtain/prompt.
            // Creation and movement hooks still skip our own UI to avoid feedback.
            IntPtr hook = SetWinEventHook(first, last, IntPtr.Zero, callback, process, 0, includeOwn ? 0u : 2u);
            if (hook != IntPtr.Zero) hooks.Add(hook);
        }
        private void OnEvent(IntPtr hook, uint type, IntPtr window, int objectId, int childId, uint thread, uint time)
        {
            // Ignore controls, caret events and child windows, including busy UI trees.
            if (disposed || window == IntPtr.Zero || objectId != 0 || childId != 0) return;
            IntPtr root = GetAncestor(window, 2); // GA_ROOT
            if (root != window && (type != Destroy || root != IntPtr.Zero))
            {
                if (root == IntPtr.Zero || !AppWindows.IsApplicationFrame(root)) return;
                window = root; // app-owned CoreWindow inside a shared Store-app frame
            }
            try { changed(window, type); }
            catch (Exception) { /* Never unwind a managed exception into the native callback. */ }
        }
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            foreach (IntPtr hook in hooks) UnhookWinEvent(hook);
            hooks.Clear(); if (callbackRoot.IsAllocated) callbackRoot.Free();
        }
        private delegate void WinEventProc(IntPtr hook, uint type, IntPtr window, int objectId, int childId, uint thread, uint time);
        [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint first, uint last, IntPtr module, WinEventProc callback, uint process, uint thread, uint flags);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnhookWinEvent(IntPtr hook);
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    }
}
