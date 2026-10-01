using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using UtilityTool.Backend;

namespace UtilityTool.Frontend
{
    // Blurred snapshots obscure app content while the password prompt stays sharp.
    internal sealed class AppLockOverlay : IDisposable
    {
        private readonly GuardProcess target;
        private readonly Dictionary<IntPtr, Form> covers = new Dictionary<IntPtr, Form>();
        private readonly Timer timer = new Timer { Interval = 100 };
        private PasswordDialog dialog;
        private IntPtr promptWindow;
        private bool promptShown;
        private TaskCompletionSource<bool> completion;
        private Func<bool> stillLocked;
        private bool disposed;
        private bool wasTargetActive;
        private Rectangle lastPromptArea;

        internal AppLockOverlay(GuardProcess process) { target = process; }

        internal Task<bool> PromptAsync(PasswordStore passwords, Func<bool> isLocked = null)
        {
            stillLocked = isLocked;
            completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            dialog = new PasswordDialog(passwords, false, "Unlock " + target.Name, true, false);
            dialog.TopMost = false;
            dialog.FormBorderStyle = FormBorderStyle.None;
            dialog.ControlBox = false;
            dialog.StartPosition = FormStartPosition.Manual;
            dialog.FormClosed += delegate
            {
                timer.Stop();
                completion.TrySetResult(dialog.DialogResult == DialogResult.OK);
            };
            // Modeless dialogs need an explicit close for their Cancel button.
            ((Button)dialog.CancelButton).Click += delegate { dialog.DialogResult = DialogResult.Cancel; dialog.Close(); };
            timer.Tick += Tick;
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
            UpdateCoversForForeground(AppWindows.ForegroundWindow);
        }

        private void UpdateCoversForForeground(IntPtr foreground)
        {
            bool targetActive = false;
            foreach (var cover in covers.Values)
                if (cover.IsHandleCreated && foreground == cover.Handle) targetActive = true;
            if (dialog.IsHandleCreated && foreground == dialog.Handle) targetActive = true;
            var windows = AppWindows.Find(target.Id);
            if (windows.ContainsKey(foreground)) targetActive = true;
            foreach (var cover in covers.Values)
                if (((LockCoverWindow)cover).UsingWindowControls) targetActive = false;
            foreach (var pair in covers)
                if (!windows.ContainsKey(pair.Key)) pair.Value.Hide();
            foreach (var pair in windows)
            {
                Rectangle client;
                if (!AppWindows.ClientBounds(pair.Key, out client)) continue;
                Form cover;
                if (!covers.TryGetValue(pair.Key, out cover))
                {
                    cover = new LockCoverWindow(null, pair.Key, target.Name) { Bounds = pair.Value };
                    IntPtr window = pair.Key;
                    cover.Click += delegate { ShowPrompt(window, true); };
                    ((LockCoverWindow)cover).DragPositionChanged += delegate(Rectangle bounds)
                    {
                        if (promptShown && promptWindow == window && !dialog.IsDisposed) PositionPrompt(bounds);
                    };
                    // Owned by the target app, above that app but below unrelated foreground apps.
                    cover.Show(new AppWindows.WindowOwner(pair.Key));
                    covers.Add(pair.Key, cover);
                    CaptureBackground(pair.Key, (LockCoverWindow)cover, client.Size);
                }
                if (!((LockCoverWindow)cover).IsDragging && cover.Bounds != pair.Value) cover.Bounds = pair.Value;
                if (!cover.Visible) cover.Show(new AppWindows.WindowOwner(pair.Key));
            }
            if (dialog.IsDisposed) return;
            if (promptShown)
            {
                if (!windows.ContainsKey(promptWindow)) dialog.Hide();
                else
                {
                    if (!((LockCoverWindow)covers[promptWindow]).IsDragging) PositionPrompt(covers[promptWindow].Bounds);
                // Restoring the target window restores its owned prompt without stealing focus.
                    if (targetActive && (!dialog.Visible || (!wasTargetActive && foreground != dialog.Handle))) ShowPrompt(promptWindow, true);
                }
            }
            else if (targetActive)
            {
                if (windows.ContainsKey(foreground)) ShowPrompt(foreground, true);
                else foreach (var pair in windows) { ShowPrompt(pair.Key, true); break; }
            }
            wasTargetActive = targetActive;
            MatchWindowOrder();
        }

        private async void CaptureBackground(IntPtr window, LockCoverWindow cover, Size size)
        {
            try { cover.SetBackground(await WindowBlur.CaptureAsync(window, size)); }
            catch (Exception) { /* Unsupported captures retain the neutral frosted cover. */ }
        }

        private void MatchWindowOrder()
        {
            var handles = new HashSet<IntPtr>();
            foreach (var cover in covers.Values) if (cover.IsHandleCreated) handles.Add(cover.Handle);
            if (dialog.IsHandleCreated) handles.Add(dialog.Handle);
            foreach (var pair in covers)
                if (pair.Value.Visible && !(dialog.Visible && pair.Key == promptWindow))
                    AppWindows.PlaceAboveTarget(pair.Value.Handle, pair.Key, handles);
            if (dialog.Visible && covers.ContainsKey(promptWindow))
            {
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
            PositionPrompt(cover.Bounds);
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
            if (dialog != null) dialog.Dispose();
            foreach (var cover in covers.Values) cover.Dispose();
            covers.Clear();
        }
    }
}
