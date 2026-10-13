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
        private bool busy;
        private readonly bool activateOnShow;
        protected override bool ShowWithoutActivation { get { return !activateOnShow; } }
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();

        internal void FocusPassword()
        {
            if (IsDisposed || !Visible || busy || ContainsFocus) return;
            // Joining input queues resets keyboard state and can lose fast keystrokes.
            // Never refocus a dialog the user is already typing or clicking in.
            if (GetForegroundWindow() != Handle) SetForegroundWindow(Handle);
            Activate();
            SelectPasswordField();
        }
        private void SelectPasswordField()
        {
            if (IsDisposed || !Visible || !password.Enabled) return;
            // Select establishes the active-control chain through the nested layout panels.
            password.Select();
            password.Focus();
        }
        internal void CancelForExitedApp() { busy = false; DialogResult = DialogResult.Cancel; Close(); }

        internal PasswordDialog(PasswordStore store, bool create, string title, bool closeOnIncorrect = false, bool focusOnShow = true, bool privacyCurtain = false)
        {
            activateOnShow = focusOnShow;
            Text = title; ClientSize = new Size(444, (create ? 374 : 306) + (privacyCurtain ? 52 : 0));
            StartPosition = FormStartPosition.CenterParent; FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            Font = new Font("Segoe UI", 10.5f);
            HandleCreated += delegate { AppTheme.ApplyWindowFrame(this, BackColor); };
            BackColor = AppTheme.FluentSurface; ForeColor = AppTheme.Ink;
            var layout = new BufferedLayout { Dock = DockStyle.Fill, Padding = new Padding(28), ColumnCount = 1, RowCount = create ? 8 : 6, BackColor = BackColor };
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
            // Reserve status space so "Checking..." never moves the button under the cursor.
            message.Dock = DockStyle.Fill; message.ForeColor = Color.FromArgb(255, 157, 157); message.Margin = new Padding(0, 10, 0, 0); layout.Controls.Add(message);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, Margin = new Padding(0), WrapContents = false };
            submit.Text = create ? "Save password" : "Unlock"; submit.Size = new Size(144, 42); submit.Margin = new Padding(0, 0, 10, 0);
            var cancel = new ModernButton { Text = "Cancel", FluentPalette = true, Size = new Size(110, 42), Margin = new Padding(0), DialogResult = DialogResult.Cancel };
            buttons.Controls.Add(submit); buttons.Controls.Add(cancel); layout.Controls.Add(buttons);
            if (privacyCurtain)
                buttons.SizeChanged += delegate { buttons.Padding = new Padding(Math.Max(0, (buttons.ClientSize.Width - submit.Width - submit.Margin.Horizontal - cancel.Width) / 2), 0, 0, 0); };
            Controls.Add(layout); AcceptButton = submit; CancelButton = cancel;
            ActiveControl = password;
            // This modeless WinForms prompt also runs in WPF's message loop, which
            // does not route WinForms default-button accelerators. Handle Enter at
            // the edit controls without moving focus or waiting for key release.
            KeyEventHandler submitOnEnter = delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode != Keys.Enter) return;
                e.Handled = true; e.SuppressKeyPress = true;
                if (!busy && submit.Enabled) submit.PerformClick();
            };
            password.KeyDown += submitOnEnter;
            confirmation.KeyDown += submitOnEnter;
            // Focus once when opening. Deferred activation callbacks can steal focus
            // between a button's mouse-down and mouse-up, cancelling the first click.
            Shown += delegate { if (focusOnShow) FocusPassword(); };
            FormClosing += delegate(object sender, FormClosingEventArgs e) { if (busy) e.Cancel = true; };
            submit.Click += async delegate
            {
                if (busy) return;
                // Minimum length temporarily disabled; confirmation must still match.
                if (create && password.Text != confirmation.Text)
                { message.Text = "Passwords must match."; return; }
                string entered = password.Text;
                busy = true; submit.Enabled = false;
                password.ReadOnly = true; confirmation.ReadOnly = true;
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
                    if (success) { busy = false; DialogResult = DialogResult.OK; Close(); }
                    else if (closeOnIncorrect)
                    {
                        message.Text = "The password is incorrect. The app will close now.";
                        await Task.Delay(1500);
                        if (IsDisposed || !Visible) return;
                        busy = false; DialogResult = DialogResult.Cancel; Close();
                    }
                    else { message.Text = "Incorrect password."; await Task.Delay(1000); }
                }
                catch (Exception error) { message.Text = "Password operation failed: " + error.Message; }
                finally
                {
                    entered = null; busy = false;
                    if (!IsDisposed)
                    {
                        UseWaitCursor = false;
                        submit.Enabled = true; password.ReadOnly = false; confirmation.ReadOnly = false;
                        if (Visible && ContainsFocus) { password.Focus(); password.SelectAll(); }
                    }
                }
            };
        }
    }
}
