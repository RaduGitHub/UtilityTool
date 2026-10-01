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
        private readonly Button submit = new ModernButton();
        private bool busy;
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

        internal PasswordDialog(PasswordStore store, bool create, string title, bool closeOnIncorrect = false, bool focusOnShow = true)
        {
            Text = title; ClientSize = new Size(420, create ? 300 : 245);
            StartPosition = FormStartPosition.CenterParent; FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            Font = new Font("Segoe UI", 10);
            HandleCreated += delegate { AppTheme.ApplyWindowFrame(this); };
            BackColor = AppTheme.Surface;
            var layout = new FrostedLayout { Dock = DockStyle.Fill, Padding = new Padding(20), ColumnCount = 1, RowCount = create ? 7 : 5 };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            if (create)
            {
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            }
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.Controls.Add(new Label { Text = create ? "Create a custom password (any length for testing)" : "Enter your UtilityTool password", AutoSize = true, MaximumSize = new Size(380, 0) });
            password.UseSystemPasswordChar = true; password.MaxLength = 0; password.Dock = DockStyle.Top;
            password.AccessibleName = "UtilityTool password"; password.TabIndex = 0;
            layout.Controls.Add(password);
            if (create)
            {
                layout.Controls.Add(new Label { Text = "Confirm password", AutoSize = true });
                confirmation.UseSystemPasswordChar = true; confirmation.MaxLength = 0; confirmation.Dock = DockStyle.Top;
                confirmation.AccessibleName = "Confirm UtilityTool password"; layout.Controls.Add(confirmation);
            }
            // Reserve status space so "Checking..." never moves the button under the cursor.
            message.Dock = DockStyle.Fill; message.ForeColor = AppTheme.Error; layout.Controls.Add(message);
            var buttons = new FlowLayoutPanel { AutoSize = true };
            submit.Text = create ? "Save password" : "Unlock"; submit.AutoSize = true;
            var cancel = new ModernButton { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
            buttons.Controls.Add(submit); buttons.Controls.Add(cancel); layout.Controls.Add(buttons);
            Controls.Add(layout); AppTheme.Apply(this); AcceptButton = submit; CancelButton = cancel;
            ActiveControl = password;
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
                        busy = false;
                        MessageBox.Show(this, "The password is incorrect, the app will close now.",
                            "Incorrect password", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        DialogResult = DialogResult.Cancel; Close();
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
