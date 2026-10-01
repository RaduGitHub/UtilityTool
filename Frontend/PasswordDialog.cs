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
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint source, uint target, bool attach);

        internal void FocusPassword()
        {
            if (IsDisposed || !Visible || busy) return;
            uint unused;
            uint foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out unused);
            uint ownThread = GetCurrentThreadId();
            bool attached = false;
            try
            {
                // Link input only during activation; never leave the app input queues joined.
                if (foregroundThread != 0 && foregroundThread != ownThread)
                    attached = AttachThreadInput(ownThread, foregroundThread, true);
                BringToFront();
                SetForegroundWindow(Handle);
                Activate();
                SelectPasswordField();
            }
            finally
            {
                if (attached) AttachThreadInput(ownThread, foregroundThread, false);
            }
        }
        private void SelectPasswordField()
        {
            if (IsDisposed || !Visible || !password.Enabled) return;
            // Select establishes the active-control chain through the nested layout panels.
            password.Select();
            password.Focus();
        }
        internal void CancelForExitedApp() { busy = false; DialogResult = DialogResult.Cancel; Close(); }

        internal PasswordDialog(PasswordStore store, bool create, string title, bool closeOnIncorrect = false)
        {
            Text = title; ClientSize = new Size(420, create ? 300 : 245);
            StartPosition = FormStartPosition.CenterParent; FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            Font = new Font("Segoe UI", 10);
            HandleCreated += delegate { AppTheme.ApplyWindowFrame(this); };
            BackColor = AppTheme.Surface;
            var layout = new FrostedLayout { Dock = DockStyle.Fill, Padding = new Padding(20), ColumnCount = 1 };
            layout.Controls.Add(new Label { Text = create ? "Create a custom password (8–256 characters)" : "Enter your UtilityTool password", AutoSize = true });
            password.UseSystemPasswordChar = true; password.MaxLength = 256; password.Dock = DockStyle.Top;
            password.AccessibleName = "UtilityTool password"; password.TabIndex = 0;
            layout.Controls.Add(password);
            if (create)
            {
                layout.Controls.Add(new Label { Text = "Confirm password", AutoSize = true });
                confirmation.UseSystemPasswordChar = true; confirmation.MaxLength = 256; confirmation.Dock = DockStyle.Top;
                confirmation.AccessibleName = "Confirm UtilityTool password"; layout.Controls.Add(confirmation);
            }
            message.AutoSize = true; message.ForeColor = AppTheme.Error; layout.Controls.Add(message);
            var buttons = new FlowLayoutPanel { AutoSize = true };
            submit.Text = create ? "Save password" : "Unlock"; submit.AutoSize = true;
            var cancel = new ModernButton { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
            buttons.Controls.Add(submit); buttons.Controls.Add(cancel); layout.Controls.Add(buttons);
            Controls.Add(layout); AppTheme.Apply(this); AcceptButton = submit; CancelButton = cancel;
            ActiveControl = password;
            Shown += delegate { FocusPassword(); BeginInvoke((Action)SelectPasswordField); };
            Activated += delegate
            {
                if (!IsDisposed && IsHandleCreated)
                    BeginInvoke((Action)SelectPasswordField);
            };
            FormClosing += delegate(object sender, FormClosingEventArgs e) { if (busy) e.Cancel = true; };
            submit.Click += async delegate
            {
                if (create && (password.Text.Length < 8 || password.Text != confirmation.Text))
                { message.Text = "Use at least 8 characters and matching passwords."; return; }
                string entered = password.Text;
                password.Clear(); confirmation.Clear();
                busy = true; submit.Enabled = false; password.Enabled = false; confirmation.Enabled = false;
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
                    if (!IsDisposed) { submit.Enabled = true; password.Enabled = true; confirmation.Enabled = true; password.Focus(); }
                }
            };
        }
    }
}
