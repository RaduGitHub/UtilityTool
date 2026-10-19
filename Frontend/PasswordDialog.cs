using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using UtilityTool.Backend;

namespace UtilityTool.Frontend
{
    internal sealed class PasswordDialog : Form
    {
        private readonly TextBox password = new TextBox();
        private readonly TextBox confirmation = new TextBox();
        private readonly Label message = new Label();
        private readonly Button submit = new ModernButton { Primary = true, FluentPalette = true };
        private readonly ComboBox allowedFor = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat };
        private readonly int[] unlockMinutes = { 0, 1, 5, 15, 30, 60 };
        private bool accessUnavailable;
        internal TimeSpan? AllowedFor { get; private set; }
        private bool busy;
        private readonly bool activateOnShow;
        private readonly bool keepLockedOnDismiss;
        private readonly Func<IntPtr> foregroundWindow;
        private readonly Action<IntPtr> requestForeground;
        private bool allowClose;
        internal event Action KeepLocked;
        protected override bool ShowWithoutActivation { get { return !activateOnShow; } }
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();

        internal void FocusPassword()
        {
            if (IsDisposed || !Visible || busy) return;
            // Joining input queues resets keyboard state and can lose fast keystrokes.
            // A child can retain this thread's keyboard focus while another app is
            // foreground. Reclaim activation in that case, without moving focus
            // between the user's password field and Unlock button.
            bool wasForeground = foregroundWindow() == Handle;
            if (!wasForeground) requestForeground(Handle);
            if (!wasForeground || !ContainsFocus) SelectPasswordField();
        }
        internal bool HasForegroundFocus
        { get { return !IsDisposed && Visible && foregroundWindow() == Handle && ContainsFocus; } }
        private void SelectPasswordField()
        {
            if (IsDisposed || !Visible || !password.Enabled) return;
            // Select establishes the active-control chain through the nested layout panels.
            password.Select();
            password.Focus();
        }
        internal void CancelForExitedApp() { allowClose = true; busy = false; DialogResult = DialogResult.Cancel; Close(); }
        internal void ShowAccessError(string error)
        {
            accessUnavailable = true;
            submit.Enabled = false; password.ReadOnly = true; allowedFor.Enabled = false;
            message.Text = error;
        }

        private void DismissKeepingLocked()
        {
            if (!keepLockedOnDismiss || busy || IsDisposed) return;
            DialogResult = DialogResult.None; password.Clear(); if (!accessUnavailable) message.Text = "";
            var handler = KeepLocked; if (handler != null) handler();
            Hide();
        }

        protected override bool ProcessCmdKey(ref Message message, Keys keyData)
        {
            if (keepLockedOnDismiss && keyData == Keys.Escape) { DismissKeepingLocked(); return true; }
            return base.ProcessCmdKey(ref message, keyData);
        }

