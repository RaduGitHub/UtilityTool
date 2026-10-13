using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using UtilityTool.Backend;

namespace UtilityTool.Frontend
{
    // An opaque privacy curtain hides app content beneath the password prompt.
    internal sealed class AppLockOverlay : IDisposable
    {
        private readonly GuardProcess target;
        private readonly Func<IntPtr> foregroundWindow;
        private readonly Dictionary<IntPtr, Form> covers = new Dictionary<IntPtr, Form>();
        private readonly Timer timer = new Timer { Interval = 100 };
        private WindowEventWatcher windowEvents;
        private Process observedProcess;
        private bool updatingCovers;
        private PasswordDialog dialog;
        private IntPtr promptWindow;
        private bool promptShown;
        private TaskCompletionSource<bool> completion;
        private Func<bool> stillLocked;
        private bool disposed;
        private bool wasTargetActive;
        private Rectangle lastPromptArea;

        internal AppLockOverlay(GuardProcess process, Func<IntPtr> foreground = null)
        { target = process; foregroundWindow = foreground ?? (() => AppWindows.ForegroundWindow); }

        internal Task<bool> PromptAsync(PasswordStore passwords, Func<bool> isLocked = null)
        {
            stillLocked = isLocked;
            completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            dialog = new PasswordDialog(passwords, false, "Unlock " + target.Name, true, false, true);
            dialog.TopMost = false;
            dialog.FormBorderStyle = FormBorderStyle.None;
            dialog.ControlBox = false;
            dialog.StartPosition = FormStartPosition.Manual;
            dialog.FormClosed += delegate
            {
                timer.Stop();
                if (windowEvents != null) { windowEvents.Dispose(); windowEvents = null; }
                completion.TrySetResult(dialog.DialogResult == DialogResult.OK);
            };
            // Modeless dialogs need an explicit close for their Cancel button.
            ((Button)dialog.CancelButton).Click += delegate { dialog.DialogResult = DialogResult.Cancel; dialog.Close(); };
            timer.Tick += Tick;
            // A hosted Store app's frame moves in a different process. Watch its
            // frame events too, filtering unrelated windows before doing any work.
            bool hosted = false;
            foreach (IntPtr window in AppWindows.Find(target.Id).Keys)
                if (AppWindows.ProcessId(window) != target.Id) { hosted = true; break; }
            windowEvents = new WindowEventWatcher(OnWindowEvent, hosted ? 0 : target.Id, true, true);
            timer.Interval = windowEvents.Active ? 1000 : 100;
            // An OS process-exit notification releases overlays promptly without
            // terminating an app that is still flushing data during normal exit.
            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            try
            {
                observedProcess = Process.GetProcessById(target.Id);
                if (target.StartTimeTicks != 0 && observedProcess.StartTime.ToUniversalTime().Ticks != target.StartTimeTicks)
                { Cancel(); return completion.Task; }
                observedProcess.Exited += delegate
                {
                    if (dispatcher.HasShutdownStarted) return;
                    try { dispatcher.BeginInvoke(new Action(Cancel)); }
                    catch (InvalidOperationException) { }
                };
                observedProcess.EnableRaisingEvents = true;
            }
            catch (ArgumentException) { Cancel(); return completion.Task; }
            catch (InvalidOperationException) { Cancel(); return completion.Task; }
            catch (System.ComponentModel.Win32Exception) { if (observedProcess != null) { observedProcess.Dispose(); observedProcess = null; } }
            UpdateCovers();
            timer.Start();
            return completion.Task;
        }

        private void Tick(object sender, EventArgs args)
        {
            if (stillLocked != null && !stillLocked()) { Cancel(); return; }
            try
            {
                using (var process = Process.GetProcessById(target.Id))
                    if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != target.StartTimeTicks)
                    { Cancel(); return; }
            }
            catch (ArgumentException) { Cancel(); return; }
            catch (InvalidOperationException) { Cancel(); return; }
            UpdateCovers();
        }

        private void UpdateCovers()
        {
            if (disposed || dialog.IsDisposed || updatingCovers) return;
            updatingCovers = true;
            try { UpdateCoversForForeground(foregroundWindow()); }
            finally { updatingCovers = false; }
        }

