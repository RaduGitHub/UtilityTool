using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
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

        internal AppLockOverlay(GuardProcess process) { target = process; }

        internal bool Prompt(PasswordStore passwords, Form owner)
        {
            using (dialog = new PasswordDialog(passwords, false, "Unlock " + target.Name, true))
            {
                dialog.TopMost = true;
                dialog.StartPosition = FormStartPosition.Manual;
                UpdateCovers();
                Rectangle area = Screen.PrimaryScreen.WorkingArea;
                foreach (var cover in covers.Values) { area = cover.Bounds; break; }
                Rectangle screen = Screen.FromRectangle(area).WorkingArea;
                dialog.Location = new Point(
                    Math.Max(screen.Left, Math.Min(area.Left + (area.Width - dialog.Width) / 2, screen.Right - dialog.Width)),
                    Math.Max(screen.Top, Math.Min(area.Top + (area.Height - dialog.Height) / 2, screen.Bottom - dialog.Height)));
                timer.Tick += Tick;
                timer.Start();
                Form primaryCover = null;
                foreach (var cover in covers.Values) { primaryCover = cover; break; }
                try { return dialog.ShowDialog(primaryCover ?? owner) == DialogResult.OK; }
                finally { timer.Stop(); timer.Tick -= Tick; }
            }
        }

        private void Tick(object sender, EventArgs args)
        {
            try
            {
                using (var process = Process.GetProcessById(target.Id))
                    if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != target.StartTimeTicks)
                    { dialog.CancelForExitedApp(); return; }
            }
            catch (ArgumentException) { dialog.CancelForExitedApp(); return; }
            catch (InvalidOperationException) { dialog.CancelForExitedApp(); return; }
            UpdateCovers();
        }

        private void UpdateCovers()
        {
            var windows = AppWindows.Find(target.Id);
            var removed = new List<IntPtr>();
            foreach (var pair in covers)
                if (!windows.ContainsKey(pair.Key)) { pair.Value.Dispose(); removed.Add(pair.Key); }
            foreach (var key in removed) covers.Remove(key);
            foreach (var pair in windows)
            {
                Form cover;
                if (!covers.TryGetValue(pair.Key, out cover))
                {
                    var background = WindowBlur.Capture(pair.Value, SystemInformation.VirtualScreen);
                    cover = new LockCoverWindow(background) { Bounds = pair.Value };
                    cover.Click += delegate { dialog.FocusPassword(); };
                    cover.Show();
                    // The prompt remains above every cover, including windows appearing later.
                    if (dialog.Visible) dialog.FocusPassword();
                    covers.Add(pair.Key, cover);
                }
                cover.Bounds = pair.Value;
            }
        }

        public void Dispose()
        {
            timer.Dispose();
            foreach (var cover in covers.Values) cover.Dispose();
            covers.Clear();
        }
    }
}