        internal PasswordDialog(PasswordStore store, bool create, string title, bool closeOnIncorrect = false, bool focusOnShow = true, bool privacyCurtain = false, bool keepLockedOnDismiss = false, Func<IntPtr> foreground = null, Action<IntPtr> activate = null)
        {
            activateOnShow = focusOnShow;
            foregroundWindow = foreground ?? (() => GetForegroundWindow());
            requestForeground = activate ?? (window => { SetForegroundWindow(window); Activate(); });
            this.keepLockedOnDismiss = keepLockedOnDismiss && !create;
            bool timedUnlock = privacyCurtain && !create;
            Text = title; ClientSize = new Size(444, (create ? 374 : 306) + (privacyCurtain ? 52 : 0) + (timedUnlock ? 66 : 0));
            StartPosition = FormStartPosition.CenterParent; FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            Font = new Font("Segoe UI", 10.5f);
            HandleCreated += delegate { AppTheme.ApplyWindowFrame(this, BackColor); };
            BackColor = AppTheme.FluentSurface; ForeColor = AppTheme.Ink;
            var layout = new BufferedLayout { Dock = DockStyle.Fill, Padding = new Padding(28), ColumnCount = 1, RowCount = (create ? 8 : 6) + (timedUnlock ? 2 : 0), BackColor = BackColor };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, privacyCurtain ? 96 : 44));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            if (create)
            {
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            }
            if (timedUnlock)
            {
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            }
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            var heading = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0) };
            heading.Controls.Add(new Label { Text = title, Font = new Font("Segoe UI Semibold", 18), Dock = DockStyle.Fill, TextAlign = privacyCurtain ? ContentAlignment.MiddleCenter : ContentAlignment.MiddleLeft, AutoEllipsis = true });
            heading.Controls.Add(new Label { Text = "\uE72E", Font = new Font("Segoe MDL2 Assets", privacyCurtain ? 30 : 23), ForeColor = Color.FromArgb(145, 201, 255), Dock = privacyCurtain ? DockStyle.Top : DockStyle.Left, Width = 46, Height = 52, TextAlign = privacyCurtain ? ContentAlignment.MiddleCenter : ContentAlignment.MiddleLeft });
            layout.Controls.Add(heading);
            layout.Controls.Add(new Label { Text = create ? "Choose your own UtilityTool password." : "This app is locked. Enter your password to continue.", ForeColor = AppTheme.FluentMuted, Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 0), TextAlign = privacyCurtain ? ContentAlignment.TopCenter : ContentAlignment.TopLeft });
            layout.Controls.Add(new Label { Text = "Password", Dock = DockStyle.Fill, Margin = new Padding(0) });
            password.AccessibleName = "UtilityTool password"; password.TabIndex = 0;
            layout.Controls.Add(new PasswordInputFrame(password));
            if (create)
            {
                layout.Controls.Add(new Label { Text = "Confirm password", Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 0) });
                confirmation.AccessibleName = "Confirm UtilityTool password"; layout.Controls.Add(new PasswordInputFrame(confirmation));
            }
            if (timedUnlock)
            {
                layout.Controls.Add(new Label { Text = "Time allowed for this unlock", ForeColor = AppTheme.FluentMuted, Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 0) });
                allowedFor.AccessibleName = "Time allowed for this unlock";
                allowedFor.TabIndex = 1; allowedFor.Dock = DockStyle.Top;
                allowedFor.BackColor = AppTheme.FluentSurface; allowedFor.ForeColor = AppTheme.Ink;
                allowedFor.Items.AddRange(new object[] { "Until the app exits", "1 minute", "5 minutes", "15 minutes", "30 minutes", "1 hour" });
                allowedFor.SelectedIndex = 0; layout.Controls.Add(allowedFor);
            }
            // Reserve status space so "Checking..." never moves the button under the cursor.
            message.Dock = DockStyle.Fill; message.ForeColor = Color.FromArgb(255, 157, 157); message.Margin = new Padding(0, 10, 0, 0); layout.Controls.Add(message);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, Margin = new Padding(0), WrapContents = false };
            submit.Text = create ? "Save password" : "Unlock"; submit.Size = new Size(144, 42); submit.Margin = new Padding(0, 0, 10, 0);
            var cancel = new ModernButton { Text = this.keepLockedOnDismiss ? "Keep locked" : "Cancel", FluentPalette = true, Size = new Size(110, 42), Margin = new Padding(0), DialogResult = this.keepLockedOnDismiss ? DialogResult.None : DialogResult.Cancel };
            buttons.Controls.Add(submit); buttons.Controls.Add(cancel); layout.Controls.Add(buttons);
            if (privacyCurtain)
                buttons.SizeChanged += delegate { buttons.Padding = new Padding(Math.Max(0, (buttons.ClientSize.Width - submit.Width - submit.Margin.Horizontal - cancel.Width) / 2), 0, 0, 0); };
            Controls.Add(layout); AcceptButton = submit; CancelButton = cancel;
            if (this.keepLockedOnDismiss)
            {
                // Form.CancelButton assigns Cancel automatically. Keep this
                // modeless dismissal separate from the access task's result.
                cancel.DialogResult = DialogResult.None;
                cancel.Click += delegate { DismissKeepingLocked(); };
            }
            ActiveControl = password;
            // This modeless WinForms prompt also runs in WPF's message loop, which
            // does not route WinForms default-button accelerators. Handle Enter at
            // the edit controls without moving focus or waiting for key release.
            KeyEventHandler submitOnEnter = delegate(object sender, KeyEventArgs e)
            {
                if (this.keepLockedOnDismiss && e.KeyCode == Keys.Escape)
                { e.Handled = true; e.SuppressKeyPress = true; DismissKeepingLocked(); return; }
                if (e.KeyCode != Keys.Enter) return;
                e.Handled = true; e.SuppressKeyPress = true;
                if (!busy && submit.Enabled) submit.PerformClick();
            };
            password.KeyDown += submitOnEnter;
            confirmation.KeyDown += submitOnEnter;
            // Focus once when opening. Deferred activation callbacks can steal focus
            // between a button's mouse-down and mouse-up, cancelling the first click.
            Shown += delegate { if (focusOnShow) FocusPassword(); };
            FormClosing += delegate(object sender, FormClosingEventArgs e)
            {
                if (busy) { e.Cancel = true; return; }
                if (this.keepLockedOnDismiss && !allowClose && DialogResult != DialogResult.OK)
                { e.Cancel = true; DismissKeepingLocked(); }
            };
            submit.Click += async delegate
            {
                if (busy || accessUnavailable) return;
                // Minimum length temporarily disabled; confirmation must still match.
                if (create && password.Text != confirmation.Text)
                { message.Text = "Passwords must match."; return; }
                string entered = password.Text;
                int chosenMinutes = timedUnlock ? unlockMinutes[Math.Max(0, allowedFor.SelectedIndex)] : 0;
                busy = true; submit.Enabled = false;
                password.ReadOnly = true; confirmation.ReadOnly = true;
                allowedFor.Enabled = false;
                UseWaitCursor = true;
                message.Text = "Checking...";
                try
                {
                    bool success = await Task.Run(delegate
                    {
                        if (create) { store.SetPassword(entered); return true; }
                        return store.Verify(entered);
                    });
                    entered = null;
                    if (IsDisposed || !Visible) return;
                    if (success) { AllowedFor = chosenMinutes == 0 ? (TimeSpan?)null : TimeSpan.FromMinutes(chosenMinutes); busy = false; DialogResult = DialogResult.OK; Close(); }
                    else if (closeOnIncorrect)
                    {
                        message.Text = "The password is incorrect. The app will close now.";
                        await Task.Delay(1500);
                        if (IsDisposed || !Visible) return;
                        busy = false; DialogResult = DialogResult.Cancel; Close();
                    }
                    else { message.Text = "Incorrect password. Try again."; }
                }
                catch (Exception error) { message.Text = "Password operation failed: " + error.Message; }
                finally
                {
                    entered = null; busy = false;
                    if (!IsDisposed)
                    {
                        UseWaitCursor = false;
                        submit.Enabled = !accessUnavailable; password.ReadOnly = accessUnavailable; confirmation.ReadOnly = false; allowedFor.Enabled = !accessUnavailable;
                        if (Visible && ContainsFocus) { password.Focus(); password.SelectAll(); }
                    }
                }
            };
        }
    }
}