        private void OnWindowEvent(IntPtr window, uint type)
        {
            if (disposed || dialog.IsDisposed) return;
            if (type != WindowEventWatcher.Foreground && !covers.ContainsKey(window) &&
                !AppWindows.WindowProcessIds(window).Contains(target.Id)) return;
            if (type == WindowEventWatcher.Destroy && !covers.ContainsKey(window)) return;
            Form restoredCover;
            if ((type == WindowEventWatcher.Show || type == WindowEventWatcher.Minimize) &&
                covers.TryGetValue(window, out restoredCover))
                ((LockCoverWindow)restoredCover).ResumeWindowControls();
            if (type == WindowEventWatcher.Location)
                foreach (var cover in covers.Values) if (((LockCoverWindow)cover).IsDragging) return;
            if (type == WindowEventWatcher.Foreground) wasTargetActive = false;
            UpdateCovers();
        }

        private void UpdateCoversForForeground(IntPtr foreground)
        {
            IntPtr activeWindow = IntPtr.Zero;
            foreach (var pair in covers)
                if (pair.Value.IsHandleCreated && foreground == pair.Value.Handle) activeWindow = pair.Key;
            if (dialog.IsHandleCreated && foreground == dialog.Handle) activeWindow = promptWindow;
            var windows = AppWindows.Find(target.Id);
            var overlayHandles = new HashSet<IntPtr>();
            foreach (var cover in covers.Values) if (cover.IsHandleCreated) overlayHandles.Add(cover.Handle);
            if (dialog.IsHandleCreated) overlayHandles.Add(dialog.Handle);
            var windowOrder = new Dictionary<IntPtr, IntPtr>();
            foreach (IntPtr window in windows.Keys) windowOrder[window] = AppWindows.PreviousOutside(window, overlayHandles);
            // Remove destroyed windows immediately. Detach the prompt first so
            // disposing its former owner doesn't dispose this reusable dialog.
            foreach (IntPtr window in new List<IntPtr>(covers.Keys))
            {
                if (AppWindows.Exists(window)) continue;
                if (promptWindow == window)
                {
                    dialog.Hide(); dialog.Owner = null; promptShown = false;
                    promptWindow = IntPtr.Zero; lastPromptArea = Rectangle.Empty;
                }
                covers[window].Dispose(); covers.Remove(window);
            }
            if (windows.ContainsKey(foreground)) activeWindow = foreground;
            foreach (var pair in covers)
                if (!windows.ContainsKey(pair.Key) && !((LockCoverWindow)pair.Value).ClosePending) pair.Value.Hide();
            foreach (var pair in windows)
            {
                Rectangle client;
                if (!AppWindows.ClientBounds(pair.Key, out client)) continue;
                Form cover;
                if (!covers.TryGetValue(pair.Key, out cover))
                {
                    cover = new LockCoverWindow(pair.Key, target.Name) { Bounds = pair.Value };
                    IntPtr window = pair.Key;
                    cover.Click += delegate { ShowPrompt(window, true); };
                    ((LockCoverWindow)cover).DragPositionChanged += delegate(Rectangle bounds)
                    {
                        if (promptShown && promptWindow == window && !dialog.IsDisposed) PositionPrompt(bounds);
                    };
                    // Owned by the target app, above that app but below unrelated foreground apps.
                    cover.Show(new AppWindows.WindowOwner(pair.Key));
                    covers.Add(pair.Key, cover);
                    overlayHandles.Add(cover.Handle);
                }
                if (!cover.Visible) cover.Show(new AppWindows.WindowOwner(pair.Key));
                ((LockCoverWindow)cover).FitToTarget(pair.Value);
            }
            if (dialog.IsDisposed) return;
            bool targetActive = activeWindow != IntPtr.Zero && covers.ContainsKey(activeWindow) &&
                !((LockCoverWindow)covers[activeWindow]).UsingWindowControls;
            // Activating our owned curtain need not raise the external owner.
            // Move that owner immediately below the opaque curtain first; later
            // z-order matching must not send the prompt back underneath Chrome.
            if (targetActive && AppWindows.Exists(activeWindow))
                AppWindows.PlaceBelowWindow(activeWindow, covers[activeWindow].Handle);
            // Always attach a visible prompt to a visible curtain, even when the
            // app opened behind Chrome. Showing it must not steal Chrome's focus.
            // Prefer the window the user returned to when the app has several.
            IntPtr nextWindow = targetActive ? activeWindow : promptWindow;
            if (!covers.ContainsKey(nextWindow) ||
                (!windows.ContainsKey(nextWindow) && !((LockCoverWindow)covers[nextWindow]).ClosePending))
            {
                nextWindow = IntPtr.Zero;
                foreach (var pair in windows) if (covers.ContainsKey(pair.Key)) { nextWindow = pair.Key; break; }
            }
            if (nextWindow != IntPtr.Zero)
            {
                bool focus = targetActive && (!dialog.Visible || nextWindow != promptWindow ||
                    (!wasTargetActive && foreground != dialog.Handle));
                ShowPrompt(nextWindow, focus);
            }
            else dialog.Hide();
            if (dialog.IsHandleCreated) overlayHandles.Add(dialog.Handle);
            // Show/ownership changes can raise the external owner, even without
            // activation. Preserve every background app window's original level.
            foreach (var pair in windowOrder)
                if (!targetActive || pair.Key != activeWindow) AppWindows.RestoreOrder(pair.Key, pair.Value, overlayHandles);
            wasTargetActive = targetActive;
            MatchWindowOrder();
        }

        private void MatchWindowOrder()
        {
            var handles = new HashSet<IntPtr>();
            foreach (var cover in covers.Values) if (cover.IsHandleCreated) handles.Add(cover.Handle);
            if (dialog.IsHandleCreated) handles.Add(dialog.Handle);
            foreach (var pair in covers)
                if (pair.Value.Visible && !(dialog.Visible && pair.Key == promptWindow) &&
                    (!((LockCoverWindow)pair.Value).ClosePending || AppWindows.Visible(pair.Key)))
                    AppWindows.PlaceAboveTarget(pair.Value.Handle, pair.Key, handles);
            if (dialog.Visible && covers.ContainsKey(promptWindow))
            {
                if (!((LockCoverWindow)covers[promptWindow]).ClosePending || AppWindows.Visible(promptWindow))
                    AppWindows.PlaceAboveTarget(dialog.Handle, promptWindow, handles);
                AppWindows.PlaceBelowWindow(covers[promptWindow].Handle, dialog.Handle);
            }
        }

        private void ShowPrompt(IntPtr window, bool focus)
        {
            Form cover;
            if (disposed || dialog.IsDisposed || !covers.TryGetValue(window, out cover)) return;
            promptWindow = window;
            promptShown = true;
            if (!((LockCoverWindow)cover).IsDragging) PositionPrompt(cover.Bounds);
            if (dialog.Owner != cover) dialog.Owner = cover;
            if (!dialog.Visible) dialog.Show(cover);
            if (focus && !dialog.ContainsFocus) dialog.FocusPassword();
            MatchWindowOrder();
        }

        private void PositionPrompt(Rectangle area)
        {
            if (area == lastPromptArea) return;
            lastPromptArea = area;
            Rectangle screen = Screen.FromRectangle(area).WorkingArea;
            var location = new Point(
                Math.Max(screen.Left, Math.Min(area.Left + (area.Width - dialog.Width) / 2, screen.Right - dialog.Width)),
                Math.Max(screen.Top, Math.Min(area.Top + (area.Height - dialog.Height) / 2, screen.Bottom - dialog.Height)));
            if (dialog.Location != location) dialog.Location = location;
        }

        internal void Cancel() { Dispose(); }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            // Hidden modeless dialogs do not reliably raise FormClosed on Close().
            // Complete pending access explicitly and release every cover immediately.
            if (completion != null) completion.TrySetResult(false);
            timer.Dispose();
            if (observedProcess != null) { observedProcess.Dispose(); observedProcess = null; }
            if (windowEvents != null) { windowEvents.Dispose(); windowEvents = null; }
            if (dialog != null) dialog.Dispose();
            foreach (var cover in covers.Values) cover.Dispose();
            covers.Clear();
        }
    }
}
